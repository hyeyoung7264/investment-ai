using System.Text.Json;
using Investment.Backtest;
using Investment.Domain.Market;
using Investment.Domain.Research;
using Investment.Domain.Trading;
using Investment.MarketData.Universe;
using Investment.PaperTrading;
using Investment.Persistence;
using Investment.Research;
using Investment.Risk;
using Investment.Strategies.MeanReversion;
using Microsoft.EntityFrameworkCore;

namespace Investment.Integration.Tests;

[Collection("paperdb")]
public sealed class PaperTradingTests(PaperDatabaseFixture fx)
{
    private static readonly string[] Tickers = ["Q00010", "Q00020", "Q00030", "Q00040", "Q00050"];
    private const string Params = """{"BandLength":10,"EntryZ":-1.0,"TrendLength":null,"ExitSmaLength":3,"MaxHoldingSessions":5}""";

    private static List<DateOnly> Days()
    {
        var days = new List<DateOnly>();
        for (var d = new DateOnly(2023, 1, 2); days.Count < 420; d = d.AddDays(1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) days.Add(d);
        return days;
    }

    /// <summary>Appends market data for days [from, to) — simulates the end-of-day feed arriving.</summary>
    private async Task FeedAsync(List<DateOnly> days, int from, int to)
    {
        var now = DateTimeOffset.UtcNow;
        var store = new MarketDataStore(fx.ConnectionString);
        await store.UpsertIndexPricesAsync(days.Skip(from).Take(to - from).Select((d, k) => new IndexPrice
        {
            IndexCode = "KOSPI", Date = d, Open = 2500 + (from + k), High = 2500 + (from + k), Low = 2500 + (from + k), Close = 2500 + (from + k),
            Source = "test", IngestedAt = now,
        }).ToList());
        var rows = new List<DailyPrice>();
        for (var k = 0; k < Tickers.Length; k++)
        {
            var rng = new Random(1000 * k + 7);
            var p = 30_000.0;
            for (var i = 0; i < to; i++)
            {
                var o = Math.Round(p * (1 + (rng.NextDouble() - 0.5) * 0.02));
                p = Math.Max(2000, o * (1 + (rng.NextDouble() - 0.5) * 0.08) + 5);
                var c = Math.Round(p);
                var vol = 300_000 + rng.Next(100_000);
                if (i < from) continue;
                rows.Add(new DailyPrice
                {
                    Ticker = Tickers[k], Date = days[i], Open = (decimal)o, High = (decimal)Math.Max(o, c) * 1.01m, Low = (decimal)Math.Min(o, c) * 0.99m,
                    Close = (decimal)c, Volume = vol, TradingValueEstimate = (decimal)c * vol, Source = "test", IngestedAt = now,
                });
            }
        }
        await store.UpsertDailyPricesAsync(rows);
    }

    [Fact]
    public async Task Paper_session_reproduces_backtest_semantics_and_records_decision_context()
    {
        var days = Days();
        var now = DateTimeOffset.UtcNow;
        await using (var db = fx.NewContext())
        {
            foreach (var t in Tickers)
                db.Securities.Add(new Security { Ticker = t, Name = t, Market = MarketType.Kospi, Kind = SecurityKind.Common, Sector = t, UpdatedAt = now });
            await db.SaveChangesAsync();
        }
        await FeedAsync(days, 0, 300);

        // a VALIDATED version with stored walk-forward evidence (normally produced by a study)
        var recorder = new BacktestRecorder(fx.NewContext);
        var version = await recorder.EnsureVersionAsync(new MeanReversionStrategy(JsonSerializer.Deserialize<MeanReversionParameters>(Params)).Descriptor);
        var evidence = new OosEvidence
        {
            Trades = 200, NetEvPerTrade = 0.01, NetEvTStat = 3,
            ByRegime = PaperTradingService.AllRegimes.ToDictionary(r => r, _ => new RegimeStats(100, 0.01, 2, 100, 0.001)),
        };
        await using (var db = fx.NewContext())
        {
            (await db.StrategyVersions.SingleAsync(v => v.Id == version.Id)).Status = StrategyStatus.Validated;
            db.StrategyEvaluations.Add(new StrategyEvaluation
            {
                Id = Guid.NewGuid(), StrategyVersionId = version.Id, Stage = "walk-forward-oos", FromStatus = StrategyStatus.Backtested,
                ToStatus = StrategyStatus.Validated, Decision = GateDecision.Promote, EvidenceJson = JsonSerializer.Serialize(evidence),
                CriteriaJson = "{}", Reasons = "test", EvaluatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        var svc = new PaperTradingService(fx.ConnectionString);
        var universe = new UniverseDefinition { TopN = 5, MinHistoryBars = 40, LiquidityLookback = 20, Tickers = Tickers, Markets = [MarketType.Kospi] };
        var risk = new RiskLimits { MaxPositions = 3, MaxPositionWeight = 0.33, MaxSectorWeight = 1, MaxDrawdown = 0.9, MaxParticipation = 1, MaxDailyLoss = 0.5 };
        var costs = new CostModel();

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(
            new PaperStartRequest("backfill", MeanReversionStrategy.Id, version.Id, days[200], 50_000_000m, universe, costs, risk)));
        var session = await svc.StartAsync(new PaperStartRequest("t", MeanReversionStrategy.Id, version.Id, null, 50_000_000m, universe, costs, risk, MinRegimeTrades: 0));
        Assert.Equal(days[299], session.StartDate);

        // three daily runs as data arrives
        var r1 = await svc.RunSessionAsync(session.Id);
        Assert.Equal(1, r1.SessionsProcessed);
        await FeedAsync(days, 300, 360);
        await svc.RunSessionAsync(session.Id);
        await FeedAsync(days, 360, 420);
        var r3 = await svc.RunSessionAsync(session.Id);
        Assert.Equal(60, r3.SessionsProcessed);
        Assert.Equal(0, (await svc.RunSessionAsync(session.Id)).SessionsProcessed); // idempotent

        // same period as a backtest: identical trades (except the backtest's forced end-of-test exits)
        var data = await new DataSetLoader(fx.ConnectionString).LoadAsync(universe, days[299], days[419]);
        var bt = new BacktestEngine().Run(new MeanReversionStrategy(JsonSerializer.Deserialize<MeanReversionParameters>(Params)), data,
            new BacktestConfig { Start = days[299], End = days[419], InitialCapital = 50_000_000m, Costs = costs, Risk = risk });
        await using (var db = fx.NewContext())
        {
            var paperTrades = await db.PaperTrades.Where(t => t.SessionId == session.Id).OrderBy(t => t.Id).ToListAsync();
            var expected = bt.Trades.Where(t => t.ExitReason != "EndOfTest").ToList();
            Assert.True(expected.Count > 10, $"expected trades, got {expected.Count}");
            Assert.Equal(expected.Select(t => (t.Ticker, t.EntryDate, t.ExitDate, t.Quantity, Math.Round(t.NetPnl, 2))),
                paperTrades.Select(t => (t.Ticker, t.EntryTime, t.ExitTime, t.Quantity, t.NetPnl)));

            var paperEquity = await db.PaperEquity.Where(e => e.SessionId == session.Id).OrderBy(e => e.Date).ToListAsync();
            Assert.Equal(bt.Equity.Take(bt.Equity.Count - 1).Select(e => (e.Date, Math.Round(e.NetEquity, 2))), paperEquity.Take(paperEquity.Count - 1).Select(e => (e.Date, e.NetEquity)));

            // decision context is stored with every trade
            Assert.All(paperTrades, t =>
            {
                Assert.True(t.SignalPrice > 0);
                Assert.True(t.SignalTime < t.EntryTime);
                Assert.NotNull(t.MarketCondition);
                Assert.Equal(0.01, t.ExpectedReturn);
                Assert.NotNull(t.StopLoss);
                Assert.False(string.IsNullOrEmpty(t.Reason));
            });
            Assert.True(await db.PaperOrders.CountAsync(o => o.SessionId == session.Id && o.Status == "Filled") >= paperTrades.Count);
            Assert.Equal(3, await db.PaperRuns.CountAsync(r => r.SessionId == session.Id && r.SessionsProcessed > 0));
            Assert.Equal(StrategyStatus.Paper, (await db.StrategyVersions.SingleAsync(v => v.Id == version.Id)).Status);
        }
    }

    [Fact]
    public void Paper_gate_disables_on_collapse_and_approves_only_with_enough_evidence()
    {
        var c = new PaperCriteria();
        var ok = new PaperEvidence(80, 40, 0.008, 2.5, 0.08, 0.01, false);
        Assert.Equal(GateDecision.Promote, PaperGate.Evaluate(StrategyStatus.Paper, ok, c).Decision);
        Assert.Equal(GateDecision.Hold, PaperGate.Evaluate(StrategyStatus.Paper, ok with { Sessions = 30 }, c).Decision);
        Assert.Equal(GateDecision.Hold, PaperGate.Evaluate(StrategyStatus.Paper, ok with { NetEvPerTrade = 0.003 }, c).Decision); // < 50% of expected
        Assert.Equal(GateDecision.Disable, PaperGate.Evaluate(StrategyStatus.Paper, ok with { MaxDrawdown = 0.25 }, c).Decision);
        Assert.Equal(GateDecision.Disable, PaperGate.Evaluate(StrategyStatus.Paper, ok with { NetEvTStat = -2.5, Trades = 25 }, c).Decision);
        Assert.Equal(GateDecision.Disable, PaperGate.Evaluate(StrategyStatus.Paper, ok with { RiskHalted = true }, c).Decision);
    }
}
