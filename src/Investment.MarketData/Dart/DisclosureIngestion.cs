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

    /// <summary>Idempotent: existing receipt numbers are skipped, so an interrupted run can simply be repeated.</summary>
    public async Task<(int Fetched, int Inserted)> RunAsync(DateOnly from, DateOnly to, IReadOnlyList<string> types, CancellationToken ct)
    {
        int fetched = 0, inserted = 0;
        foreach (var type in types)
        {
            for (var start = from; start <= to; start = start.AddMonths(3))
            {
                var end = start.AddMonths(3).AddDays(-1);
                if (end > to) end = to;
                IReadOnlyList<Disclosure> items;
                try { items = await client.SearchAsync(start, end, type, ct); }
                catch (DartException e) when (e.Status is "020" or "021")
                {
                    _log($"DART limit reached at {type} {start:yyyy-MM}: {e.Message}; rerun later to resume");
                    return (fetched, inserted);
                }
                fetched += items.Count;
                await using var db = dbFactory();
                var ids = items.Select(i => i.ReceiptNo).ToList();
                var existing = (await db.Disclosures.Where(d => ids.Contains(d.ReceiptNo)).Select(d => d.ReceiptNo).ToListAsync(ct)).ToHashSet();
                var fresh = items.Where(i => !existing.Contains(i.ReceiptNo)).GroupBy(i => i.ReceiptNo).Select(g => g.First()).ToList();
                db.Disclosures.AddRange(fresh);
                await db.SaveChangesAsync(ct);
                inserted += fresh.Count;
                _log($"disclosures {type} {start:yyyy-MM-dd}..{end:yyyy-MM-dd}: {items.Count} fetched, {fresh.Count} new");
            }
        }
        return (fetched, inserted);
    }
}
