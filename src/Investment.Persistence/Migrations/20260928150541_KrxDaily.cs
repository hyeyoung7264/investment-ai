using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Investment.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class KrxDaily : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "krx_daily",
                columns: table => new
                {
                    ticker = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    market = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    open = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    high = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    low = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    close = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    volume = table.Column<long>(type: "bigint", nullable: false),
                    trading_value = table.Column<decimal>(type: "numeric(24,0)", precision: 24, scale: 0, nullable: false),
                    market_cap = table.Column<decimal>(type: "numeric(24,0)", precision: 24, scale: 0, nullable: false),
                    listed_shares = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_krx_daily", x => new { x.ticker, x.date });
                });

            migrationBuilder.CreateIndex(
                name: "ix_krx_daily_date",
                table: "krx_daily",
                column: "date");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "krx_daily");
        }
    }
}
