namespace Investment.Domain.Market;

public enum TrendRegime
{
    Sideways = 0,
    Bull = 1,
    Bear = 2,
}

public enum VolatilityRegime
{
    Low = 0,
    High = 1,
}

public readonly record struct RegimeLabel(TrendRegime Trend, VolatilityRegime Volatility)
{
    public override string ToString() => $"{Trend}/{Volatility}Vol";
}

/// <summary>Stored daily regime of the benchmark index (known at that session's close).</summary>
public sealed class MarketRegimeDay
{
    public required string IndexCode { get; set; }
    public DateOnly Date { get; set; }
    public TrendRegime Trend { get; set; }
    public VolatilityRegime Volatility { get; set; }
    public double Close { get; set; }
    public double Sma200 { get; set; }
    public double RealizedVol20 { get; set; }
    public double VolThreshold { get; set; }
}

/// <summary>
/// Point-in-time regime from an index series (only bars up to the as-of date).
/// Trend: close vs SMA200 and the SMA200's 20-session slope — both agree → Bull/Bear, otherwise Sideways.
/// Volatility: 20-session annualized realized vol above 1.25 × its trailing 250-session median → High.
/// </summary>
public static class RegimeClassifier
{
    public const int SmaLength = 200;
    public const int SlopeLookback = 20;
    public const int VolLength = 20;
    public const int VolHistory = 250;
    public const double HighVolMultiple = 1.25;

    public static int MinBars => SmaLength + SlopeLookback;

    public static RegimeLabel? Classify(BarSeries index) => Compute(index)?.Label;

    public static (RegimeLabel Label, double Sma, double Vol, double Threshold)? Compute(BarSeries index)
    {
        if (index.Count < MinBars + 1) return null;
        var close = index.CloseAgo(0);
        var sma = Sma(index, 0);
        var smaPrev = Sma(index, SlopeLookback);
        var trend = close > sma && sma > smaPrev ? TrendRegime.Bull
            : close < sma && sma < smaPrev ? TrendRegime.Bear
            : TrendRegime.Sideways;

        var vol = RealizedVol(index, 0);
        var history = new List<double>();
        for (var k = 0; k < VolHistory && index.Count - 1 - k - VolLength >= 0; k += 5) history.Add(RealizedVol(index, k));
        history.Sort();
        var median = history.Count == 0 ? vol : history[history.Count / 2];
        var threshold = median * HighVolMultiple;
        return (new RegimeLabel(trend, vol > threshold ? VolatilityRegime.High : VolatilityRegime.Low), sma, vol, threshold);
    }

    private static double Sma(BarSeries s, int offset)
    {
        var sum = 0.0;
        for (var k = 0; k < SmaLength; k++) sum += s.CloseAgo(offset + k);
        return sum / SmaLength;
    }

    private static double RealizedVol(BarSeries s, int offset)
    {
        var rs = new double[VolLength];
        for (var k = 0; k < VolLength; k++) rs[k] = Math.Log(s.CloseAgo(offset + k) / s.CloseAgo(offset + k + 1));
        var m = rs.Average();
        return Math.Sqrt(rs.Sum(r => (r - m) * (r - m)) / (VolLength - 1)) * Math.Sqrt(252);
    }
}
