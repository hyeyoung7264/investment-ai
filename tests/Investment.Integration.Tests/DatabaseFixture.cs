using Investment.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Investment.Integration.Tests;

/// <summary>
/// Uses dedicated databases of the local cluster (scripts/db.sh init). Override the server with
/// INVESTMENT_TEST_DB_HOST_CONNECTION (without Database=). The schema is reset once per test run.
/// </summary>
public abstract class DatabaseFixtureBase(string database) : IAsyncLifetime
{
    public string ConnectionString { get; } =
        (Environment.GetEnvironmentVariable("INVESTMENT_TEST_DB_HOST_CONNECTION") ?? "Host=127.0.0.1;Port=55432;Username=postgres;Include Error Detail=true")
        + $";Database={database}";

    public InvestmentDbContext NewContext() => Database.Create(ConnectionString);

    public async Task InitializeAsync()
    {
        await using var db = NewContext();
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

public sealed class DatabaseFixture() : DatabaseFixtureBase("investment_test");

public sealed class PaperDatabaseFixture() : DatabaseFixtureBase("investment_paper_test");

[CollectionDefinition("db")]
public sealed class DbCollection : ICollectionFixture<DatabaseFixture>;

[CollectionDefinition("paperdb")]
public sealed class PaperDbCollection : ICollectionFixture<PaperDatabaseFixture>;
