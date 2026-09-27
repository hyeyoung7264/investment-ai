using Investment.Domain.Market;
using Investment.Domain.Research;
using Microsoft.EntityFrameworkCore;

namespace Investment.Integration.Tests;

[Collection("db")]
public sealed class PersistenceTests(DatabaseFixture fx)
{
    [Fact]
    public async Task Security_and_prices_round_trip()
    {
        var now = DateTimeOffset.UtcNow;
        await using (var db = fx.NewContext())
        {
            db.Securities.Add(new Security { Ticker = "T00010", Name = "테스트", Market = MarketType.Kospi, Kind = SecurityKind.Common, UpdatedAt = now });
            db.DailyPrices.Add(new DailyPrice
            {
                Ticker = "T00010", Date = new DateOnly(2024, 1, 2), Open = 100, High = 110, Low = 95, Close = 105,
                Volume = 1000, TradingValueEstimate = 105000, Source = "test", IngestedAt = now,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = fx.NewContext())
        {
            var s = await db.Securities.SingleAsync(x => x.Ticker == "T00010");
            Assert.Equal(MarketType.Kospi, s.Market);
            var p = await db.DailyPrices.SingleAsync(x => x.Ticker == "T00010");
            Assert.Equal(105m, p.Close);
            Assert.Equal(new DateOnly(2024, 1, 2), p.Date);
        }
    }

    [Fact]
    public async Task Strategy_versions_are_unique_per_parameters_and_cannot_be_cascade_deleted()
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = fx.NewContext();
        db.Strategies.Add(new StrategyDefinition { Id = "test.s", Name = "s", Family = "Test", Hypothesis = "h", CreatedAt = now, UpdatedAt = now });
        db.StrategyVersions.Add(new StrategyVersion { Id = Guid.NewGuid(), StrategyId = "test.s", Version = 1, LogicVersion = 1, ParametersJson = "{\"a\":1}", ParametersHash = "h1", CreatedAt = now });
        await db.SaveChangesAsync();

        db.StrategyVersions.Add(new StrategyVersion { Id = Guid.NewGuid(), StrategyId = "test.s", Version = 2, LogicVersion = 1, ParametersJson = "{\"a\":1}", ParametersHash = "h1", CreatedAt = now });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        await using var db2 = fx.NewContext();
        await Assert.ThrowsAnyAsync<Exception>(() => db2.Database.ExecuteSqlRawAsync("DELETE FROM strategies WHERE id = 'test.s'"));
    }
}
