namespace Investment.Risk;

/// <summary>
/// Portfolio risk rules. The Risk Engine has the final say over strategy signals; the same limits are
/// applied in backtests and paper trading.
/// </summary>
public sealed record RiskLimits
{
    public int MaxPositions { get; init; } = 10;

    /// <summary>Max weight of one position at entry (fraction of equity).</summary>
    public double MaxPositionWeight { get; init; } = 0.10;

    /// <summary>Max sum of position values / equity. 1.0 = fully invested, no leverage.</summary>
    public double MaxGrossExposure { get; init; } = 1.0;

    /// <summary>Max weight of one sector (KIND industry) at entry.</summary>
    public double MaxSectorWeight { get; init; } = 0.30;

    /// <summary>A session loss at or beyond this blocks new entries for the next session.</summary>
    public double MaxDailyLoss { get; init; } = 0.03;

    /// <summary>Drawdown from peak at or beyond this liquidates everything and halts the strategy.</summary>
    public double MaxDrawdown { get; init; } = 0.25;

    /// <summary>Per-position stop loss from entry fill price (null = none).</summary>
    public double? StopLoss { get; init; } = 0.10;

    public double? TakeProfit { get; init; }

    /// <summary>Forced exit after this many sessions held (null = none).</summary>
    public int? MaxHoldingSessions { get; init; }

    /// <summary>Order value may not exceed this fraction of the 20-session median trading value.</summary>
    public double MaxParticipation { get; init; } = 0.05;
}

public enum RiskState
{
    Normal,
    EntriesBlocked,
    Halted,
}

public sealed record RiskAssessment(RiskState State, string? Reason);

public sealed record PositionExposure(string Ticker, double MarketValue, string? Sector);

public sealed record PortfolioSnapshot(
    double Equity,
    double Cash,
    double PeakEquity,
    double SessionReturn,
    IReadOnlyList<PositionExposure> Positions);

public sealed record EntryCandidate(string Ticker, double Score, double ReferencePrice, double MedianTradingValue, string? Sector, string Reason);

public sealed record ApprovedEntry(string Ticker, long Quantity, double ReferencePrice, double Score, string Reason);

public sealed record RejectedEntry(string Ticker, string Rule);

public sealed record AllocationResult(IReadOnlyList<ApprovedEntry> Approved, IReadOnlyList<RejectedEntry> Rejected);

public sealed class RiskEngine(RiskLimits limits)
{
    public RiskLimits Limits { get; } = limits;

    public RiskAssessment Assess(PortfolioSnapshot p)
    {
        var drawdown = p.PeakEquity > 0 ? 1 - p.Equity / p.PeakEquity : 0;
        if (drawdown >= Limits.MaxDrawdown)
            return new(RiskState.Halted, $"drawdown {drawdown:P1} >= limit {Limits.MaxDrawdown:P0}");
        if (p.SessionReturn <= -Limits.MaxDailyLoss)
            return new(RiskState.EntriesBlocked, $"session loss {p.SessionReturn:P2} <= -{Limits.MaxDailyLoss:P0}");
        return new(RiskState.Normal, null);
    }

    /// <summary>
    /// Sizes new entries in score order under position-count, weight, exposure, sector, cash and
    /// liquidity limits. <paramref name="exiting"/> positions are assumed sold at the next open.
    /// </summary>
    public AllocationResult Allocate(PortfolioSnapshot p, IEnumerable<EntryCandidate> candidates, IReadOnlySet<string> exiting, double costBuffer)
    {
        var approved = new List<ApprovedEntry>();
        var rejected = new List<RejectedEntry>();
        var remaining = p.Positions.Where(x => !exiting.Contains(x.Ticker)).ToList();
        var held = remaining.Select(x => x.Ticker).ToHashSet(StringComparer.Ordinal);
        var exposure = remaining.Sum(x => x.MarketValue);
        var sectorValue = remaining.GroupBy(x => x.Sector ?? "").ToDictionary(g => g.Key, g => g.Sum(x => x.MarketValue));
        // cash after planned exits (valued at last close; exit costs ignored here, re-checked at fill)
        var cash = p.Cash + p.Positions.Where(x => exiting.Contains(x.Ticker)).Sum(x => x.MarketValue);
        var slots = Limits.MaxPositions - remaining.Count;

        foreach (var c in candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Ticker, StringComparer.Ordinal))
        {
            if (held.Contains(c.Ticker)) continue;
            if (slots <= 0) { rejected.Add(new(c.Ticker, "max-positions")); continue; }
            if (c.ReferencePrice <= 0) { rejected.Add(new(c.Ticker, "no-price")); continue; }

            var target = p.Equity * Math.Min(Limits.MaxPositionWeight, 1.0 / Limits.MaxPositions);
            target = Math.Min(target, Limits.MaxGrossExposure * p.Equity - exposure);
            var sectorKey = c.Sector ?? "";
            target = Math.Min(target, Limits.MaxSectorWeight * p.Equity - sectorValue.GetValueOrDefault(sectorKey));
            target = Math.Min(target, Limits.MaxParticipation * c.MedianTradingValue);
            target = Math.Min(target, cash / (1 + costBuffer));

            var qty = (long)Math.Floor(target / (c.ReferencePrice * (1 + costBuffer)));
            if (qty <= 0)
            {
                rejected.Add(new(c.Ticker, RejectionRule(p, exposure, sectorValue.GetValueOrDefault(sectorKey), cash, c)));
                continue;
            }
            var value = qty * c.ReferencePrice;
            approved.Add(new(c.Ticker, qty, c.ReferencePrice, c.Score, c.Reason));
            held.Add(c.Ticker);
            exposure += value;
            sectorValue[sectorKey] = sectorValue.GetValueOrDefault(sectorKey) + value;
            cash -= value * (1 + costBuffer);
            slots--;
        }
        return new(approved, rejected);
    }

    private string RejectionRule(PortfolioSnapshot p, double exposure, double sector, double cash, EntryCandidate c)
    {
        if (Limits.MaxGrossExposure * p.Equity - exposure < c.ReferencePrice) return "max-exposure";
        if (Limits.MaxSectorWeight * p.Equity - sector < c.ReferencePrice) return "max-sector";
        if (Limits.MaxParticipation * c.MedianTradingValue < c.ReferencePrice) return "liquidity";
        if (cash < c.ReferencePrice) return "cash";
        return "size";
    }
}
