using System.Text.Json;
using Investment.Backtest;
using Investment.Domain.Market;
using Investment.Domain.Research;
using Investment.Domain.Strategies;
using Investment.Domain.Trading;
using Investment.MarketData.Universe;
using Investment.Persistence;
using Investment.Research;
using Investment.Risk;
using Investment.Strategies;
using Microsoft.EntityFrameworkCore;

namespace Investment.PaperTrading;

public sealed record PaperStartRequest(
    string Name,
    string StrategyId,
    Guid? StrategyVersionId,
    DateOnly? StartDate,
    decimal Capital,
    UniverseDefinition Universe,
    CostModel Costs,
    RiskLimits Risk,
    int MinRegimeTrades = 20,
    BookLimits? Book = null);

/// <summary>
/// Capital book shared by all paper sessions: no strategy may hold more than <see cref="MaxStrategyWeight"/> of the
/// book, and a combined drawdown beyond <see cref="MaxBookDrawdown"/> stops every session.
/// </summary>
public sealed record BookLimits
{
    public decimal BookCapital { get; init; } = 200_000_000m;
    public double MaxStrategyWeight { get; init; } = 0.5;
    public double MaxBookDrawdown { get; init; } = 0.15;
}

public sealed record PaperDailyResult(Guid SessionId, string Name, int SessionsProcessed, DateOnly? From, DateOnly? To, int NewOrders, int NewTrades, decimal Equity, string? Regime, bool RegimeBlocked, GateResult? Gate);

/// <summary>Buys are suppressed while the market regime is one the validated evidence does not support.</summary>
public sealed class RegimeGuardStrategy(IStrategy inner, IReadOnlySet<string> blocked) : IStrategy
{
    public StrategyDescriptor Descriptor => inner.Descriptor;
    public int WarmupBars => inner.WarmupBars;
    public bool UsesCorporateEvents => inner.UsesCorporateEvents;
    public int SuppressedBuys { get; private set; }

    public IReadOnlyList<Signal> GenerateSignals(StrategyContext context)
    {
        var signals = inner.GenerateSignals(context);
        var regime = context.MarketIndex is { } idx ? RegimeClassifier.Classify(idx)?.ToString() : null;
        if (regime is null || !blocked.Contains(regime)) return signals;
        SuppressedBuys += signals.Count(s => s.Action == SignalAction.Buy);
        return signals.Where(s => s.Action != SignalAction.Buy).ToList();
    }
}

public sealed class PaperTradingService(string connectionString, Action<string>? log = null)
{
    private readonly Action<string> _log = log ?? (_ => { });
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static readonly string[] AllRegimes =
        [.. from t in Enum.GetValues<TrendRegime>() from v in Enum.GetValues<VolatilityRegime>() select new RegimeLabel(t, v).ToString()];

    private InvestmentDbContext Db() => Database.Create(connectionString);

    /// <summary>
    /// Opens a forward paper session. Only VALIDATED versions may start (Promotion Gate). Regimes with negative or
    /// insufficient validated evidence are blocked for new entries ("stop on regime change").
    /// </summary>
    public async Task<PaperSession> StartAsync(PaperStartRequest req, CancellationToken ct = default)
    {
        await using var db = Db();
        var version = req.StrategyVersionId is { } vid
            ? await db.StrategyVersions.SingleAsync(v => v.Id == vid, ct)
            : await db.StrategyVersions.Where(v => v.StrategyId == req.StrategyId && v.Status == StrategyStatus.Validated)
                .OrderByDescending(v => v.Version).FirstOrDefaultAsync(ct)
              ?? throw new InvalidOperationException($"no VALIDATED version of {req.StrategyId}; run a walk-forward study first");
        if (version.Status != StrategyStatus.Validated)
            throw new InvalidOperationException($"{version.StrategyId} v{version.Version} is {version.Status}; only VALIDATED versions can start paper trading");

        var evaluation = await db.StrategyEvaluations
            .Where(e => e.StrategyVersionId == version.Id && e.Decision == GateDecision.Promote && e.ToStatus == StrategyStatus.Validated)
            .OrderByDescending(e => e.EvaluatedAt).FirstAsync(ct);
        var evidence = JsonSerializer.Deserialize<OosEvidence>(evaluation.EvidenceJson)!;
        var blocked = AllRegimes.Where(r =>
            !evidence.ByRegime.TryGetValue(r, out var s) || s.Trades < req.MinRegimeTrades || s.NetEvPerTrade <= 0).ToList();

        var lastData = await db.IndexPrices.Where(p => p.IndexCode == "KOSPI").MaxAsync(p => (DateOnly?)p.Date, ct)
                       ?? throw new InvalidOperationException("no market data");
        var start = req.StartDate ?? lastData;
        if (start < lastData)
            throw new InvalidOperationException($"paper sessions cannot start in the past ({start:yyyy-MM-dd} < latest data {lastData:yyyy-MM-dd}); that would be a backtest");

        var book = req.Book ?? new BookLimits();
        var allocated = await db.PaperSessions.Where(x => x.Status == PaperSessionStatus.Active).SumAsync(x => (decimal?)x.InitialCapital, ct) ?? 0;
        if (req.Capital > book.BookCapital * (decimal)book.MaxStrategyWeight)
            throw new InvalidOperationException($"capital {req.Capital:N0} exceeds {book.MaxStrategyWeight:P0} of the book {book.BookCapital:N0} (one strategy may not dominate)");
        if (allocated + req.Capital > book.BookCapital)
            throw new InvalidOperationException($"book {book.BookCapital:N0} already has {allocated:N0} allocated; {req.Capital:N0} does not fit");
        if (await db.PaperSessions.AnyAsync(x => x.Status == PaperSessionStatus.Active && x.StrategyVersionId == version.Id, ct))
            throw new InvalidOperationException($"{version.StrategyId} v{version.Version} already has an active paper session");

        var now = DateTimeOffset.UtcNow;
        var session = new PaperSession
        {
            Id = Guid.NewGuid(), Name = req.Name, StrategyId = version.StrategyId, StrategyVersionId = version.Id,
            ParametersJson = version.ParametersJson, Status = PaperSessionStatus.Active, StartDate = start,
            InitialCapital = req.Capital, UniverseJson = JsonSerializer.Serialize(req.Universe, Json),
            CostModelJson = JsonSerializer.Serialize(req.Costs, Json), RiskLimitsJson = JsonSerializer.Serialize(req.Risk, Json),
            StateJson = SimulationState.Initial(start, req.Capital).ToJson(),
            ExpectedEvPerTrade = evidence.NetEvPerTrade, BlockedRegimesJson = JsonSerializer.Serialize(blocked, Json),
            EvidenceEvaluationId = evaluation.Id, CreatedAt = now, UpdatedAt = now,
        };
        db.PaperSessions.Add(session);
        db.StrategyEvaluations.Add(new StrategyEvaluation
        {
            Id = Guid.NewGuid(), StrategyVersionId = version.Id, Stage = "paper-start", FromStatus = version.Status,
            ToStatus = StrategyStatus.Paper, Decision = GateDecision.Promote, EvidenceJson = evaluation.EvidenceJson,
            CriteriaJson = JsonSerializer.Serialize(new { req.MinRegimeTrades, blocked }), EvaluatedAt = now,
            Reasons = $"validated by evaluation {evaluation.Id}; paper session {session.Id} from {start:yyyy-MM-dd}; entries paused in {string.Join(", ", blocked)}",
        });
        version.Status = StrategyStatus.Paper;
        await db.SaveChangesAsync(ct);
        await db.RollupStrategyStatusAsync(version.StrategyId, ct);
        return session;
    }

    private sealed class Recorder(PaperSession session, Simulator? sim) : ISimulationObserver
    {
        public Simulator? Sim { get; set; } = sim;
        public List<PaperOrder> NewOrders { get; } = [];
        public List<PaperTrade> NewTrades { get; } = [];
        public List<PaperEquityPoint> Equity { get; } = [];
        public List<(DateOnly Date, OrderState Order, decimal Fill, long Qty)> Fills { get; } = [];
        public List<RiskEvent> Events { get; } = [];
        public Dictionary<(string, DateOnly), decimal> BuySignalPrices { get; } = new();

        public void OnOrder(DateOnly asOf, OrderState o, double signalPrice, string? regime)
        {
            NewOrders.Add(new PaperOrder
            {
                SessionId = session.Id, Ticker = o.Ticker, Side = o.IsBuy ? "Buy" : "Sell", Quantity = o.Quantity,
                SignalDate = asOf, SignalPrice = (decimal)signalPrice, Reason = Trim(o.Reason), Score = o.Score,
                MarketCondition = regime, ExpectedReturn = o.IsBuy ? session.ExpectedEvPerTrade : 0, Status = "Pending",
                RecordedAt = DateTimeOffset.UtcNow,
            });
            if (o.IsBuy) BuySignalPrices[(o.Ticker, asOf)] = (decimal)signalPrice;
        }

        public void OnFill(DateOnly date, OrderState order, decimal fill, long quantity) => Fills.Add((date, order, fill, quantity));
        public void OnRiskEvent(RiskEvent e) => Events.Add(e);
        public void OnEquity(EquityPoint p) => Equity.Add(new PaperEquityPoint
        {
            SessionId = session.Id, Date = p.Date, NetEquity = p.NetEquity, GrossEquity = p.GrossEquity, Cash = p.Cash,
            Positions = p.Positions, Regime = Sim?.RegimeAt(p.Date),
        });

        public void OnTrade(TradeRecord t)
        {
            var risk = JsonSerializer.Deserialize<RiskLimits>(session.RiskLimitsJson)!;
            NewTrades.Add(new PaperTrade
            {
                SessionId = session.Id, StrategyId = session.StrategyId, Ticker = t.Ticker,
                SignalTime = t.EntrySignalDate, SignalPrice = BuySignalPrices.GetValueOrDefault((t.Ticker, t.EntrySignalDate)),
                EntryTime = t.EntryDate, EntryPrice = t.EntryPrice, ExitTime = t.ExitDate, ExitPrice = t.ExitPrice, Quantity = t.Quantity,
                ExpectedReturn = session.ExpectedEvPerTrade, ActualReturn = t.NetReturn, GrossReturn = t.GrossReturn,
                StopLoss = risk.StopLoss is { } sl ? t.EntryPrice * (1 - (decimal)sl) : null,
                TakeProfit = risk.TakeProfit is { } tp ? t.EntryPrice * (1 + (decimal)tp) : null,
                MarketCondition = Sim?.RegimeAt(t.EntrySignalDate), Reason = Trim(t.EntryReason), ExitReason = Trim(t.ExitReason),
                Result = t.NetPnl > 0 ? "Win" : t.NetPnl < 0 ? "Loss" : "Flat", Costs = t.Costs, NetPnl = t.NetPnl,
                HoldingSessions = t.HoldingSessions, RecordedAt = DateTimeOffset.UtcNow,
            });
        }

        private static string Trim(string s) => s.Length <= 500 ? s : s[..500];
    }

    /// <summary>Advances every active session through all new sessions available in the database.</summary>
    public async Task<IReadOnlyList<PaperDailyResult>> RunDailyAsync(CancellationToken ct = default)
    {
        List<PaperSession> sessions;
        await using (var db = Db())
            sessions = await db.PaperSessions.Where(s => s.Status == PaperSessionStatus.Active).OrderBy(s => s.CreatedAt).ToListAsync(ct);
        var results = new List<PaperDailyResult>();
        foreach (var s in sessions) results.Add(await RunSessionAsync(s.Id, ct));
        await EnforceBookDrawdownAsync(new BookLimits(), ct);
        return results;
    }

    /// <summary>
    /// Portfolio-level stop: combined equity of all active sessions vs its peak. Individual sessions have their own
    /// risk limits; this catches correlated losses across strategies.
    /// </summary>
    public async Task<double> EnforceBookDrawdownAsync(BookLimits limits, CancellationToken ct = default)
    {
        await using var db = Db();
        var active = await db.PaperSessions.Where(s => s.Status == PaperSessionStatus.Active).ToListAsync(ct);
        if (active.Count == 0) return 0;
        var ids = active.Select(s => s.Id).ToList();
        var points = await db.PaperEquity.Where(e => ids.Contains(e.SessionId)).ToListAsync(ct);
        var capital = active.ToDictionary(s => s.Id, s => s.InitialCapital);
        var last = new Dictionary<Guid, decimal>(capital);
        double peak = (double)capital.Values.Sum(), dd = 0;
        foreach (var day in points.GroupBy(p => p.Date).OrderBy(g => g.Key))
        {
            foreach (var p in day) last[p.SessionId] = p.NetEquity;
            var total = (double)last.Values.Sum();
            peak = Math.Max(peak, total);
            dd = 1 - total / peak;
        }
        if (dd >= limits.MaxBookDrawdown)
        {
            foreach (var s in active)
            {
                s.Status = PaperSessionStatus.Stopped;
                s.StoppedReason = $"book drawdown {dd:P1} >= {limits.MaxBookDrawdown:P0}";
                s.UpdatedAt = DateTimeOffset.UtcNow;
            }
            await db.SaveChangesAsync(ct);
            _log($"BOOK STOP: combined paper drawdown {dd:P1}; all sessions stopped");
        }
        return dd;
    }

    public async Task<PaperDailyResult> RunSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        PaperSession session;
        DateOnly lastData;
        await using (var db = Db())
        {
            session = await db.PaperSessions.AsNoTracking().SingleAsync(s => s.Id == sessionId, ct);
            lastData = await db.IndexPrices.Where(p => p.IndexCode == "KOSPI").MaxAsync(p => p.Date, ct);
        }
        var universe = JsonSerializer.Deserialize<UniverseDefinition>(session.UniverseJson)!;
        var cfg = new BacktestConfig
        {
            Start = session.StartDate, End = lastData, InitialCapital = session.InitialCapital,
            Costs = JsonSerializer.Deserialize<CostModel>(session.CostModelJson)!,
            Risk = JsonSerializer.Deserialize<RiskLimits>(session.RiskLimitsJson)!,
        };
        var state = SimulationState.FromJson(session.StateJson);
        var blocked = JsonSerializer.Deserialize<List<string>>(session.BlockedRegimesJson)!.ToHashSet();
        var guard = new RegimeGuardStrategy(StrategyCatalog.Create(session.StrategyId, session.ParametersJson), blocked);

        if (lastData < session.StartDate || state.LastProcessed == lastData)
            return new PaperDailyResult(session.Id, session.Name, 0, null, null, 0, 0, state.PrevNetEquity, null, false, null);

        var data = await new DataSetLoader(connectionString).LoadAsync(universe, session.StartDate, lastData, includeEvents: guard.UsesCorporateEvents, ct: ct);
        var recorder = new Recorder(session, null);
        // orders decided in earlier daily runs: their signal prices belong to trades that close in this run
        await using (var db = Db())
            foreach (var o in await db.PaperOrders.AsNoTracking().Where(o => o.SessionId == session.Id && o.Side == "Buy").ToListAsync(ct))
                recorder.BuySignalPrices[(o.Ticker, o.SignalDate)] = o.SignalPrice;
        var sim = new Simulator(guard, data, cfg, state, recorder);
        recorder.Sim = sim;

        var cal = data.Calendar;
        DateOnly? from = null, to = null;
        var processed = 0;
        for (var i = 0; i < cal.Count; i++)
        {
            if (cal[i] < session.StartDate || (state.LastProcessed is { } lp && cal[i] <= lp)) continue;
            sim.Step(i, isLast: false);
            from ??= cal[i];
            to = cal[i];
            processed++;
        }

        var code = CodeVersion.Detect();
        var regime = sim.RegimeAt(lastData);
        await using (var db = Db())
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // previously pending orders: buys expire after their fill session, sells stay pending until executed
            var pendingOrders = await db.PaperOrders.Where(o => o.SessionId == session.Id && o.Status == "Pending").ToListAsync(ct);
            db.PaperOrders.AddRange(recorder.NewOrders);
            var allOrders = pendingOrders.Concat(recorder.NewOrders).ToList();
            foreach (var f in recorder.Fills)
            {
                var o = allOrders.FirstOrDefault(x => x.Status == "Pending" && x.Ticker == f.Order.Ticker && x.SignalDate == f.Order.SignalDate && x.Side == (f.Order.IsBuy ? "Buy" : "Sell"));
                if (o is null) continue;
                o.Status = "Filled"; o.FillDate = f.Date; o.FillPrice = f.Fill; o.FilledQuantity = f.Qty;
            }
            var stillPendingSells = state.Pending.Where(p => !p.IsBuy).Select(p => (p.Ticker, p.SignalDate)).ToHashSet();
            var stillPendingBuys = state.Pending.Where(p => p.IsBuy).Select(p => (p.Ticker, p.SignalDate)).ToHashSet();
            foreach (var o in allOrders.Where(o => o.Status == "Pending"))
            {
                if (o.Side == "Buy" && !stillPendingBuys.Contains((o.Ticker, o.SignalDate))) o.Status = "NotFilled";
                if (o.Side == "Sell" && !stillPendingSells.Contains((o.Ticker, o.SignalDate))) o.Status = "Superseded";
            }
            db.PaperTrades.AddRange(recorder.NewTrades);
            db.PaperEquity.AddRange(recorder.Equity);
            db.PaperRuns.Add(new PaperRunLog
            {
                SessionId = session.Id, RunAt = DateTimeOffset.UtcNow, ProcessedFrom = from, ProcessedTo = to, SessionsProcessed = processed,
                DataHash = data.ComputeHash(), CodeCommit = code.Commit + (code.Dirty ? "+dirty" : ""),
                Notes = $"regime {regime}{(blocked.Contains(regime ?? "") ? " (entries paused)" : "")}; suppressed buys {guard.SuppressedBuys}" +
                        (recorder.Events.Count > 0 ? "; risk: " + string.Join(", ", recorder.Events.Select(e => $"{e.Date:MM-dd} {e.Kind}")) : ""),
            });
            var tracked = await db.PaperSessions.SingleAsync(s => s.Id == session.Id, ct);
            tracked.StateJson = state.ToJson();
            tracked.LastProcessedDate = state.LastProcessed;
            tracked.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        var gate = await EvaluateAsync(session.Id, ct);
        _log($"paper {session.Name}: processed {processed} session(s) {from:yyyy-MM-dd}..{to:yyyy-MM-dd}, {recorder.NewOrders.Count} orders, {recorder.NewTrades.Count} closed trades, regime {regime}");
        return new PaperDailyResult(session.Id, session.Name, processed, from, to, recorder.NewOrders.Count, recorder.NewTrades.Count,
            state.PrevNetEquity, regime, regime is not null && blocked.Contains(regime), gate);
    }

    /// <summary>PAPER → APPROVED / hold / DISABLED from accumulated paper evidence.</summary>
    public async Task<GateResult> EvaluateAsync(Guid sessionId, CancellationToken ct = default)
    {
        await using var db = Db();
        var session = await db.PaperSessions.SingleAsync(s => s.Id == sessionId, ct);
        var version = await db.StrategyVersions.SingleAsync(v => v.Id == session.StrategyVersionId, ct);
        var trades = await db.PaperTrades.Where(t => t.SessionId == sessionId).Select(t => t.ActualReturn).ToListAsync(ct);
        var equity = await db.PaperEquity.Where(e => e.SessionId == sessionId).OrderBy(e => e.Date).Select(e => (double)e.NetEquity).ToListAsync(ct);
        var state = SimulationState.FromJson(session.StateJson);
        var sd = Metrics.Stdev(trades);
        var evidence = new PaperEvidence(
            equity.Count, trades.Count, trades.Count == 0 ? 0 : trades.Average(),
            trades.Count > 1 && sd > 0 ? trades.Average() / (sd / Math.Sqrt(trades.Count)) : 0,
            Metrics.MaxDrawdown((double)session.InitialCapital, equity), session.ExpectedEvPerTrade, state.HaltedOn is not null);
        var gate = PaperGate.Evaluate(version.Status, evidence, new PaperCriteria());
        if (gate.To != version.Status)
        {
            var now = DateTimeOffset.UtcNow;
            db.StrategyEvaluations.Add(new StrategyEvaluation
            {
                Id = Guid.NewGuid(), StrategyVersionId = version.Id, Stage = "paper", FromStatus = version.Status, ToStatus = gate.To,
                Decision = gate.Decision, EvidenceJson = JsonSerializer.Serialize(evidence), CriteriaJson = JsonSerializer.Serialize(new PaperCriteria()),
                Reasons = string.Join("; ", gate.Reasons), EvaluatedAt = now,
            });
            version.Status = gate.To;
            if (gate.Decision == GateDecision.Disable)
            {
                session.Status = PaperSessionStatus.Disabled;
                session.StoppedReason = string.Join("; ", gate.Reasons);
            }
            await db.SaveChangesAsync(ct);
            await db.RollupStrategyStatusAsync(version.StrategyId, ct);
        }
        return gate;
    }
}

public sealed record PaperEvidence(int Sessions, int Trades, double NetEvPerTrade, double NetEvTStat, double MaxDrawdown, double ExpectedEvPerTrade, bool RiskHalted);

public sealed record PaperCriteria
{
    public int MinSessions { get; init; } = 60;
    public int MinTrades { get; init; } = 30;
    public double MaxDrawdown { get; init; } = 0.20;
    public double CollapseTStat { get; init; } = -2.0;
    public int CollapseMinTrades { get; init; } = 20;

    /// <summary>Paper EV must reach at least this fraction of the validated expectation to be approved.</summary>
    public double MinRealizationRatio { get; init; } = 0.5;
}

/// <summary>Paper results outrank backtests: collapse disables the strategy; approval still never means live trading.</summary>
public static class PaperGate
{
    public static GateResult Evaluate(StrategyStatus current, PaperEvidence e, PaperCriteria c)
    {
        if (current is not (StrategyStatus.Paper or StrategyStatus.Approved))
            return new(GateDecision.Hold, current, [$"status {current} is not in paper stage"]);
        var disable = new List<string>();
        if (e.RiskHalted) disable.Add("risk engine halted the session");
        if (e.MaxDrawdown > c.MaxDrawdown) disable.Add($"paper MDD {e.MaxDrawdown:P1} > {c.MaxDrawdown:P0}");
        if (e.Trades >= c.CollapseMinTrades && e.NetEvTStat <= c.CollapseTStat) disable.Add($"paper EV collapsed (t={e.NetEvTStat:F2})");
        if (disable.Count > 0) return new(GateDecision.Disable, StrategyStatus.Disabled, disable);

        var hold = new List<string>();
        if (e.Sessions < c.MinSessions) hold.Add($"paper sessions {e.Sessions} < {c.MinSessions}");
        if (e.Trades < c.MinTrades) hold.Add($"paper trades {e.Trades} < {c.MinTrades}");
        if (e.NetEvPerTrade <= 0) hold.Add($"paper EV {e.NetEvPerTrade:P3} <= 0");
        if (e.ExpectedEvPerTrade > 0 && e.NetEvPerTrade < c.MinRealizationRatio * e.ExpectedEvPerTrade)
            hold.Add($"paper EV {e.NetEvPerTrade:P3} < {c.MinRealizationRatio:P0} of expected {e.ExpectedEvPerTrade:P3}");
        if (hold.Count > 0) return new(GateDecision.Hold, current, hold);
        return new(GateDecision.Promote, StrategyStatus.Approved,
            [$"paper EV {e.NetEvPerTrade:P3} over {e.Trades} trades / {e.Sessions} sessions, MDD {e.MaxDrawdown:P1} (APPROVED ≠ live: live needs separate owner approval)"]);
    }
}
