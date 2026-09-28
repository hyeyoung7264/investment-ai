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
            // the API occasionally answers with an empty body: transient, retried with backoff
            if (string.IsNullOrWhiteSpace(body)) throw new HttpRequestException("KRX empty response");
            return body;
        }, ct);
        return Parse(json, date);
    }

    public static IReadOnlyList<KrxDaily> Parse(string json, DateOnly expected)
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
                Market = r.GetProperty("MKT_NM").GetString() ?? "",
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
