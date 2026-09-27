using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Investment.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PaperTrading : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "paper_equity",
                columns: table => new
                {
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    net_equity = table.Column<decimal>(type: "numeric(20,2)", precision: 20, scale: 2, nullable: false),
                    gross_equity = table.Column<decimal>(type: "numeric(20,2)", precision: 20, scale: 2, nullable: false),
                    cash = table.Column<decimal>(type: "numeric(20,2)", precision: 20, scale: 2, nullable: false),
                    positions = table.Column<int>(type: "integer", nullable: false),
                    regime = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_paper_equity", x => new { x.session_id, x.date });
                });

            migrationBuilder.CreateTable(
                name: "paper_orders",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticker = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    side = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    quantity = table.Column<long>(type: "bigint", nullable: false),
                    signal_date = table.Column<DateOnly>(type: "date", nullable: false),
                    signal_price = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    score = table.Column<double>(type: "double precision", nullable: false),
                    market_condition = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    expected_return = table.Column<double>(type: "double precision", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    fill_date = table.Column<DateOnly>(type: "date", nullable: true),
                    fill_price = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: true),
                    filled_quantity = table.Column<long>(type: "bigint", nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_paper_orders", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "paper_runs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_from = table.Column<DateOnly>(type: "date", nullable: true),
                    processed_to = table.Column<DateOnly>(type: "date", nullable: true),
                    sessions_processed = table.Column<int>(type: "integer", nullable: false),
                    data_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    code_commit = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_paper_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "paper_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    strategy_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    strategy_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parameters_json = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    initial_capital = table.Column<decimal>(type: "numeric(20,2)", precision: 20, scale: 2, nullable: false),
                    universe_json = table.Column<string>(type: "jsonb", nullable: false),
                    cost_model_json = table.Column<string>(type: "jsonb", nullable: false),
                    risk_limits_json = table.Column<string>(type: "jsonb", nullable: false),
                    state_json = table.Column<string>(type: "jsonb", nullable: false),
                    last_processed_date = table.Column<DateOnly>(type: "date", nullable: true),
                    expected_ev_per_trade = table.Column<double>(type: "double precision", nullable: false),
                    blocked_regimes_json = table.Column<string>(type: "jsonb", nullable: false),
                    evidence_evaluation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    stopped_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_paper_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_paper_sessions_strategy_versions_strategy_version_id",
                        column: x => x.strategy_version_id,
                        principalTable: "strategy_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "paper_trades",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    strategy_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ticker = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    signal_time = table.Column<DateOnly>(type: "date", nullable: false),
                    signal_price = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    entry_time = table.Column<DateOnly>(type: "date", nullable: false),
                    entry_price = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    exit_time = table.Column<DateOnly>(type: "date", nullable: false),
                    exit_price = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    quantity = table.Column<long>(type: "bigint", nullable: false),
                    expected_return = table.Column<double>(type: "double precision", nullable: false),
                    actual_return = table.Column<double>(type: "double precision", nullable: false),
                    gross_return = table.Column<double>(type: "double precision", nullable: false),
                    stop_loss = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: true),
                    take_profit = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: true),
                    market_condition = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    exit_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    result = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    costs = table.Column<decimal>(type: "numeric(20,2)", precision: 20, scale: 2, nullable: false),
                    net_pnl = table.Column<decimal>(type: "numeric(20,2)", precision: 20, scale: 2, nullable: false),
                    holding_sessions = table.Column<int>(type: "integer", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_paper_trades", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_paper_orders_session_id_signal_date",
                table: "paper_orders",
                columns: new[] { "session_id", "signal_date" });

            migrationBuilder.CreateIndex(
                name: "ix_paper_runs_session_id",
                table: "paper_runs",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_paper_sessions_strategy_version_id",
                table: "paper_sessions",
                column: "strategy_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_paper_trades_session_id",
                table: "paper_trades",
                column: "session_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "paper_equity");

            migrationBuilder.DropTable(
                name: "paper_orders");

            migrationBuilder.DropTable(
                name: "paper_runs");

            migrationBuilder.DropTable(
                name: "paper_sessions");

            migrationBuilder.DropTable(
                name: "paper_trades");
        }
    }
}
