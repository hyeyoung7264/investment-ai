using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Investment.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ResearchEvaluation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "study_id",
                table: "backtest_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "experiment_failures",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    strategy_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    strategy_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    evaluation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    hypothesis = table.Column<string>(type: "text", nullable: false),
                    parameters_json = table.Column<string>(type: "jsonb", nullable: false),
                    test_from = table.Column<DateOnly>(type: "date", nullable: false),
                    test_to = table.Column<DateOnly>(type: "date", nullable: false),
                    result_json = table.Column<string>(type: "jsonb", nullable: false),
                    failure_reason = table.Column<string>(type: "text", nullable: false),
                    regime_notes = table.Column<string>(type: "text", nullable: true),
                    rejected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_experiment_failures", x => x.id);
                    table.ForeignKey(
                        name: "fk_experiment_failures_strategy_versions_strategy_version_id",
                        column: x => x.strategy_version_id,
                        principalTable: "strategy_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "market_regimes",
                columns: table => new
                {
                    index_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    trend = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    volatility = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    close = table.Column<double>(type: "double precision", nullable: false),
                    sma200 = table.Column<double>(type: "double precision", nullable: false),
                    realized_vol20 = table.Column<double>(type: "double precision", nullable: false),
                    vol_threshold = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_market_regimes", x => new { x.index_code, x.date });
                });

            migrationBuilder.CreateTable(
                name: "research_studies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    strategy_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    hypothesis = table.Column<string>(type: "text", nullable: false),
                    plan_json = table.Column<string>(type: "jsonb", nullable: false),
                    variants_tried = table.Column<int>(type: "integer", nullable: false),
                    summary_json = table.Column<string>(type: "jsonb", nullable: true),
                    code_commit = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_research_studies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "strategy_evaluations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    strategy_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    study_id = table.Column<Guid>(type: "uuid", nullable: true),
                    stage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    from_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    to_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    decision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    evidence_json = table.Column<string>(type: "jsonb", nullable: false),
                    criteria_json = table.Column<string>(type: "jsonb", nullable: false),
                    reasons = table.Column<string>(type: "text", nullable: false),
                    evaluated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_strategy_evaluations", x => x.id);
                    table.ForeignKey(
                        name: "fk_strategy_evaluations_strategy_versions_strategy_version_id",
                        column: x => x.strategy_version_id,
                        principalTable: "strategy_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_backtest_runs_study_id",
                table: "backtest_runs",
                column: "study_id");

            migrationBuilder.CreateIndex(
                name: "ix_experiment_failures_strategy_id",
                table: "experiment_failures",
                column: "strategy_id");

            migrationBuilder.CreateIndex(
                name: "ix_experiment_failures_strategy_version_id",
                table: "experiment_failures",
                column: "strategy_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_research_studies_strategy_id",
                table: "research_studies",
                column: "strategy_id");

            migrationBuilder.CreateIndex(
                name: "ix_strategy_evaluations_strategy_version_id",
                table: "strategy_evaluations",
                column: "strategy_version_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "experiment_failures");

            migrationBuilder.DropTable(
                name: "market_regimes");

            migrationBuilder.DropTable(
                name: "research_studies");

            migrationBuilder.DropTable(
                name: "strategy_evaluations");

            migrationBuilder.DropIndex(
                name: "ix_backtest_runs_study_id",
                table: "backtest_runs");

            migrationBuilder.DropColumn(
                name: "study_id",
                table: "backtest_runs");
        }
    }
}
