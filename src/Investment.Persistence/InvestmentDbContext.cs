using System.Text;
using Investment.Domain.Market;
using Investment.Domain.Research;
using Investment.Domain.Trading;
using Microsoft.EntityFrameworkCore;

namespace Investment.Persistence;

public sealed class InvestmentDbContext(DbContextOptions<InvestmentDbContext> options) : DbContext(options)
{
    public DbSet<Security> Securities => Set<Security>();
    public DbSet<DailyPrice> DailyPrices => Set<DailyPrice>();
    public DbSet<IndexPrice> IndexPrices => Set<IndexPrice>();
    public DbSet<IngestionRun> IngestionRuns => Set<IngestionRun>();
    public DbSet<SplitEvent> SplitEvents => Set<SplitEvent>();
    public DbSet<Disclosure> Disclosures => Set<Disclosure>();
    public DbSet<FinancialReportLine> FinancialReportLines => Set<FinancialReportLine>();
    public DbSet<StrategyDefinition> Strategies => Set<StrategyDefinition>();
    public DbSet<StrategyVersion> StrategyVersions => Set<StrategyVersion>();
    public DbSet<BacktestRun> BacktestRuns => Set<BacktestRun>();
    public DbSet<BacktestMetric> BacktestMetrics => Set<BacktestMetric>();
    public DbSet<BacktestTrade> BacktestTrades => Set<BacktestTrade>();
    public DbSet<BacktestEquityPoint> BacktestEquity => Set<BacktestEquityPoint>();
    public DbSet<MarketRegimeDay> MarketRegimes => Set<MarketRegimeDay>();
    public DbSet<ResearchStudy> ResearchStudies => Set<ResearchStudy>();
    public DbSet<StrategyEvaluation> StrategyEvaluations => Set<StrategyEvaluation>();
    public DbSet<ExperimentFailure> ExperimentFailures => Set<ExperimentFailure>();
    public DbSet<PaperSession> PaperSessions => Set<PaperSession>();
    public DbSet<PaperOrder> PaperOrders => Set<PaperOrder>();
    public DbSet<PaperTrade> PaperTrades => Set<PaperTrade>();
    public DbSet<PaperEquityPoint> PaperEquity => Set<PaperEquityPoint>();
    public DbSet<PaperRunLog> PaperRuns => Set<PaperRunLog>();

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

        b.Entity<Disclosure>(e =>
        {
            e.ToTable("disclosures");
            e.HasKey(x => x.ReceiptNo);
            e.Property(x => x.ReceiptNo).HasMaxLength(20);
            e.Property(x => x.CorpCode).HasMaxLength(12);
            e.Property(x => x.CorpName).HasMaxLength(200);
            e.Property(x => x.Ticker).HasMaxLength(12);
            e.Property(x => x.CorpClass).HasMaxLength(2);
            e.Property(x => x.ReportName).HasMaxLength(500);
            e.Property(x => x.Filer).HasMaxLength(200);
            e.Property(x => x.Remark).HasMaxLength(20);
            e.Property(x => x.DisclosureType).HasMaxLength(2);
            e.Property(x => x.Event).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(x => new { x.Ticker, x.ReceiptDate });
            e.HasIndex(x => new { x.Event, x.ReceiptDate });
        });

        b.Entity<FinancialReportLine>(e =>
        {
            e.ToTable("financial_report_lines");
            e.HasKey(x => new { x.CorpCode, x.FiscalYear, x.ReportCode, x.FsDiv, x.Account });
            e.Property(x => x.CorpCode).HasMaxLength(12);
            e.Property(x => x.Ticker).HasMaxLength(12);
            e.Property(x => x.ReportCode).HasMaxLength(8);
            e.Property(x => x.FsDiv).HasMaxLength(4);
            e.Property(x => x.Account).HasMaxLength(32);
            e.Property(x => x.ReceiptNo).HasMaxLength(20);
            foreach (var p in new[] { nameof(FinancialReportLine.ThisAmount), nameof(FinancialReportLine.ThisCumulative), nameof(FinancialReportLine.PriorAmount), nameof(FinancialReportLine.PriorCumulative) })
                e.Property(p).HasPrecision(24, 0);
            e.HasIndex(x => new { x.Ticker, x.ReceiptDate });
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
            e.HasIndex(x => x.StudyId);
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

        b.Entity<MarketRegimeDay>(e =>
        {
            e.ToTable("market_regimes");
            e.HasKey(x => new { x.IndexCode, x.Date });
            e.Property(x => x.IndexCode).HasMaxLength(16);
            e.Property(x => x.Trend).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Volatility).HasConversion<string>().HasMaxLength(16);
        });

        b.Entity<ResearchStudy>(e =>
        {
            e.ToTable("research_studies");
            e.Property(x => x.StrategyId).HasMaxLength(64);
            e.Property(x => x.Kind).HasMaxLength(32);
            e.Property(x => x.CodeCommit).HasMaxLength(64);
            e.Property(x => x.PlanJson).HasColumnType("jsonb");
            e.Property(x => x.SummaryJson).HasColumnType("jsonb");
            e.HasIndex(x => x.StrategyId);
        });

        b.Entity<StrategyEvaluation>(e =>
        {
            e.ToTable("strategy_evaluations");
            e.Property(x => x.Stage).HasMaxLength(32);
            e.Property(x => x.FromStatus).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.ToStatus).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Decision).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.EvidenceJson).HasColumnType("jsonb");
            e.Property(x => x.CriteriaJson).HasColumnType("jsonb");
            e.HasOne<StrategyVersion>().WithMany().HasForeignKey(x => x.StrategyVersionId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.StrategyVersionId);
        });

        b.Entity<ExperimentFailure>(e =>
        {
            e.ToTable("experiment_failures");
            e.Property(x => x.StrategyId).HasMaxLength(64);
            e.Property(x => x.ParametersJson).HasColumnType("jsonb");
            e.Property(x => x.ResultJson).HasColumnType("jsonb");
            e.HasOne<StrategyVersion>().WithMany().HasForeignKey(x => x.StrategyVersionId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.StrategyId);
        });

        b.Entity<PaperSession>(e =>
        {
            e.ToTable("paper_sessions");
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.StrategyId).HasMaxLength(64);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.InitialCapital).HasPrecision(20, 2);
            foreach (var p in new[] { nameof(PaperSession.ParametersJson), nameof(PaperSession.UniverseJson), nameof(PaperSession.CostModelJson),
                         nameof(PaperSession.RiskLimitsJson), nameof(PaperSession.StateJson), nameof(PaperSession.BlockedRegimesJson) })
                e.Property(p).HasColumnType("jsonb");
            e.Property(x => x.StoppedReason).HasMaxLength(500);
            e.HasOne<StrategyVersion>().WithMany().HasForeignKey(x => x.StrategyVersionId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<PaperOrder>(e =>
        {
            e.ToTable("paper_orders");
            e.Property(x => x.Ticker).HasMaxLength(12);
            e.Property(x => x.Side).HasMaxLength(8);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.MarketCondition).HasMaxLength(64);
            e.Property(x => x.SignalPrice).HasPrecision(20, 4);
            e.Property(x => x.FillPrice).HasPrecision(20, 4);
            e.HasIndex(x => new { x.SessionId, x.SignalDate });
        });

        b.Entity<PaperTrade>(e =>
        {
            e.ToTable("paper_trades");
            e.Property(x => x.StrategyId).HasMaxLength(64);
            e.Property(x => x.Ticker).HasMaxLength(12);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.ExitReason).HasMaxLength(500);
            e.Property(x => x.Result).HasMaxLength(8);
            e.Property(x => x.MarketCondition).HasMaxLength(64);
            foreach (var p in new[] { nameof(PaperTrade.SignalPrice), nameof(PaperTrade.EntryPrice), nameof(PaperTrade.ExitPrice), nameof(PaperTrade.StopLoss), nameof(PaperTrade.TakeProfit) })
                e.Property(p).HasPrecision(20, 4);
            e.Property(x => x.Costs).HasPrecision(20, 2);
            e.Property(x => x.NetPnl).HasPrecision(20, 2);
            e.HasIndex(x => x.SessionId);
        });

        b.Entity<PaperEquityPoint>(e =>
        {
            e.ToTable("paper_equity");
            e.HasKey(x => new { x.SessionId, x.Date });
            e.Property(x => x.NetEquity).HasPrecision(20, 2);
            e.Property(x => x.GrossEquity).HasPrecision(20, 2);
            e.Property(x => x.Cash).HasPrecision(20, 2);
            e.Property(x => x.Regime).HasMaxLength(64);
        });

        b.Entity<PaperRunLog>(e =>
        {
            e.ToTable("paper_runs");
            e.Property(x => x.DataHash).HasMaxLength(64);
            e.Property(x => x.CodeCommit).HasMaxLength(64);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.HasIndex(x => x.SessionId);
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
