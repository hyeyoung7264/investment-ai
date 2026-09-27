using Investment.Domain.Market;
using Investment.Domain.Strategies;
using Investment.Risk;

namespace Investment.Backtest;

/// <summary>
/// Daily event-driven simulator. For each session D:
///   1. open   — fill orders decided at the previous close (sells first, then buys) at D's open ± slippage
///   2. intraday — stop-loss / take-profit (gap → open price; stop wins if both touched)
///   3. close  — value positions, record equity, risk assessment (daily-loss block / drawdown halt)
///   4. after close — strategy sees data up to D only; risk engine sizes entries for D+1's open
/// Gross and net results come from the same path: gross session return = (net equity + that session's costs)
/// / previous net equity − 1, compounded — i.e. identical positions with costs removed.
/// </summary>
public sealed class BacktestEngine
{
    private sealed class TickerData(string ticker, Bar[] bars, Security? security, IReadOnlySet<DateOnly>? discontinuities, int sessions)
    {
        public string Ticker { get; } = ticker;
        public Bar[] Bars { get; } = bars;
        public Security? Security { get; } = security;
        public IReadOnlySet<DateOnly>? Discontinuities { get; } = discontinuities;
        /// <summary>Per calendar session: index of the bar dated that session, or -1.</summary>
        public int[] BarOn { get; } = new int[sessions];
        /// <summary>Per calendar session: number of bars dated on or before that session.</summary>
        public int[] Visible { get; } = new int[sessions];
        public int CooldownUntil { get; set; } = -1;
    }

    private sealed class Position
    {
        public required string Ticker { get; init; }
        public long Quantity { get; set; }
        public DateOnly EntrySignalDate { get; init; }
        public DateOnly EntryDate { get; init; }
        public int EntrySession { get; init; }
        public decimal EntryFill { get; init; }
        public decimal EntryRef { get; init; }
        public decimal EntryCosts { get; init; }
        public required string EntryReason { get; init; }
        public double Score { get; init; }
        public double LastClose { get; set; }
    }

    private sealed record PendingOrder(string Ticker, bool IsBuy, long Quantity, DateOnly SignalDate, string Reason, double Score);

    public BacktestResult Run(IStrategy strategy, MarketDataSet data, BacktestConfig cfg)
    {
        var cal = data.Calendar;
        var first = FirstIndexOnOrAfter(cal, cfg.Start);
        var last = LastIndexOnOrBefore(cal, cfg.End);
        if (first < 0 || last < first) throw new ArgumentException("no sessions in the requested period");

        var tickers = BuildTickerData(data);
        var risk = new RiskEngine(cfg.Risk);
        var costs = cfg.Costs;

        decimal cash = cfg.InitialCapital + 0.00m; // normalize decimal scale (CLI vs DB-restored values)
        decimal cumulativeCosts = 0;
        decimal costsAtPrevClose = 0;
        decimal grossEquity = cash;
        decimal prevNetEquity = cash;
        double peak = (double)cfg.InitialCapital;
        double prevEquity = (double)cfg.InitialCapital;
        var positions = new SortedDictionary<string, Position>(StringComparer.Ordinal);
        var pending = new List<PendingOrder>();
        var trades = new List<TradeRecord>();
        var equity = new List<EquityPoint>();
        var events = new List<RiskEvent>();
        var rejections = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var signalCount = 0;
        DateOnly? haltedOn = null;
        string? haltReason = null;
        var entriesBlocked = false;

        void Reject(string rule) => rejections[rule] = rejections.GetValueOrDefault(rule) + 1;

        void ClosePosition(Position p, int session, decimal refPrice, decimal fill, string reason)
        {
            var d = cal[session];
            var notional = fill * p.Quantity;
            var commission = notional * (decimal)costs.CommissionRate;
            var tax = notional * (decimal)costs.SellTaxRate(d);
            var slip = (refPrice - fill) * p.Quantity;
            var exitCosts = commission + tax + slip;
            cash += notional - commission - tax;
            cumulativeCosts += exitCosts;
            var gross = (refPrice - p.EntryRef) * p.Quantity;
            var totalCosts = p.EntryCosts + exitCosts;
            trades.Add(new TradeRecord(p.Ticker, p.EntrySignalDate, p.EntryDate, p.EntryFill, p.EntryRef, d, fill, refPrice,
                p.Quantity, gross, totalCosts, gross - totalCosts, session - p.EntrySession, p.EntryReason, reason, p.Score));
            positions.Remove(p.Ticker);
        }

        decimal SellFill(TickerData t, int session, double reference, long qty)
        {
            var slip = costs.Slippage(reference * qty, MedianValue(t, Math.Max(0, t.Visible[session] - 1), cfg.LiquidityLookback));
            return (decimal)(reference * (1 - slip));
        }

        for (var i = first; i <= last; i++)
        {
            var date = cal[i];

            // ---------- 1. open: fills ----------
            var buys = new List<PendingOrder>();
            foreach (var o in pending.Where(o => !o.IsBuy).OrderBy(o => o.Ticker, StringComparer.Ordinal).ToList())
            {
                if (!positions.TryGetValue(o.Ticker, out var p)) { pending.Remove(o); continue; }
                var t = tickers[o.Ticker];
                var bi = t.BarOn[i];
                if (bi < 0 || t.Bars[bi].IsHalted) continue; // retry next session (handled at close if data ended)
                var bar = t.Bars[bi];
                if (bi > 0 && KrxRules.IsLockedLimitDown(bar, t.Bars[bi - 1].Close)) { Reject("sell-locked-limit-down"); continue; }
                if (t.Discontinuities?.Contains(date) == true)
                {
                    ClosePosition(p, i, (decimal)p.LastClose, SellFill(t, i, p.LastClose, p.Quantity), "DataDiscontinuity");
                    pending.Remove(o);
                    continue;
                }
                ClosePosition(p, i, (decimal)bar.Open, SellFill(t, i, bar.Open, p.Quantity), o.Reason);
                pending.Remove(o);
            }
            buys.AddRange(pending.Where(o => o.IsBuy));
            pending.RemoveAll(o => o.IsBuy); // buy orders live for one session only

            foreach (var o in buys.OrderByDescending(o => o.Score).ThenBy(o => o.Ticker, StringComparer.Ordinal))
            {
                var t = tickers[o.Ticker];
                var bi = t.BarOn[i];
                if (bi < 0 || t.Bars[bi].IsHalted) { Reject("buy-not-trading"); continue; }
                var bar = t.Bars[bi];
                if (t.Discontinuities?.Contains(date) == true) { Reject("buy-discontinuity"); continue; }
                if (bi > 0 && KrxRules.IsLockedLimitUp(bar, t.Bars[bi - 1].Close)) { Reject("buy-locked-limit-up"); continue; }
                var median = MedianValue(t, Math.Max(0, t.Visible[i] - 1), cfg.LiquidityLookback);
                var slip = costs.Slippage(bar.Open * o.Quantity, median);
                var fill = (decimal)(bar.Open * (1 + slip));
                var qty = o.Quantity;
                var perShare = fill * (1 + (decimal)costs.CommissionRate);
                if (perShare * qty > cash) qty = (long)Math.Floor(cash / perShare);
                if (qty <= 0) { Reject("buy-cash"); continue; }
                var notional = fill * qty;
                var commission = notional * (decimal)costs.CommissionRate;
                var slipCost = (fill - (decimal)bar.Open) * qty;
                cash -= notional + commission;
                cumulativeCosts += commission + slipCost;
                positions[o.Ticker] = new Position
                {
                    Ticker = o.Ticker, Quantity = qty, EntrySignalDate = o.SignalDate, EntryDate = date, EntrySession = i,
                    EntryFill = fill, EntryRef = (decimal)bar.Open, EntryCosts = commission + slipCost,
                    EntryReason = o.Reason, Score = o.Score, LastClose = bar.Open,
                };
            }

            // ---------- 2. intraday: data discontinuities, stops, targets ----------
            foreach (var p in positions.Values.ToList())
            {
                var t = tickers[p.Ticker];
                var bi = t.BarOn[i];
                if (bi < 0 || t.Bars[bi].IsHalted) continue;
                var bar = t.Bars[bi];
                if (t.Discontinuities?.Contains(date) == true && p.EntrySession < i)
                {
                    // the jump is not a tradable return: exit at the last valid price
                    ClosePosition(p, i, (decimal)p.LastClose, SellFill(t, i, p.LastClose, p.Quantity), "DataDiscontinuity");
                    t.CooldownUntil = i + cfg.DiscontinuityCooldownSessions;
                    continue;
                }
                if (bi > 0 && KrxRules.IsLockedLimitDown(bar, t.Bars[bi - 1].Close)) continue;
                var enteredToday = p.EntrySession == i;
                var entry = (double)p.EntryFill;
                if (cfg.Risk.StopLoss is { } sl)
                {
                    var stop = entry * (1 - sl);
                    double? px = !enteredToday && bar.Open <= stop ? bar.Open : bar.Low <= stop ? stop : null;
                    if (px is { } s) { ClosePosition(p, i, (decimal)s, SellFill(t, i, s, p.Quantity), "StopLoss"); continue; }
                }
                if (cfg.Risk.TakeProfit is { } tp)
                {
                    var target = entry * (1 + tp);
                    double? px = !enteredToday && bar.Open >= target ? bar.Open : bar.High >= target ? target : null;
                    if (px is { } x) ClosePosition(p, i, (decimal)x, SellFill(t, i, x, p.Quantity), "TakeProfit");
                }
            }

            // ---------- 3. close: valuation, data end, risk ----------
            foreach (var p in positions.Values.ToList())
            {
                var t = tickers[p.Ticker];
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
            foreach (var t in tickers.Values)
                if (t.BarOn[i] >= 0 && t.Discontinuities?.Contains(date) == true)
                    t.CooldownUntil = Math.Max(t.CooldownUntil, i + cfg.DiscontinuityCooldownSessions);

            if (i == last)
            {
                foreach (var p in positions.Values.ToList())
                    ClosePosition(p, i, (decimal)p.LastClose, SellFill(tickers[p.Ticker], i, p.LastClose, p.Quantity), "EndOfTest");
                pending.Clear();
            }

            var invested = positions.Values.Sum(p => (decimal)p.LastClose * p.Quantity);
            var netEquity = cash + invested;
            // gross = same positions with the session's costs added back, compounded session by session
            var sessionCosts = cumulativeCosts - costsAtPrevClose;
            costsAtPrevClose = cumulativeCosts;
            if (prevNetEquity > 0) grossEquity *= (netEquity + sessionCosts) / prevNetEquity;
            prevNetEquity = netEquity;
            equity.Add(new EquityPoint(date, netEquity, grossEquity, cash, positions.Count, invested));
            var eq = (double)netEquity;
            peak = Math.Max(peak, eq);
            var sessionReturn = prevEquity > 0 ? eq / prevEquity - 1 : 0;
            prevEquity = eq;
            if (i == last || haltedOn is not null) continue;

            var snapshot = new PortfolioSnapshot(eq, (double)cash, peak, sessionReturn,
                positions.Values.Select(p => new PositionExposure(p.Ticker, p.LastClose * p.Quantity, tickers[p.Ticker].Security?.Sector)).ToList());
            var assessment = risk.Assess(snapshot);
            entriesBlocked = assessment.State == RiskState.EntriesBlocked;
            if (assessment.State == RiskState.Halted)
            {
                haltedOn = date;
                haltReason = assessment.Reason;
                events.Add(new RiskEvent(date, "Halt", assessment.Reason!));
                pending.Clear();
                foreach (var p in positions.Values)
                    pending.Add(new PendingOrder(p.Ticker, false, p.Quantity, date, "RiskHalt", 0));
                continue;
            }
            if (entriesBlocked) events.Add(new RiskEvent(date, "EntriesBlocked", assessment.Reason!));

            // ---------- 4. after close: signals for next session ----------
            var universe = data.Universe.MembersOn(date).Where(tk => IsEnterable(tickers, tk, i, strategy.WarmupBars)).ToList();
            var held = positions.Values.ToDictionary(p => p.Ticker,
                p => new HeldPosition(p.Ticker, p.EntryDate, (double)p.EntryFill, i - p.EntrySession), StringComparer.Ordinal);
            var ctx = new StrategyContext(date, universe,
                tk => tickers.TryGetValue(tk, out var td) ? new BarSeries(tk, td.Bars, td.Visible[i]) : null,
                new BarSeries(data.IndexCode, data.Index, VisibleIndex(data.Index, date)),
                held);
            var signals = strategy.GenerateSignals(ctx);
            signalCount += signals.Count;

            var exiting = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in positions.Values)
            {
                var sig = signals.FirstOrDefault(s => s.Ticker == p.Ticker && s.Action == SignalAction.Sell);
                string? reason = sig?.Reason;
                if (reason is null && cfg.Risk.MaxHoldingSessions is { } mh && i - p.EntrySession >= mh) reason = "MaxHolding";
                if (reason is null) continue;
                exiting.Add(p.Ticker);
                if (!pending.Any(o => o.Ticker == p.Ticker && !o.IsBuy))
                    pending.Add(new PendingOrder(p.Ticker, false, p.Quantity, date, reason, 0));
            }
            foreach (var o in pending.Where(o => !o.IsBuy)) exiting.Add(o.Ticker);

            if (entriesBlocked) continue;
            var universeSet = universe.ToHashSet(StringComparer.Ordinal);
            var candidates = new List<EntryCandidate>();
            foreach (var s in signals.Where(s => s.Action == SignalAction.Buy))
            {
                if (!universeSet.Contains(s.Ticker)) { Reject("signal-outside-universe"); continue; }
                if (positions.ContainsKey(s.Ticker)) continue;
                var t = tickers[s.Ticker];
                var bar = t.Bars[t.BarOn[i]];
                candidates.Add(new EntryCandidate(s.Ticker, s.Score, bar.Close,
                    MedianValue(t, t.Visible[i], cfg.LiquidityLookback), t.Security?.Sector, s.Reason));
            }
            var alloc = risk.Allocate(snapshot, candidates, exiting, costs.EntryBuffer);
            foreach (var r in alloc.Rejected) Reject(r.Rule);
            foreach (var a in alloc.Approved)
                pending.Add(new PendingOrder(a.Ticker, true, a.Quantity, date, a.Reason, a.Score));
        }

        var bench = BenchmarkReturn(data.Index, cal[first], cal[last]);
        return new BacktestResult
        {
            Strategy = strategy.Descriptor,
            Config = cfg,
            Trades = trades,
            Equity = equity,
            RiskEvents = events,
            Rejections = rejections,
            DataHash = data.ComputeHash(),
            UniverseHash = data.Universe.Hash(),
            DataSource = data.Source,
            BenchmarkReturn = bench,
            SignalCount = signalCount,
            HaltedOn = haltedOn,
            HaltReason = haltReason,
        };
    }

    private static bool IsEnterable(Dictionary<string, TickerData> tickers, string ticker, int session, int warmup)
    {
        if (!tickers.TryGetValue(ticker, out var t)) return false;
        var bi = t.BarOn[session];
        if (bi < 0 || t.Bars[bi].IsHalted) return false;
        if (t.Visible[session] < warmup) return false;
        if (session <= t.CooldownUntil) return false;
        if (t.Security?.DelistedDate is { } dd && dd.DayNumber - t.Bars[bi].Date.DayNumber <= KrxRules.KnownDelistingWindowDays)
            return false;
        return true;
    }

    private static Dictionary<string, TickerData> BuildTickerData(MarketDataSet data)
    {
        var cal = data.Calendar;
        var result = new Dictionary<string, TickerData>(StringComparer.Ordinal);
        foreach (var (ticker, bars) in data.Bars)
        {
            data.Securities.TryGetValue(ticker, out var sec);
            data.Discontinuities.TryGetValue(ticker, out var disc);
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

    private static int VisibleIndex(Bar[] index, DateOnly date)
    {
        int lo = 0, hi = index.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (index[mid].Date <= date) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private static double BenchmarkReturn(Bar[] index, DateOnly from, DateOnly to)
    {
        // benchmark measured from the close before the first session to the last close (same as equity)
        var a = VisibleIndex(index, from.AddDays(-1));
        var b = VisibleIndex(index, to);
        if (a == 0 || b == 0) return double.NaN;
        return index[b - 1].Close / index[a - 1].Close - 1;
    }

    private static int FirstIndexOnOrAfter(IReadOnlyList<DateOnly> cal, DateOnly d)
    {
        for (var i = 0; i < cal.Count; i++) if (cal[i] >= d) return i;
        return -1;
    }

    private static int LastIndexOnOrBefore(IReadOnlyList<DateOnly> cal, DateOnly d)
    {
        for (var i = cal.Count - 1; i >= 0; i--) if (cal[i] <= d) return i;
        return -1;
    }
}
