using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace PolymarketWeather.Core.Storage;

public sealed class WeatherDbContext(DbContextOptions<WeatherDbContext> options) : DbContext(options)
{
    public DbSet<EventRow> Events => Set<EventRow>();
    public DbSet<BucketRow> Buckets => Set<BucketRow>();
    public DbSet<PriceRow> Prices => Set<PriceRow>();
    public DbSet<PriceFetchRow> PriceFetches => Set<PriceFetchRow>();
    public DbSet<StationRow> Stations => Set<StationRow>();
    public DbSet<ObservationRow> Observations => Set<ObservationRow>();
    public DbSet<ForecastDailyRow> ForecastDaily => Set<ForecastDailyRow>();
    public DbSet<SkippedEventRow> SkippedEvents => Set<SkippedEventRow>();
    public DbSet<CountEventRow> CountEvents => Set<CountEventRow>();
    public DbSet<CountBucketRow> CountBuckets => Set<CountBucketRow>();
    public DbSet<TrackingRow> Trackings => Set<TrackingRow>();
    public DbSet<PostRow> Posts => Set<PostRow>();
    public DbSet<BookSnapshotRow> BookSnapshots => Set<BookSnapshotRow>();
    public DbSet<MetarLiveRow> MetarLive => Set<MetarLiveRow>();
    public DbSet<WbSignalRow> WbSignals => Set<WbSignalRow>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<Enum>().HaveConversion<string>();

        // SQLite has no date-time type: keep the ISO 8601 round-trip text the data was first written with,
        // normalised to UTC so text comparison orders correctly.
        if (Database.IsSqlite())
            builder.Properties<DateTimeOffset>().HaveConversion<UtcRoundTripConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<EventRow>(e =>
        {
            e.ToTable("events").HasKey(x => x.EventId);
            e.HasIndex(x => x.Slug).IsUnique().HasDatabaseName("ix_events_slug");
            e.HasIndex(x => new { x.City, x.Kind, x.Date }).HasDatabaseName("ix_events_city_kind_date");
        });
        b.Entity<BucketRow>(e =>
        {
            e.ToTable("buckets").HasKey(x => x.MarketId);
            e.HasOne<EventRow>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => x.EventId).HasDatabaseName("ix_buckets_event");
        });
        b.Entity<PriceRow>().ToTable("prices").HasKey(x => new { x.MarketId, x.Ts });
        b.Entity<PriceFetchRow>().ToTable("price_fetches").HasKey(x => x.MarketId);
        b.Entity<StationRow>().ToTable("stations").HasKey(x => x.StationId);
        b.Entity<ObservationRow>().ToTable("observations").HasKey(x => new { x.StationId, x.Ts });
        b.Entity<ForecastDailyRow>().ToTable("forecast_daily").HasKey(x => new { x.StationId, x.Model, x.LeadDays, x.Date });
        b.Entity<SkippedEventRow>().ToTable("skipped_events").HasKey(x => x.Slug);

        b.Entity<CountEventRow>(e =>
        {
            e.ToTable("count_events").HasKey(x => x.EventId);
            e.HasIndex(x => x.Slug).IsUnique().HasDatabaseName("ix_count_events_slug");
        });
        b.Entity<CountBucketRow>(e =>
        {
            e.ToTable("count_buckets").HasKey(x => x.MarketId);
            e.HasIndex(x => x.EventId).HasDatabaseName("ix_count_buckets_event");
        });
        b.Entity<TrackingRow>().ToTable("trackings").HasKey(x => x.TrackingId);
        b.Entity<PostRow>(e =>
        {
            e.ToTable("posts").HasKey(x => x.PlatformId);
            e.HasIndex(x => new { x.Handle, x.CreatedAt }).HasDatabaseName("ix_posts_handle_created");
        });
        b.Entity<BookSnapshotRow>().ToTable("book_snapshots").HasKey(x => new { x.TokenId, x.Ts });
        b.Entity<MetarLiveRow>().ToTable("metar_live").HasKey(x => new { x.StationId, x.Ts });
        b.Entity<WbSignalRow>(e =>
        {
            e.ToTable("wb_signals").HasKey(x => x.MarketId);
            e.HasIndex(x => x.DetectedAt).HasDatabaseName("ix_wb_signals_detected");
        });

        foreach (var entity in b.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties())
                property.SetColumnName(ToSnakeCase(property.Name));
    }

    private static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
                sb.Append('_');
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }

    private sealed class UtcRoundTripConverter() : ValueConverter<DateTimeOffset, string>(
        v => v.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        v => DateTimeOffset.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
}
