using Investment.Domain.Market;

namespace Investment.MarketData.Krx;

/// <summary>
/// Builds split/rights-adjusted bars from official KRX raw records. KRX publishes each day's change against the
/// previous day's base price, which it resets after corporate actions (splits, reverse splits, bonus/rights
/// issues): base_t = close_t − change_t. The adjustment on day t is base_t / close_{t−1}; earlier prices are multiplied
/// by the product of all later adjustments and volumes divided by it (price × volume preserved).
/// </summary>
public static class KrxPriceAdjuster
{
    public static Bar[] Build(IReadOnlyList<KrxDaily> rows)
    {
        var n = rows.Count;
        var factor = new double[n];
        var m = 1.0;
        for (var t = n - 1; t >= 0; t--)
        {
            factor[t] = m;
            if (t == 0) break;
            var prevClose = (double)rows[t - 1].Close;
            var baseToday = (double)(rows[t].Close - rows[t].ChangeFromPrevious);
            if (prevClose > 0 && baseToday > 0)
            {
                var r = baseToday / prevClose;
                if (Math.Abs(r - 1) > 0.0005) m *= r; // exact 1 on ordinary days; tolerance for tick rounding
            }
        }
        var bars = new Bar[n];
        for (var t = 0; t < n; t++)
        {
            var k = rows[t];
            var f = factor[t];
            var halted = k.Volume == 0 || k.Open <= 0;
            var c = (double)k.Close * f;
            bars[t] = halted
                ? new Bar(k.Date, c, c, c, c, 0, true)
                : new Bar(k.Date, (double)k.Open * f, (double)k.High * f, (double)k.Low * f, c, (long)Math.Round(k.Volume / f), false);
        }
        return bars;
    }
}
