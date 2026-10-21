using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PolymarketWeather.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddWbSignals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "wb_signals",
                columns: table => new
                {
                    market_id = table.Column<string>(type: "TEXT", nullable: false),
                    event_id = table.Column<string>(type: "TEXT", nullable: false),
                    city = table.Column<string>(type: "TEXT", nullable: false),
                    station_id = table.Column<string>(type: "TEXT", nullable: false),
                    date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    label = table.Column<string>(type: "TEXT", nullable: false),
                    upper = table.Column<int>(type: "INTEGER", nullable: false),
                    observed_max = table.Column<int>(type: "INTEGER", nullable: false),
                    metar_ts = table.Column<long>(type: "INTEGER", nullable: false),
                    metar_received_at = table.Column<string>(type: "TEXT", nullable: true),
                    detected_at = table.Column<string>(type: "TEXT", nullable: false),
                    yes_best_bid = table.Column<double>(type: "REAL", nullable: true),
                    yes_bid_size = table.Column<double>(type: "REAL", nullable: true),
                    no_best_ask = table.Column<double>(type: "REAL", nullable: true),
                    no_ask_size = table.Column<double>(type: "REAL", nullable: true),
                    no_asks = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wb_signals", x => x.market_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_wb_signals_detected",
                table: "wb_signals",
                column: "detected_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wb_signals");
        }
    }
}
