using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace PolymarketWeather.Core.Storage;

/// <summary>
/// SQLite files created before EF Core migrations already hold the initial schema. Records the initial
/// migration as applied for them, so MigrateAsync only runs the migrations that came after it.
/// </summary>
internal static class SchemaBaseline
{
    public static async Task ApplyAsync(WeatherDbContext db, CancellationToken ct)
    {
        if (!db.Database.IsSqlite())
            return;

        var history = db.GetService<IHistoryRepository>();
        if ((await db.Database.GetAppliedMigrationsAsync(ct)).Any() || !await HasLegacyTablesAsync(db, ct))
            return;

        await db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript(), ct);
        await db.Database.ExecuteSqlRawAsync(
            history.GetInsertScript(new HistoryRow(db.Database.GetMigrations().First(), ProductInfo.GetVersion())), ct);
    }

    private static async Task<bool> HasLegacyTablesAsync(WeatherDbContext db, CancellationToken ct) =>
        await db.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table' AND name = 'events'")
            .AnyAsync(ct);
}
