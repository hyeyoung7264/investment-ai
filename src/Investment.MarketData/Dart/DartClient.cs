using System.Globalization;
using System.Text.Json;
using Investment.Domain.Market;

namespace Investment.MarketData.Dart;

public sealed class DartException(string status, string message) : Exception($"DART {status}: {message}")
{
    public string Status { get; } = status;
}

/// <summary>
/// OpenDART disclosure search (list.json). The key comes from OPENDART_API_KEY or ~/.config/investment-ai/opendart.key
/// and is never logged. Searches without corp_code are limited to 3-month windows.
/// </summary>
public sealed class DartClient(string apiKey, HttpClient? http = null)
{
    private readonly HttpClient _http = http ?? CreateDartHttpClient();

    /// <summary>
    /// opendart.fss.or.kr only negotiates static-RSA key exchange suites (no ECDHE), which .NET on Linux disables by
    /// default. They are enabled for this client only, alongside the modern suites; other hosts keep the default policy.
    /// </summary>
    public static HttpClient CreateDartHttpClient()
    {
        var handler = new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            handler.SslOptions.CipherSuitesPolicy = new System.Net.Security.CipherSuitesPolicy(
            [
                System.Net.Security.TlsCipherSuite.TLS_AES_256_GCM_SHA384,
                System.Net.Security.TlsCipherSuite.TLS_AES_128_GCM_SHA256,
                System.Net.Security.TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384,
                System.Net.Security.TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256,
                System.Net.Security.TlsCipherSuite.TLS_RSA_WITH_AES_256_GCM_SHA384,
                System.Net.Security.TlsCipherSuite.TLS_RSA_WITH_AES_128_GCM_SHA256,
            ]);
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (investment-research; non-commercial)");
        return client;
    }

    public static string? LoadKey()
    {
        var env = Environment.GetEnvironmentVariable("OPENDART_API_KEY");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "investment-ai", "opendart.key");
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    public async Task<IReadOnlyList<Disclosure>> SearchAsync(DateOnly from, DateOnly to, string type, CancellationToken ct)
    {
        if (to.DayNumber - from.DayNumber > 92) throw new ArgumentException("DART allows at most 3 months per search without corp_code");
        var result = new List<Disclosure>();
        for (int page = 1, pages = 1; page <= pages; page++)
        {
            var url = "https://opendart.fss.or.kr/api/list.json?crtfc_key=" + Uri.EscapeDataString(apiKey) +
                      $"&bgn_de={from:yyyyMMdd}&end_de={to:yyyyMMdd}&pblntf_ty={type}&page_count=100&page_no={page}";
            var json = await Http.RetryAsync(async () =>
            {
                using var resp = await _http.GetAsync(url, ct);
                if ((int)resp.StatusCode >= 500) throw new HttpRequestException($"DART HTTP {(int)resp.StatusCode}");
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync(ct);
            }, ct);
            var (items, totalPages) = Parse(json, type);
            result.AddRange(items);
            pages = totalPages;
            await Task.Delay(120, ct); // stay well under DART's rate limits
        }
        return result;
    }

    public static (IReadOnlyList<Disclosure> Items, int TotalPages) Parse(string json, string type)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var status = root.GetProperty("status").GetString()!;
        if (status == "013") return ([], 0); // no data
        if (status != "000") throw new DartException(status, root.GetProperty("message").GetString() ?? "");
        var now = DateTimeOffset.UtcNow;
        var list = new List<Disclosure>();
        foreach (var x in root.GetProperty("list").EnumerateArray())
        {
            var name = x.GetProperty("report_nm").GetString()!.Trim();
            var (ev, corr) = DisclosureClassifier.Classify(name);
            var stock = x.GetProperty("stock_code").GetString()?.Trim();
            list.Add(new Disclosure
            {
                ReceiptNo = x.GetProperty("rcept_no").GetString()!,
                CorpCode = x.GetProperty("corp_code").GetString()!,
                CorpName = x.GetProperty("corp_name").GetString()!,
                Ticker = string.IsNullOrEmpty(stock) ? null : stock,
                CorpClass = x.GetProperty("corp_cls").GetString(),
                ReportName = name,
                ReceiptDate = DateOnly.ParseExact(x.GetProperty("rcept_dt").GetString()!, "yyyyMMdd", CultureInfo.InvariantCulture),
                Filer = x.GetProperty("flr_nm").GetString(),
                Remark = x.GetProperty("rm").GetString(),
                DisclosureType = type,
                Event = ev,
                IsCorrection = corr,
                IngestedAt = now,
            });
        }
        return (list, root.GetProperty("total_page").GetInt32());
    }

    /// <summary>All DART corp codes with their (current or last) stock code. Delisted companies keep theirs.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetCorpCodesByTickerAsync(CancellationToken ct)
    {
        var bytes = await Http.RetryAsync(() => _http.GetByteArrayAsync(
            "https://opendart.fss.or.kr/api/corpCode.xml?crtfc_key=" + Uri.EscapeDataString(apiKey), ct), ct);
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes));
        await using var stream = zip.Entries[0].Open();
        var doc = await System.Xml.Linq.XDocument.LoadAsync(stream, System.Xml.Linq.LoadOptions.None, ct);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in doc.Descendants("list"))
        {
            var stock = e.Element("stock_code")?.Value.Trim();
            if (string.IsNullOrEmpty(stock)) continue;
            map[stock] = e.Element("corp_code")!.Value.Trim(); // later entries win (re-registrations)
        }
        return map;
    }

    /// <summary>Key accounts (revenue, operating income, net income) for up to 100 companies of one periodic report.</summary>
    public async Task<IReadOnlyList<FinancialReportLine>> GetKeyAccountsAsync(IReadOnlyList<string> corpCodes, int year, string reportCode, CancellationToken ct)
    {
        if (corpCodes.Count is 0 or > 100) throw new ArgumentException("1..100 corp codes per call");
        var url = "https://opendart.fss.or.kr/api/fnlttMultiAcnt.json?crtfc_key=" + Uri.EscapeDataString(apiKey) +
                  $"&corp_code={string.Join(',', corpCodes)}&bsns_year={year}&reprt_code={reportCode}";
        var json = await Http.RetryAsync(async () =>
        {
            using var resp = await _http.GetAsync(url, ct);
            if ((int)resp.StatusCode >= 500) throw new HttpRequestException($"DART HTTP {(int)resp.StatusCode}");
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(ct);
        }, ct);
        return ParseKeyAccounts(json);
    }

    private static readonly Dictionary<string, string> Accounts = new()
    {
        ["매출액"] = "Revenue", ["수익(매출액)"] = "Revenue", ["영업수익"] = "Revenue",
        ["영업이익"] = "OperatingIncome", ["영업이익(손실)"] = "OperatingIncome",
        ["당기순이익"] = "NetIncome", ["당기순이익(손실)"] = "NetIncome",
    };

    public static IReadOnlyList<FinancialReportLine> ParseKeyAccounts(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var status = root.GetProperty("status").GetString()!;
        if (status == "013") return [];
        if (status != "000") throw new DartException(status, root.GetProperty("message").GetString() ?? "");
        var result = new Dictionary<(string, string, string), FinancialReportLine>();
        foreach (var x in root.GetProperty("list").EnumerateArray())
        {
            if (!Accounts.TryGetValue(Str(x, "account_nm")?.Replace(" ", "") ?? "", out var account)) continue;
            var rcept = Str(x, "rcept_no")!;
            var line = new FinancialReportLine
            {
                CorpCode = Str(x, "corp_code")!, Ticker = Str(x, "stock_code") is { Length: > 0 } t ? t.Trim() : null,
                FiscalYear = int.Parse(Str(x, "bsns_year")!, CultureInfo.InvariantCulture), ReportCode = Str(x, "reprt_code")!,
                FsDiv = Str(x, "fs_div")!, Account = account,
                ThisAmount = Amount(x, "thstrm_amount"), ThisCumulative = Amount(x, "thstrm_add_amount"),
                PriorAmount = Amount(x, "frmtrm_amount"), PriorCumulative = Amount(x, "frmtrm_add_amount"),
                ReceiptNo = rcept,
                ReceiptDate = DateOnly.ParseExact(rcept[..8], "yyyyMMdd", CultureInfo.InvariantCulture),
            };
            result.TryAdd((line.CorpCode, line.FsDiv, line.Account), line); // first occurrence = income statement line
        }
        return result.Values.ToList();
    }

    private static string? Str(JsonElement x, string name) => x.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static decimal? Amount(JsonElement x, string name)
    {
        var s = Str(x, name)?.Replace(",", "").Trim();
        return string.IsNullOrEmpty(s) || s == "-" ? null : decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
