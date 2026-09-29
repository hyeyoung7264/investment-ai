using System.Globalization;
using System.Text.Json;
using Investment.Domain.Market;

namespace Investment.MarketData.Krx;

public sealed class KrxException(string code, string message) : Exception($"KRX {code}: {message}")
{
    public string Code { get; } = code;
}

/// <summary>
/// KRX Open API (data-dbg.krx.co.kr). The key comes from KRX_API_KEY or ~/.config/investment-ai/krx.key and is
/// sent only in the AUTH_KEY header, never logged. Prices are unadjusted (official exchange records).
/// </summary>
public sealed class KrxClient(string apiKey, HttpClient? http = null)
{
    private readonly HttpClient _http = http ?? Http.CreateClient();

    public static string? LoadKey()
    {
        var env = Environment.GetEnvironmentVariable("KRX_API_KEY");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "investment-ai", "krx.key");
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    /// <summary>All ETFs on one day (market recorded as "ETF").</summary>
    public async Task<IReadOnlyList<KrxDaily>> GetEtfDailyAsync(DateOnly date, CancellationToken ct) =>
        Parse(await GetJsonAsync($"https://data-dbg.krx.co.kr/svc/apis/etp/etf_bydd_trd?basDd={date:yyyyMMdd}", ct), date, "ETF");

    /// <summary>Index rows of one KRX index family on one day: api = kospi_dd_trd, kosdaq_dd_trd, drvprod_dd_trd, ...</summary>
    public async Task<IReadOnlyList<(string Name, decimal Open, decimal High, decimal Low, decimal Close)>> GetIndexFamilyAsync(string api, DateOnly date, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await GetJsonAsync($"https://data-dbg.krx.co.kr/svc/apis/idx/{api}?basDd={date:yyyyMMdd}", ct));
        if (!doc.RootElement.TryGetProperty("OutBlock_1", out var rows)) throw new KrxException("?", "no OutBlock_1");
        var list = new List<(string, decimal, decimal, decimal, decimal)>();
        foreach (var r in rows.EnumerateArray())
        {
            if (DateOnly.ParseExact(r.GetProperty("BAS_DD").GetString()!, "yyyyMMdd", CultureInfo.InvariantCulture) != date) continue;
            list.Add((r.GetProperty("IDX_NM").GetString()!.Trim(), Dec(r, "OPNPRC_IDX"), Dec(r, "HGPRC_IDX"), Dec(r, "LWPRC_IDX"), Dec(r, "CLSPRC_IDX")));
        }
        return list;
    }

    private async Task<string> GetJsonAsync(string url, CancellationToken ct) => await Http.RetryAsync(async () =>
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("AUTH_KEY", apiKey);
        using var resp = await _http.SendAsync(req, ct);
        if ((int)resp.StatusCode >= 500) throw new HttpRequestException($"KRX HTTP {(int)resp.StatusCode}");
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body)) throw new HttpRequestException("KRX empty response");
        // gateway error pages come back as HTML with status 200: transient, retried
        if (body.TrimStart().StartsWith('<')) throw new HttpRequestException("KRX returned HTML instead of JSON");
        return body;
    }, ct);

    /// <summary>All stocks of one market on one day. market: "stk" (KOSPI) or "ksq" (KOSDAQ).</summary>
    public async Task<IReadOnlyList<KrxDaily>> GetDailyAsync(string market, DateOnly date, CancellationToken ct)
    {
        var url = $"https://data-dbg.krx.co.kr/svc/apis/sto/{market}_bydd_trd?basDd={date:yyyyMMdd}";
        var json = await Http.RetryAsync(async () =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("AUTH_KEY", apiKey);
            using var resp = await _http.SendAsync(req, ct);
            if ((int)resp.StatusCode >= 500) throw new HttpRequestException($"KRX HTTP {(int)resp.StatusCode}");
            var body = await resp.Content.ReadAsStringAsync(ct);
            // the API occasionally answers with an empty body or an HTML error page: transient, retried with backoff
            if (string.IsNullOrWhiteSpace(body)) throw new HttpRequestException("KRX empty response");
            if (body.TrimStart().StartsWith('<')) throw new HttpRequestException("KRX returned HTML instead of JSON");
            return body;
        }, ct);
        return Parse(json, date);
    }

    public static IReadOnlyList<KrxDaily> Parse(string json, DateOnly expected, string? market = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("OutBlock_1", out var rows))
            throw new KrxException(root.TryGetProperty("respCode", out var c) ? c.GetString() ?? "?" : "?",
                root.TryGetProperty("respMsg", out var m) ? m.GetString() ?? "" : json[..Math.Min(100, json.Length)]);
        var list = new List<KrxDaily>();
        foreach (var r in rows.EnumerateArray())
        {
            var date = DateOnly.ParseExact(r.GetProperty("BAS_DD").GetString()!, "yyyyMMdd", CultureInfo.InvariantCulture);
            if (date != expected) continue;
            list.Add(new KrxDaily
            {
                Ticker = r.GetProperty("ISU_CD").GetString()!.Trim(),
                Date = date,
                Market = market ?? (r.TryGetProperty("MKT_NM", out var mk) ? mk.GetString() ?? "" : ""),
                Open = Dec(r, "TDD_OPNPRC"), High = Dec(r, "TDD_HGPRC"), Low = Dec(r, "TDD_LWPRC"), Close = Dec(r, "TDD_CLSPRC"), ChangeFromPrevious = Dec(r, "CMPPREVDD_PRC"),
                Volume = (long)Dec(r, "ACC_TRDVOL"), TradingValue = Dec(r, "ACC_TRDVAL"),
                MarketCap = Dec(r, "MKTCAP"), ListedShares = (long)Dec(r, "LIST_SHRS"),
            });
        }
        return list;
    }

    private static decimal Dec(JsonElement r, string name)
    {
        var s = r.TryGetProperty(name, out var v) ? v.GetString()?.Replace(",", "").Trim() : null;
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }
}
