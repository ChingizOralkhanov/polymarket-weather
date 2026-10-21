using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PolymarketWeather.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "book_snapshots",
                columns: table => new
                {
                    token_id = table.Column<string>(type: "TEXT", nullable: false),
                    ts = table.Column<long>(type: "INTEGER", nullable: false),
                    market_id = table.Column<string>(type: "TEXT", nullable: false),
                    best_bid = table.Column<double>(type: "REAL", nullable: true),
                    best_ask = table.Column<double>(type: "REAL", nullable: true),
                    bid_size = table.Column<double>(type: "REAL", nullable: true),
                    ask_size = table.Column<double>(type: "REAL", nullable: true),
                    bids = table.Column<string>(type: "TEXT", nullable: false),
                    asks = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_book_snapshots", x => new { x.token_id, x.ts });
                });

            migrationBuilder.CreateTable(
                name: "count_buckets",
                columns: table => new
                {
                    market_id = table.Column<string>(type: "TEXT", nullable: false),
                    event_id = table.Column<string>(type: "TEXT", nullable: false),
                    position = table.Column<int>(type: "INTEGER", nullable: false),
                    condition_id = table.Column<string>(type: "TEXT", nullable: false),
                    yes_token_id = table.Column<string>(type: "TEXT", nullable: false),
                    no_token_id = table.Column<string>(type: "TEXT", nullable: false),
                    label = table.Column<string>(type: "TEXT", nullable: false),
                    lower = table.Column<int>(type: "INTEGER", nullable: true),
                    upper = table.Column<int>(type: "INTEGER", nullable: true),
                    outcome = table.Column<string>(type: "TEXT", nullable: false),
                    closed_time = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_count_buckets", x => x.market_id);
                });

            migrationBuilder.CreateTable(
                name: "count_events",
                columns: table => new
                {
                    event_id = table.Column<string>(type: "TEXT", nullable: false),
                    slug = table.Column<string>(type: "TEXT", nullable: false),
                    series_id = table.Column<string>(type: "TEXT", nullable: true),
                    title = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<string>(type: "TEXT", nullable: true),
                    end_date = table.Column<string>(type: "TEXT", nullable: true),
                    closed = table.Column<bool>(type: "INTEGER", nullable: false),
                    volume = table.Column<double>(type: "REAL", nullable: true),
                    winning_market_id = table.Column<string>(type: "TEXT", nullable: true),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    updated_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_count_events", x => x.event_id);
                });

            migrationBuilder.CreateTable(
                name: "events",
                columns: table => new
                {
                    event_id = table.Column<string>(type: "TEXT", nullable: false),
                    slug = table.Column<string>(type: "TEXT", nullable: false),
                    series_id = table.Column<string>(type: "TEXT", nullable: true),
                    city = table.Column<string>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    unit = table.Column<string>(type: "TEXT", nullable: false),
                    source_kind = table.Column<string>(type: "TEXT", nullable: false),
                    station_id = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<string>(type: "TEXT", nullable: true),
                    end_date = table.Column<string>(type: "TEXT", nullable: true),
                    closed = table.Column<bool>(type: "INTEGER", nullable: false),
                    volume = table.Column<double>(type: "REAL", nullable: true),
                    winning_market_id = table.Column<string>(type: "TEXT", nullable: true),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    updated_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_events", x => x.event_id);
                });

            migrationBuilder.CreateTable(
                name: "forecast_daily",
                columns: table => new
                {
                    station_id = table.Column<string>(type: "TEXT", nullable: false),
                    model = table.Column<string>(type: "TEXT", nullable: false),
                    lead_days = table.Column<int>(type: "INTEGER", nullable: false),
                    date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    tmax_c = table.Column<double>(type: "REAL", nullable: true),
                    tmin_c = table.Column<double>(type: "REAL", nullable: true),
                    hours = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_forecast_daily", x => new { x.station_id, x.model, x.lead_days, x.date });
                });

            migrationBuilder.CreateTable(
                name: "metar_live",
                columns: table => new
                {
                    station_id = table.Column<string>(type: "TEXT", nullable: false),
                    ts = table.Column<long>(type: "INTEGER", nullable: false),
                    received_at = table.Column<string>(type: "TEXT", nullable: true),
                    collected_at = table.Column<string>(type: "TEXT", nullable: false),
                    temp_c = table.Column<double>(type: "REAL", nullable: true),
                    raw = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metar_live", x => new { x.station_id, x.ts });
                });

            migrationBuilder.CreateTable(
                name: "observations",
                columns: table => new
                {
                    station_id = table.Column<string>(type: "TEXT", nullable: false),
                    ts = table.Column<long>(type: "INTEGER", nullable: false),
                    temp_c = table.Column<double>(type: "REAL", nullable: true),
                    temp_f = table.Column<double>(type: "REAL", nullable: true),
                    metar = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_observations", x => new { x.station_id, x.ts });
                });

            migrationBuilder.CreateTable(
                name: "posts",
                columns: table => new
                {
                    platform_id = table.Column<string>(type: "TEXT", nullable: false),
                    handle = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<string>(type: "TEXT", nullable: false),
                    imported_at = table.Column<string>(type: "TEXT", nullable: true),
                    collected_at = table.Column<string>(type: "TEXT", nullable: false),
                    content = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_posts", x => x.platform_id);
                });

            migrationBuilder.CreateTable(
                name: "price_fetches",
                columns: table => new
                {
                    market_id = table.Column<string>(type: "TEXT", nullable: false),
                    fidelity = table.Column<int>(type: "INTEGER", nullable: false),
                    points = table.Column<int>(type: "INTEGER", nullable: false),
                    fetched_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_price_fetches", x => x.market_id);
                });

            migrationBuilder.CreateTable(
                name: "prices",
                columns: table => new
                {
                    market_id = table.Column<string>(type: "TEXT", nullable: false),
                    ts = table.Column<long>(type: "INTEGER", nullable: false),
                    price = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_prices", x => new { x.market_id, x.ts });
                });

            migrationBuilder.CreateTable(
                name: "skipped_events",
                columns: table => new
                {
                    slug = table.Column<string>(type: "TEXT", nullable: false),
                    event_id = table.Column<string>(type: "TEXT", nullable: true),
                    error = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skipped_events", x => x.slug);
                });

            migrationBuilder.CreateTable(
                name: "stations",
                columns: table => new
                {
                    station_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    latitude = table.Column<double>(type: "REAL", nullable: false),
                    longitude = table.Column<double>(type: "REAL", nullable: false),
                    elevation = table.Column<double>(type: "REAL", nullable: true),
                    time_zone = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stations", x => x.station_id);
                });

            migrationBuilder.CreateTable(
                name: "trackings",
                columns: table => new
                {
                    tracking_id = table.Column<string>(type: "TEXT", nullable: false),
                    handle = table.Column<string>(type: "TEXT", nullable: false),
                    title = table.Column<string>(type: "TEXT", nullable: false),
                    period_start = table.Column<string>(type: "TEXT", nullable: false),
                    period_end = table.Column<string>(type: "TEXT", nullable: false),
                    market_slug = table.Column<string>(type: "TEXT", nullable: true),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trackings", x => x.tracking_id);
                });

            migrationBuilder.CreateTable(
                name: "buckets",
                columns: table => new
                {
                    market_id = table.Column<string>(type: "TEXT", nullable: false),
                    event_id = table.Column<string>(type: "TEXT", nullable: false),
                    position = table.Column<int>(type: "INTEGER", nullable: false),
                    condition_id = table.Column<string>(type: "TEXT", nullable: false),
                    yes_token_id = table.Column<string>(type: "TEXT", nullable: false),
                    no_token_id = table.Column<string>(type: "TEXT", nullable: false),
                    label = table.Column<string>(type: "TEXT", nullable: false),
                    lower = table.Column<int>(type: "INTEGER", nullable: true),
                    upper = table.Column<int>(type: "INTEGER", nullable: true),
                    outcome = table.Column<string>(type: "TEXT", nullable: false),
                    closed_time = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_buckets", x => x.market_id);
                    table.ForeignKey(
                        name: "FK_buckets_events_event_id",
                        column: x => x.event_id,
                        principalTable: "events",
                        principalColumn: "event_id");
                });

            migrationBuilder.CreateIndex(
                name: "ix_buckets_event",
                table: "buckets",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "ix_count_buckets_event",
                table: "count_buckets",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "ix_count_events_slug",
                table: "count_events",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_events_city_kind_date",
                table: "events",
                columns: new[] { "city", "kind", "date" });

            migrationBuilder.CreateIndex(
                name: "ix_events_slug",
                table: "events",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_posts_handle_created",
                table: "posts",
                columns: new[] { "handle", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "book_snapshots");

            migrationBuilder.DropTable(
                name: "buckets");

            migrationBuilder.DropTable(
                name: "count_buckets");

            migrationBuilder.DropTable(
                name: "count_events");

            migrationBuilder.DropTable(
                name: "forecast_daily");

            migrationBuilder.DropTable(
                name: "metar_live");

            migrationBuilder.DropTable(
                name: "observations");

            migrationBuilder.DropTable(
                name: "posts");

            migrationBuilder.DropTable(
                name: "price_fetches");

            migrationBuilder.DropTable(
                name: "prices");

            migrationBuilder.DropTable(
                name: "skipped_events");

            migrationBuilder.DropTable(
                name: "stations");

            migrationBuilder.DropTable(
                name: "trackings");

            migrationBuilder.DropTable(
                name: "events");
        }
    }
}
