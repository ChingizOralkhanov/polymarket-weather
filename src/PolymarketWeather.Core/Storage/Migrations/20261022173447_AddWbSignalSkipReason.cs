using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PolymarketWeather.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddWbSignalSkipReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "skip_reason",
                table: "wb_signals",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "skip_reason",
                table: "wb_signals");
        }
    }
}
