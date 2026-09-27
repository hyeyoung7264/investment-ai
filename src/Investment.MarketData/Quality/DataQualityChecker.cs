using Investment.Domain.Market;

namespace Investment.MarketData.Quality;

public enum QualityIssueKind
{
    OhlcInconsistent,
    NonPositivePrice,
    ReturnBeyondPriceLimit,
    SuspectedUnadjustedVolume,
    MissingSessions,
    BarsAfterDelisting,
    LongHalt,
}

public sealed record QualityIssue(string Ticker, QualityIssueKind Kind, DateOnly? Date, string Detail);

/// <summary>
/// Per-ticker data checks. Pure: takes bars + calendar, returns issues. Nothing is auto-"fixed";
/// issues are reported so decisions about exclusions are explicit.
/// </summary>
public static class DataQualityChecker
{
    /// <summary>KRX daily limit is ±30% since 2015-06-15 (±15% before). Liquidation trading has no limit.</summary>
    public static double PriceLimitOn(DateOnly d) => d >= new DateOnly(2015, 6, 15) ? 0.30 : 0.15;

    /// <summary>
    /// A close-to-close move beyond the daily price limit that is NOT explained by liquidation trading
    /// (정리매매, no price limit, just before delisting). These are unadjusted corporate actions, market
    /// transfers with base-price resets, or source errors — the jump is not a tradable return.
    /// </summary>
    public static bool IsDiscontinuity(Security s, Bar previous, Bar current)
    {
        if (previous.Close <= 0) return false;
        var r = current.Close / previous.Close - 1;
        if (Math.Abs(r) <= PriceLimitOn(current.Date) + 0.005) return false; // +0.5%p tick rounding tolerance
        return !(s.DelistedDate is { } dd && dd.DayNumber - current.Date.DayNumber <= LiquidationWindowDays);
    }

    /// <summary>Calendar days before delisting treated as the liquidation-trading window.</summary>
    public const int LiquidationWindowDays = 20;

    public static IReadOnlyList<QualityIssue> Check(Security s, IReadOnlyList<Bar> bars, IReadOnlyList<DateOnly> calendar)
    {
        var issues = new List<QualityIssue>();
        if (bars.Count == 0) return issues;

        for (var i = 0; i < bars.Count; i++)
        {
            var b = bars[i];
            if (b.Close <= 0 || b.Open <= 0 || b.Low <= 0 || b.High <= 0)
                issues.Add(new(s.Ticker, QualityIssueKind.NonPositivePrice, b.Date, $"O={b.Open} H={b.High} L={b.Low} C={b.Close}"));
            // adjusted prices carry rounding (e.g. C=23545 > H=23544); only >0.5% inconsistencies are defects
            else if (!b.IsHalted && (b.Low > Math.Min(b.Open, b.Close) * 1.005 || b.High < Math.Max(b.Open, b.Close) * 0.995))
                issues.Add(new(s.Ticker, QualityIssueKind.OhlcInconsistent, b.Date, $"O={b.Open} H={b.High} L={b.Low} C={b.Close}"));

            if (i > 0 && IsDiscontinuity(s, bars[i - 1], b))
                issues.Add(new(s.Ticker, QualityIssueKind.ReturnBeyondPriceLimit, b.Date,
                    $"ret={b.Close / bars[i - 1].Close - 1:P1} prev={bars[i - 1].Date:yyyy-MM-dd}{(bars[i - 1].IsHalted ? " after-halt" : "")}"));
        }

        CheckVolumeRegimeJumps(s.Ticker, bars, issues);
        CheckCalendar(s, bars, calendar, issues);
        return issues;
    }

    /// <summary>
    /// Splits/reverse splits halt trading and change share count; the source adjusts prices but not volume,
    /// so a persistent volume regime shift right after a halt is the signature. Heuristic flag, not ground truth.
    /// </summary>
    private static void CheckVolumeRegimeJumps(string ticker, IReadOnlyList<Bar> bars, List<QualityIssue> issues)
    {
        const int w = 40;
        if (bars.Count < 2 * w) return;
        for (var i = w; i + w <= bars.Count; i++)
        {
            // splits / reverse splits in Korea go through a trading halt; test only the first session after one
            if (!bars[i - 1].IsHalted || bars[i].IsHalted) continue;
            var before = MedianVolume(bars, i - w, i, skipHalted: true);
            var after = MedianVolume(bars, i, i + w, skipHalted: true);
            if (before <= 0 || after <= 0) continue;
            var ratio = after / before;
            if (ratio >= 2.5 || ratio <= 0.4)
                issues.Add(new(ticker, QualityIssueKind.SuspectedUnadjustedVolume, bars[i].Date, $"median volume x{ratio:F2} after halt"));
        }
    }

    private static void CheckCalendar(Security s, IReadOnlyList<Bar> bars, IReadOnlyList<DateOnly> calendar, List<QualityIssue> issues)
    {
        if (calendar.Count == 0) return;
        var first = bars[0].Date;
        var last = bars[^1].Date;
        var have = new HashSet<DateOnly>(bars.Select(b => b.Date));
        var missing = calendar.Where(d => d > first && d < last && !have.Contains(d)).ToList();
        if (missing.Count > 0)
            issues.Add(new(s.Ticker, QualityIssueKind.MissingSessions, missing[0], $"{missing.Count} sessions missing between first and last bar"));

        if (s.DelistedDate is { } dd && last > dd)
            issues.Add(new(s.Ticker, QualityIssueKind.BarsAfterDelisting, last, $"last bar {last:yyyy-MM-dd} after delisting {dd:yyyy-MM-dd}"));

        var run = 0;
        DateOnly? runStart = null;
        foreach (var b in bars)
        {
            if (b.IsHalted) { run++; runStart ??= b.Date; }
            else
            {
                if (run >= 20) issues.Add(new(s.Ticker, QualityIssueKind.LongHalt, runStart, $"{run} consecutive halted sessions"));
                run = 0; runStart = null;
            }
        }
        if (run >= 20) issues.Add(new(s.Ticker, QualityIssueKind.LongHalt, runStart, $"{run} consecutive halted sessions (to end)"));
    }

    private static double MedianVolume(IReadOnlyList<Bar> bars, int from, int to, bool skipHalted) =>
        Median(Enumerable.Range(from, to - from).Where(i => !skipHalted || !bars[i].IsHalted).Select(i => (double)bars[i].Volume));

    private static double Median(IEnumerable<double> xs)
    {
        var a = xs.OrderBy(x => x).ToArray();
        return a.Length == 0 ? 0 : a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
    }
}
