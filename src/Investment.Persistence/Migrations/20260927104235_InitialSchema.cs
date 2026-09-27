using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Investment.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "daily_prices",
                columns: table => new
                {
                    ticker = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    open = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    high = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    low = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    close = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    volume = table.Column<long>(type: "bigint", nullable: false),
                    trading_value_estimate = table.Column<decimal>(type: "numeric(24,2)", precision: 24, scale: 2, nullable: false),
                    is_halted = table.Column<bool>(type: "boolean", nullable: false),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ingested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_daily_prices", x => new { x.ticker, x.date });
                });

            migrationBuilder.CreateTable(
                name: "index_prices",
                columns: table => new
                {
                    index_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    open = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    high = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    low = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    close = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    volume = table.Column<long>(type: "bigint", nullable: false),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ingested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_index_prices", x => new { x.index_code, x.date });
                });

            migrationBuilder.CreateTable(
                name: "ingestion_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    items_requested = table.Column<int>(type: "integer", nullable: false),
                    items_failed = table.Column<int>(type: "integer", nullable: false),
                    rows_upserted = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ingestion_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "securities",
                columns: table => new
                {
                    ticker = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    market = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    sector = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    listed_date = table.Column<DateOnly>(type: "date", nullable: true),
                    delisted_date = table.Column<DateOnly>(type: "date", nullable: true),
                    delisting_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_securities", x => x.ticker);
                });

            migrationBuilder.CreateTable(
                name: "strategies",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    family = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    hypothesis = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_strategies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "strategy_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    strategy_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    logic_version = table.Column<int>(type: "integer", nullable: false),
                    parameters_json = table.Column<string>(type: "jsonb", nullable: false),
                    parameters_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_strategy_versions", x => x.id);
                    table.ForeignKey(
                        name: "fk_strategy_versions_strategies_strategy_id",
                        column: x => x.strategy_id,
                        principalTable: "strategies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "backtest_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    strategy_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    parent_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    initial_capital = table.Column<decimal>(type: "numeric(20,2)", precision: 20, scale: 2, nullable: false),
                    universe_json = table.Column<string>(type: "jsonb", nullable: false),
                    universe_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    cost_model_json = table.Column<string>(type: "jsonb", nullable: false),
                    risk_limits_json = table.Column<string>(type: "jsonb", nullable: false),
                    data_source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    data_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    code_commit = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    code_dirty = table.Column<bool>(type: "boolean", nullable: false),
                    engine_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    result_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    halted_on = table.Column<DateOnly>(type: "date", nullable: true),
                    halt_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    duration_ms = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_backtest_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_backtest_runs_strategy_versions_strategy_version_id",
                        column: x => x.strategy_version_id,
                        principalTable: "strategy_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "backtest_equity",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    net_equity = table.Column<decimal>(type: "numeric(20,2)", precision: 20, scale: 2, nullable: false),
                    gross_equity = table.Column<decimal>(type: "numeric(20,2)", precision: 20, scale: 2, nullable: false),
                    cash = table.Column<decimal>(type: "numeric(20,2)", precision: 20, scale: 2, nullable: false),
                    positions = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_backtest_equity", x => new { x.run_id, x.date });
                    table.ForeignKey(
                        name: "fk_backtest_equity_backtest_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "backtest_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "backtest_metrics",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    basis = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    total_return = table.Column<double>(type: "double precision", nullable: false),
                    cagr = table.Column<double>(type: "double precision", nullable: false),
                    avg_daily_return = table.Column<double>(type: "double precision", nullable: false),
                    daily_return_stdev = table.Column<double>(type: "double precision", nullable: false),
                    win_rate = table.Column<double>(type: "double precision", nullable: false),
                    profit_factor = table.Column<double>(type: "double precision", nullable: false),
                    avg_profit = table.Column<double>(type: "double precision", nullable: false),
                    avg_loss = table.Column<double>(type: "double precision", nullable: false),
                    expected_value_per_trade = table.Column<double>(type: "double precision", nullable: false),
                    expected_value_per_trade_krw = table.Column<double>(type: "double precision", nullable: false),
                    expected_value_t_stat = table.Column<double>(type: "double precision", nullable: false),
                    max_drawdown = table.Column<double>(type: "double precision", nullable: false),
                    sharpe = table.Column<double>(type: "double precision", nullable: false),
                    sortino = table.Column<double>(type: "double precision", nullable: false),
                    number_of_trades = table.Column<int>(type: "integer", nullable: false),
                    annual_turnover = table.Column<double>(type: "double precision", nullable: false),
                    avg_holding_sessions = table.Column<double>(type: "double precision", nullable: false),
                    exposure = table.Column<double>(type: "double precision", nullable: false),
                    total_costs = table.Column<double>(type: "double precision", nullable: false),
                    benchmark_return = table.Column<double>(type: "double precision", nullable: false),
                    trading_days = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_backtest_metrics", x => new { x.run_id, x.basis });
                    table.ForeignKey(
                        name: "fk_backtest_metrics_backtest_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "backtest_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "backtest_trades",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticker = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    entry_signal_date = table.Column<DateOnly>(type: "date", nullable: false),
                    entry_date = table.Column<DateOnly>(type: "date", nullable: false),
                    entry_price = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    entry_reference_price = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    exit_date = table.Column<DateOnly>(type: "date", nullable: false),
                    exit_price = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    exit_reference_price = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    quantity = table.Column<long>(type: "bigint", nullable: false),
                    gross_pnl = table.Column<decimal>(type: "numeric(20,2)", precision: 20, scale: 2, nullable: false),
                    costs = table.Column<decimal>(type: "numeric(20,2)", precision: 20, scale: 2, nullable: false),
                    net_pnl = table.Column<decimal>(type: "numeric(20,2)", precision: 20, scale: 2, nullable: false),
                    gross_return = table.Column<double>(type: "double precision", nullable: false),
                    net_return = table.Column<double>(type: "double precision", nullable: false),
                    holding_sessions = table.Column<int>(type: "integer", nullable: false),
                    entry_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    exit_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    entry_score = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_backtest_trades", x => x.id);
                    table.ForeignKey(
                        name: "fk_backtest_trades_backtest_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "backtest_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_backtest_runs_parent_run_id",
                table: "backtest_runs",
                column: "parent_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_backtest_runs_strategy_version_id",
                table: "backtest_runs",
                column: "strategy_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_backtest_trades_run_id",
                table: "backtest_trades",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ix_daily_prices_date",
                table: "daily_prices",
                column: "date");

            migrationBuilder.CreateIndex(
                name: "ix_strategy_versions_strategy_id_logic_version_parameters_hash",
                table: "strategy_versions",
                columns: new[] { "strategy_id", "logic_version", "parameters_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_strategy_versions_strategy_id_version",
                table: "strategy_versions",
                columns: new[] { "strategy_id", "version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "backtest_equity");

            migrationBuilder.DropTable(
                name: "backtest_metrics");

            migrationBuilder.DropTable(
                name: "backtest_trades");

            migrationBuilder.DropTable(
                name: "daily_prices");

            migrationBuilder.DropTable(
                name: "index_prices");

            migrationBuilder.DropTable(
                name: "ingestion_runs");

            migrationBuilder.DropTable(
                name: "securities");

            migrationBuilder.DropTable(
                name: "backtest_runs");

            migrationBuilder.DropTable(
                name: "strategy_versions");

            migrationBuilder.DropTable(
                name: "strategies");
        }
    }
}
