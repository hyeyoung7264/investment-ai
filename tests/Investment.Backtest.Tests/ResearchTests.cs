using Investment.Domain.Market;
using Investment.Domain.Research;
using Investment.Research;
using Investment.Strategies.Composite;
using Investment.Domain.Strategies;

namespace Investment.Backtest.Tests;

public sealed class ResearchTests
{
    private static OosEvidence Good => new()
    {
        Folds = 6, FoldsTraded = 6, FoldsPositive = 5, Trades = 300, NetEvPerTrade = 0.004, NetEvTStat = 3.0,
        NetSharpe = 1.1, NetMaxDrawdown = 0.15, NetTotalReturn = 0.4, RandomControlNetEvPerTrade = -0.005, VariantsTried = 1,
    };

    [Fact]
    public void Gate_promotes_only_with_complete_evidence()
    {
        var r = PromotionGate.EvaluateValidation(StrategyStatus.Backtested, Good, new GateCriteria());
        Assert.Equal(GateDecision.Promote, r.Decision);
        Assert.Equal(StrategyStatus.Validated, r.To);
    }

    [Theory]
    [InlineData(-0.001, 0.1, 0.3, GateDecision.Reject)]   // EV <= 0
    [InlineData(0.004, 0.45, 0.3, GateDecision.Reject)]   // MDD above limit
    [InlineData(0.004, 0.15, -0.1, GateDecision.Reject)]  // losing total return
    public void Gate_rejects_failed_evidence(double ev, double mdd, double total, GateDecision expected)
    {
        var r = PromotionGate.EvaluateValidation(StrategyStatus.Backtested, Good with { NetEvPerTrade = ev, NetMaxDrawdown = mdd, NetTotalReturn = total }, new GateCriteria());
        Assert.Equal(expected, r.Decision);
        Assert.Equal(StrategyStatus.Rejected, r.To);
    }

    [Fact]
    public void Gate_holds_positive_but_insignificant_or_thin_evidence()
    {
        var c = new GateCriteria();
        Assert.Equal(GateDecision.Hold, PromotionGate.EvaluateValidation(StrategyStatus.Backtested, Good with { NetEvTStat = 1.2 }, c).Decision);
        Assert.Equal(GateDecision.Hold, PromotionGate.EvaluateValidation(StrategyStatus.Backtested, Good with { Trades = 40 }, c).Decision);
        Assert.Equal(GateDecision.Hold, PromotionGate.EvaluateValidation(StrategyStatus.Backtested, Good with { FoldsPositive = 2 }, c).Decision);
        // t = 2.0 passes with 1 variant but not after trying 10 (multiple-testing penalty)
        Assert.Equal(GateDecision.Promote, PromotionGate.EvaluateValidation(StrategyStatus.Backtested, Good with { NetEvTStat = 2.0 }, c).Decision);
        Assert.Equal(GateDecision.Hold, PromotionGate.EvaluateValidation(StrategyStatus.Backtested, Good with { NetEvTStat = 2.0, VariantsTried = 10 }, c).Decision);
    }

    [Fact]
    public void Rejected_versions_are_never_resurrected()
    {
        var r = PromotionGate.EvaluateValidation(StrategyStatus.Rejected, Good, new GateCriteria());
        Assert.Equal(StrategyStatus.Rejected, r.To);
    }

    [Theory]
    [InlineData(1, 1.645)]
    [InlineData(4, 2.241)]
    [InlineData(10, 2.576)]
    public void Required_t_grows_with_variants_tried(int n, double t) => Assert.Equal(t, Stats.RequiredT(n), 2);

    [Fact]
    public void Walk_forward_folds_roll_without_overlap_between_train_validation_and_oos()
    {
        var plan = new WalkForwardPlan { FirstTrainStart = new DateOnly(2017, 1, 1), LastDate = new DateOnly(2026, 9, 23) };
        var folds = plan.Folds();
        Assert.Equal(6, folds.Count);
        Assert.Equal(new DateOnly(2021, 1, 1), folds[0].OosStart);
        Assert.Equal(new DateOnly(2026, 9, 23), folds[^1].OosEnd);
        foreach (var f in folds)
        {
            Assert.True(f.TrainEnd < f.ValidationStart && f.ValidationEnd < f.OosStart);
            Assert.Equal(f.TrainEnd.AddDays(1), f.ValidationStart);
            Assert.Equal(f.ValidationEnd.AddDays(1), f.OosStart);
        }
        for (var i = 1; i < folds.Count; i++) Assert.Equal(folds[i - 1].OosEnd.AddDays(1), folds[i].OosStart);
    }

    private static Bar[] Index(IEnumerable<double> closes) =>
        closes.Select((c, i) => new Bar(new DateOnly(2020, 1, 1).AddDays(i), c, c, c, c, 0, false)).ToArray();

    [Fact]
    public void Regime_classifier_labels_trends_point_in_time()
    {
        var up = Index(Enumerable.Range(0, 300).Select(i => 1000 + i * 2.0 + (i % 2) * 3));
        var down = Index(Enumerable.Range(0, 300).Select(i => 2000 - i * 2.0 + (i % 2) * 3));
        Assert.Equal(TrendRegime.Bull, RegimeClassifier.Classify(new BarSeries("I", up, up.Length))!.Value.Trend);
        Assert.Equal(TrendRegime.Bear, RegimeClassifier.Classify(new BarSeries("I", down, down.Length))!.Value.Trend);
        Assert.Null(RegimeClassifier.Classify(new BarSeries("I", up, 100)));
    }

    [Fact]
    public void Regime_filter_blocks_buys_but_passes_sells_outside_allowed_regime()
    {
        var up = Index(Enumerable.Range(0, 300).Select(i => 1000 + i * 2.0 + (i % 2) * 3));
        var down = Index(Enumerable.Range(0, 300).Select(i => 2000 - i * 2.0 + (i % 2) * 3));
        var stock = Index(Enumerable.Repeat(100.0, 300));
        // inner: random control with probability 1 -> always buys "A"; held "B" (6 sessions) -> always sells
        var filter = new RegimeFilterStrategy(new RegimeFilterParameters
        {
            Inner = "control.random",
            InnerParameters = System.Text.Json.Nodes.JsonNode.Parse("""{"EntryProbability":1.0,"HoldingSessions":5}""")!.AsObject(),
            AllowedTrends = [TrendRegime.Bull],
        });
        var held = new Dictionary<string, HeldPosition> { ["B"] = new("B", up[0].Date, 100, 6) };
        StrategyContext Ctx(Bar[] index) => new(index[^1].Date, ["A"], t => new BarSeries(t, stock, stock.Length), new BarSeries("I", index, index.Length), held);

        var bull = filter.GenerateSignals(Ctx(up));
        Assert.Contains(bull, s => s.Ticker == "A" && s.Action == SignalAction.Buy && s.Reason.Contains("Bull"));
        var bear = filter.GenerateSignals(Ctx(down));
        Assert.DoesNotContain(bear, s => s.Action == SignalAction.Buy);
        Assert.Contains(bear, s => s.Ticker == "B" && s.Action == SignalAction.Sell);
        Assert.Contains("\"AllowedTrends\":[1]", filter.Descriptor.ParametersJson);
    }
}
