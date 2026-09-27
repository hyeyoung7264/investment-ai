using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Investment.Domain.Strategies;
using Investment.Risk;

namespace Investment.Backtest;

public sealed record BacktestConfig
{
    public required DateOnly Start { get; init; }
    public required DateOnly End { get; init; }
    public decimal InitialCapital { get; init; } = 100_000_000m;
    public CostModel Costs { get; init; } = new();
    public RiskLimits Risk { get; init; } = new();

    /// <summary>Sessions after a data discontinuity during which the ticker cannot be entered.</summary>
    public int DiscontinuityCooldownSessions { get; init; } = 60;

    /// <summary>Sessions used for the median trading value (liquidity / slippage).</summary>
    public int LiquidityLookback { get; init; } = 20;
}

public sealed record TradeRecord(
    string Ticker,
    DateOnly EntrySignalDate,
    DateOnly EntryDate,
    decimal EntryPrice,
    decimal EntryReferencePrice,
    DateOnly ExitDate,
    decimal ExitPrice,
    decimal ExitReferencePrice,
    long Quantity,
    decimal GrossPnl,
    decimal Costs,
    decimal NetPnl,
    int HoldingSessions,
    string EntryReason,
    string ExitReason,
    double EntryScore)
{
    public decimal EntryNotional => EntryReferencePrice * Quantity;
    public double GrossReturn => EntryNotional == 0 ? 0 : (double)(GrossPnl / EntryNotional);
    public double NetReturn => EntryNotional == 0 ? 0 : (double)(NetPnl / EntryNotional);
}

public sealed record EquityPoint(DateOnly Date, decimal NetEquity, decimal GrossEquity, decimal Cash, int Positions, decimal InvestedValue);

public sealed record RiskEvent(DateOnly Date, string Kind, string Detail);

public sealed class BacktestResult
{
    public required StrategyDescriptor Strategy { get; init; }
    public required BacktestConfig Config { get; init; }
    public required IReadOnlyList<TradeRecord> Trades { get; init; }
    public required IReadOnlyList<EquityPoint> Equity { get; init; }
    public required IReadOnlyList<RiskEvent> RiskEvents { get; init; }
    public required IReadOnlyDictionary<string, int> Rejections { get; init; }
    public required string DataHash { get; init; }
    public required string UniverseHash { get; init; }
    public required string DataSource { get; init; }
    public required double BenchmarkReturn { get; init; }
    public required int SignalCount { get; init; }
    public DateOnly? HaltedOn { get; init; }
    public string? HaltReason { get; init; }

    public const string EngineVersion = "1.0.0";

    /// <summary>Canonical digest of trades + equity. Same inputs must give the same hash.</summary>
    public string ResultHash()
    {
        var sb = new StringBuilder();
        foreach (var t in Trades)
            sb.Append(t.Ticker).Append(',').Append(t.EntryDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)).Append(',')
              .Append(t.ExitDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)).Append(',').Append(t.Quantity).Append(',')
              .Append(Math.Round(t.EntryPrice, 4).ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(Math.Round(t.ExitPrice, 4).ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(Math.Round(t.NetPnl, 2).ToString(CultureInfo.InvariantCulture)).Append(',').Append(t.ExitReason).Append('\n');
        foreach (var e in Equity)
            sb.Append(e.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)).Append(',')
              .Append(Math.Round(e.NetEquity, 2).ToString(CultureInfo.InvariantCulture)).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
