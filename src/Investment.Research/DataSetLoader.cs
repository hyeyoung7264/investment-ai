using Investment.Domain.Market;
using Investment.MarketData.Splits;
using Investment.MarketData.Universe;
using Investment.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Investment.Research;

/// <summary>
/// Builds a <see cref="MarketDataSet"/> from the database in two streaming passes:
/// (1) every security's bars → point-in-time universe, (2) full bars only for tickers that were ever members.
/// </summary>
public sealed class DataSetLoader(string connectionString)
{
    /// <summary>Calendar days of history loaded before the start date for indicator warmup.</summary>
    public const int WarmupCalendarDays = 450;

    public async Task<MarketDataSet> LoadAsync(UniverseDefinition universe, DateOnly start, DateOnly end, string indexCode = "KOSPI",
        bool includeEvents = false, CancellationToken ct = default)
    {
        var store = new MarketDataStore(connectionString);
        var loadFrom = start.AddDays(-WarmupCalendarDays);
        var index = await store.LoadIndexAsync(indexCode, loadFrom, end, ct);
        if (index.Count == 0) throw new InvalidOperationException($"no {indexCode} index data; run `ingest indices` first");
        var calendar = index.Select(b => b.Date).ToList();

        Dictionary<string, Security> securities;
        Dictionary<string, List<SplitEvent>> splits;
        await using (var db = Database.Create(connectionString))
        {
            securities = await db.Securities.AsNoTracking().ToDictionaryAsync(s => s.Ticker, ct);
            splits = (await db.SplitEvents.AsNoTracking().ToListAsync(ct)).GroupBy(e => e.Ticker).ToDictionary(g => g.Key, g => g.ToList());
        }
        // source leaves volume unadjusted for some splits; correct it so adjusted close × volume ≈ raw trading value
        List<Bar> Corrected(string ticker, List<Bar> bars) =>
            splits.TryGetValue(ticker, out var ev) ? SplitVerifier.ApplyVolumeCorrections(bars, ev) : bars;

        var builder = new UniverseBuilder(universe, calendar, start, end);
        var candidates = securities.Values.Where(builder.IsCandidateSecurity).Select(s => s.Ticker).ToList();
        await foreach (var (ticker, bars) in store.StreamBarsByTickerAsync(loadFrom, end, candidates, ct))
            if (securities.TryGetValue(ticker, out var sec))
                builder.Add(sec, Corrected(ticker, bars));
        var pit = builder.Build();

        var members = pit.AllMembers();
        var memberBars = new Dictionary<string, Bar[]>(StringComparer.Ordinal);
        await foreach (var (ticker, bars) in store.StreamBarsByTickerAsync(loadFrom, end, members, ct))
            memberBars[ticker] = Corrected(ticker, bars).ToArray();

        Dictionary<string, CorporateEvent[]>? events = null;
        if (includeEvents)
        {
            await using var db = Database.Create(connectionString);
            var tickers = members.ToList();
            // original filings only: corrections restate an event already known on its first receipt date
            var rows = await db.Disclosures.AsNoTracking()
                .Where(d => d.Ticker != null && tickers.Contains(d.Ticker) && !d.IsCorrection && d.Event != DisclosureEvent.Other
                            && d.ReceiptDate >= loadFrom && d.ReceiptDate <= end)
                .Select(d => new { d.Ticker, d.ReceiptDate, d.Event, d.ReportName, d.ReceiptNo })
                .ToListAsync(ct);
            var all = rows.Select(r => (r.Ticker!, r.ReceiptNo, new CorporateEvent(r.ReceiptDate, r.Event, r.ReportName))).ToList();

            // earnings reports with point-in-time SUE (only timely filings: amendments years later are not news)
            var lines = await db.FinancialReportLines.AsNoTracking().Where(l => l.Ticker != null && tickers.Contains(l.Ticker)).ToListAsync(ct);
            foreach (var (q, sue) in Investment.MarketData.Dart.EarningsCalculator.Surprises(Investment.MarketData.Dart.EarningsCalculator.Quarters(lines)))
            {
                if (q.ReceiptDate < loadFrom || q.ReceiptDate > end || !Investment.MarketData.Dart.EarningsCalculator.IsTimely(q)) continue;
                all.Add((q.Ticker, $"FIN{q.FiscalYear}Q{q.Quarter}", new CorporateEvent(q.ReceiptDate, DisclosureEvent.EarningsReport,
                    $"{q.FiscalYear}Q{q.Quarter} OI {q.OperatingIncome:N0} vs {q.PriorOperatingIncome:N0} ({q.FsDiv})", sue)));
            }
            events = all.GroupBy(r => r.Item1).ToDictionary(g => g.Key,
                g => g.OrderBy(r => r.Item3.Date).ThenBy(r => r.Item2, StringComparer.Ordinal).Select(r => r.Item3).ToArray());
        }

        return new MarketDataSet(calendar, memberBars,
            securities.Where(kv => members.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value),
            index.ToArray(), indexCode, pit, includeEvents ? "naver-chart+kind+dart" : "naver-chart+kind", events);
    }
}
