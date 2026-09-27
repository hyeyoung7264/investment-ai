using Investment.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Investment.Integration.Tests;

/// <summary>
/// Uses the dedicated `investment_test` database of the local cluster (scripts/db.sh init).
/// Override with INVESTMENT_TEST_DB_CONNECTION. The schema is reset once per test run.
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    public string ConnectionString { get; } =
        Environment.GetEnvironmentVariable("INVESTMENT_TEST_DB_CONNECTION")
        ?? "Host=127.0.0.1;Port=55432;Database=investment_test;Username=postgres;Include Error Detail=true";

    public InvestmentDbContext NewContext() => Database.Create(ConnectionString);

    public async Task InitializeAsync()
    {
        await using var db = NewContext();
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

[CollectionDefinition("db")]
public sealed class DbCollection : ICollectionFixture<DatabaseFixture>;
