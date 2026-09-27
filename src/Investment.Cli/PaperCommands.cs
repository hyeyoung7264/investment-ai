using System.Text.Json;
using Investment.Backtest;
using Investment.Domain.Trading;
using Investment.MarketData;
using Investment.PaperTrading;
using Investment.Persistence;
using Investment.Research;
using Microsoft.EntityFrameworkCore;

namespace Investment.Cli;

public static class PaperCommands
{
    public static async Task<int> RunAsync(string? sub, CliOptions o, CancellationToken ct)
    {
        var cs = Database.ConnectionString(o.Get("db"));
        var svc = new PaperTradingService(cs, Log);
        switch (sub)
        {
            case "start":
            {
                var cfg = ResearchCommands.Config(o, DateOnly.MinValue, DateOnly.MaxValue);
                var session = await svc.StartAsync(new PaperStartRequest(
                    o.Get("name") ?? $"{o.Require("strategy")}-{DateTime.Today:yyyyMMdd}", o.Require("strategy"),
                    o.Get("version-id") is { } v ? Guid.Parse(v) : null,
                    o.Get("start") is { } s ? DateOnly.Parse(s) : null,
                    cfg.InitialCapital, ResearchCommands.Universe(o), cfg.Costs, cfg.Risk,
                    Book: new BookLimits { BookCapital = (decimal)o.GetDouble("book", 200_000_000), MaxStrategyWeight = o.GetDouble("max-strategy-weight", 0.5) }), ct);
                Console.WriteLine($"paper session {session.Id} '{session.Name}' started {session.StartDate:yyyy-MM-dd}");
                Console.WriteLine($"  strategy {session.StrategyId} params {session.ParametersJson}");
                Console.WriteLine($"  expected EV/trade {ReportFormatter.P(session.ExpectedEvPerTrade, 3)}; entries paused in regimes {session.BlockedRegimesJson}");
                return 0;
            }
            case "daily":
            {
                if (o.Has("ingest"))
                {
                    var ingest = new IngestionService(() => Database.Create(cs), new MarketDataStore(cs), new KindSecurityMasterSource(), new NaverPriceSource(), Log);
                    var opt = new IngestOptions { From = new DateOnly(2015, 1, 1) };
                    await ingest.SyncSecurityMasterAsync(opt.From, ct);
                    await ingest.IngestIndicesAsync(opt, ct);
                    await ingest.IngestPricesAsync(opt, ct);
                    await new Investment.MarketData.Splits.SplitVerificationService(() => Database.Create(cs), new MarketDataStore(cs),
                        new Investment.MarketData.Splits.NaverRawQuoteSource(), Log).RunAsync(opt.From, 6, ct);
                }
                foreach (var r in await svc.RunDailyAsync(ct))
                    Console.WriteLine($"{r.Name}: +{r.SessionsProcessed} sessions ({r.From:yyyy-MM-dd}..{r.To:yyyy-MM-dd}), orders {r.NewOrders}, closed trades {r.NewTrades}, equity {r.Equity:N0}, regime {r.Regime}{(r.RegimeBlocked ? " [entries paused]" : "")}, gate {r.Gate?.Decision}: {string.Join("; ", r.Gate?.Reasons ?? [])}");
                return 0;
            }
            case "status":
                return await StatusAsync(cs, o, ct);
            default:
                throw new CliUsageException("paper start|daily|status");
        }
    }

    private static async Task<int> StatusAsync(string cs, CliOptions o, CancellationToken ct)
    {
        await using var db = Database.Create(cs);
        var sessions = await db.PaperSessions.AsNoTracking().OrderBy(s => s.CreatedAt).ToListAsync(ct);
        foreach (var s in sessions)
        {
            var state = SimulationState.FromJson(s.StateJson);
            var trades = await db.PaperTrades.AsNoTracking().Where(t => t.SessionId == s.Id).OrderBy(t => t.ExitTime).ToListAsync(ct);
            var equity = await db.PaperEquity.AsNoTracking().Where(e => e.SessionId == s.Id).OrderBy(e => e.Date).ToListAsync(ct);
            var last = equity.LastOrDefault();
            Console.WriteLine($"== {s.Name} [{s.Status}] {s.StrategyId} since {s.StartDate:yyyy-MM-dd}, last processed {s.LastProcessedDate:yyyy-MM-dd}");
            Console.WriteLine($"   equity {last?.NetEquity ?? s.InitialCapital:N0} / {s.InitialCapital:N0} ({ReportFormatter.P((double)((last?.NetEquity ?? s.InitialCapital) / s.InitialCapital - 1))}), sessions {equity.Count}, regime {last?.Regime}");
            Console.WriteLine($"   closed trades {trades.Count}: actual EV {(trades.Count == 0 ? "n/a" : ReportFormatter.P(trades.Average(t => t.ActualReturn), 3))} vs expected {ReportFormatter.P(s.ExpectedEvPerTrade, 3)}");
            foreach (var p in state.Positions.Values)
                Console.WriteLine($"   holding {p.Ticker} x{p.Quantity} since {p.EntryDate:yyyy-MM-dd} @ {p.EntryFill:N0} last {p.LastClose:N0} ({ReportFormatter.P(p.LastClose / (double)p.EntryFill - 1)}) — {p.EntryReason}");
            foreach (var p in state.Pending)
                Console.WriteLine($"   next open: {(p.IsBuy ? "BUY" : "SELL")} {p.Ticker} x{p.Quantity} — {p.Reason}");
            foreach (var t in trades.TakeLast(o.GetInt("trades", 10)))
                Console.WriteLine($"   trade {t.Ticker} {t.EntryTime:MM-dd}->{t.ExitTime:MM-dd} {ReportFormatter.P(t.ActualReturn)} ({t.Result}, {t.ExitReason}) regime {t.MarketCondition}");
        }
        if (sessions.Count == 0) Console.WriteLine("no paper sessions");
        return 0;
    }

    private static void Log(string msg) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
}
