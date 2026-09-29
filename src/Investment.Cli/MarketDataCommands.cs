using System.Text.Json;
using Investment.Domain.Market;
using Investment.MarketData;
using Investment.MarketData.Quality;
using Investment.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Investment.Cli;

public static class MarketDataCommands
{
    public static async Task<int> IngestAsync(string what, CliOptions o, CancellationToken ct)
    {
        var cs = Database.ConnectionString(o.Get("db"));
        var svc = new IngestionService(() => Database.Create(cs), new MarketDataStore(cs),
            new KindSecurityMasterSource(), new NaverPriceSource(), Log);
        var opt = new IngestOptions
        {
            From = o.GetDate("from", new DateOnly(2015, 1, 1)),
            To = o.GetDate("to", DateOnly.FromDateTime(DateTime.Today)),
            Concurrency = o.GetInt("concurrency", 4),
            Tickers = o.Get("tickers")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        };

        if (what is "securities" or "all") await svc.SyncSecurityMasterAsync(opt.From, ct);
        if (what is "indices" or "all") await svc.IngestIndicesAsync(opt, ct);
        if (what is "disclosures")
        {
            var key = Investment.MarketData.Dart.DartClient.LoadKey()
                ?? throw new CliUsageException("OpenDART key missing: set OPENDART_API_KEY or ~/.config/investment-ai/opendart.key");
            var ing = new Investment.MarketData.Dart.DisclosureIngestion(() => Database.Create(cs), new Investment.MarketData.Dart.DartClient(key), Log);
            if (!o.Has("reclassify-only"))
            {
                var r = await ing.RunAsync(o.GetDate("from", new DateOnly(2016, 1, 1)), opt.To, (o.Get("types") ?? "B,I").Split(','), ct);
                Log($"disclosures: fetched {r.Fetched}, inserted {r.Inserted}");
            }
            Log($"disclosures reclassified: {await ing.ReclassifyAsync(ct)}");
            return 0;
        }
        if (what is "krx")
        {
            var key = Investment.MarketData.Krx.KrxClient.LoadKey()
                ?? throw new CliUsageException("KRX key missing: set KRX_API_KEY or ~/.config/investment-ai/krx.key");
            await new Investment.MarketData.Krx.KrxIngestion(() => Database.Create(cs), new MarketDataStore(cs), new Investment.MarketData.Krx.KrxClient(key), Log)
                .RunAsync(o.GetDate("from", new DateOnly(2015, 1, 1)), opt.To, ct);
            return 0;
        }
        if (what is "krx-etf")
        {
            var key = Investment.MarketData.Krx.KrxClient.LoadKey()
                ?? throw new CliUsageException("KRX key missing: set KRX_API_KEY or ~/.config/investment-ai/krx.key");
            await new Investment.MarketData.Krx.KrxIngestion(() => Database.Create(cs), new MarketDataStore(cs), new Investment.MarketData.Krx.KrxClient(key), Log)
                .RunEtfAndIndicesAsync(o.GetDate("from", new DateOnly(2015, 1, 1)), opt.To, ct);
            return 0;
        }
        if (what is "financials")
        {
            var key = Investment.MarketData.Dart.DartClient.LoadKey()
                ?? throw new CliUsageException("OpenDART key missing: set OPENDART_API_KEY or ~/.config/investment-ai/opendart.key");
            await new Investment.MarketData.Dart.FinancialsIngestion(() => Database.Create(cs), new Investment.MarketData.Dart.DartClient(key), Log)
                .RunAsync(o.GetInt("from-year", 2015), o.GetInt("to-year", DateTime.Today.Year), ct);
            return 0;
        }
        if (what is "splits")
        {
            var r = await new Investment.MarketData.Splits.SplitVerificationService(() => Database.Create(cs), new MarketDataStore(cs),
                new Investment.MarketData.Splits.NaverRawQuoteSource(), Log).RunAsync(opt.From, opt.Concurrency, ct);
            Log($"split verification: candidates={r.Candidates} events={r.Events} volume_corrections={r.VolumeCorrections} unverifiable={r.Unverifiable}");
            return 0;
        }
        if (what is "prices" or "all")
        {
            var run = await svc.IngestPricesAsync(opt, ct);
            if (run.Status != "ok") Console.Error.WriteLine($"price ingestion finished with status {run.Status}: {run.Notes}");
        }
        if (what is not ("securities" or "indices" or "prices" or "all"))
            throw new CliUsageException("ingest securities|indices|prices|splits|disclosures|financials|krx|krx-etf|all");
        return 0;
    }

    public static async Task<int> QualityAsync(CliOptions o, CancellationToken ct)
    {
        var cs = Database.ConnectionString(o.Get("db"));
        var store = new MarketDataStore(cs);
        var from = o.GetDate("from", new DateOnly(2015, 1, 1));
        var to = o.GetDate("to", DateOnly.FromDateTime(DateTime.Today));
        var calendar = (await store.LoadIndexAsync("KOSPI", from, to, ct)).Select(b => b.Date).ToList();
        Dictionary<string, Security> securities;
        await using (var db = Database.Create(cs))
            securities = await db.Securities.AsNoTracking().ToDictionaryAsync(s => s.Ticker, ct);

        var issues = new List<QualityIssue>();
        int tickers = 0;
        long bars = 0, halted = 0;
        var krxSource = (o.Get("source") ?? "naver") == "krx";
        async IAsyncEnumerable<(string, List<Bar>)> Source()
        {
            if (krxSource)
                await foreach (var (t, rows) in store.StreamKrxByTickerAsync(from, to, securities.Keys.ToList(), ct))
                    yield return (t, Investment.MarketData.Krx.KrxPriceAdjuster.Build(rows).ToList());
            else
                await foreach (var x in store.StreamBarsByTickerAsync(from, to, null, ct)) yield return x;
        }
        await foreach (var (ticker, series) in Source())
        {
            tickers++;
            bars += series.Count;
            halted += series.Count(b => b.IsHalted);
            if (securities.TryGetValue(ticker, out var s))
                issues.AddRange(DataQualityChecker.Check(s, series, calendar));
        }

        Console.WriteLine($"tickers={tickers} bars={bars} halted_bars={halted} calendar_sessions={calendar.Count}");
        foreach (var g in issues.GroupBy(i => i.Kind).OrderBy(g => g.Key))
            Console.WriteLine($"  {g.Key,-28} issues={g.Count(),6} tickers={g.Select(i => i.Ticker).Distinct().Count(),5}  e.g. {string.Join("; ", g.Take(3).Select(i => $"{i.Ticker}@{i.Date:yyyy-MM-dd} {i.Detail}"))}");

        var outPath = o.Get("out") ?? Path.Combine("reports", krxSource ? "data-quality-krx.json" : "data-quality.json");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(new
        {
            generatedAt = DateTimeOffset.UtcNow,
            from, to, tickers, bars, halted,
            summary = issues.GroupBy(i => i.Kind.ToString()).ToDictionary(g => g.Key, g => new { issues = g.Count(), tickers = g.Select(i => i.Ticker).Distinct().Count() }),
            issues = issues.Select(i => new { i.Ticker, kind = i.Kind.ToString(), i.Date, i.Detail }),
        }, new JsonSerializerOptions { WriteIndented = true }), ct);
        Console.WriteLine($"written {outPath}");
        return 0;
    }

    private static void Log(string msg) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
}
