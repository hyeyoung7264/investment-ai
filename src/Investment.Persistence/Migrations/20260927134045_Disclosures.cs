using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Investment.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Disclosures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "disclosures",
                columns: table => new
                {
                    receipt_no = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    corp_code = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    corp_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ticker = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    corp_class = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    report_name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    receipt_date = table.Column<DateOnly>(type: "date", nullable: false),
                    filer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    remark = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    disclosure_type = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    @event = table.Column<string>(name: "event", type: "character varying(32)", maxLength: 32, nullable: false),
                    is_correction = table.Column<bool>(type: "boolean", nullable: false),
                    ingested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_disclosures", x => x.receipt_no);
                });

            migrationBuilder.CreateIndex(
                name: "ix_disclosures_event_receipt_date",
                table: "disclosures",
                columns: new[] { "event", "receipt_date" });

            migrationBuilder.CreateIndex(
                name: "ix_disclosures_ticker_receipt_date",
                table: "disclosures",
                columns: new[] { "ticker", "receipt_date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "disclosures");
        }
    }
}
