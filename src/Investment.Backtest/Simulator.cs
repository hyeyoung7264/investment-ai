using Investment.Domain.Market;
using Investment.Domain.Strategies;
using Investment.Risk;

namespace Investment.Backtest;

public sealed class PositionState
{
    public required string Ticker { get; init; }
    public long Quantity { get; set; }
    public DateOnly EntrySignalDate { get; init; }
    public DateOnly EntryDate { get; init; }
    public decimal EntryFill { get; init; }
    public decimal EntryRef { get; init; }
    public decimal EntryCosts { get; init; }
    public required string EntryReason { get; init; }
    public double Score { get; init; }
    public double LastClose { get; set; }
    public string? EntryRegime { get; init; }

    /// <summary>Closed at the close of the entry session (or the next tradable close).</summary>
    public bool DayTrade { get; init; }
}

public sealed record OrderState(string Ticker, bool IsBuy, long Quantity, DateOnly SignalDate, string Reason, double Score, bool DayTrade = false);

/// <summary>
/// Complete, serializable simulation state. A backtest keeps it in memory; paper trading persists it between
/// sessions, so both run exactly the same <see cref="Simulator.Step"/> code.
/// </summary>
public sealed class SimulationState
{
    public DateOnly StartDate { get; set; }
    public decimal InitialCapital { get; set; }
    public decimal Cash { get; set; }
    public decimal CumulativeCosts { get; set; }
    public decimal CostsAtPrevClose { get; set; }
    public decimal GrossEquity { get; set; }
    public decimal PrevNetEquity { get; set; }
    public double Peak { get; set; }
    public double PrevEquity { get; set; }
    public DateOnly? LastProcessed { get; set; }
    public DateOnly? HaltedOn { get; set; }
    public string? HaltReason { get; set; }
    public SortedDictionary<string, PositionState> Positions { get; set; } = new(StringComparer.Ordinal);
    public List<OrderState> Pending { get; set; } = [];

    private static readonly System.Text.Json.JsonSerializerOptions Json = new() { WriteIndented = false };

    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(this, Json);

    /// <summary>Restores state; ordinal ticker ordering is re-applied (deserialization loses the comparer).</summary>
    public static SimulationState FromJson(string json)
    {
        var s = System.Text.Json.JsonSerializer.Deserialize<SimulationState>(json, Json)!;
        s.Positions = new SortedDictionary<string, PositionState>(s.Positions, StringComparer.Ordinal);
        return s;
    }

    public static SimulationState Initial(DateOnly start, decimal capital)
    {
        var cash = capital + 0.00m; // normalize decimal scale (CLI vs DB-restored values)
        return new SimulationState
        {
            StartDate = start, InitialCapital = cash, Cash = cash, GrossEquity = cash, PrevNetEquity = cash,
            Peak = (double)capital, PrevEquity = (double)capital,
        };
    }
}

/// <summary>Receives everything the simulator decides, with the information known at that moment.</summary>
public interface ISimulationObserver
{
    void OnTrade(TradeRecord trade) { }
    void OnEquity(EquityPoint point) { }
    void OnRiskEvent(RiskEvent e) { }
    void OnRejection(string rule) { }
    void OnSignals(DateOnly asOf, int count) { }
    void OnOrder(DateOnly asOf, OrderState order, double signalPrice, string? regime) { }
    void OnFill(DateOnly date, OrderState order, decimal fill, long quantity) { }
}

/// <summary>
/// Daily event-driven simulator. For each session D:
///   1. open   — fill orders decided at the previous close (sells first, then buys) at D's open ± slippage
///   2. intraday — stop-loss / take-profit (gap → open price; stop wins if both touched)
///   3. close  — value positions, record equity, risk assessment (daily-loss block / drawdown halt)
///   4. after close — strategy sees data up to D only; risk engine sizes entries for D+1's open
/// Gross session return = (net equity + that session's costs) / previous net equity − 1, compounded — i.e.
/// identical positions with costs removed.
/// </summary>
public sealed class Simulator
{
    private sealed class TickerData(string ticker, Bar[] bars, Security? security, int[] discontinuitySessions, int sessions)
    {
        public string Ticker { get; } = ticker;
        public Bar[] Bars { get; } = bars;
        public Security? Security { get; } = security;
        public int[] DiscontinuitySessions { get; } = discontinuitySessions;
        public int[] BarOn { get; } = new int[sessions];
        public int[] Visible { get; } = new int[sessions];
    }

    private readonly IStrategy _strategy;
    private readonly MarketDataSet _data;
    private readonly BacktestConfig _cfg;
    private readonly SimulationState _s;
    private readonly ISimulationObserver _obs;
    private readonly Dictionary<string, TickerData> _tickers;
    private readonly Dictionary<DateOnly, int> _sessionOf;
    private readonly RiskEngine _risk;
    private readonly int _firstSession;

    public Simulator(IStrategy strategy, MarketDataSet data, BacktestConfig cfg, SimulationState state, ISimulationObserver observer)
    {
        _strategy = strategy;
        _data = data;
        _cfg = cfg;
        _s = state;
        _obs = observer;
        _risk = new RiskEngine(cfg.Risk);
        _sessionOf = data.Calendar.Select((d, i) => (d, i)).ToDictionary(x => x.d, x => x.i);
        _firstSession = FirstIndexOnOrAfter(data.Calendar, state.StartDate);
        _tickers = BuildTickerData(data, _sessionOf);
    }

    public SimulationState State => _s;

    public static int FirstIndexOnOrAfter(IReadOnlyList<DateOnly> cal, DateOnly d)
    {
        for (var i = 0; i < cal.Count; i++) if (cal[i] >= d) return i;
        return -1;
    }

    public static int LastIndexOnOrBefore(IReadOnlyList<DateOnly> cal, DateOnly d)
    {
        for (var i = cal.Count - 1; i >= 0; i--) if (cal[i] <= d) return i;
        return -1;
    }

    private int SessionOf(DateOnly d) => _sessionOf[d];

    /// <summary>Processes calendar session <paramref name="i"/>. <paramref name="isLast"/> liquidates everything at the close.</summary>
    public void Step(int i, bool isLast)
    {
        var cal = _data.Calendar;
        var date = cal[i];
        var costs = _cfg.Costs;
        var positions = _s.Positions;
        var pending = _s.Pending;

        // ---------- 1. open: fills ----------
        foreach (var o in pending.Where(o => !o.IsBuy).OrderBy(o => o.Ticker, StringComparer.Ordinal).ToList())
        {
            if (!positions.TryGetValue(o.Ticker, out var p)) { pending.Remove(o); continue; }
            var t = _tickers[o.Ticker];
            var bi = t.BarOn[i];
            if (bi < 0 || t.Bars[bi].IsHalted) continue; // retry next session (handled at close if data ended)
            var bar = t.Bars[bi];
            if (bi > 0 && KrxRules.IsLockedLimitDown(bar, t.Bars[bi - 1].Close)) { _obs.OnRejection("sell-locked-limit-down"); continue; }
            if (t.Security is not null && _data.Discontinuities.TryGetValue(o.Ticker, out var dset) && dset.Contains(date))
            {
                ClosePosition(p, i, (decimal)p.LastClose, SellFill(t, i, p.LastClose, p.Quantity), "DataDiscontinuity");
                pending.Remove(o);
                continue;
            }
            var fill = SellFill(t, i, bar.Open, p.Quantity);
            _obs.OnFill(date, o, fill, p.Quantity);
            ClosePosition(p, i, (decimal)bar.Open, fill, o.Reason);
            pending.Remove(o);
        }
        var buys = pending.Where(o => o.IsBuy).ToList();
        pending.RemoveAll(o => o.IsBuy); // buy orders live for one session only

        foreach (var o in buys.OrderByDescending(o => o.Score).ThenBy(o => o.Ticker, StringComparer.Ordinal))
        {
            var t = _tickers[o.Ticker];
            var bi = t.BarOn[i];
            if (bi < 0 || t.Bars[bi].IsHalted) { _obs.OnRejection("buy-not-trading"); continue; }
            var bar = t.Bars[bi];
            if (IsDiscontinuity(o.Ticker, date)) { _obs.OnRejection("buy-discontinuity"); continue; }
            if (bi > 0 && KrxRules.IsLockedLimitUp(bar, t.Bars[bi - 1].Close)) { _obs.OnRejection("buy-locked-limit-up"); continue; }
            var median = MedianValue(t, Math.Max(0, t.Visible[i] - 1), _cfg.LiquidityLookback);
            var slip = costs.Slippage(bar.Open * o.Quantity, median);
            var fill = (decimal)(bar.Open * (1 + slip));
            var qty = o.Quantity;
            var perShare = fill * (1 + (decimal)costs.CommissionRate);
            if (perShare * qty > _s.Cash) qty = (long)Math.Floor(_s.Cash / perShare);
            if (qty <= 0) { _obs.OnRejection("buy-cash"); continue; }
            var notional = fill * qty;
            var commission = notional * (decimal)costs.CommissionRate;
            var slipCost = (fill - (decimal)bar.Open) * qty;
            _s.Cash -= notional + commission;
            _s.CumulativeCosts += commission + slipCost;
            positions[o.Ticker] = new PositionState
            {
                Ticker = o.Ticker, Quantity = qty, EntrySignalDate = o.SignalDate, EntryDate = date,
                EntryFill = fill, EntryRef = (decimal)bar.Open, EntryCosts = commission + slipCost,
                EntryReason = o.Reason, Score = o.Score, LastClose = bar.Open,
                EntryRegime = RegimeAt(o.SignalDate),
                DayTrade = o.DayTrade,
            };
            _obs.OnFill(date, o, fill, qty);
        }

        // ---------- 2. intraday: data discontinuities, stops, targets ----------
        foreach (var p in positions.Values.ToList())
        {
            var t = _tickers[p.Ticker];
            var bi = t.BarOn[i];
            if (bi < 0 || t.Bars[bi].IsHalted) continue;
            var bar = t.Bars[bi];
            var entrySession = SessionOf(p.EntryDate);
            if (IsDiscontinuity(p.Ticker, date) && entrySession < i)
            {
                // the jump is not a tradable return: exit at the last valid price
                ClosePosition(p, i, (decimal)p.LastClose, SellFill(t, i, p.LastClose, p.Quantity), "DataDiscontinuity");
                continue;
            }
            if (bi > 0 && KrxRules.IsLockedLimitDown(bar, t.Bars[bi - 1].Close)) continue;
            var enteredToday = entrySession == i;
            var entry = (double)p.EntryFill;
            if (_cfg.Risk.StopLoss is { } sl)
            {
                var stop = entry * (1 - sl);
                double? px = !enteredToday && bar.Open <= stop ? bar.Open : bar.Low <= stop ? stop : null;
                if (px is { } s) { ClosePosition(p, i, (decimal)s, SellFill(t, i, s, p.Quantity), "StopLoss"); continue; }
            }
            if (_cfg.Risk.TakeProfit is { } tp)
            {
                var target = entry * (1 + tp);
                double? px = !enteredToday && bar.Open >= target ? bar.Open : bar.High >= target ? target : null;
                if (px is { } x) ClosePosition(p, i, (decimal)x, SellFill(t, i, x, p.Quantity), "TakeProfit");
            }
        }

        // ---------- 3. close: valuation, data end, risk ----------
        foreach (var p in positions.Values.ToList())
        {
            var t = _tickers[p.Ticker];
            var bi = t.BarOn[i];
            if (bi >= 0) { p.LastClose = t.Bars[bi].Close; continue; }
            if (t.Bars.Length > 0 && t.Bars[^1].Date < date)
            {
                // delisted / data ended: the last traded close (incl. liquidation trading) is the exit value
                ClosePosition(p, i, (decimal)p.LastClose, SellFill(t, i, p.LastClose, p.Quantity),
                    t.Security?.DelistedDate is not null ? "Delisted" : "DataEnd");
                pending.RemoveAll(o => o.Ticker == p.Ticker);
            }
        }

        // day trades: exit at the session close (not possible when halted, missing or locked limit-down)
        foreach (var p in positions.Values.Where(p => p.DayTrade).ToList())
        {
            var t = _tickers[p.Ticker];
            var bi = t.BarOn[i];
            if (bi < 0 || t.Bars[bi].IsHalted) continue;
            var bar = t.Bars[bi];
            if (bi > 0 && KrxRules.IsLockedLimitDown(bar, t.Bars[bi - 1].Close)) continue;
            ClosePosition(p, i, (decimal)bar.Close, SellFill(t, i, bar.Close, p.Quantity), "SessionClose");
            pending.RemoveAll(o => o.Ticker == p.Ticker && !o.IsBuy);
        }

        if (isLast)
        {
            foreach (var p in positions.Values.ToList())
                ClosePosition(p, i, (decimal)p.LastClose, SellFill(_tickers[p.Ticker], i, p.LastClose, p.Quantity), "EndOfTest");
            pending.Clear();
        }

        var invested = positions.Values.Sum(p => (decimal)p.LastClose * p.Quantity);
        var netEquity = _s.Cash + invested;
        var sessionCosts = _s.CumulativeCosts - _s.CostsAtPrevClose;
        _s.CostsAtPrevClose = _s.CumulativeCosts;
        if (_s.PrevNetEquity > 0) _s.GrossEquity *= (netEquity + sessionCosts) / _s.PrevNetEquity;
        _s.PrevNetEquity = netEquity;
        _obs.OnEquity(new EquityPoint(date, netEquity, _s.GrossEquity, _s.Cash, positions.Count, invested));
        var eq = (double)netEquity;
        _s.Peak = Math.Max(_s.Peak, eq);
        var sessionReturn = _s.PrevEquity > 0 ? eq / _s.PrevEquity - 1 : 0;
        _s.PrevEquity = eq;
        _s.LastProcessed = date;
        if (isLast || _s.HaltedOn is not null) return;

        var snapshot = new PortfolioSnapshot(eq, (double)_s.Cash, _s.Peak, sessionReturn,
            positions.Values.Select(p => new PositionExposure(p.Ticker, p.LastClose * p.Quantity, _tickers[p.Ticker].Security?.Sector)).ToList());
        var assessment = _risk.Assess(snapshot);
        var entriesBlocked = assessment.State == RiskState.EntriesBlocked;
        if (assessment.State == RiskState.Halted)
        {
            _s.HaltedOn = date;
            _s.HaltReason = assessment.Reason;
            _obs.OnRiskEvent(new RiskEvent(date, "Halt", assessment.Reason!));
            pending.Clear();
            foreach (var p in positions.Values)
            {
                var o = new OrderState(p.Ticker, false, p.Quantity, date, "RiskHalt", 0);
                pending.Add(o);
                _obs.OnOrder(date, o, p.LastClose, RegimeAt(date));
            }
            return;
        }
        if (entriesBlocked) _obs.OnRiskEvent(new RiskEvent(date, "EntriesBlocked", assessment.Reason!));

        // ---------- 4. after close: signals for next session ----------
        var universe = _data.Universe.MembersOn(date).Where(tk => IsEnterable(tk, i)).ToList();
        var held = positions.Values.ToDictionary(p => p.Ticker,
            p => new HeldPosition(p.Ticker, p.EntryDate, (double)p.EntryFill, i - SessionOf(p.EntryDate), p.EntryReason), StringComparer.Ordinal);
        var ctx = new StrategyContext(date, universe,
            tk => _tickers.TryGetValue(tk, out var td) ? new BarSeries(tk, td.Bars, td.Visible[i]) : null,
            new BarSeries(_data.IndexCode, _data.Index, VisibleIndex(_data.Index, date)),
            held,
            _data.Events is { } ev ? tk => ev.TryGetValue(tk, out var list) ? list : [] : null,
            _data.Fundamentals is { } fu ? tk => fu.TryGetValue(tk, out var f) ? f : null : null,
            _data.AuxIndices is { } aux ? code => aux.TryGetValue(code, out var b) ? b : null : null);
        var signals = _strategy.GenerateSignals(ctx);
        _obs.OnSignals(date, signals.Count);
        var regime = RegimeAt(date);

        var exiting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in positions.Values)
        {
            var sig = signals.FirstOrDefault(s => s.Ticker == p.Ticker && s.Action == SignalAction.Sell);
            string? reason = sig?.Reason;
            if (reason is null && _cfg.Risk.MaxHoldingSessions is { } mh && i - SessionOf(p.EntryDate) >= mh) reason = "MaxHolding";
            if (reason is null) continue;
            exiting.Add(p.Ticker);
            if (!pending.Any(o => o.Ticker == p.Ticker && !o.IsBuy))
            {
                var o = new OrderState(p.Ticker, false, p.Quantity, date, reason, 0);
                pending.Add(o);
                _obs.OnOrder(date, o, p.LastClose, regime);
            }
        }
        foreach (var o in pending.Where(o => !o.IsBuy)) exiting.Add(o.Ticker);

        if (entriesBlocked) return;
        var universeSet = universe.ToHashSet(StringComparer.Ordinal);
        var candidates = new List<EntryCandidate>();
        foreach (var s in signals.Where(s => s.Action == SignalAction.Buy))
        {
            if (!universeSet.Contains(s.Ticker)) { _obs.OnRejection("signal-outside-universe"); continue; }
            if (positions.ContainsKey(s.Ticker)) continue;
            var t = _tickers[s.Ticker];
            var bar = t.Bars[t.BarOn[i]];
            candidates.Add(new EntryCandidate(s.Ticker, s.Score, bar.Close,
                MedianValue(t, t.Visible[i], _cfg.LiquidityLookback), t.Security?.Sector, s.Reason));
        }
        var alloc = _risk.Allocate(snapshot, candidates, exiting, costs.EntryBuffer);
        foreach (var r in alloc.Rejected) _obs.OnRejection(r.Rule);
        var dayTrade = signals.Where(s => s.Action == SignalAction.Buy && s.DayTrade).Select(s => s.Ticker).ToHashSet(StringComparer.Ordinal);
        foreach (var a in alloc.Approved)
        {
            var o = new OrderState(a.Ticker, true, a.Quantity, date, a.Reason, a.Score, dayTrade.Contains(a.Ticker));
            pending.Add(o);
            _obs.OnOrder(date, o, a.ReferencePrice, regime);
        }
    }

    private void ClosePosition(PositionState p, int session, decimal refPrice, decimal fill, string reason)
    {
        var d = _data.Calendar[session];
        var costs = _cfg.Costs;
        var notional = fill * p.Quantity;
        var commission = notional * (decimal)costs.CommissionRate;
        var tax = notional * (decimal)costs.SellTaxRate(d, p.Ticker);
        var slip = (refPrice - fill) * p.Quantity;
        var exitCosts = commission + tax + slip;
        _s.Cash += notional - commission - tax;
        _s.CumulativeCosts += exitCosts;
        var gross = (refPrice - p.EntryRef) * p.Quantity;
        var totalCosts = p.EntryCosts + exitCosts;
        _obs.OnTrade(new TradeRecord(p.Ticker, p.EntrySignalDate, p.EntryDate, p.EntryFill, p.EntryRef, d, fill, refPrice,
            p.Quantity, gross, totalCosts, gross - totalCosts, session - SessionOf(p.EntryDate), p.EntryReason, reason, p.Score));
        _s.Positions.Remove(p.Ticker);
    }

    private decimal SellFill(TickerData t, int session, double reference, long qty)
    {
        var slip = _cfg.Costs.Slippage(reference * qty, MedianValue(t, Math.Max(0, t.Visible[session] - 1), _cfg.LiquidityLookback));
        return (decimal)(reference * (1 - slip));
    }

    private bool IsDiscontinuity(string ticker, DateOnly date) =>
        _data.Discontinuities.TryGetValue(ticker, out var set) && set.Contains(date);

    /// <summary>
    /// Enterable = trading today, enough history, not within the cooldown after a data discontinuity observed
    /// since the simulation start, and not inside the publicly known delisting window.
    /// </summary>
    private bool IsEnterable(string ticker, int session)
    {
        if (!_tickers.TryGetValue(ticker, out var t)) return false;
        var bi = t.BarOn[session];
        if (bi < 0 || t.Bars[bi].IsHalted) return false;
        if (t.Visible[session] < _strategy.WarmupBars) return false;
        foreach (var k in t.DiscontinuitySessions)
            if (k >= _firstSession && k <= session && session <= k + _cfg.DiscontinuityCooldownSessions) return false;
        if (t.Security?.DelistedDate is { } dd && dd.DayNumber - t.Bars[bi].Date.DayNumber <= KrxRules.KnownDelistingWindowDays)
            return false;
        return true;
    }

    private readonly Dictionary<DateOnly, string?> _regimeCache = new();

    /// <summary>Benchmark regime known at the close of <paramref name="date"/> (evidence for decisions).</summary>
    public string? RegimeAt(DateOnly date)
    {
        if (_regimeCache.TryGetValue(date, out var r)) return r;
        var label = RegimeClassifier.Classify(new BarSeries(_data.IndexCode, _data.Index, VisibleIndex(_data.Index, date)));
        return _regimeCache[date] = label?.ToString();
    }

    private static Dictionary<string, TickerData> BuildTickerData(MarketDataSet data, Dictionary<DateOnly, int> sessionOf)
    {
        var cal = data.Calendar;
        var result = new Dictionary<string, TickerData>(StringComparer.Ordinal);
        foreach (var (ticker, bars) in data.Bars)
        {
            data.Securities.TryGetValue(ticker, out var sec);
            var disc = data.Discontinuities.TryGetValue(ticker, out var set)
                ? set.Where(sessionOf.ContainsKey).Select(d => sessionOf[d]).Order().ToArray()
                : [];
            var t = new TickerData(ticker, bars, sec, disc, cal.Count);
            var j = 0;
            for (var i = 0; i < cal.Count; i++)
            {
                while (j < bars.Length && bars[j].Date <= cal[i]) j++;
                t.Visible[i] = j;
                t.BarOn[i] = j > 0 && bars[j - 1].Date == cal[i] ? j - 1 : -1;
            }
            result[ticker] = t;
        }
        return result;
    }

    /// <summary>Median trading value of the last <paramref name="lookback"/> bars among the first <paramref name="visible"/>.</summary>
    private static double MedianValue(TickerData t, int visible, int lookback)
    {
        var n = Math.Min(lookback, visible);
        if (n <= 0) return 0;
        Span<double> xs = stackalloc double[n];
        for (var k = 0; k < n; k++)
        {
            var b = t.Bars[visible - 1 - k];
            xs[k] = b.IsHalted ? 0 : b.TradingValue;
        }
        xs.Sort();
        return n % 2 == 1 ? xs[n / 2] : (xs[n / 2 - 1] + xs[n / 2]) / 2;
    }

    public static int VisibleIndex(Bar[] index, DateOnly date)
    {
        int lo = 0, hi = index.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (index[mid].Date <= date) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}
