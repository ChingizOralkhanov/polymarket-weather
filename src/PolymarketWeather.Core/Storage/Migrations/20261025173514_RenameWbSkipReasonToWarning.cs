using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PolymarketWeather.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class RenameWbSkipReasonToWarning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "skip_reason",
                table: "wb_signals",
                newName: "warning");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "warning",
                table: "wb_signals",
                newName: "skip_reason");
        }
    }
}
