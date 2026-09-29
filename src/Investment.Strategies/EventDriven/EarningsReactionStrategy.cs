using Investment.Domain.Market;
using Investment.Domain.Strategies;

namespace Investment.Strategies.EventDriven;

public sealed record EarningsReactionParameters
{
    /// <summary>Minimum abnormal return (stock − KOSPI) from the close before the filing to the close of the next session.</summary>
    public double MinAbnormal { get; init; } = 0.05;
    public int HoldingSessions { get; init; } = 20;

    /// <summary>Calendar days a filing stays eligible (covers weekends/holidays between filing and reaction).</summary>
    public int FreshDays { get; init; } = 10;
}

/// <summary>
/// Earnings-announcement-return drift. The surprise is the market's own reaction to a preliminary-earnings (or
/// earnings-change) disclosure: the stock's return from the close before the filing date to the close of the session
/// after the first session on/after it, minus KOSPI over the same window. Because the filing time is unknown, the
/// window spans both possible reaction days and the signal fires only once that window has closed.
/// </summary>
public sealed class EarningsReactionStrategy(EarningsReactionParameters? parameters = null) : IStrategy
{
    public const string Id = "event.ear";
    private readonly EarningsReactionParameters _p = parameters ?? new EarningsReactionParameters();

    public StrategyDescriptor Descriptor => new(Id, "Earnings announcement reaction drift", "EventDriven", 1,
        "After a preliminary-earnings filing, buy if the 2-session abnormal reaction >= MinAbnormal; hold HoldingSessions.", _p);

    public int WarmupBars => 3;
    public bool UsesCorporateEvents => true;

    public IReadOnlyList<Signal> GenerateSignals(StrategyContext ctx)
    {
        var signals = new List<Signal>();
        var since = ctx.AsOf.AddDays(-_p.FreshDays);
        var idx = ctx.MarketIndex;
        foreach (var t in ctx.Universe)
        {
            if (ctx.Positions.ContainsKey(t)) continue;
            var ev = ctx.Events(t).LastOrDefault(e => e.Date > since &&
                e.Type is DisclosureEvent.PreliminaryEarnings or DisclosureEvent.EarningsChange);
            if (ev.Title is null) continue;
            var h = ctx.History(t);
            if (h is null || h.Count < WarmupBars) continue;
            // b0 = first visible bar on/after the filing date; the window closes one session later (= today)
            var last = h.Count - 1;
            if (last < 2 || h[last - 1].Date < ev.Date || h[last - 2].Date >= ev.Date) continue;
            var before = h[last - 2];
            var stockRet = h[last].Close / before.Close - 1;
            var idxRet = idx is null ? 0 : IndexReturn(idx, before.Date, ctx.AsOf);
            if (double.IsNaN(idxRet)) continue;
            var ar = stockRet - idxRet;
            if (ar >= _p.MinAbnormal)
                signals.Add(new Signal(t, SignalAction.Buy, ar, $"earnings reaction {ar:P1} vs KOSPI (filed {ev.Date:yyyy-MM-dd}: {ev.Title})"));
        }
        foreach (var (t, pos) in ctx.Positions)
            if (pos.HoldingSessions >= _p.HoldingSessions)
                signals.Add(new Signal(t, SignalAction.Sell, 0, $"held {pos.HoldingSessions}"));
        return signals;
    }

    private static double IndexReturn(BarSeries idx, DateOnly from, DateOnly to)
    {
        double? a = null, b = null;
        for (var k = 0; k < Math.Min(idx.Count, 30); k++)
        {
            var bar = idx.Ago(k);
            if (b is null && bar.Date <= to) b = bar.Close;
            if (bar.Date <= from) { a = bar.Close; break; }
        }
        return a is > 0 && b is > 0 ? b.Value / a.Value - 1 : double.NaN;
    }
}
