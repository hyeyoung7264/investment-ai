using System.Collections.Concurrent;
using Investment.Domain.Market;
using Investment.MarketData.Kind;
using Investment.MarketData.Naver;
using Investment.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Investment.MarketData;

public sealed record IngestOptions
{
    public DateOnly From { get; init; } = new(2015, 1, 1);
    public DateOnly To { get; init; } = DateOnly.FromDateTime(DateTime.Today);
    public int Concurrency { get; init; } = 4;
    public bool CommonSharesOnly { get; init; } = true;
    public IReadOnlyList<string>? Tickers { get; init; }
    public IReadOnlyList<string> Indices { get; init; } = ["KOSPI", "KOSDAQ"];
}

public sealed class IngestionService(
    Func<InvestmentDbContext> dbFactory,
    MarketDataStore store,
    ISecurityMasterSource master,
    IPriceSource prices,
    Action<string>? log = null)
{
    private readonly Action<string> _log = log ?? (_ => { });

    public async Task<IngestionRun> SyncSecurityMasterAsync(DateOnly delistedFrom, CancellationToken ct)
    {
        var run = await StartRunAsync("kind", "security-master", ct);
        try
        {
            var listed = await master.GetListedAsync(ct);
            var delisted = await master.GetDelistedAsync(delistedFrom, DateOnly.FromDateTime(DateTime.Today), ct);
            var now = DateTimeOffset.UtcNow;

            await using var db = dbFactory();
            var existing = await db.Securities.ToDictionaryAsync(s => s.Ticker, ct);
            var listedTickers = listed.Select(l => l.Ticker).ToHashSet();
            int added = 0, changed = 0, transfers = 0;

            foreach (var l in listed)
            {
                var kind = SecurityClassifier.Classify(l.Ticker, l.Name, l.Sector, l.Products);
                if (!existing.TryGetValue(l.Ticker, out var s))
                {
                    s = new Security { Ticker = l.Ticker, Name = l.Name };
                    db.Securities.Add(s);
                    existing[l.Ticker] = s;
                    added++;
                }
                else changed++;
                s.Name = l.Name;
                s.Market = l.Market;
                s.Kind = kind;
                s.Sector = l.Sector;
                s.ListedDate = l.ListedDate;
                s.DelistedDate = null;
                s.DelistingReason = null;
                s.UpdatedAt = now;
            }

            // latest delisting event per ticker; market transfers and re-listed tickers are not delistings
            foreach (var d in delisted.OrderBy(d => d.DelistedDate))
            {
                if (d.IsMarketTransfer) { transfers++; continue; }
                if (listedTickers.Contains(d.Ticker)) continue;
                if (!existing.TryGetValue(d.Ticker, out var s))
                {
                    s = new Security { Ticker = d.Ticker, Name = d.Name };
                    db.Securities.Add(s);
                    existing[d.Ticker] = s;
                    added++;
                }
                s.Name = d.Name;
                if (d.Market != MarketType.Unknown) s.Market = d.Market;
                s.Kind = SecurityClassifier.Classify(d.Ticker, d.Name, s.Sector, null);
                s.DelistedDate = d.DelistedDate;
                s.DelistingReason = d.Reason;
                s.UpdatedAt = now;
            }

            var orphans = existing.Values.Count(s => s.DelistedDate is null && !listedTickers.Contains(s.Ticker));
            await db.SaveChangesAsync(ct);
            run.ItemsRequested = listed.Count + delisted.Count;
            run.RowsUpserted = added + changed;
            run.Notes = $"listed={listed.Count} delisted={delisted.Count} transfers_ignored={transfers} added={added} " +
                        $"not_in_any_master_list={orphans}";
            _log(run.Notes);
            return await FinishRunAsync(run, "ok", ct);
        }
        catch (Exception e)
        {
            run.Notes = e.Message;
            await FinishRunAsync(run, "failed", ct);
            throw;
        }
    }

    public async Task<IngestionRun> IngestIndicesAsync(IngestOptions opt, CancellationToken ct)
    {
        var run = await StartRunAsync(prices.Name, "index", ct);
        long rows = 0;
        foreach (var code in opt.Indices)
        {
            var bars = await prices.GetIndexDailyAsync(code, opt.From, opt.To, ct);
            var now = DateTimeOffset.UtcNow;
            await store.UpsertIndexPricesAsync(bars.Select(b => new IndexPrice
            {
                IndexCode = code, Date = b.Date, Open = b.Open, High = b.High, Low = b.Low, Close = b.Close,
                Volume = b.Volume, Source = prices.Name, IngestedAt = now,
            }).ToList(), ct);
            rows += bars.Count;
            _log($"index {code}: {bars.Count} rows");
        }
        run.ItemsRequested = opt.Indices.Count;
        run.RowsUpserted = rows;
        return await FinishRunAsync(run, "ok", ct);
    }

    /// <summary>
    /// Full-history refresh per ticker. Split adjustments rewrite the whole adjusted history, so an
    /// incremental append would silently mix adjusted and unadjusted prices.
    /// </summary>
    public async Task<IngestionRun> IngestPricesAsync(IngestOptions opt, CancellationToken ct)
    {
        List<Security> targets;
        await using (var db = dbFactory())
        {
            var q = db.Securities.AsNoTracking().Where(s => s.Market == MarketType.Kospi || s.Market == MarketType.Kosdaq);
            if (opt.CommonSharesOnly) q = q.Where(s => s.Kind == SecurityKind.Common);
            // skip securities delisted before the requested window
            q = q.Where(s => s.DelistedDate == null || s.DelistedDate >= opt.From);
            targets = await q.OrderBy(s => s.Ticker).ToListAsync(ct);
        }
        if (opt.Tickers is { Count: > 0 } only) targets = targets.Where(t => only.Contains(t.Ticker)).ToList();

        var run = await StartRunAsync(prices.Name, "daily-prices", ct);
        run.ItemsRequested = targets.Count;
        long inserted = 0, updated = 0, deleted = 0;
        int done = 0, failed = 0, empty = 0;
        var revised = new ConcurrentBag<string>();
        var failures = new ConcurrentBag<string>();

        await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = opt.Concurrency, CancellationToken = ct }, async (s, token) =>
        {
            try
            {
                var bars = await prices.GetDailyAsync(s.Ticker, opt.From, opt.To, token);
                if (bars.Count == 0) { Interlocked.Increment(ref empty); return; }
                var now = DateTimeOffset.UtcNow;
                var rows = bars.Select(b => ToDailyPrice(s.Ticker, b, prices.Name, now)).ToList();
                var r = await store.UpsertDailyPricesAsync(rows, token);
                var del = await store.DeleteMissingAsync(s.Ticker, opt.From, opt.To, bars.Select(b => b.Date).ToList(), token);
                Interlocked.Add(ref inserted, r.Inserted);
                Interlocked.Add(ref updated, r.Updated);
                Interlocked.Add(ref deleted, del);
                if (r.Updated > 5) revised.Add($"{s.Ticker}:{r.Updated}");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Interlocked.Increment(ref failed);
                failures.Add($"{s.Ticker}:{e.GetType().Name}");
            }
            var n = Interlocked.Increment(ref done);
            if (n % 200 == 0) _log($"prices {n}/{targets.Count} (inserted={inserted} updated={updated} failed={failed})");
        });

        run.ItemsFailed = failed;
        run.RowsUpserted = inserted + updated;
        run.Notes = $"inserted={inserted} updated={updated} deleted={deleted} empty={empty} failed={failed}" +
                    (revised.IsEmpty ? "" : $" history_revised=[{string.Join(',', revised.Order().Take(50))}]") +
                    (failures.IsEmpty ? "" : $" failures=[{string.Join(',', failures.Order().Take(50))}]");
        _log(run.Notes);
        return await FinishRunAsync(run, failed == 0 ? "ok" : "partial", ct);
    }

    public static DailyPrice ToDailyPrice(string ticker, RawDailyBar b, string source, DateTimeOffset now) => new()
    {
        Ticker = ticker,
        Date = b.Date,
        Open = b.Open,
        High = b.High,
        Low = b.Low,
        Close = b.Close,
        Volume = b.Volume,
        TradingValueEstimate = b.Close * b.Volume,
        IsHalted = b.IsHalted,
        Source = source,
        IngestedAt = now,
    };

    private async Task<IngestionRun> StartRunAsync(string source, string kind, CancellationToken ct)
    {
        var run = new IngestionRun { Id = Guid.NewGuid(), Source = source, Kind = kind, StartedAt = DateTimeOffset.UtcNow, Status = "running" };
        await using var db = dbFactory();
        db.IngestionRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return run;
    }

    private async Task<IngestionRun> FinishRunAsync(IngestionRun run, string status, CancellationToken ct)
    {
        run.Status = status;
        run.FinishedAt = DateTimeOffset.UtcNow;
        await using var db = dbFactory();
        db.IngestionRuns.Update(run);
        await db.SaveChangesAsync(CancellationToken.None);
        return run;
    }
}
