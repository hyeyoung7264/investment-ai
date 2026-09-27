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
            throw new CliUsageException("ingest securities|indices|prices|splits|all");
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
        await foreach (var (ticker, series) in store.StreamBarsByTickerAsync(from, to, null, ct))
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

        var outPath = o.Get("out") ?? Path.Combine("reports", "data-quality.json");
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
