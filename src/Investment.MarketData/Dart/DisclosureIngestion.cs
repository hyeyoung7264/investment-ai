using Investment.Domain.Market;
using Investment.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Investment.MarketData.Dart;

public sealed class DisclosureIngestion(Func<InvestmentDbContext> dbFactory, DartClient client, Action<string>? log = null)
{
    private readonly Action<string> _log = log ?? (_ => { });

    /// <summary>Re-applies the current classifier to stored rows (report names are the source of truth).</summary>
    public async Task<int> ReclassifyAsync(CancellationToken ct)
    {
        await using var db = dbFactory();
        var changed = 0;
        foreach (var d in await db.Disclosures.ToListAsync(ct))
        {
            var (ev, corr) = DisclosureClassifier.Classify(d.ReportName);
            if (d.Event == ev && d.IsCorrection == corr) continue;
            d.Event = ev;
            d.IsCorrection = corr;
            changed++;
        }
        await db.SaveChangesAsync(ct);
        return changed;
    }

    /// <summary>
    /// Idempotent: existing receipt numbers are skipped, so an interrupted run can simply be repeated.
    /// Windows are fetched a few at a time (the API is slow per page); a rate-limit answer stops new windows.
    /// </summary>
    public async Task<(int Fetched, int Inserted)> RunAsync(DateOnly from, DateOnly to, IReadOnlyList<string> types, CancellationToken ct, int parallel = 4)
    {
        var windows = new List<(string Type, DateOnly Start, DateOnly End)>();
        foreach (var type in types)
            for (var start = from; start <= to; start = start.AddMonths(3))
            {
                var end = start.AddMonths(3).AddDays(-1);
                windows.Add((type, start, end > to ? to : end));
            }

        int fetched = 0, inserted = 0;
        var limited = false;
        var gate = new SemaphoreSlim(1);
        await Parallel.ForEachAsync(windows, new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = ct }, async (w, token) =>
        {
            if (Volatile.Read(ref limited)) return;
            IReadOnlyList<Disclosure> items;
            try { items = await client.SearchAsync(w.Start, w.End, w.Type, token); }
            catch (DartException e) when (e.Status is "020" or "021")
            {
                Volatile.Write(ref limited, true);
                _log($"DART limit reached at {w.Type} {w.Start:yyyy-MM}: {e.Message}; rerun later to resume");
                return;
            }
            await gate.WaitAsync(token);
            try
            {
                await using var db = dbFactory();
                var ids = items.Select(i => i.ReceiptNo).ToList();
                var existing = (await db.Disclosures.Where(d => ids.Contains(d.ReceiptNo)).Select(d => d.ReceiptNo).ToListAsync(token)).ToHashSet();
                var fresh = items.Where(i => !existing.Contains(i.ReceiptNo)).GroupBy(i => i.ReceiptNo).Select(g => g.First()).ToList();
                db.Disclosures.AddRange(fresh);
                await db.SaveChangesAsync(token);
                fetched += items.Count;
                inserted += fresh.Count;
                _log($"disclosures {w.Type} {w.Start:yyyy-MM-dd}..{w.End:yyyy-MM-dd}: {items.Count} fetched, {fresh.Count} new");
            }
            finally { gate.Release(); }
        });
        return (fetched, inserted);
    }
}
