using Investment.Domain.Market;

namespace Investment.MarketData.Universe;

public sealed record UniverseDefinition
{
    public IReadOnlyList<MarketType> Markets { get; init; } = [MarketType.Kospi, MarketType.Kosdaq];
    public IReadOnlyList<SecurityKind> Kinds { get; init; } = [SecurityKind.Common];

    /// <summary>Number of most liquid names kept at each reconstitution.</summary>
    public int TopN { get; init; } = 100;

    /// <summary>Sessions used for the median trading value.</summary>
    public int LiquidityLookback { get; init; } = 60;

    /// <summary>Minimum sessions of history at selection (avoids fresh IPOs with no warmup).</summary>
    public int MinHistoryBars { get; init; } = 120;

    /// <summary>Minimum close at selection (KRW). Excludes penny stocks with huge relative tick size.</summary>
    public double MinPrice { get; init; } = 1000;

    /// <summary>
    /// "naver" (adjusted chart API; mixes Nextrade prices since 2025-03 on ~2% of days) or "krx" (official exchange
    /// records, adjusted with KRX base prices). Default "naver" keeps earlier runs reproducible.
    /// </summary>
    public string PriceSource { get; init; } = "naver";

    /// <summary>Optional explicit ticker list; when set, only these are candidates (still point-in-time filtered).</summary>
    public IReadOnlyList<string>? Tickers { get; init; }
}

/// <summary>
/// Builds a monthly point-in-time universe from all securities, including delisted ones.
/// Selection on the last session of month m-1 using only bars dated ≤ that session; effective from the
/// first session of month m. Tickers are fed one at a time so the full market never has to be in memory.
/// </summary>
public sealed class UniverseBuilder
{

    private readonly UniverseDefinition _def;
    private readonly IReadOnlyList<(DateOnly Selection, DateOnly Effective)> _schedule;
    private readonly List<(double Score, string Ticker)>[] _candidates;

    public UniverseBuilder(UniverseDefinition definition, IReadOnlyList<DateOnly> tradingCalendar, DateOnly from, DateOnly to)
    {
        _def = definition;
        _schedule = MonthlySchedule(tradingCalendar, from, to);
        _candidates = _schedule.Select(_ => new List<(double, string)>()).ToArray();
    }

    public IReadOnlyList<(DateOnly Selection, DateOnly Effective)> Schedule => _schedule;

    public static IReadOnlyList<(DateOnly Selection, DateOnly Effective)> MonthlySchedule(IReadOnlyList<DateOnly> calendar, DateOnly from, DateOnly to)
    {
        var result = new List<(DateOnly, DateOnly)>();
        for (var i = 1; i < calendar.Count; i++)
        {
            var d = calendar[i];
            var prev = calendar[i - 1];
            var isFirstSessionOfMonth = d.Month != prev.Month || d.Year != prev.Year;
            if (!isFirstSessionOfMonth || d > to) continue;
            // include the reconstitution in force at `from`
            if (d <= from && result.Count > 0) result.Clear();
            result.Add((prev, d));
        }
        return result;
    }

    public bool IsCandidateSecurity(Security s) =>
        _def.Markets.Contains(s.Market) && _def.Kinds.Contains(s.Kind) &&
        (_def.Tickers is null || _def.Tickers.Contains(s.Ticker));

    /// <summary>Feed one security's full bar history (ascending by date).</summary>
    public void Add(Security security, IReadOnlyList<Bar> bars)
    {
        if (!IsCandidateSecurity(security) || bars.Count == 0) return;
        var values = new double[_def.LiquidityLookback];
        for (var k = 0; k < _schedule.Count; k++)
        {
            var sel = _schedule[k].Selection;
            var idx = LastIndexOnOrBefore(bars, sel);
            if (idx < 0) continue;
            var last = bars[idx];
            // must have traded on the selection session itself (excludes delisted, halted, stale)
            if (last.Date != sel || last.IsHalted) continue;
            if (idx + 1 < _def.MinHistoryBars) continue;
            if (last.Close < _def.MinPrice) continue;
            // Only the final liquidation-trading window (정리매매, ~7 sessions) is public knowledge before the
            // delisting date, so exclusion is limited to that window to avoid using future delisting info.
            if (security.DelistedDate is { } dd && dd.DayNumber - sel.DayNumber <= KrxRules.KnownDelistingWindowDays) continue;

            var n = Math.Min(_def.LiquidityLookback, idx + 1);
            for (var j = 0; j < n; j++)
            {
                var b = bars[idx - j];
                values[j] = b.IsHalted ? 0 : b.TradingValue;
            }
            var median = Median(values.AsSpan(0, n));
            _candidates[k].Add((median, security.Ticker));
        }
    }

    public PointInTimeUniverse Build()
    {
        var snaps = new List<UniverseSnapshot>(_schedule.Count);
        for (var k = 0; k < _schedule.Count; k++)
        {
            var members = _candidates[k]
                .OrderByDescending(c => c.Score)
                .ThenBy(c => c.Ticker, StringComparer.Ordinal) // deterministic tie-break
                .Take(_def.TopN)
                .Select(c => c.Ticker)
                .ToList();
            snaps.Add(new UniverseSnapshot(_schedule[k].Selection, _schedule[k].Effective, members));
        }
        return new PointInTimeUniverse(snaps);
    }

    private static int LastIndexOnOrBefore(IReadOnlyList<Bar> bars, DateOnly date)
    {
        int lo = 0, hi = bars.Count - 1, ans = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (bars[mid].Date <= date) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return ans;
    }

    private static double Median(Span<double> xs)
    {
        xs.Sort();
        var n = xs.Length;
        return n % 2 == 1 ? xs[n / 2] : (xs[n / 2 - 1] + xs[n / 2]) / 2.0;
    }
}
