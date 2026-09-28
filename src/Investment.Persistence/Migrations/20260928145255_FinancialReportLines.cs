using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Investment.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FinancialReportLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "financial_report_lines",
                columns: table => new
                {
                    corp_code = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    fiscal_year = table.Column<int>(type: "integer", nullable: false),
                    report_code = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    fs_div = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    account = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ticker = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    this_amount = table.Column<decimal>(type: "numeric(24,0)", precision: 24, scale: 0, nullable: true),
                    this_cumulative = table.Column<decimal>(type: "numeric(24,0)", precision: 24, scale: 0, nullable: true),
                    prior_amount = table.Column<decimal>(type: "numeric(24,0)", precision: 24, scale: 0, nullable: true),
                    prior_cumulative = table.Column<decimal>(type: "numeric(24,0)", precision: 24, scale: 0, nullable: true),
                    receipt_no = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    receipt_date = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_report_lines", x => new { x.corp_code, x.fiscal_year, x.report_code, x.fs_div, x.account });
                });

            migrationBuilder.CreateIndex(
                name: "ix_financial_report_lines_ticker_receipt_date",
                table: "financial_report_lines",
                columns: new[] { "ticker", "receipt_date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "financial_report_lines");
        }
    }
}
