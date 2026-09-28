using Investment.Domain.Market;
using Investment.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Investment.MarketData.Dart;

public sealed class FinancialsIngestion(Func<InvestmentDbContext> dbFactory, DartClient client, Action<string>? log = null)
{
    private readonly Action<string> _log = log ?? (_ => { });
    public static readonly string[] ReportCodes = ["11013", "11012", "11014", "11011"];

    /// <summary>
    /// Key accounts for every KOSPI/KOSDAQ common share (incl. delisted) for the given fiscal years. Idempotent
    /// upsert per (corp, year, report, fs, account) — a later amendment replaces the line with its own receipt date.
    /// </summary>
    public async Task<(int Calls, int Lines)> RunAsync(int fromYear, int toYear, CancellationToken ct, int parallel = 4)
    {
        List<string> tickers;
        await using (var db = dbFactory())
            tickers = await db.Securities.Where(s => s.Kind == SecurityKind.Common && (s.Market == MarketType.Kospi || s.Market == MarketType.Kosdaq))
                .Select(s => s.Ticker).ToListAsync(ct);
        var corpByTicker = await client.GetCorpCodesByTickerAsync(ct);
        var corps = tickers.Where(corpByTicker.ContainsKey).Select(t => corpByTicker[t]).Distinct().Order().ToList();
        _log($"financials: {corps.Count}/{tickers.Count} securities mapped to DART corp codes");

        var jobs = new List<(int Year, string Code, List<string> Batch)>();
        for (var y = fromYear; y <= toYear; y++)
            foreach (var code in ReportCodes)
                for (var i = 0; i < corps.Count; i += 100)
                    jobs.Add((y, code, corps.Skip(i).Take(100).ToList()));

        int calls = 0, lines = 0;
        var limited = false;
        var gate = new SemaphoreSlim(1);
        await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = ct }, async (job, token) =>
        {
            if (Volatile.Read(ref limited)) return;
            IReadOnlyList<FinancialReportLine> rows;
            try { rows = await client.GetKeyAccountsAsync(job.Batch, job.Year, job.Code, token); }
            catch (DartException e) when (e.Status is "020" or "021")
            {
                Volatile.Write(ref limited, true);
                _log($"DART limit reached ({e.Message}); rerun later to resume");
                return;
            }
            await gate.WaitAsync(token);
            try
            {
                await using var db = dbFactory();
                // the API can return periods other than the requested one (non-December fiscal years): key on the full id
                var unique = rows.GroupBy(r => (r.CorpCode, r.FiscalYear, r.ReportCode, r.FsDiv, r.Account)).Select(g => g.First()).ToList();
                var corpsInRows = unique.Select(r => r.CorpCode).Distinct().ToList();
                var years = unique.Select(r => r.FiscalYear).Distinct().ToList();
                var existing = await db.FinancialReportLines
                    .Where(l => corpsInRows.Contains(l.CorpCode) && years.Contains(l.FiscalYear)).ToListAsync(token);
                var map = existing.ToDictionary(l => (l.CorpCode, l.FiscalYear, l.ReportCode, l.FsDiv, l.Account));
                foreach (var r in unique)
                {
                    if (map.TryGetValue((r.CorpCode, r.FiscalYear, r.ReportCode, r.FsDiv, r.Account), out var old))
                    {
                        if (old.ReceiptNo == r.ReceiptNo) continue;
                        // amendment: update in place
                        old.Ticker = r.Ticker; old.ThisAmount = r.ThisAmount; old.ThisCumulative = r.ThisCumulative;
                        old.PriorAmount = r.PriorAmount; old.PriorCumulative = r.PriorCumulative;
                        old.ReceiptNo = r.ReceiptNo; old.ReceiptDate = r.ReceiptDate;
                    }
                    else db.FinancialReportLines.Add(r);
                }
                await db.SaveChangesAsync(token);
                calls++;
                lines += rows.Count;
                if (calls % 50 == 0) _log($"financials: {calls}/{jobs.Count} calls, {lines} lines");
            }
            finally { gate.Release(); }
            await Task.Delay(100, token);
        });
        _log($"financials: done {calls}/{jobs.Count} calls, {lines} lines");
        return (calls, lines);
    }
}
