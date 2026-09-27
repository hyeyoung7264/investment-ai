namespace Investment.Backtest;

public sealed record TaxPeriod(DateOnly From, double Rate);

/// <summary>
/// Korean equity trading costs. Sell tax = securities transaction tax + 농어촌특별세 (KOSPI), combined rate
/// by effective date. Slippage = base + impact × participation (order value / median trading value).
/// </summary>
public sealed record CostModel
{
    public double CommissionRate { get; init; } = 0.00015;
    public double BaseSlippage { get; init; } = 0.001;
    public double ImpactCoefficient { get; init; } = 0.1;
    public IReadOnlyList<TaxPeriod> SellTax { get; init; } = KoreaSellTax;

    public static readonly IReadOnlyList<TaxPeriod> KoreaSellTax =
    [
        new(DateOnly.MinValue, 0.0030),
        new(new DateOnly(2019, 6, 3), 0.0025),
        new(new DateOnly(2021, 1, 1), 0.0023),
        new(new DateOnly(2023, 1, 1), 0.0020),
        new(new DateOnly(2024, 1, 1), 0.0018),
        new(new DateOnly(2025, 1, 1), 0.0015),
        new(new DateOnly(2026, 1, 1), 0.0020),
    ];

    public static CostModel Zero => new() { CommissionRate = 0, BaseSlippage = 0, ImpactCoefficient = 0, SellTax = [new(DateOnly.MinValue, 0)] };

    public double SellTaxRate(DateOnly date)
    {
        var rate = 0.0;
        foreach (var p in SellTax)
            if (p.From <= date) rate = p.Rate;
        return rate;
    }

    public double Slippage(double orderValue, double medianTradingValue)
    {
        var participation = medianTradingValue > 0 ? orderValue / medianTradingValue : 1.0;
        return BaseSlippage + ImpactCoefficient * participation;
    }

    /// <summary>Upper bound of round-trip cost as a fraction, used for sizing buffers.</summary>
    public double EntryBuffer => CommissionRate + BaseSlippage * 2;
}
