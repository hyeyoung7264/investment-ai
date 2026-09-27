using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Investment.Persistence;

public static class Database
{
    /// <summary>Local dev cluster from scripts/db.sh. Override with INVESTMENT_DB_CONNECTION.</summary>
    public const string DefaultConnection = "Host=127.0.0.1;Port=55432;Database=investment;Username=postgres;Include Error Detail=true";

    public static string ConnectionString(string? overrideValue = null) =>
        overrideValue
        ?? Environment.GetEnvironmentVariable("INVESTMENT_DB_CONNECTION")
        ?? DefaultConnection;

    public static DbContextOptions<InvestmentDbContext> Options(string? connectionString = null) =>
        new DbContextOptionsBuilder<InvestmentDbContext>()
            .UseNpgsql(ConnectionString(connectionString), o =>
            {
                o.SetPostgresVersion(12, 0);
                o.CommandTimeout(600);
            })
            .Options;

    public static InvestmentDbContext Create(string? connectionString = null) => new(Options(connectionString));
}

public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<InvestmentDbContext>
{
    public InvestmentDbContext CreateDbContext(string[] args) => Database.Create();
}
