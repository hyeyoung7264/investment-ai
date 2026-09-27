using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Models.Messages;

namespace Investment.Research.Agent;

/// <summary>
/// Deterministic proposals derived from stored evidence:
///  1. unexplored catalog strategies get a default study;
///  2. cost-killed ideas (OOS gross EV &gt; 0, net EV ≤ 0) get a lower-turnover variant;
///  3. validated strategies with a losing regime get a regime-excluding composite (flagged post-hoc).
/// </summary>
public sealed class RuleBasedGenerator : IHypothesisGenerator
{
    public string Name => "rules";

    private static readonly HashSet<string> NotResearchTargets = ["control.liquidity-leaders", "control.random", "composite.regime-filter"];

    /// <summary>Per-strategy parameter change that lowers turnover (fewer, more selective trades).</summary>
    private static readonly Dictionary<string, string> LowerTurnover = new()
    {
        ["meanrev.zscore"] = """{"EntryZ":-3.0,"TrendLength":null}""",
        ["reversal.st"] = """{"Lookback":10,"TopK":5,"HoldingSessions":10}""",
        ["momentum.xsec"] = """{"Lookback":120,"Skip":20,"TopK":10,"ExitRank":40}""",
    };

    public Task<IReadOnlyList<HypothesisProposal>> ProposeAsync(ResearchContext ctx, CancellationToken ct)
    {
        var list = new List<HypothesisProposal>();
        var studied = ctx.Studies.Select(s => s.Study.StrategyId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var id in ctx.CatalogIds.Where(id => !NotResearchTargets.Contains(id) && !studied.Contains(id)).Order())
            list.Add(new HypothesisProposal(id, [null], $"{id} with default parameters has positive OOS EV", "never studied", "rule:unexplored"));

        foreach (var (study, eval) in ctx.Studies)
        {
            if (eval is null) continue;
            var e = JsonSerializer.Deserialize<OosEvidence>(eval.EvidenceJson)!;
            var costKilled = e.GrossEvPerTrade > 0 && (e.NetEvPerTrade <= 0.001 || eval.Decision == Domain.Research.GateDecision.Hold);
            if (costKilled && LowerTurnover.TryGetValue(study.StrategyId, out var p))
                list.Add(new HypothesisProposal(study.StrategyId, [p],
                    $"{study.StrategyId} lower-turnover variant keeps its gross edge after costs",
                    $"study {study.Id}: OOS gross EV {e.GrossEvPerTrade:P3} but net {e.NetEvPerTrade:P3} — costs dominate", "rule:cost-reduction"));

            if (eval.ToStatus is Domain.Research.StrategyStatus.Validated && eval.Stage == "walk-forward-oos")
            {
                var losing = e.ByRegime.Where(r => r.Value.Trades >= 30 && r.Value.NetEvPerTrade <= 0).Select(r => r.Key).Order().ToList();
                if (losing.Count == 0) continue;
                var inner = JsonNode.Parse(ParamsOfStudy(study.PlanJson) ?? "{}");
                var composite = new JsonObject
                {
                    ["Inner"] = study.StrategyId, ["InnerParameters"] = inner,
                    ["AllowedTrends"] = new JsonArray("Bull", "Bear", "Sideways"),
                    ["BlockedRegimes"] = new JsonArray(losing.Select(l => (JsonNode)l).ToArray()),
                };
                list.Add(new HypothesisProposal("composite.regime-filter", [composite.ToJsonString()],
                    $"{study.StrategyId} without entries in {string.Join(", ", losing)}",
                    $"post-hoc: validated study {study.Id} lost in {string.Join(", ", losing)} — weak evidence, paper trading decides", "rule:regime-exclusion (post-hoc)"));
            }
        }
        return Task.FromResult<IReadOnlyList<HypothesisProposal>>(list);
    }

    private static string? ParamsOfStudy(string planJson)
    {
        using var doc = JsonDocument.Parse(planJson);
        if (!doc.RootElement.TryGetProperty("plan", out var plan) || !plan.TryGetProperty("ParameterGrid", out var grid)) return null;
        var last = grid.EnumerateArray().LastOrDefault();
        return last.ValueKind == JsonValueKind.String ? last.GetString() : null;
    }
}

/// <summary>
/// Asks Claude for new hypotheses, constrained to the strategy catalog and returned as schema-validated JSON.
/// Only enabled when an API credential is configured. Its proposals get no special treatment: they go through
/// deduplication, the multiple-testing penalty and the same walk-forward gate as everything else.
/// </summary>
public sealed class LlmHypothesisGenerator(AnthropicClient client, string model = "claude-opus-5", int maxProposals = 3) : IHypothesisGenerator
{
    public string Name => "llm";

    public static LlmHypothesisGenerator? FromEnvironment() =>
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")) ? null : new LlmHypothesisGenerator(new AnthropicClient());

    public async Task<IReadOnlyList<HypothesisProposal>> ProposeAsync(ResearchContext ctx, CancellationToken ct)
    {
        var summary = new
        {
            currentRegime = ctx.CurrentRegime,
            catalog = ctx.CatalogIds.Order().ToList(),
            parameterSchemas = new Dictionary<string, object>
            {
                ["meanrev.zscore"] = new { BandLength = "int", EntryZ = "double<0", TrendLength = "int|null", ExitSmaLength = "int", MaxHoldingSessions = "int" },
                ["reversal.st"] = new { Lookback = "int", TopK = "int", HoldingSessions = "int", MaxLoss = "double<0" },
                ["momentum.xsec"] = new { Lookback = "int", Skip = "int", TopK = "int", ExitRank = "int" },
                ["composite.regime-filter"] = new { Inner = "catalog id", InnerParameters = "object", AllowedTrends = "[Bull|Bear|Sideways]", AllowedVolatility = "[Low|High]|null" },
            },
            studies = ctx.Studies.Select(s => new
            {
                s.Study.StrategyId, s.Study.Hypothesis, decision = s.Evaluation?.Decision.ToString(), reasons = s.Evaluation?.Reasons,
                evidence = s.Evaluation is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(s.Evaluation.EvidenceJson),
            }),
        };

        var schema = new Dictionary<string, JsonElement>
        {
            ["type"] = JsonSerializer.SerializeToElement("object"),
            ["properties"] = JsonSerializer.SerializeToElement(new
            {
                proposals = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            strategyId = new { type = "string" },
                            parametersJson = new { type = "string" },
                            hypothesis = new { type = "string" },
                            rationale = new { type = "string" },
                        },
                        required = new[] { "strategyId", "parametersJson", "hypothesis", "rationale" },
                        additionalProperties = false,
                    },
                },
            }),
            ["required"] = JsonSerializer.SerializeToElement(new[] { "proposals" }),
            ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
        };

        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = model,
            MaxTokens = 16000,
            Thinking = new ThinkingConfigAdaptive(),
            OutputConfig = new OutputConfig { Format = new JsonOutputFormat { Schema = schema } },
            System = """
                You are a quantitative research assistant for a Korean equity research system. You propose testable
                hypotheses only; a walk-forward study with transaction costs and a multiple-testing-adjusted gate decides.
                Prefer ideas with an economic rationale over parameter tweaks, avoid repeating rejected ideas, and prefer
                lower turnover because costs (~0.5% round trip) dominate short-term edges. Use only catalog strategy ids and
                the listed parameters. parametersJson must be a JSON object string.
                """,
            Messages = [new() { Role = Role.User, Content = $"Evidence so far:\n{JsonSerializer.Serialize(summary)}\n\nPropose up to {maxProposals} new studies." }],
        }, cancellationToken: ct);

        if (response.StopReason == "refusal") return [];
        var text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("proposals").EnumerateArray().Take(maxProposals).Select(p => new HypothesisProposal(
            p.GetProperty("strategyId").GetString()!,
            [p.GetProperty("parametersJson").GetString()],
            p.GetProperty("hypothesis").GetString()!,
            p.GetProperty("rationale").GetString()!,
            $"llm:{model}")).ToList();
    }
}
