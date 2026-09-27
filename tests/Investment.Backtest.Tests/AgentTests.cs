using System.Text.Json;
using Investment.Domain.Research;
using Investment.Research;
using Investment.Research.Agent;

namespace Investment.Backtest.Tests;

public sealed class AgentTests
{
    private static readonly WalkForwardPlan Plan = new() { FirstTrainStart = new DateOnly(2017, 1, 1), LastDate = new DateOnly(2026, 9, 23) };

    [Fact]
    public void Fingerprint_ignores_json_key_order_and_whitespace_but_not_values()
    {
        var a = new HypothesisProposal("meanrev.zscore", ["""{"EntryZ":-2.5,"TrendLength":null}"""], "", "", "");
        var b = new HypothesisProposal("meanrev.zscore", ["""{ "TrendLength": null, "EntryZ": -2.5 }"""], "other text", "", "llm");
        var c = new HypothesisProposal("meanrev.zscore", ["""{"EntryZ":-3.0,"TrendLength":null}"""], "", "", "");
        Assert.Equal(ResearchAgent.Fingerprint(a, Plan), ResearchAgent.Fingerprint(b, Plan));
        Assert.NotEqual(ResearchAgent.Fingerprint(a, Plan), ResearchAgent.Fingerprint(c, Plan));
        Assert.NotEqual(ResearchAgent.Fingerprint(a, Plan), ResearchAgent.Fingerprint(a with { UniverseTopN = 50 }, Plan));
    }

    [Fact]
    public void Studies_recorded_before_fingerprints_are_recognized_as_duplicates()
    {
        var universe = new Investment.MarketData.Universe.UniverseDefinition();
        var planJson = JsonSerializer.Serialize(new { plan = Plan with { ParameterGrid = [null] }, universe });
        var fromPlan = ResearchAgent.TryFingerprintFromPlan(planJson, "reversal.st");
        Assert.Equal(ResearchAgent.Fingerprint(new HypothesisProposal("reversal.st", [null], "", "", ""), Plan), fromPlan);
    }

    [Fact]
    public async Task Rules_propose_unexplored_and_cost_killed_ideas()
    {
        var evidence = new OosEvidence { Trades = 300, GrossEvPerTrade = 0.004, NetEvPerTrade = -0.002 };
        var study = new ResearchStudy { StrategyId = "reversal.st", Kind = "walk-forward", Hypothesis = "h", PlanJson = "{}", CodeCommit = "x" };
        var eval = new StrategyEvaluation { Stage = "walk-forward-oos", EvidenceJson = JsonSerializer.Serialize(evidence), CriteriaJson = "{}", Reasons = "", ToStatus = StrategyStatus.Rejected };
        var ctx = new ResearchContext([], [(study, eval)], [], ["reversal.st", "meanrev.zscore", "control.random"], "Bull/LowVol");
        var proposals = await new RuleBasedGenerator().ProposeAsync(ctx, CancellationToken.None);
        Assert.Contains(proposals, p => p.StrategyId == "meanrev.zscore" && p.Origin == "rule:unexplored");
        Assert.Contains(proposals, p => p.StrategyId == "reversal.st" && p.Origin == "rule:cost-reduction");
        Assert.DoesNotContain(proposals, p => p.StrategyId == "control.random");
    }
}
