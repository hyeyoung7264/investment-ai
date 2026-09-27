using Investment.Backtest;
using Investment.Domain.Market;
using Investment.Domain.Research;
using Investment.MarketData.Universe;
using Investment.Persistence;
using Investment.Research;
using Investment.Risk;
using Investment.Strategies.MeanReversion;
using Microsoft.EntityFrameworkCore;

namespace Investment.Integration.Tests;

[Collection("db")]
public sealed class PipelineTests(DatabaseFixture fx)
{
    private static readonly string[] Tickers = ["P00010", "P00020", "P00030", "P00040"];

    private async Task SeedAsync()
    {
        await using var db = fx.NewContext();
        if (await db.Securities.AnyAsync(s => s.Ticker == Tickers[0])) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var t in Tickers)
            db.Securities.Add(new Security { Ticker = t, Name = t, Market = MarketType.Kosdaq, Kind = SecurityKind.Common, Sector = "S" + t[^2], UpdatedAt = now });
        await db.SaveChangesAsync();

        var days = new List<DateOnly>();
        for (var d = new DateOnly(2022, 1, 3); days.Count < 400; d = d.AddDays(1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) days.Add(d);

        var store = new MarketDataStore(fx.ConnectionString);
        await store.UpsertIndexPricesAsync(days.Select(d => new IndexPrice
        {
            IndexCode = "KOSPI", Date = d, Open = 2500, High = 2500, Low = 2500, Close = 2500, Source = "test", IngestedAt = now,
        }).ToList());

        var rows = new List<DailyPrice>();
        for (var k = 0; k < Tickers.Length; k++)
        {
            var rng = new Random(k + 11);
            var p = 20_000.0;
            foreach (var d in days)
            {
                var o = Math.Round(p * (1 + (rng.NextDouble() - 0.5) * 0.02));
                p = Math.Max(1000, o * (1 + (rng.NextDouble() - 0.5) * 0.10) + 15);
                var c = Math.Round(p);
                var vol = 200_000 + rng.Next(50_000) + k * 100_000;
                rows.Add(new DailyPrice
                {
                    Ticker = Tickers[k], Date = d, Open = (decimal)o, High = (decimal)Math.Max(o, c) * 1.01m, Low = (decimal)Math.Min(o, c) * 0.99m,
                    Close = (decimal)c, Volume = vol, TradingValueEstimate = (decimal)c * vol, Source = "test", IngestedAt = now,
                });
            }
        }
        await store.UpsertDailyPricesAsync(rows);
    }

    [Fact]
    public async Task Backtest_is_persisted_with_gross_net_metrics_and_reproduces_from_stored_inputs()
    {
        await SeedAsync();
        var runner = new BacktestRunner(fx.ConnectionString);
        var universe = new UniverseDefinition { TopN = 3, MinHistoryBars = 40, LiquidityLookback = 20, Tickers = Tickers };
        var cfg = new BacktestConfig
        {
            Start = new DateOnly(2022, 11, 1), End = new DateOnly(2023, 7, 31), InitialCapital = 50_000_000m,
            Risk = new RiskLimits { MaxPositions = 3, MaxPositionWeight = 0.33, MaxSectorWeight = 1, MaxDrawdown = 1, MaxParticipation = 1 },
        };
        var parameters = """{"BandLength":10,"EntryZ":-1.2,"TrendLength":null,"ExitSmaLength":3,"MaxHoldingSessions":5}""";
        var outcome = await runner.RunAsync(new BacktestRequest(MeanReversionStrategy.Id, parameters, universe, cfg));

        Assert.True(outcome.Result.Trades.Count > 5, $"expected trades, got {outcome.Result.Trades.Count}");
        await using (var db = fx.NewContext())
        {
            var run = await db.BacktestRuns.Include(r => r.Metrics).SingleAsync(r => r.Id == outcome.Run.Id);
            Assert.Equal(2, run.Metrics.Count);
            var gross = run.Metrics.Single(m => m.Basis == CostBasis.Gross);
            var net = run.Metrics.Single(m => m.Basis == CostBasis.Net);
            Assert.True(gross.TotalReturn > net.TotalReturn, "costs must reduce returns");
            Assert.Equal(outcome.Result.Trades.Count, await db.BacktestTrades.CountAsync(t => t.RunId == run.Id));
            Assert.Equal(outcome.Result.Equity.Count, await db.BacktestEquity.CountAsync(e => e.RunId == run.Id));
            Assert.Equal(StrategyStatus.Backtested, (await db.StrategyVersions.SingleAsync(v => v.Id == run.StrategyVersionId)).Status);
        }

        // fresh runner (no cache): everything is rebuilt from what was stored
        var rerun = await new BacktestRunner(fx.ConnectionString).RerunAsync(outcome.Run.Id);
        Assert.True(rerun.DataMatches);
        Assert.True(rerun.ParametersMatch);
        Assert.True(rerun.ResultMatches, rerun.FirstDifference);
    }

    [Fact]
    public async Task Same_parameters_reuse_the_strategy_version_and_new_parameters_create_one()
    {
        var recorder = new BacktestRecorder(fx.NewContext);
        var a = await recorder.EnsureVersionAsync(new MeanReversionStrategy().Descriptor);
        var b = await recorder.EnsureVersionAsync(new MeanReversionStrategy().Descriptor);
        var c = await recorder.EnsureVersionAsync(new MeanReversionStrategy(new MeanReversionParameters { EntryZ = -2.5 }).Descriptor);
        Assert.Equal(a.Id, b.Id);
        Assert.NotEqual(a.Id, c.Id);
        Assert.Equal(a.Version + 1, c.Version);
    }
}
