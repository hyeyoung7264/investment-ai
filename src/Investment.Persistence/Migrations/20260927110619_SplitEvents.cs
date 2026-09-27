using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Investment.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SplitEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "split_events",
                columns: table => new
                {
                    ticker = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    event_date = table.Column<DateOnly>(type: "date", nullable: false),
                    price_factor = table.Column<double>(type: "double precision", nullable: false),
                    source_volume_adjusted = table.Column<bool>(type: "boolean", nullable: false),
                    volume_correction = table.Column<double>(type: "double precision", nullable: false),
                    checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_split_events", x => new { x.ticker, x.event_date });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "split_events");
        }
    }
}
