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
}
