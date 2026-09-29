using Investment.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Investment.MarketData.Krx;

public sealed class KrxIngestion(Func<InvestmentDbContext> dbFactory, MarketDataStore store, KrxClient client, Action<string>? log = null)
{
    private readonly Action<string> _log = log ?? (_ => { });

    /// <summary>Every session of the KOSPI calendar in [from, to] not yet stored (resumable), both markets.</summary>
    public async Task<(int Days, long Rows)> RunAsync(DateOnly from, DateOnly to, CancellationToken ct, int parallel = 4)
    {
        List<DateOnly> calendar, done;
        await using (var db = dbFactory())
        {
            calendar = await db.IndexPrices.Where(p => p.IndexCode == "KOSPI" && p.Date >= from && p.Date <= to).Select(p => p.Date).OrderBy(d => d).ToListAsync(ct);
            done = await db.KrxDailyRows.Where(k => k.Date >= from && k.Date <= to).Select(k => k.Date).Distinct().ToListAsync(ct);
        }
        var todo = calendar.Except(done).ToList();
        _log($"krx: {todo.Count} of {calendar.Count} sessions to fetch");
        int days = 0;
        long rows = 0;
        await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = ct }, async (d, token) =>
        {
            List<Investment.Domain.Market.KrxDaily> all;
            try { all = (await client.GetDailyAsync("stk", d, token)).Concat(await client.GetDailyAsync("ksq", d, token)).ToList(); }
            catch (HttpRequestException e)
            {
                _log($"krx: {d:yyyy-MM-dd} skipped after retries ({e.Message}); rerun to fill");
                return;
            }
            await store.UpsertKrxDailyAsync(all, token);
            Interlocked.Add(ref rows, all.Count);
            if (Interlocked.Increment(ref days) % 100 == 0) _log($"krx: {days}/{todo.Count} sessions, {rows} rows");
        });
        _log($"krx: done {days} sessions, {rows} rows");
        return (days, rows);
    }

    /// <summary>Codes stored in index_prices for KRX index names.</summary>
    public static readonly (string Api, string Name, string Code)[] Indices =
    [
        ("kospi_dd_trd", "코스피 200", "KOSPI200"),
        ("drvprod_dd_trd", "코스피 200 변동성지수", "VKOSPI"),
    ];

    /// <summary>All ETF records and selected indices (KOSPI200, VKOSPI) for sessions not yet stored.</summary>
    public async Task<(int Days, long EtfRows)> RunEtfAndIndicesAsync(DateOnly from, DateOnly to, CancellationToken ct, int parallel = 2)
    {
        List<DateOnly> calendar, etfDone, idxDone;
        await using (var db = dbFactory())
        {
            calendar = await db.IndexPrices.Where(p => p.IndexCode == "KOSPI" && p.Date >= from && p.Date <= to).Select(p => p.Date).OrderBy(d => d).ToListAsync(ct);
            etfDone = await db.KrxDailyRows.Where(k => k.Market == "ETF" && k.Date >= from && k.Date <= to).Select(k => k.Date).Distinct().ToListAsync(ct);
            idxDone = await db.IndexPrices.Where(p => p.IndexCode == "VKOSPI" && p.Date >= from && p.Date <= to).Select(p => p.Date).ToListAsync(ct);
        }
        var todo = calendar.Where(d => !etfDone.Contains(d) || !idxDone.Contains(d)).ToList();
        _log($"krx etf/index: {todo.Count} of {calendar.Count} sessions to fetch");
        int days = 0;
        long rows = 0;
        await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = ct }, async (d, token) =>
        {
            try
            {
                var etf = await client.GetEtfDailyAsync(d, token);
                await store.UpsertKrxDailyAsync(etf, token);
                Interlocked.Add(ref rows, etf.Count);
                var idx = new List<Investment.Domain.Market.IndexPrice>();
                foreach (var g in Indices.GroupBy(i => i.Api))
                {
                    var family = await client.GetIndexFamilyAsync(g.Key, d, token);
                    foreach (var (_, name, code) in g)
                        if (family.FirstOrDefault(f => f.Name == name) is { Name: not null } r && r.Close > 0)
                            idx.Add(new Investment.Domain.Market.IndexPrice { IndexCode = code, Date = d, Open = r.Open, High = r.High, Low = r.Low, Close = r.Close, Source = "krx", IngestedAt = DateTimeOffset.UtcNow });
                }
                await store.UpsertIndexPricesAsync(idx, token);
            }
            catch (HttpRequestException e) { _log($"krx etf/index: {d:yyyy-MM-dd} skipped ({e.Message})"); return; }
            if (Interlocked.Increment(ref days) % 200 == 0) _log($"krx etf/index: {days}/{todo.Count} sessions");
        });
        _log($"krx etf/index: done {days} sessions, {rows} ETF rows");
        return (days, rows);
    }

    /// <summary>KOSPI 200 front-month basis in basis points (index code "K200BASIS": Close = (F−S)/S × 10,000).</summary>
    public async Task<int> RunFuturesBasisAsync(DateOnly from, DateOnly to, CancellationToken ct, int parallel = 2)
    {
        List<DateOnly> calendar, done;
        await using (var db = dbFactory())
        {
            calendar = await db.IndexPrices.Where(p => p.IndexCode == "KOSPI" && p.Date >= from && p.Date <= to).Select(p => p.Date).OrderBy(d => d).ToListAsync(ct);
            done = await db.IndexPrices.Where(p => p.IndexCode == "K200BASIS" && p.Date >= from && p.Date <= to).Select(p => p.Date).ToListAsync(ct);
        }
        var todo = calendar.Except(done).ToList();
        _log($"futures basis: {todo.Count} sessions to fetch");
        var n = 0;
        await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = ct }, async (d, token) =>
        {
            try
            {
                if (await client.GetKospi200FrontFutureAsync(d, token) is not { } f) return;
                var bp = (f.Futures - f.Spot) / f.Spot * 10_000m;
                await store.UpsertIndexPricesAsync([new Investment.Domain.Market.IndexPrice
                {
                    IndexCode = "K200BASIS", Date = d, Open = bp, High = bp, Low = bp, Close = bp, Volume = f.OpenInterest, Source = "krx", IngestedAt = DateTimeOffset.UtcNow,
                }], token);
                if (Interlocked.Increment(ref n) % 500 == 0) _log($"futures basis: {n}/{todo.Count}");
            }
            catch (HttpRequestException e) { _log($"futures basis: {d:yyyy-MM-dd} skipped ({e.Message})"); }
        });
        _log($"futures basis: done {n}");
        return n;
    }
}
