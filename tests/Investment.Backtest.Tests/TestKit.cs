using Investment.Domain.Market;
using Investment.Domain.Strategies;

namespace Investment.Backtest.Tests;

/// <summary>Synthetic market builder: weekday calendar, hand-specified bars, whole-dataset universe.</summary>
public sealed class TestMarket
{
    public List<DateOnly> Calendar { get; }
    public Dictionary<string, List<Bar>> Bars { get; } = new();
    public Dictionary<string, Security> Securities { get; } = new();

    public TestMarket(int sessions, DateOnly? start = null)
    {
        Calendar = [];
        for (var d = start ?? new DateOnly(2024, 1, 1); Calendar.Count < sessions; d = d.AddDays(1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) Calendar.Add(d);
    }

    public TestMarket Flat(string ticker, double price, long volume = 1_000_000, string? sector = null, DateOnly? delisted = null)
    {
        Bars[ticker] = Calendar.Select(d => new Bar(d, price, price, price, price, volume, false)).ToList();
        Securities[ticker] = new Security { Ticker = ticker, Name = ticker, Market = MarketType.Kospi, Sector = sector, DelistedDate = delisted };
        return this;
    }

    public TestMarket Set(string ticker, int session, double open, double high, double low, double close, long volume = 1_000_000, bool halted = false)
    {
        Bars[ticker][session] = new Bar(Calendar[session], open, high, low, close, volume, halted);
        return this;
    }

    public TestMarket Truncate(string ticker, int sessions)
    {
        Bars[ticker] = Bars[ticker].Take(sessions).ToList();
        return this;
    }

    public MarketDataSet Build()
    {
        var index = Calendar.Select(d => new Bar(d, 100, 100, 100, 100, 0, false)).ToArray();
        var universe = new PointInTimeUniverse([new UniverseSnapshot(Calendar[0].AddDays(-1), Calendar[0], Bars.Keys.Order().ToList())]);
        return new MarketDataSet(Calendar, Bars.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()), Securities, index, "TEST", universe, "synthetic");
    }
}

/// <summary>Emits pre-scripted signals keyed by session index.</summary>
public sealed class ScriptedStrategy(IReadOnlyList<DateOnly> calendar) : IStrategy
{
    private readonly Dictionary<DateOnly, List<Signal>> _script = new();

    public ScriptedStrategy At(int session, string ticker, SignalAction action, double score = 1)
    {
        var d = calendar[session];
        if (!_script.TryGetValue(d, out var list)) _script[d] = list = [];
        list.Add(new Signal(ticker, action, score, $"scripted {action}"));
        return this;
    }

    public StrategyDescriptor Descriptor { get; } = new("test.scripted", "Scripted", "Test", 1, "test", new { });
    public int WarmupBars => 0;

    public IReadOnlyList<Signal> GenerateSignals(StrategyContext context) =>
        _script.TryGetValue(context.AsOf, out var s) ? s : [];
}

/// <summary>Wraps a strategy and records every signal with the as-of date.</summary>
public sealed class RecordingStrategy(IStrategy inner) : IStrategy
{
    public List<(DateOnly AsOf, Signal Signal)> Log { get; } = [];
    public StrategyDescriptor Descriptor => inner.Descriptor;
    public int WarmupBars => inner.WarmupBars;

    public IReadOnlyList<Signal> GenerateSignals(StrategyContext context)
    {
        var s = inner.GenerateSignals(context);
        Log.AddRange(s.Select(x => (context.AsOf, x)));
        return s;
    }
}
