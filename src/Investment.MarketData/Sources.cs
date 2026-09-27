using System.Globalization;
using System.Net;
using System.Text;
using Investment.MarketData.Kind;
using Investment.MarketData.Naver;

namespace Investment.MarketData;

public interface IPriceSource
{
    string Name { get; }
    Task<IReadOnlyList<RawDailyBar>> GetDailyAsync(string ticker, DateOnly from, DateOnly to, CancellationToken ct);
    Task<IReadOnlyList<RawDailyBar>> GetIndexDailyAsync(string indexCode, DateOnly from, DateOnly to, CancellationToken ct);
}

public interface ISecurityMasterSource
{
    Task<IReadOnlyList<ListedCompany>> GetListedAsync(CancellationToken ct);
    Task<IReadOnlyList<DelistedCompany>> GetDelistedAsync(DateOnly from, DateOnly to, CancellationToken ct);
}

internal static class Http
{
    static Http() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static HttpClient CreateClient()
    {
        var c = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (investment-research; non-commercial)");
        return c;
    }

    public static Encoding EucKr => Encoding.GetEncoding("euc-kr");

    /// <summary>Retries transient failures with exponential backoff.</summary>
    public static async Task<T> RetryAsync<T>(Func<Task<T>> action, CancellationToken ct, int attempts = 4)
    {
        for (var i = 1; ; i++)
        {
            try { return await action(); }
            catch (Exception e) when (i < attempts && e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * Math.Pow(2, i)), ct);
            }
        }
    }
}

/// <summary>Unofficial Naver chart API. See docs/DATA-QUALITY.md for known limitations.</summary>
public sealed class NaverPriceSource(HttpClient? http = null) : IPriceSource
{
    private readonly HttpClient _http = http ?? Http.CreateClient();

    public string Name => "naver-chart";

    public Task<IReadOnlyList<RawDailyBar>> GetDailyAsync(string ticker, DateOnly from, DateOnly to, CancellationToken ct) =>
        FetchAsync($"https://api.stock.naver.com/chart/domestic/item/{Uri.EscapeDataString(ticker)}/day", from, to, ct);

    public Task<IReadOnlyList<RawDailyBar>> GetIndexDailyAsync(string indexCode, DateOnly from, DateOnly to, CancellationToken ct) =>
        FetchAsync($"https://api.stock.naver.com/chart/domestic/index/{Uri.EscapeDataString(indexCode)}/day", from, to, ct);

    private Task<IReadOnlyList<RawDailyBar>> FetchAsync(string baseUrl, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var url = $"{baseUrl}?startDateTime={from.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}0000&endDateTime={to.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}2359";
        return Http.RetryAsync(async () =>
        {
            using var resp = await _http.GetAsync(url, ct);
            if ((int)resp.StatusCode >= 500 || resp.StatusCode == HttpStatusCode.TooManyRequests)
                throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {url}");
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            return NaverParser.ParseDaily(json);
        }, ct);
    }
}

public sealed class KindSecurityMasterSource(HttpClient? http = null) : ISecurityMasterSource
{
    private readonly HttpClient _http = http ?? Http.CreateClient();

    public async Task<IReadOnlyList<ListedCompany>> GetListedAsync(CancellationToken ct)
    {
        var bytes = await Http.RetryAsync(() => _http.GetByteArrayAsync(
            "https://kind.krx.co.kr/corpgeneral/corpList.do?method=download&searchType=13", ct), ct);
        var list = KindParser.ParseListed(Http.EucKr.GetString(bytes));
        if (list.Count < 1000)
            throw new InvalidDataException($"KIND listed list suspiciously small ({list.Count}); aborting to avoid a partial master.");
        return list;
    }

    public async Task<IReadOnlyList<DelistedCompany>> GetDelistedAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        const int pageSize = 100;
        var all = new List<DelistedCompany>();
        int? total = null;
        for (var page = 1; total is null || all.Count < total; page++)
        {
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["method"] = "searchDelCompanySub",
                ["currentPageSize"] = pageSize.ToString(CultureInfo.InvariantCulture),
                ["pageIndex"] = page.ToString(CultureInfo.InvariantCulture),
                ["orderMode"] = "2",
                ["orderStat"] = "D",
                ["marketType"] = "",
                ["searchCorpName"] = "",
                ["fromDate"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["toDate"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            });
            var html = await Http.RetryAsync(async () =>
            {
                using var resp = await _http.PostAsync("https://kind.krx.co.kr/investwarn/delcompany.do", form, ct);
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync(ct);
            }, ct);
            total ??= KindParser.ParseTotalCount(html) ?? 0;
            var rows = KindParser.ParseDelisted(html);
            if (rows.Count == 0) break;
            all.AddRange(rows);
        }
        if (total is > 0 && all.Count != total)
            throw new InvalidDataException($"KIND delisted list incomplete: {all.Count}/{total}");
        return all;
    }
}
