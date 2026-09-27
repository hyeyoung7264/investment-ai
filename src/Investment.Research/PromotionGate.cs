using Investment.Domain.Research;

namespace Investment.Research;

public static class Stats
{
    /// <summary>Standard normal CDF (Abramowitz–Stegun 7.1.26 erf, |error| &lt; 1.5e-7).</summary>
    public static double NormalCdf(double x)
    {
        var z = Math.Abs(x) / Math.Sqrt(2);
        var t = 1 / (1 + 0.3275911 * z);
        var erf = 1 - ((((1.061405429 * t - 1.453152027) * t + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-z * z);
        return x >= 0 ? 0.5 * (1 + erf) : 0.5 * (1 - erf);
    }

    public static double NormalQuantile(double p)
    {
        if (p <= 0 || p >= 1) throw new ArgumentOutOfRangeException(nameof(p));
        double lo = -10, hi = 10;
        for (var i = 0; i < 100; i++)
        {
            var mid = (lo + hi) / 2;
            if (NormalCdf(mid) < p) lo = mid; else hi = mid;
        }
        return (lo + hi) / 2;
    }

    /// <summary>
    /// One-sided t threshold for "EV &gt; 0" with Bonferroni correction for the number of variants tried
    /// (1 → 1.645, 4 → 2.24, 10 → 2.58, 50 → 3.09 at alpha 0.05).
    /// </summary>
    public static double RequiredT(int variantsTried, double alpha = 0.05) =>
        NormalQuantile(1 - alpha / Math.Max(1, variantsTried));
}

/// <summary>Aggregated out-of-sample evidence of one walk-forward study (net of costs unless noted).</summary>
public sealed record OosEvidence
{
    public int Folds { get; init; }
    public int FoldsTraded { get; init; }
    public int FoldsPositive { get; init; }
    public int Trades { get; init; }
    public double NetEvPerTrade { get; init; }
    public double NetEvTStat { get; init; }
    public double GrossEvPerTrade { get; init; }
    public double NetSharpe { get; init; }
    public double NetMaxDrawdown { get; init; }
    public double NetAvgDailyReturn { get; init; }
    public double NetTotalReturn { get; init; }
    public double NetCagr { get; init; }
    public double RandomControlNetEvPerTrade { get; init; }
    public double BenchmarkReturn { get; init; }
    public int VariantsTried { get; init; }
    public DateOnly From { get; init; }
    public DateOnly To { get; init; }
    public IReadOnlyDictionary<string, RegimeStats> ByRegime { get; init; } = new Dictionary<string, RegimeStats>();
}

public sealed record RegimeStats(int Trades, double NetEvPerTrade, double NetEvTStat, int Sessions, double AvgDailyReturn);

public sealed record GateCriteria
{
    public int MinOosTrades { get; init; } = 100;
    public double MinOosSharpe { get; init; } = 0.5;
    public double MaxOosDrawdown { get; init; } = 0.30;
    public double MinFoldPositiveFraction { get; init; } = 0.5;
    public double Alpha { get; init; } = 0.05;
}

public sealed record GateResult(GateDecision Decision, StrategyStatus To, IReadOnlyList<string> Reasons);

/// <summary>
/// Evidence-based status transitions. Nothing is promoted on assertion; every rule is explicit and every
/// evaluation (including holds) is stored with its evidence and criteria.
/// </summary>
public static class PromotionGate
{
    /// <summary>
    /// A hypothesis derived from the same out-of-sample period it is tested on cannot be validated by that test:
    /// promotion is capped at HOLD; only forward (paper) evidence can confirm it. Rejections still apply.
    /// </summary>
    public static GateResult CapPostHoc(GateResult r, StrategyStatus current) =>
        r.Decision == GateDecision.Promote
            ? new(GateDecision.Hold, current == StrategyStatus.Experimental ? StrategyStatus.Backtested : current,
                [.. r.Reasons, "post-hoc hypothesis: OOS overlaps the data that suggested it — promotion requires forward evidence"])
            : r;

    /// <summary>BACKTESTED → VALIDATED / hold / REJECTED from walk-forward out-of-sample evidence.</summary>
    public static GateResult EvaluateValidation(StrategyStatus current, OosEvidence e, GateCriteria c)
    {
        if (current is StrategyStatus.Rejected or StrategyStatus.Disabled)
            return new(GateDecision.Hold, current, [$"status {current} is terminal for this version; create a new version"]);
        if (current is StrategyStatus.Paper or StrategyStatus.Approved)
        {
            // forward evidence outranks backtests: only the paper gate may move a version in the forward stage
            var would = EvaluateValidation(StrategyStatus.Backtested, e, c);
            return new(GateDecision.Hold, current,
                [$"version is in {current}; backtest evidence is recorded but does not change its status (would be {would.Decision}: {string.Join("; ", would.Reasons)})"]);
        }

        var reject = new List<string>();
        if (e.Trades > 0 && e.NetEvPerTrade <= 0) reject.Add($"OOS net EV/trade {e.NetEvPerTrade:P3} <= 0");
        if (e.NetMaxDrawdown > c.MaxOosDrawdown) reject.Add($"OOS MDD {e.NetMaxDrawdown:P1} > {c.MaxOosDrawdown:P0}");
        if (e.Trades > 0 && e.NetTotalReturn <= 0) reject.Add($"OOS net total return {e.NetTotalReturn:P1} <= 0");
        if (reject.Count > 0) return new(GateDecision.Reject, StrategyStatus.Rejected, reject);

        var hold = new List<string>();
        var requiredT = Stats.RequiredT(e.VariantsTried, c.Alpha);
        if (e.Trades < c.MinOosTrades) hold.Add($"OOS trades {e.Trades} < {c.MinOosTrades}");
        if (e.NetEvTStat < requiredT) hold.Add($"OOS EV t {e.NetEvTStat:F2} < required {requiredT:F2} ({e.VariantsTried} variants)");
        if (e.NetSharpe < c.MinOosSharpe) hold.Add($"OOS Sharpe {e.NetSharpe:F2} < {c.MinOosSharpe:F2}");
        if (e.Folds > 0 && (double)e.FoldsPositive / e.Folds < c.MinFoldPositiveFraction)
            hold.Add($"positive folds {e.FoldsPositive}/{e.Folds} < {c.MinFoldPositiveFraction:P0}");
        if (e.NetEvPerTrade <= e.RandomControlNetEvPerTrade) hold.Add("does not beat random-entry control");
        if (hold.Count > 0) return new(GateDecision.Hold, current == StrategyStatus.Experimental ? StrategyStatus.Backtested : current, hold);

        return new(GateDecision.Promote, StrategyStatus.Validated,
            [$"OOS net EV {e.NetEvPerTrade:P3}/trade (t={e.NetEvTStat:F2} >= {requiredT:F2}), Sharpe {e.NetSharpe:F2}, MDD {e.NetMaxDrawdown:P1}, {e.FoldsPositive}/{e.Folds} folds positive"]);
    }
}
