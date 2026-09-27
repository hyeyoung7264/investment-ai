namespace Investment.Domain.Market;

/// <summary>Exchange rules and data conventions shared by data checks and the simulator.</summary>
public static class KrxRules
{
    /// <summary>Daily price limit: ±30% since 2015-06-15, ±15% before. Liquidation trading has no limit.</summary>
    public static double PriceLimitOn(DateOnly d) => d >= new DateOnly(2015, 6, 15) ? 0.30 : 0.15;

    /// <summary>Calendar days before delisting treated as the liquidation-trading (정리매매) window.</summary>
    public const int LiquidationWindowDays = 20;

    /// <summary>
    /// Days before delisting during which the delisting is public knowledge (the 정리매매 announcement),
    /// so excluding the name from new entries is not look-ahead.
    /// </summary>
    public const int KnownDelistingWindowDays = 14;

    /// <summary>
    /// A close-to-close move beyond the daily price limit that is NOT explained by liquidation trading.
    /// These are unadjusted corporate actions, market transfers with base-price resets, or source errors —
    /// the jump is not a tradable return.
    /// </summary>
    public static bool IsDiscontinuity(Security s, Bar previous, Bar current)
    {
        if (previous.Close <= 0) return false;
        var r = current.Close / previous.Close - 1;
        if (Math.Abs(r) <= PriceLimitOn(current.Date) + 0.005) return false; // +0.5%p tick rounding tolerance
        return !(s.DelistedDate is { } dd && dd.DayNumber - current.Date.DayNumber <= LiquidationWindowDays);
    }

    /// <summary>Repairs adjustment rounding (C &gt; H by 1원) and zero lows: H=max(O,H,C), L=min(O,L,C).</summary>
    public static Bar Normalize(Bar b)
    {
        var high = Math.Max(b.High, Math.Max(b.Open, b.Close));
        var low = b.Low > 0 ? Math.Min(b.Low, Math.Min(b.Open, b.Close)) : Math.Min(b.Open, b.Close);
        return b with { High = high, Low = low };
    }

    /// <summary>
    /// A session with no range at the upper (lower) limit: in practice no sell (buy) orders fill for newcomers.
    /// </summary>
    public static bool IsLockedLimitUp(Bar b, double prevClose) =>
        prevClose > 0 && b.High == b.Low && b.Open >= prevClose * (1 + PriceLimitOn(b.Date) - 0.01);

    public static bool IsLockedLimitDown(Bar b, double prevClose) =>
        prevClose > 0 && b.High == b.Low && b.Open <= prevClose * (1 - PriceLimitOn(b.Date) + 0.01);
}
