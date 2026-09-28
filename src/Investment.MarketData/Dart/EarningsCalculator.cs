using Investment.Domain.Market;

namespace Investment.MarketData.Dart;

/// <summary>
/// Turns filed key-account lines into point-in-time quarters and standardized unexpected earnings (SUE).
/// Pure functions — every number is computed from filings dated on or before the event date.
/// </summary>
public static class EarningsCalculator
{
    private static readonly Dictionary<string, int> QuarterOf = new() { ["11013"] = 1, ["11012"] = 2, ["11014"] = 3, ["11011"] = 4 };

    /// <summary>
    /// One quarter per (ticker, year, quarter): consolidated figures when filed, else separate. Q1–Q3 use the
    /// 3-month amounts; Q4 = annual − nine-month cumulative of the Q3 report (both filed by the Q4 date).
    /// </summary>
    public static IReadOnlyList<QuarterResult> Quarters(IEnumerable<FinancialReportLine> lines)
    {
        var result = new List<QuarterResult>();
        foreach (var company in lines.Where(l => l.Ticker is not null).GroupBy(l => l.Ticker!))
        {
            var byReport = company.GroupBy(l => (l.FiscalYear, l.ReportCode)).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var ((year, code), rows) in byReport)
            {
                if (!QuarterOf.TryGetValue(code, out var q)) continue;
                var fs = rows.Any(r => r.FsDiv == "CFS") ? "CFS" : "OFS";
                FinancialReportLine? Line(IEnumerable<FinancialReportLine> src, string acc) => src.FirstOrDefault(r => r.FsDiv == fs && r.Account == acc);
                var oi = Line(rows, "OperatingIncome");
                if (oi is null) continue;
                decimal? cur = oi.ThisAmount, prior = oi.PriorAmount;
                decimal? rev = Line(rows, "Revenue")?.ThisAmount, ni = Line(rows, "NetIncome")?.ThisAmount;
                if (q == 4)
                {
                    // annual figures minus the nine-month cumulative of the same year's Q3 report
                    if (!byReport.TryGetValue((year, "11014"), out var q3rows)) continue;
                    var q3oi = Line(q3rows, "OperatingIncome");
                    if (q3oi?.ThisCumulative is null) continue;
                    cur = oi.ThisAmount - q3oi.ThisCumulative;
                    prior = oi.PriorAmount is { } pa && q3oi.PriorCumulative is { } pc ? pa - pc : null;
                    var q3rev = Line(q3rows, "Revenue")?.ThisCumulative;
                    rev = rev is { } r && q3rev is { } r3 ? r - r3 : null;
                    var q3ni = Line(q3rows, "NetIncome")?.ThisCumulative;
                    ni = ni is { } n && q3ni is { } n3 ? n - n3 : null;
                }
                result.Add(new QuarterResult(company.Key, year, q, oi.ReceiptDate, fs, rev, cur, prior, ni));
            }
        }
        return result.OrderBy(r => r.Ticker, StringComparer.Ordinal).ThenBy(r => r.FiscalYear).ThenBy(r => r.Quarter).ToList();
    }

    /// <summary>
    /// SUE of each quarter = (OI − same quarter last year) / stdev of that change over the previous (up to 8) quarters
    /// of the same company that were filed before this quarter's receipt date. Needs at least 4 prior changes.
    /// </summary>
    public static IReadOnlyList<(QuarterResult Quarter, double Sue)> Surprises(IReadOnlyList<QuarterResult> quarters, int window = 8, int minHistory = 4)
    {
        var result = new List<(QuarterResult, double)>();
        foreach (var company in quarters.GroupBy(q => q.Ticker))
        {
            var ordered = company.Where(q => q.OperatingIncome is not null && q.PriorOperatingIncome is not null)
                .OrderBy(q => q.FiscalYear).ThenBy(q => q.Quarter).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var cur = ordered[i];
                var history = ordered.Take(i).Where(p => p.ReceiptDate < cur.ReceiptDate).TakeLast(window)
                    .Select(p => (double)(p.OperatingIncome!.Value - p.PriorOperatingIncome!.Value)).ToList();
                if (history.Count < minHistory) continue;
                var mean = history.Average();
                var sd = Math.Sqrt(history.Sum(x => (x - mean) * (x - mean)) / (history.Count - 1));
                if (sd <= 0) continue;
                result.Add((cur, (double)(cur.OperatingIncome!.Value - cur.PriorOperatingIncome!.Value) / sd));
            }
        }
        return result;
    }

    /// <summary>
    /// Filed within the regular deadline (quarterly 45 days, annual 90 days, plus slack). Later receipts are
    /// amendments of old periods — stale information, not an earnings announcement.
    /// </summary>
    public static bool IsTimely(QuarterResult q)
    {
        var periodEnd = new DateOnly(q.FiscalYear, q.Quarter * 3, 1).AddMonths(1).AddDays(-1);
        var days = q.ReceiptDate.DayNumber - periodEnd.DayNumber;
        return days >= 0 && days <= (q.Quarter == 4 ? 120 : 100);
    }
}
