using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Investment.Backtest;
using Investment.Domain.Research;
using Investment.Domain.Strategies;
using Investment.MarketData.Universe;
using Investment.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Investment.Research;

/// <summary>Persists backtest results with everything needed to re-run them.</summary>
public sealed class BacktestRecorder(Func<InvestmentDbContext> dbFactory)
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public async Task<StrategyVersion> EnsureVersionAsync(StrategyDescriptor d, CancellationToken ct = default)
    {
        await using var db = dbFactory();
        var now = DateTimeOffset.UtcNow;
        var def = await db.Strategies.FindAsync([d.Id], ct);
        if (def is null)
        {
            def = new StrategyDefinition
            {
                Id = d.Id, Name = d.Name, Family = d.Family, Hypothesis = d.Hypothesis,
                Status = StrategyStatus.Experimental, CreatedAt = now, UpdatedAt = now,
            };
            db.Strategies.Add(def);
        }

        var json = d.ParametersJson;
        var hash = Sha256(json);
        var existing = await db.StrategyVersions.SingleOrDefaultAsync(
            v => v.StrategyId == d.Id && v.LogicVersion == d.LogicVersion && v.ParametersHash == hash, ct);
        if (existing is not null)
        {
            await db.SaveChangesAsync(ct);
            return existing;
        }
        var next = (await db.StrategyVersions.Where(v => v.StrategyId == d.Id).MaxAsync(v => (int?)v.Version, ct) ?? 0) + 1;
        var version = new StrategyVersion
        {
            Id = Guid.NewGuid(), StrategyId = d.Id, Version = next, LogicVersion = d.LogicVersion,
            ParametersJson = json, ParametersHash = hash, Status = StrategyStatus.Experimental, CreatedAt = now,
        };
        db.StrategyVersions.Add(version);
        await db.SaveChangesAsync(ct);
        return version;
    }

    public async Task<BacktestRun> SaveAsync(
        BacktestResult result, UniverseDefinition universe, CodeVersion code, string runKind, string? label,
        Guid? parentRunId, double durationMs, CancellationToken ct = default)
    {
        var version = await EnsureVersionAsync(result.Strategy, ct);
        var run = new BacktestRun
        {
            Id = Guid.NewGuid(),
            StrategyVersionId = version.Id,
            RunKind = runKind,
            Label = label,
            ParentRunId = parentRunId,
            StartDate = result.Equity.Count > 0 ? result.Equity[0].Date : result.Config.Start,
            EndDate = result.Equity.Count > 0 ? result.Equity[^1].Date : result.Config.End,
            InitialCapital = result.Config.InitialCapital,
            UniverseJson = JsonSerializer.Serialize(universe, Json),
            UniverseHash = result.UniverseHash,
            CostModelJson = JsonSerializer.Serialize(result.Config.Costs, Json),
            RiskLimitsJson = JsonSerializer.Serialize(new RunSettings(result.Config.Risk, result.Config.DiscontinuityCooldownSessions, result.Config.LiquidityLookback, result.Config.Start, result.Config.End), Json),
            DataSource = result.DataSource,
            DataHash = result.DataHash,
            CodeCommit = code.Commit,
            CodeDirty = code.Dirty,
            EngineVersion = BacktestResult.EngineVersion,
            ResultHash = result.ResultHash(),
            HaltedOn = result.HaltedOn,
            HaltReason = result.HaltReason,
            CreatedAt = DateTimeOffset.UtcNow,
            DurationMs = durationMs,
        };
        foreach (var basis in new[] { CostBasis.Gross, CostBasis.Net })
        {
            var m = Metrics.Compute(result, basis);
            m.RunId = run.Id;
            m.ProfitFactor = double.IsInfinity(m.ProfitFactor) ? 999 : m.ProfitFactor;
            run.Metrics.Add(m);
        }
        run.Trades.AddRange(result.Trades.Select(t => new BacktestTrade
        {
            RunId = run.Id, Ticker = t.Ticker, EntrySignalDate = t.EntrySignalDate, EntryDate = t.EntryDate,
            EntryPrice = t.EntryPrice, EntryReferencePrice = t.EntryReferencePrice, ExitDate = t.ExitDate,
            ExitPrice = t.ExitPrice, ExitReferencePrice = t.ExitReferencePrice, Quantity = t.Quantity,
            GrossPnl = t.GrossPnl, Costs = t.Costs, NetPnl = t.NetPnl, GrossReturn = t.GrossReturn, NetReturn = t.NetReturn,
            HoldingSessions = t.HoldingSessions, EntryReason = Trim(t.EntryReason), ExitReason = Trim(t.ExitReason), EntryScore = t.EntryScore,
        }));
        run.Equity.AddRange(result.Equity.Select(e => new BacktestEquityPoint
        {
            RunId = run.Id, Date = e.Date, NetEquity = e.NetEquity, GrossEquity = e.GrossEquity, Cash = e.Cash, Positions = e.Positions,
        }));

        await using var db = dbFactory();
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        db.BacktestRuns.Add(run);
        await db.SaveChangesAsync(ct);

        await using var db2 = dbFactory();
        var v = await db2.StrategyVersions.Include(x => x.Strategy).SingleAsync(x => x.Id == version.Id, ct);
        if (v.Status == StrategyStatus.Experimental)
        {
            // evidence exists now; validation (M2) decides anything beyond BACKTESTED
            v.Status = StrategyStatus.Backtested;
            if (v.Strategy!.Status == StrategyStatus.Experimental) v.Strategy.Status = StrategyStatus.Backtested;
            v.Strategy.UpdatedAt = DateTimeOffset.UtcNow;
            await db2.SaveChangesAsync(ct);
        }
        return run;
    }

    private static string Trim(string s) => s.Length <= 500 ? s : s[..500];

    public static string Sha256(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}

/// <summary>Engine settings stored with each run (risk limits + engine knobs + requested period).</summary>
public sealed record RunSettings(Investment.Risk.RiskLimits Risk, int DiscontinuityCooldownSessions, int LiquidityLookback, DateOnly RequestedStart, DateOnly RequestedEnd);
