using System.Text;
using Investment.Domain.Market;
using Investment.Domain.Research;
using Microsoft.EntityFrameworkCore;

namespace Investment.Persistence;

public sealed class InvestmentDbContext(DbContextOptions<InvestmentDbContext> options) : DbContext(options)
{
    public DbSet<Security> Securities => Set<Security>();
    public DbSet<DailyPrice> DailyPrices => Set<DailyPrice>();
    public DbSet<IndexPrice> IndexPrices => Set<IndexPrice>();
    public DbSet<IngestionRun> IngestionRuns => Set<IngestionRun>();
    public DbSet<SplitEvent> SplitEvents => Set<SplitEvent>();
    public DbSet<StrategyDefinition> Strategies => Set<StrategyDefinition>();
    public DbSet<StrategyVersion> StrategyVersions => Set<StrategyVersion>();
    public DbSet<BacktestRun> BacktestRuns => Set<BacktestRun>();
    public DbSet<BacktestMetric> BacktestMetrics => Set<BacktestMetric>();
    public DbSet<BacktestTrade> BacktestTrades => Set<BacktestTrade>();
    public DbSet<BacktestEquityPoint> BacktestEquity => Set<BacktestEquityPoint>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Security>(e =>
        {
            e.ToTable("securities");
            e.HasKey(x => x.Ticker);
            e.Property(x => x.Ticker).HasMaxLength(12);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Sector).HasMaxLength(200);
            e.Property(x => x.DelistingReason).HasMaxLength(1000);
            e.Property(x => x.Market).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Ignore(x => x.IsDelisted);
        });

        b.Entity<DailyPrice>(e =>
        {
            e.ToTable("daily_prices");
            e.HasKey(x => new { x.Ticker, x.Date });
            e.Property(x => x.Ticker).HasMaxLength(12);
            e.Property(x => x.Source).HasMaxLength(32);
            e.HasIndex(x => x.Date);
            PriceColumns(e.Property(x => x.Open), e.Property(x => x.High), e.Property(x => x.Low), e.Property(x => x.Close));
            e.Property(x => x.TradingValueEstimate).HasPrecision(24, 2);
        });

        b.Entity<IndexPrice>(e =>
        {
            e.ToTable("index_prices");
            e.HasKey(x => new { x.IndexCode, x.Date });
            e.Property(x => x.IndexCode).HasMaxLength(16);
            e.Property(x => x.Source).HasMaxLength(32);
            PriceColumns(e.Property(x => x.Open), e.Property(x => x.High), e.Property(x => x.Low), e.Property(x => x.Close));
        });

        b.Entity<SplitEvent>(e =>
        {
            e.ToTable("split_events");
            e.HasKey(x => new { x.Ticker, x.EventDate });
            e.Property(x => x.Ticker).HasMaxLength(12);
            e.Property(x => x.Notes).HasMaxLength(500);
        });

        b.Entity<IngestionRun>(e =>
        {
            e.ToTable("ingestion_runs");
            e.Property(x => x.Source).HasMaxLength(32);
            e.Property(x => x.Kind).HasMaxLength(32);
            e.Property(x => x.Status).HasMaxLength(16);
        });

        b.Entity<StrategyDefinition>(e =>
        {
            e.ToTable("strategies");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Family).HasMaxLength(64);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasMany(x => x.Versions).WithOne(x => x.Strategy).HasForeignKey(x => x.StrategyId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<StrategyVersion>(e =>
        {
            e.ToTable("strategy_versions");
            e.Property(x => x.StrategyId).HasMaxLength(64);
            e.Property(x => x.ParametersJson).HasColumnType("jsonb");
            e.Property(x => x.ParametersHash).HasMaxLength(64);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.StrategyId, x.Version }).IsUnique();
            e.HasIndex(x => new { x.StrategyId, x.LogicVersion, x.ParametersHash }).IsUnique();
        });

        b.Entity<BacktestRun>(e =>
        {
            e.ToTable("backtest_runs");
            e.Property(x => x.RunKind).HasMaxLength(32);
            e.Property(x => x.Label).HasMaxLength(200);
            e.Property(x => x.UniverseJson).HasColumnType("jsonb");
            e.Property(x => x.CostModelJson).HasColumnType("jsonb");
            e.Property(x => x.RiskLimitsJson).HasColumnType("jsonb");
            e.Property(x => x.UniverseHash).HasMaxLength(64);
            e.Property(x => x.DataHash).HasMaxLength(64);
            e.Property(x => x.ResultHash).HasMaxLength(64);
            e.Property(x => x.DataSource).HasMaxLength(64);
            e.Property(x => x.CodeCommit).HasMaxLength(64);
            e.Property(x => x.EngineVersion).HasMaxLength(32);
            e.Property(x => x.HaltReason).HasMaxLength(500);
            e.Property(x => x.InitialCapital).HasPrecision(20, 2);
            e.HasOne(x => x.StrategyVersion).WithMany().HasForeignKey(x => x.StrategyVersionId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Metrics).WithOne().HasForeignKey(x => x.RunId);
            e.HasMany(x => x.Trades).WithOne().HasForeignKey(x => x.RunId);
            e.HasMany(x => x.Equity).WithOne().HasForeignKey(x => x.RunId);
            e.HasIndex(x => x.ParentRunId);
        });

        b.Entity<BacktestMetric>(e =>
        {
            e.ToTable("backtest_metrics");
            e.HasKey(x => new { x.RunId, x.Basis });
            e.Property(x => x.Basis).HasConversion<string>().HasMaxLength(8);
        });

        b.Entity<BacktestTrade>(e =>
        {
            e.ToTable("backtest_trades");
            e.Property(x => x.Ticker).HasMaxLength(12);
            e.Property(x => x.EntryReason).HasMaxLength(500);
            e.Property(x => x.ExitReason).HasMaxLength(500);
            e.HasIndex(x => x.RunId);
            foreach (var p in new[] { nameof(BacktestTrade.EntryPrice), nameof(BacktestTrade.EntryReferencePrice), nameof(BacktestTrade.ExitPrice), nameof(BacktestTrade.ExitReferencePrice) })
                e.Property(p).HasPrecision(20, 4);
            foreach (var p in new[] { nameof(BacktestTrade.GrossPnl), nameof(BacktestTrade.Costs), nameof(BacktestTrade.NetPnl) })
                e.Property(p).HasPrecision(20, 2);
        });

        b.Entity<BacktestEquityPoint>(e =>
        {
            e.ToTable("backtest_equity");
            e.HasKey(x => new { x.RunId, x.Date });
            e.Property(x => x.NetEquity).HasPrecision(20, 2);
            e.Property(x => x.GrossEquity).HasPrecision(20, 2);
            e.Property(x => x.Cash).HasPrecision(20, 2);
        });

        ApplySnakeCase(b);
    }

    private static void PriceColumns(params Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<decimal>[] props)
    {
        foreach (var p in props) p.HasPrecision(18, 4);
    }

    private static void ApplySnakeCase(ModelBuilder b)
    {
        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var prop in entity.GetProperties())
                prop.SetColumnName(ToSnake(prop.Name));
            foreach (var key in entity.GetKeys())
                key.SetName(ToSnake(key.GetName() ?? ""));
            foreach (var fk in entity.GetForeignKeys())
                fk.SetConstraintName(ToSnake(fk.GetConstraintName() ?? ""));
            foreach (var ix in entity.GetIndexes())
                ix.SetDatabaseName(ToSnake(ix.GetDatabaseName() ?? ""));
        }
    }

    internal static string ToSnake(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && name[i - 1] != '_' && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
                    sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
