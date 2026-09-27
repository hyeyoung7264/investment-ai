using Investment.Domain.Market;
using Investment.Domain.Strategies;
using Investment.Risk;

namespace Investment.Backtest.Tests;

public sealed class EngineTests
{
    private static readonly CostModel Costs = new() { CommissionRate = 0.001, BaseSlippage = 0.01, ImpactCoefficient = 0 };

    private static BacktestConfig Config(TestMarket m, CostModel? costs = null, RiskLimits? risk = null) => new()
    {
        Start = m.Calendar[0],
        End = m.Calendar[^1],
        InitialCapital = 10_000_000m,
        Costs = costs ?? Costs,
        Risk = risk ?? new RiskLimits { MaxPositions = 1, MaxPositionWeight = 1.0, MaxSectorWeight = 1.0, StopLoss = null, MaxHoldingSessions = null, MaxParticipation = 1.0, MaxDailyLoss = 1.0, MaxDrawdown = 1.0 },
    };

    [Fact]
    public void Signal_at_close_fills_at_next_open_with_slippage_commission_and_sell_tax()
    {
        var m = new TestMarket(8).Flat("A", 10_000);
        m.Set("A", 1, 10_000, 10_000, 10_000, 10_000);
        m.Set("A", 4, 11_000, 11_000, 11_000, 11_000);
        var s = new ScriptedStrategy(m.Calendar).At(0, "A", SignalAction.Buy).At(3, "A", SignalAction.Sell);
        var r = new BacktestEngine().Run(s, m.Build(), Config(m));

        var t = Assert.Single(r.Trades);
        Assert.Equal(m.Calendar[1], t.EntryDate);
        Assert.Equal(m.Calendar[4], t.ExitDate);
        Assert.Equal(10_100m, t.EntryPrice);          // open × (1 + 1%)
        Assert.Equal(10_890m, t.ExitPrice);           // open × (1 − 1%)
        Assert.Equal(3, t.HoldingSessions);

        // qty: floor(min(equity, cash/(1+buffer)) / (close × (1+buffer))), buffer = 0.001 + 0.02
        var buffer = 0.021;
        var expectedQty = (long)Math.Floor(10_000_000 / (1 + buffer) / (10_000 * (1 + buffer)));
        Assert.Equal(expectedQty, t.Quantity);

        var q = (decimal)t.Quantity;
        var entryCosts = 10_100m * q * 0.001m + 100m * q;
        var tax = 10_890m * q * (decimal)Costs.SellTaxRate(m.Calendar[4]);
        var exitCosts = 10_890m * q * 0.001m + tax + 110m * q;
        Assert.Equal(1_000m * q, t.GrossPnl);
        Assert.Equal(entryCosts + exitCosts, t.Costs, 6);
        Assert.Equal(t.GrossPnl - t.Costs, t.NetPnl);

        var final = r.Equity[^1];
        Assert.Equal(10_000_000m + t.NetPnl, final.NetEquity, 6);
        // gross compounds session returns with costs added back: ≈ net + costs (second-order difference only)
        // entry costs are added back on session 1, exit costs on session 4
        var e = r.Equity;
        var expectedGross = 10_000_000m * (e[1].NetEquity + entryCosts) / 10_000_000m
                            * (e[4].NetEquity + exitCosts) / e[3].NetEquity * e[^1].NetEquity / e[4].NetEquity * e[3].NetEquity / e[1].NetEquity;
        Assert.Equal(expectedGross, final.GrossEquity, 4);
        Assert.True(final.GrossEquity > final.NetEquity + t.Costs); // saved entry costs would have compounded with the +10% move
        var grossReturns = Metrics.DailyReturns(10_000_000, r.Equity.Select(e => (double)e.GrossEquity).ToList());
        var netReturns = Metrics.DailyReturns(10_000_000, r.Equity.Select(e => (double)e.NetEquity).ToList());
        Assert.Equal(netReturns[0], grossReturns[0], 12); // no trades on the first session
    }

    [Fact]
    public void Zero_cost_model_gives_identical_gross_and_net()
    {
        var m = new TestMarket(6).Flat("A", 1000);
        m.Set("A", 3, 1100, 1100, 1100, 1100);
        var s = new ScriptedStrategy(m.Calendar).At(0, "A", SignalAction.Buy).At(2, "A", SignalAction.Sell);
        var r = new BacktestEngine().Run(s, m.Build(), Config(m, CostModel.Zero));
        Assert.All(r.Equity, e => Assert.Equal(e.NetEquity, e.GrossEquity));
        Assert.Equal(0m, Assert.Single(r.Trades).Costs);
    }

    [Fact]
    public void Stop_loss_gap_fills_at_open_and_intraday_touch_fills_at_stop()
    {
        var risk = new RiskLimits { MaxPositions = 2, MaxPositionWeight = 0.5, MaxSectorWeight = 1, StopLoss = 0.10, MaxHoldingSessions = null, MaxParticipation = 1, MaxDailyLoss = 1, MaxDrawdown = 1 };
        var m = new TestMarket(6).Flat("GAP", 1000).Flat("TCH", 1000);
        m.Set("GAP", 2, 800, 850, 780, 820);   // gaps below the 900 stop
        m.Set("TCH", 2, 950, 960, 880, 940);   // trades through 900 intraday
        var s = new ScriptedStrategy(m.Calendar).At(0, "GAP", SignalAction.Buy).At(0, "TCH", SignalAction.Buy);
        var r = new BacktestEngine().Run(s, m.Build(), Config(m, CostModel.Zero, risk));

        var gap = r.Trades.Single(t => t.Ticker == "GAP");
        Assert.Equal("StopLoss", gap.ExitReason);
        Assert.Equal(800m, gap.ExitPrice);
        var touch = r.Trades.Single(t => t.Ticker == "TCH");
        Assert.Equal("StopLoss", touch.ExitReason);
        Assert.Equal(900m, touch.ExitPrice);
    }

    [Fact]
    public void Halted_session_drops_buy_and_retries_sell()
    {
        var m = new TestMarket(8).Flat("A", 1000).Flat("B", 1000);
        m.Set("B", 1, 1000, 1000, 1000, 1000, 0, halted: true);
        m.Set("A", 4, 1000, 1000, 1000, 1000, 0, halted: true);
        var s = new ScriptedStrategy(m.Calendar)
            .At(0, "B", SignalAction.Buy)          // B halted at session 1 → no fill
            .At(1, "A", SignalAction.Buy)
            .At(3, "A", SignalAction.Sell);        // A halted at session 4 → sells at 5
        var r = new BacktestEngine().Run(s, m.Build(), Config(m, CostModel.Zero));

        Assert.DoesNotContain(r.Trades, t => t.Ticker == "B");
        var a = Assert.Single(r.Trades);
        Assert.Equal(m.Calendar[5], a.ExitDate);
        Assert.Equal(1, r.Rejections["buy-not-trading"]);
    }

    [Fact]
    public void Delisted_position_exits_at_last_traded_close_including_liquidation_crash()
    {
        var m = new TestMarket(40);
        m.Flat("D", 1000, delisted: m.Calendar[30]);
        m.Set("D", 29, 300, 300, 100, 120);   // liquidation trading crash (no price limit)
        m.Truncate("D", 30);
        var s = new ScriptedStrategy(m.Calendar).At(0, "D", SignalAction.Buy);
        var r = new BacktestEngine().Run(s, m.Build(), Config(m, CostModel.Zero));

        var t = Assert.Single(r.Trades);
        Assert.Equal("Delisted", t.ExitReason);
        Assert.Equal(120m, t.ExitPrice);
        Assert.True(t.NetPnl < 0);
    }

    [Fact]
    public void Data_discontinuity_is_not_counted_as_return_and_blocks_reentry()
    {
        var m = new TestMarket(12).Flat("R", 1000);
        for (var i = 4; i < 12; i++) m.Set("R", i, 10_000, 10_000, 10_000, 10_000); // unadjusted 10:1 reverse split
        var s = new ScriptedStrategy(m.Calendar).At(0, "R", SignalAction.Buy).At(6, "R", SignalAction.Buy);
        var r = new BacktestEngine().Run(s, m.Build(), Config(m, CostModel.Zero));

        var t = Assert.Single(r.Trades);
        Assert.Equal("DataDiscontinuity", t.ExitReason);
        Assert.Equal(1000m, t.ExitPrice);
        Assert.Equal(0m, t.NetPnl);
        Assert.Equal(10_000_000m, r.Equity[^1].NetEquity);
    }

    [Fact]
    public void Drawdown_limit_liquidates_and_halts()
    {
        var risk = new RiskLimits { MaxPositions = 1, MaxPositionWeight = 1, MaxSectorWeight = 1, StopLoss = null, MaxHoldingSessions = null, MaxParticipation = 1, MaxDailyLoss = 1, MaxDrawdown = 0.15 };
        var m = new TestMarket(10).Flat("A", 1000).Flat("B", 1000);
        for (var i = 3; i < 10; i++) m.Set("A", i, 800, 800, 800, 800);
        var s = new ScriptedStrategy(m.Calendar).At(0, "A", SignalAction.Buy).At(5, "B", SignalAction.Buy);
        var r = new BacktestEngine().Run(s, m.Build(), Config(m, CostModel.Zero, risk));

        Assert.Equal(m.Calendar[3], r.HaltedOn);
        var t = Assert.Single(r.Trades);
        Assert.Equal("RiskHalt", t.ExitReason);
        Assert.Equal(m.Calendar[4], t.ExitDate);
        Assert.DoesNotContain(r.Trades, x => x.Ticker == "B");
    }

    [Fact]
    public void Daily_loss_limit_blocks_next_session_entries()
    {
        var risk = new RiskLimits { MaxPositions = 2, MaxPositionWeight = 0.5, MaxSectorWeight = 1, StopLoss = null, MaxHoldingSessions = null, MaxParticipation = 1, MaxDailyLoss = 0.02, MaxDrawdown = 1 };
        var m = new TestMarket(8).Flat("A", 1000).Flat("B", 1000);
        m.Set("A", 2, 900, 900, 900, 900);
        for (var i = 3; i < 8; i++) m.Set("A", i, 900, 900, 900, 900);
        var s = new ScriptedStrategy(m.Calendar).At(0, "A", SignalAction.Buy).At(2, "B", SignalAction.Buy).At(3, "B", SignalAction.Buy);
        var r = new BacktestEngine().Run(s, m.Build(), Config(m, CostModel.Zero, risk));

        Assert.Contains(r.RiskEvents, e => e.Kind == "EntriesBlocked" && e.Date == m.Calendar[2]);
        var b = r.Trades.Single(t => t.Ticker == "B");
        Assert.Equal(m.Calendar[4], b.EntryDate); // signal of session 2 blocked, session 3 accepted
    }

    [Fact]
    public void Locked_limit_up_session_rejects_buy()
    {
        var m = new TestMarket(6).Flat("U", 1000);
        for (var i = 1; i < 6; i++) m.Set("U", i, 1300, 1300, 1300, 1300);
        var s = new ScriptedStrategy(m.Calendar).At(0, "U", SignalAction.Buy);
        var r = new BacktestEngine().Run(s, m.Build(), Config(m, CostModel.Zero));
        Assert.Empty(r.Trades);
        Assert.Equal(1, r.Rejections["buy-locked-limit-up"]);
    }

    [Fact]
    public void Position_count_and_weight_limits_are_enforced()
    {
        var risk = new RiskLimits { MaxPositions = 2, MaxPositionWeight = 0.3, MaxSectorWeight = 1, StopLoss = null, MaxHoldingSessions = null, MaxParticipation = 1, MaxDailyLoss = 1, MaxDrawdown = 1 };
        var m = new TestMarket(5).Flat("A", 1000).Flat("B", 1000).Flat("C", 1000);
        var s = new ScriptedStrategy(m.Calendar).At(0, "A", SignalAction.Buy, 3).At(0, "B", SignalAction.Buy, 2).At(0, "C", SignalAction.Buy, 1);
        var r = new BacktestEngine().Run(s, m.Build(), Config(m, CostModel.Zero, risk));
        Assert.Equal(["A", "B"], r.Trades.Select(t => t.Ticker).Order());
        Assert.All(r.Trades, t => Assert.True(t.EntryNotional <= 10_000_000m * 0.3m));
        Assert.Equal(1, r.Rejections["max-positions"]);
    }

    [Fact]
    public void Sector_limit_is_enforced()
    {
        var risk = new RiskLimits { MaxPositions = 3, MaxPositionWeight = 0.3, MaxSectorWeight = 0.3, StopLoss = null, MaxHoldingSessions = null, MaxParticipation = 1, MaxDailyLoss = 1, MaxDrawdown = 1 };
        var m = new TestMarket(5).Flat("A", 1000, sector: "chips").Flat("B", 1000, sector: "chips").Flat("C", 1000, sector: "food");
        var s = new ScriptedStrategy(m.Calendar).At(0, "A", SignalAction.Buy, 3).At(0, "B", SignalAction.Buy, 2).At(0, "C", SignalAction.Buy, 1);
        var r = new BacktestEngine().Run(s, m.Build(), Config(m, CostModel.Zero, risk));
        Assert.Equal(["A", "C"], r.Trades.Select(t => t.Ticker).Order());
    }

    [Fact]
    public void Max_holding_sessions_forces_exit()
    {
        var risk = new RiskLimits { MaxPositions = 1, MaxPositionWeight = 1, MaxSectorWeight = 1, StopLoss = null, MaxHoldingSessions = 3, MaxParticipation = 1, MaxDailyLoss = 1, MaxDrawdown = 1 };
        var m = new TestMarket(10).Flat("A", 1000);
        var s = new ScriptedStrategy(m.Calendar).At(0, "A", SignalAction.Buy);
        var r = new BacktestEngine().Run(s, m.Build(), Config(m, CostModel.Zero, risk));
        var t = Assert.Single(r.Trades);
        Assert.Equal("MaxHolding", t.ExitReason);
        Assert.Equal(4, t.HoldingSessions); // decided after 3 sessions, filled at the next open
    }

    [Fact]
    public void Signals_outside_universe_are_ignored()
    {
        var m = new TestMarket(5).Flat("A", 1000);
        var s = new ScriptedStrategy(m.Calendar).At(0, "ZZZ", SignalAction.Buy);
        var r = new BacktestEngine().Run(s, m.Build(), Config(m, CostModel.Zero));
        Assert.Empty(r.Trades);
        Assert.Equal(1, r.Rejections["signal-outside-universe"]);
    }

    [Fact]
    public void Day_trade_enters_at_open_and_exits_at_the_same_session_close()
    {
        var m = new TestMarket(6).Flat("A", 1000);
        m.Set("A", 1, 1000, 1080, 990, 1050);
        var s = new ScriptedStrategy(m.Calendar).At(0, "A", SignalAction.Buy, dayTrade: true);
        var r = new BacktestEngine().Run(s, m.Build(), Config(m, CostModel.Zero));
        var t = Assert.Single(r.Trades);
        Assert.Equal(m.Calendar[1], t.EntryDate);
        Assert.Equal(m.Calendar[1], t.ExitDate);
        Assert.Equal(1000m, t.EntryPrice);
        Assert.Equal(1050m, t.ExitPrice);
        Assert.Equal("SessionClose", t.ExitReason);
        Assert.Equal(0, r.Equity[1].Positions);
    }

    [Fact]
    public void Day_trade_stop_loss_fires_before_the_close_exit()
    {
        var risk = new RiskLimits { MaxPositions = 1, MaxPositionWeight = 1, MaxSectorWeight = 1, StopLoss = 0.05, MaxParticipation = 1, MaxDailyLoss = 1, MaxDrawdown = 1 };
        var m = new TestMarket(6).Flat("A", 1000);
        m.Set("A", 1, 1000, 1010, 900, 1040);
        var s = new ScriptedStrategy(m.Calendar).At(0, "A", SignalAction.Buy, dayTrade: true);
        var r = new BacktestEngine().Run(s, m.Build(), Config(m, CostModel.Zero, risk));
        var t = Assert.Single(r.Trades);
        Assert.Equal("StopLoss", t.ExitReason);
        Assert.Equal(950m, t.ExitPrice);
    }
}
