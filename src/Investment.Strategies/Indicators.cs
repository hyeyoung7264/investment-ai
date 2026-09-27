using Investment.Domain.Market;

namespace Investment.Strategies;

/// <summary>Indicators over a point-in-time <see cref="BarSeries"/>; the latest visible bar is "now".</summary>
public static class Indicators
{
    public static double Sma(BarSeries s, int length, int offset = 0)
    {
        var sum = 0.0;
        for (var k = 0; k < length; k++) sum += s.CloseAgo(offset + k);
        return sum / length;
    }

    /// <summary>Population standard deviation of closes over <paramref name="length"/> bars.</summary>
    public static double StdDev(BarSeries s, int length, int offset = 0)
    {
        var mean = Sma(s, length, offset);
        var acc = 0.0;
        for (var k = 0; k < length; k++)
        {
            var d = s.CloseAgo(offset + k) - mean;
            acc += d * d;
        }
        return Math.Sqrt(acc / length);
    }

    /// <summary>Return from <paramref name="from"/> bars ago to <paramref name="to"/> bars ago.</summary>
    public static double Return(BarSeries s, int from, int to) => s.CloseAgo(to) / s.CloseAgo(from) - 1;

    public static double MedianTradingValue(BarSeries s, int length)
    {
        var xs = new double[length];
        for (var k = 0; k < length; k++)
        {
            var b = s.Ago(k);
            xs[k] = b.IsHalted ? 0 : b.TradingValue;
        }
        Array.Sort(xs);
        return length % 2 == 1 ? xs[length / 2] : (xs[length / 2 - 1] + xs[length / 2]) / 2;
    }

    /// <summary>True if any of the last <paramref name="length"/> bars was a halted session.</summary>
    public static bool AnyHalted(BarSeries s, int length)
    {
        for (var k = 0; k < length; k++)
            if (s.Ago(k).IsHalted) return true;
        return false;
    }
}
