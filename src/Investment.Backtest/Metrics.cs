using Investment.Domain.Research;

namespace Investment.Backtest;

/// <summary>
/// Performance metrics. Formulas (rf = 0, 252 sessions/year):
///   daily r_t = E_t / E_{t-1} - 1 (E_0 = initial capital)
///   CAGR = (E_T/E_0)^(365.25/calendar days) - 1
///   Sharpe = mean(r)/stdev(r)·√252, Sortino = mean(r)/√(mean(min(r,0)²))·√252
///   EV/trade = mean(trade return), t = mean/(sd/√n)
///   Profit factor = Σ positive PnL / |Σ negative PnL|
///   Turnover = Σ traded notional (buy+sell) / 2 / mean equity / years
/// </summary>
public static class Metrics
{
    public const double SessionsPerYear = 252;

    public static BacktestMetric Compute(BacktestResult r, CostBasis basis)
    {
        var initial = (double)r.Config.InitialCapital;
        var curve = r.Equity.Select(e => (double)(basis == CostBasis.Net ? e.NetEquity : e.GrossEquity)).ToArray();
        var daily = DailyReturns(initial, curve);
        var trades = r.Trades;
        var tradeReturns = trades.Select(t => basis == CostBasis.Net ? t.NetReturn : t.GrossReturn).ToArray();
        var tradePnl = trades.Select(t => (double)(basis == CostBasis.Net ? t.NetPnl : t.GrossPnl)).ToArray();

        var final = curve.Length > 0 ? curve[^1] : initial;
        var totalReturn = final / initial - 1;
        var days = r.Equity.Count > 0 ? r.Equity[^1].Date.DayNumber - r.Equity[0].Date.DayNumber + 1 : 0;
        var years = Math.Max(days / 365.25, 1e-9);

        var mean = Mean(daily);
        var sd = Stdev(daily);
        var downside = Math.Sqrt(daily.Length == 0 ? 0 : daily.Select(x => Math.Min(x, 0)).Select(x => x * x).Average());

        var wins = tradeReturns.Where(x => x > 0).ToArray();
        var losses = tradeReturns.Where(x => x <= 0).ToArray();
        var grossProfit = tradePnl.Where(x => x > 0).Sum();
        var grossLoss = -tradePnl.Where(x => x < 0).Sum();
        var evSd = Stdev(tradeReturns);

        var traded = trades.Sum(t => (double)(t.EntryPrice * t.Quantity + t.ExitPrice * t.Quantity));
        var meanEquity = curve.Length > 0 ? curve.Average() : initial;

        return new BacktestMetric
        {
            Basis = basis,
            TotalReturn = totalReturn,
            Cagr = final > 0 ? Math.Pow(final / initial, 1 / years) - 1 : -1,
            AvgDailyReturn = mean,
            DailyReturnStdev = sd,
            WinRate = tradeReturns.Length == 0 ? 0 : (double)wins.Length / tradeReturns.Length,
            ProfitFactor = grossLoss > 0 ? grossProfit / grossLoss : grossProfit > 0 ? double.PositiveInfinity : 0,
            AvgProfit = wins.Length == 0 ? 0 : wins.Average(),
            AvgLoss = losses.Length == 0 ? 0 : losses.Average(),
            ExpectedValuePerTrade = Mean(tradeReturns),
            ExpectedValuePerTradeKrw = Mean(tradePnl),
            ExpectedValueTStat = tradeReturns.Length > 1 && evSd > 0 ? Mean(tradeReturns) / (evSd / Math.Sqrt(tradeReturns.Length)) : 0,
            MaxDrawdown = MaxDrawdown(initial, curve),
            Sharpe = sd > 0 ? mean / sd * Math.Sqrt(SessionsPerYear) : 0,
            Sortino = downside > 0 ? mean / downside * Math.Sqrt(SessionsPerYear) : 0,
            NumberOfTrades = trades.Count,
            AnnualTurnover = meanEquity > 0 ? traded / 2 / meanEquity / years : 0,
            AvgHoldingSessions = trades.Count == 0 ? 0 : trades.Average(t => t.HoldingSessions),
            Exposure = r.Equity.Count == 0 ? 0 : r.Equity.Average(e => e.NetEquity > 0 ? (double)(e.InvestedValue / e.NetEquity) : 0),
            TotalCosts = trades.Sum(t => (double)t.Costs) / initial,
            BenchmarkReturn = r.BenchmarkReturn,
            TradingDays = r.Equity.Count,
        };
    }

    public static double[] DailyReturns(double initial, IReadOnlyList<double> curve)
    {
        var result = new double[curve.Count];
        var prev = initial;
        for (var i = 0; i < curve.Count; i++)
        {
            result[i] = prev > 0 ? curve[i] / prev - 1 : 0;
            prev = curve[i];
        }
        return result;
    }

    public static double MaxDrawdown(double initial, IReadOnlyList<double> curve)
    {
        double peak = initial, mdd = 0;
        foreach (var v in curve)
        {
            peak = Math.Max(peak, v);
            if (peak > 0) mdd = Math.Max(mdd, 1 - v / peak);
        }
        return mdd;
    }

    public static double Mean(IReadOnlyCollection<double> xs) => xs.Count == 0 ? 0 : xs.Average();

    /// <summary>Sample standard deviation (n-1).</summary>
    public static double Stdev(IReadOnlyCollection<double> xs)
    {
        if (xs.Count < 2) return 0;
        var m = xs.Average();
        return Math.Sqrt(xs.Sum(x => (x - m) * (x - m)) / (xs.Count - 1));
    }
}
