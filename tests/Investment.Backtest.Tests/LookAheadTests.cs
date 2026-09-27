using Investment.Domain.Market;
using Investment.Domain.Research;
using Investment.Domain.Strategies;
using Investment.Risk;

namespace Investment.Backtest.Tests;

public sealed class LookAheadTests
{
    /// <summary>Uses real history: buys 5-session winners, sells 5-session losers.</summary>
    private sealed class HistoryStrategy : IStrategy
    {
        public StrategyDescriptor Descriptor { get; } = new("test.history", "History", "Test", 1, "test", new { lookback = 5 });
        public int WarmupBars => 6;

        public IReadOnlyList<Signal> GenerateSignals(StrategyContext ctx)
        {
            var list = new List<Signal>();
            foreach (var t in ctx.Universe.Concat(ctx.Positions.Keys).Distinct())
            {
                var h = ctx.History(t)!;
                if (h.Count < 6) continue;
                var r = h.CloseAgo(0) / h.CloseAgo(5) - 1;
                list.Add(new Signal(t, r > 0 ? SignalAction.Buy : SignalAction.Sell, r, $"r5={r:P2}"));
            }
            return list;
        }
    }

    private sealed class PeekingStrategy : IStrategy
    {
        public StrategyDescriptor Descriptor { get; } = new("test.peek", "Peek", "Test", 1, "test", new { });
        public int WarmupBars => 0;

        public IReadOnlyList<Signal> GenerateSignals(StrategyContext ctx)
        {
            var h = ctx.History("A")!;
            _ = h[h.Count]; // tomorrow's bar
            return [];
        }
    }

    private static TestMarket RandomMarket(int seed, int sessions, int? scrambleFrom = null, int scrambleSeed = 0)
    {
        var m = new TestMarket(sessions);
        var names = new[] { "A", "B", "C", "D", "E" };
        for (var n = 0; n < names.Length; n++)
        {
            var t = names[n];
            // independent streams per ticker so scrambling one future cannot shift another's past
            var rng = new Random(seed * 100 + n);
            var scramble = new Random(scrambleSeed * 100 + n);
            m.Flat(t, 10_000);
            var p = 10_000.0;
            for (var i = 0; i < sessions; i++)
            {
                var src = scrambleFrom is { } k && i >= k ? scramble : rng;
                var o = p * (1 + (src.NextDouble() - 0.5) * 0.02);
                var c = o * (1 + (src.NextDouble() - 0.5) * 0.08);
                m.Set(t, i, Math.Round(o), Math.Round(Math.Max(o, c) * 1.01), Math.Round(Math.Min(o, c) * 0.99), Math.Round(c), 1_000_000 + src.Next(1000));
                p = c;
            }
        }
        return m;
    }

    private static BacktestConfig Cfg(TestMarket m) => new()
    {
        Start = m.Calendar[10],
        End = m.Calendar[^1],
        Risk = new RiskLimits { MaxPositions = 3, MaxPositionWeight = 0.3, MaxSectorWeight = 1, StopLoss = 0.05, MaxDrawdown = 0.9, MaxDailyLoss = 0.5, MaxParticipation = 1 },
    };

    [Fact]
    public void Changing_future_data_does_not_change_past_signals_or_equity()
    {
        const int cutoff = 60;
        var baseline = RandomMarket(7, 120);
        var altered = RandomMarket(7, 120, scrambleFrom: cutoff, scrambleSeed: 99);
        // sanity: the futures really differ
        Assert.NotEqual(baseline.Bars["A"][cutoff + 5], altered.Bars["A"][cutoff + 5]);
        Assert.Equal(baseline.Bars["A"][cutoff - 1], altered.Bars["A"][cutoff - 1]);

        var s1 = new RecordingStrategy(new HistoryStrategy());
        var s2 = new RecordingStrategy(new HistoryStrategy());
        var r1 = new BacktestEngine().Run(s1, baseline.Build(), Cfg(baseline));
        var r2 = new BacktestEngine().Run(s2, altered.Build(), Cfg(altered));

        var boundary = baseline.Calendar[cutoff];
        Assert.Equal(s1.Log.Where(x => x.AsOf < boundary), s2.Log.Where(x => x.AsOf < boundary));
        Assert.Equal(r1.Equity.Where(e => e.Date < boundary), r2.Equity.Where(e => e.Date < boundary));
        Assert.Equal(r1.Trades.Where(t => t.ExitDate < boundary), r2.Trades.Where(t => t.ExitDate < boundary));
        Assert.NotEqual(r1.ResultHash(), r2.ResultHash());
    }

    [Fact]
    public void Reading_beyond_the_as_of_date_throws()
    {
        var m = RandomMarket(1, 30);
        Assert.Throws<LookAheadException>(() => new BacktestEngine().Run(new PeekingStrategy(), m.Build(), Cfg(m)));
    }

    [Fact]
    public void Same_inputs_give_identical_results()
    {
        var m = RandomMarket(3, 150);
        var a = new BacktestEngine().Run(new HistoryStrategy(), m.Build(), Cfg(m));
        var b = new BacktestEngine().Run(new HistoryStrategy(), RandomMarket(3, 150).Build(), Cfg(m));
        Assert.True(a.Trades.Count > 10);
        Assert.Equal(a.ResultHash(), b.ResultHash());
        Assert.Equal(a.DataHash, b.DataHash);
    }

    [Fact]
    public void Universe_selected_on_a_date_is_not_effective_until_the_next_session()
    {
        Assert.Throws<ArgumentException>(() => new PointInTimeUniverse([new UniverseSnapshot(new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 2), [])]));
    }
}

public sealed class MetricsTests
{
    [Fact]
    public void Drawdown_and_daily_returns_follow_definitions()
    {
        var curve = new double[] { 110, 99, 121, 60.5 };
        Assert.Equal(0.5, Metrics.MaxDrawdown(100, curve), 10);
        var r = Metrics.DailyReturns(100, curve);
        Assert.Equal(new[] { 0.1, -0.1, 121.0 / 99 - 1, -0.5 }.Select(x => Math.Round(x, 10)), r.Select(x => Math.Round(x, 10)));
    }

    [Fact]
    public void Trade_statistics_follow_definitions()
    {
        var d = new DateOnly(2024, 1, 1);
        TradeRecord T(decimal pnl) => new("X", d, d, 100, 100, d.AddDays(1), 100, 100, 10, pnl, 0, pnl, 1, "", "", 0);
        var result = new BacktestResult
        {
            Strategy = new StrategyDescriptor("t", "t", "t", 1, "", new { }),
            Config = new BacktestConfig { Start = d, End = d.AddDays(3), InitialCapital = 1000m },
            Trades = [T(100), T(-50), T(30), T(-20)],
            Equity =
            [
                new(d, 1100, 1100, 0, 0, 0), new(d.AddDays(1), 1050, 1050, 0, 0, 0),
                new(d.AddDays(2), 1080, 1080, 0, 0, 0), new(d.AddDays(3), 1060, 1060, 0, 0, 0),
            ],
            RiskEvents = [], Rejections = new Dictionary<string, int>(), DataHash = "", UniverseHash = "", DataSource = "",
            BenchmarkReturn = 0, SignalCount = 0,
        };
        var m = Metrics.Compute(result, CostBasis.Net);
        Assert.Equal(0.5, m.WinRate);
        Assert.Equal(130.0 / 70.0, m.ProfitFactor, 10);
        Assert.Equal((0.1 - 0.05 + 0.03 - 0.02) / 4, m.ExpectedValuePerTrade, 10);
        Assert.Equal(15, m.ExpectedValuePerTradeKrw, 10);
        Assert.Equal(0.065, m.AvgProfit, 10);
        Assert.Equal(-0.035, m.AvgLoss, 10);
        Assert.Equal(0.06, m.TotalReturn, 10);
        Assert.Equal(1 - 1050.0 / 1100, m.MaxDrawdown, 10);
        var daily = Metrics.DailyReturns(1000, [1100, 1050, 1080, 1060]);
        Assert.Equal(daily.Average() / Metrics.Stdev(daily) * Math.Sqrt(252), m.Sharpe, 10);
    }

    [Theory]
    [InlineData("2019-06-02", 0.0030)]
    [InlineData("2019-06-03", 0.0025)]
    [InlineData("2022-12-31", 0.0023)]
    [InlineData("2023-06-01", 0.0020)]
    [InlineData("2024-06-01", 0.0018)]
    [InlineData("2025-06-01", 0.0015)]
    [InlineData("2026-06-01", 0.0020)]
    public void Sell_tax_follows_korean_schedule(string date, double rate) =>
        Assert.Equal(rate, new CostModel().SellTaxRate(DateOnly.Parse(date)));

    [Fact]
    public void Slippage_grows_with_participation() =>
        Assert.Equal(0.001 + 0.1 * 0.02, new CostModel().Slippage(2_000_000, 100_000_000), 12);
}

public sealed class ResultHashTests
{
    [Fact]
    public void Result_hash_does_not_depend_on_decimal_scale()
    {
        var d = new DateOnly(2024, 1, 2);
        BacktestResult Make(decimal equity) => new()
        {
            Strategy = new Investment.Domain.Strategies.StrategyDescriptor("t", "t", "t", 1, "", new { }),
            Config = new BacktestConfig { Start = d, End = d },
            Trades = [], Equity = [new EquityPoint(d, equity, equity, equity, 0, 0)],
            RiskEvents = [], Rejections = new Dictionary<string, int>(), DataHash = "", UniverseHash = "", DataSource = "",
            BenchmarkReturn = 0, SignalCount = 0,
        };
        Assert.Equal(Make(100_000_000m).ResultHash(), Make(100_000_000.00m).ResultHash());
    }
}
