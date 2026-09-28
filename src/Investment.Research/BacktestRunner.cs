using System.Diagnostics;
using System.Text.Json;
using Investment.Backtest;
using Investment.Domain.Market;
using Investment.Domain.Research;
using Investment.Domain.Strategies;
using Investment.MarketData.Universe;
using Investment.Persistence;
using Investment.Strategies;
using Microsoft.EntityFrameworkCore;

namespace Investment.Research;

public sealed record BacktestRequest(
    string StrategyId,
    string? ParametersJson,
    UniverseDefinition Universe,
    BacktestConfig Config,
    string RunKind = "single",
    string? Label = null,
    Guid? ParentRunId = null,
    Guid? StudyId = null);

public sealed record RunOutcome(BacktestRun Run, BacktestResult Result, BacktestMetric Gross, BacktestMetric Net);

public sealed record RerunReport(Guid RunId, bool DataMatches, bool ResultMatches, string StoredDataHash, string NewDataHash, string StoredResultHash, string NewResultHash,
    bool ParametersMatch, string? FirstDifference);

public sealed class BacktestRunner(string connectionString, Action<string>? log = null)
{
    private readonly Action<string> _log = log ?? (_ => { });
    private readonly Dictionary<(string, DateOnly, DateOnly, bool, bool), MarketDataSet> _cache = new();
    private readonly CodeVersion _code = CodeVersion.Detect();

    public Func<InvestmentDbContext> DbFactory => () => Database.Create(connectionString);

    public async Task<MarketDataSet> LoadDataAsync(UniverseDefinition universe, DateOnly start, DateOnly end, CancellationToken ct,
        bool includeEvents = false, bool includeFundamentals = false)
    {
        var key = (JsonSerializer.Serialize(universe), start, end, includeEvents, includeFundamentals);
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var sw = Stopwatch.StartNew();
        var data = await new DataSetLoader(connectionString).LoadAsync(universe, start, end, includeEvents: includeEvents, ct: ct, includeFundamentals: includeFundamentals);
        _log($"data loaded: {data.Bars.Count} tickers, {data.Bars.Values.Sum(b => b.Length):N0} bars, {data.Universe.Snapshots.Count} universe snapshots, {data.Discontinuities.Count} tickers with discontinuities ({sw.Elapsed.TotalSeconds:F1}s)");
        _cache[key] = data;
        return data;
    }

    public async Task<RunOutcome> RunAsync(BacktestRequest req, CancellationToken ct = default)
    {
        var strategy = StrategyCatalog.Create(req.StrategyId, req.ParametersJson);
        var data = await LoadDataAsync(req.Universe, req.Config.Start, req.Config.End, ct, strategy.UsesCorporateEvents, strategy.UsesFundamentals);
        return await RunOnDataAsync(strategy, data, req, ct);
    }

    public async Task<RunOutcome> RunOnDataAsync(IStrategy strategy, MarketDataSet data, BacktestRequest req, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new BacktestEngine().Run(strategy, data, req.Config);
        var elapsed = sw.Elapsed.TotalMilliseconds;
        var run = await new BacktestRecorder(DbFactory).SaveAsync(result, req.Universe, _code, req.RunKind, req.Label, req.ParentRunId, elapsed, ct, req.StudyId);
        _log($"{strategy.Descriptor.Id} {req.Config.Start:yyyy-MM-dd}..{req.Config.End:yyyy-MM-dd} [{req.RunKind}] run {run.Id} ({elapsed / 1000:F1}s, {result.Trades.Count} trades)");
        return new RunOutcome(run, result, run.Metrics.Single(m => m.Basis == CostBasis.Gross), run.Metrics.Single(m => m.Basis == CostBasis.Net));
    }

    /// <summary>Re-executes a stored run from its recorded inputs and compares data and result hashes.</summary>
    public async Task<RerunReport> RerunAsync(Guid runId, CancellationToken ct = default)
    {
        BacktestRun stored;
        StrategyVersion version;
        await using (var db = DbFactory())
        {
            stored = await db.BacktestRuns.AsNoTracking().SingleAsync(r => r.Id == runId, ct);
            version = await db.StrategyVersions.AsNoTracking().SingleAsync(v => v.Id == stored.StrategyVersionId, ct);
        }
        if (stored.EngineVersion != BacktestResult.EngineVersion)
            throw new InvalidOperationException($"run was produced by engine {stored.EngineVersion}, current engine is {BacktestResult.EngineVersion}; check out commit {stored.CodeCommit} to reproduce it");
        var strategy = StrategyCatalog.Create(version.StrategyId, version.ParametersJson);
        if (strategy.Descriptor.LogicVersion != version.LogicVersion)
            throw new InvalidOperationException($"stored logic v{version.LogicVersion} but code has v{strategy.Descriptor.LogicVersion}; check out commit {stored.CodeCommit}");

        var universe = JsonSerializer.Deserialize<UniverseDefinition>(stored.UniverseJson)!;
        var settings = JsonSerializer.Deserialize<RunSettings>(stored.RiskLimitsJson)!;
        var costs = JsonSerializer.Deserialize<CostModel>(stored.CostModelJson)!;
        var cfg = new BacktestConfig
        {
            Start = settings.RequestedStart, End = settings.RequestedEnd, InitialCapital = stored.InitialCapital,
            Costs = costs, Risk = settings.Risk, DiscontinuityCooldownSessions = settings.DiscontinuityCooldownSessions,
            LiquidityLookback = settings.LiquidityLookback,
        };
        var data = await LoadDataAsync(universe, cfg.Start, cfg.End, ct, strategy.UsesCorporateEvents, strategy.UsesFundamentals);
        var result = new BacktestEngine().Run(strategy, data, cfg);
        var newResult = result.ResultHash();
        var parametersMatch = BacktestRecorder.Sha256(strategy.Descriptor.ParametersJson) == version.ParametersHash;

        string? firstDiff = null;
        if (newResult != stored.ResultHash)
        {
            List<BacktestTrade> storedTrades;
            await using (var db = DbFactory())
                storedTrades = await db.BacktestTrades.AsNoTracking().Where(t => t.RunId == runId).OrderBy(t => t.Id).ToListAsync(ct);
            for (var i = 0; i < Math.Max(storedTrades.Count, result.Trades.Count); i++)
            {
                var a = i < storedTrades.Count ? storedTrades[i] : null;
                var b = i < result.Trades.Count ? result.Trades[i] : null;
                if (a is null || b is null || a.Ticker != b.Ticker || a.EntryDate != b.EntryDate || a.ExitDate != b.ExitDate || a.Quantity != b.Quantity)
                {
                    firstDiff = $"trade #{i}: stored {a?.Ticker} {a?.EntryDate}->{a?.ExitDate} q{a?.Quantity} | now {b?.Ticker} {b?.EntryDate}->{b?.ExitDate} q{b?.Quantity}";
                    break;
                }
            }
            firstDiff ??= "trades identical; equity differs";
        }
        return new RerunReport(runId, result.DataHash == stored.DataHash, newResult == stored.ResultHash,
            stored.DataHash, result.DataHash, stored.ResultHash, newResult, parametersMatch, firstDiff);
    }
}
