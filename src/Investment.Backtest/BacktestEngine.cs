using Investment.Domain.Market;
using Investment.Domain.Strategies;

namespace Investment.Backtest;

/// <summary>Runs a <see cref="Simulator"/> over a period and collects the result. See Simulator for the event order.</summary>
public sealed class BacktestEngine
{
    private sealed class Collector : ISimulationObserver
    {
        public List<TradeRecord> Trades { get; } = [];
        public List<EquityPoint> Equity { get; } = [];
        public List<RiskEvent> Events { get; } = [];
        public SortedDictionary<string, int> Rejections { get; } = new(StringComparer.Ordinal);
        public int Signals { get; private set; }

        public void OnTrade(TradeRecord trade) => Trades.Add(trade);
        public void OnEquity(EquityPoint point) => Equity.Add(point);
        public void OnRiskEvent(RiskEvent e) => Events.Add(e);
        public void OnRejection(string rule) => Rejections[rule] = Rejections.GetValueOrDefault(rule) + 1;
        public void OnSignals(DateOnly asOf, int count) => Signals += count;
    }

    public BacktestResult Run(IStrategy strategy, MarketDataSet data, BacktestConfig cfg)
    {
        var cal = data.Calendar;
        var first = Simulator.FirstIndexOnOrAfter(cal, cfg.Start);
        var last = Simulator.LastIndexOnOrBefore(cal, cfg.End);
        if (first < 0 || last < first) throw new ArgumentException("no sessions in the requested period");

        var collector = new Collector();
        var state = SimulationState.Initial(cal[first], cfg.InitialCapital);
        var sim = new Simulator(strategy, data, cfg, state, collector);
        for (var i = first; i <= last; i++) sim.Step(i, i == last);

        return new BacktestResult
        {
            Strategy = strategy.Descriptor,
            Config = cfg,
            Trades = collector.Trades,
            Equity = collector.Equity,
            RiskEvents = collector.Events,
            Rejections = collector.Rejections,
            DataHash = data.ComputeHash(),
            UniverseHash = data.Universe.Hash(),
            DataSource = data.Source,
            BenchmarkReturn = BenchmarkReturn(data.Index, cal[first], cal[last]),
            SignalCount = collector.Signals,
            HaltedOn = state.HaltedOn,
            HaltReason = state.HaltReason,
        };
    }

    private static double BenchmarkReturn(Bar[] index, DateOnly from, DateOnly to)
    {
        // benchmark measured from the close before the first session to the last close (same as equity)
        var a = Simulator.VisibleIndex(index, from.AddDays(-1));
        var b = Simulator.VisibleIndex(index, to);
        if (a == 0 || b == 0) return double.NaN;
        return index[b - 1].Close / index[a - 1].Close - 1;
    }
}
