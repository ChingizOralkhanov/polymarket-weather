using PolymarketWeather.Backfill;
using PolymarketWeather.Core.Forecasts;
using PolymarketWeather.Core.Markets;
using PolymarketWeather.Core.Observations;
using PolymarketWeather.Core.Polymarket.Clob;
using PolymarketWeather.Core.Polymarket.Gamma;
using PolymarketWeather.Core.Storage;

const string Usage = """
    Usage:
      backfill markets [series-slug-filter]
      backfill prices [--fidelity=<minutes>] [--kind=Max|Min] [--cities=nyc,london]
      backfill observations
      backfill forecasts [--stations=KLGA,EGLC]
    """;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddHttpClient<GammaClient>(c =>
{
    c.BaseAddress = new Uri(GammaClient.DefaultBaseAddress);
    c.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddHttpClient<ClobClient>(c =>
{
    c.BaseAddress = new Uri(ClobClient.DefaultBaseAddress);
    c.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddHttpClient<IemClient>(c =>
{
    c.BaseAddress = new Uri(IemClient.DefaultBaseAddress);
    c.Timeout = TimeSpan.FromMinutes(5);
});
builder.Services.AddHttpClient<OpenMeteoClient>(c =>
{
    c.BaseAddress = new Uri(OpenMeteoClient.PreviousRunsBaseAddress);
    c.Timeout = TimeSpan.FromMinutes(2);
});
builder.Services.AddWeatherDb(
    builder.Configuration.GetConnectionString("Weather")
    ?? throw new InvalidOperationException("ConnectionStrings:Weather is not configured"));
builder.Services.AddTransient<MarketsBackfill>();
builder.Services.AddTransient<PricesBackfill>();
builder.Services.AddTransient<ObservationsBackfill>();
builder.Services.AddTransient<ForecastsBackfill>();

using var host = builder.Build();
var positional = args.Where(a => !a.StartsWith("--")).ToList();
var options = args.Where(a => a.StartsWith("--") && a.Contains('='))
    .Select(a => a[2..].Split('=', 2))
    .ToDictionary(kv => kv[0], kv => kv[1], StringComparer.OrdinalIgnoreCase);

switch (positional.FirstOrDefault())
{
    case "markets":
        await host.Services.GetRequiredService<MarketsBackfill>().RunAsync(positional.ElementAtOrDefault(1), CancellationToken.None);
        return 0;

    case "prices":
        var fidelity = options.TryGetValue("fidelity", out var f) ? int.Parse(f) : 10;
        TemperatureKind? kind = options.TryGetValue("kind", out var k) ? Enum.Parse<TemperatureKind>(k, ignoreCase: true) : null;
        var cities = options.TryGetValue("cities", out var c)
            ? c.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        await host.Services.GetRequiredService<PricesBackfill>().RunAsync(fidelity, kind, cities, CancellationToken.None);
        return 0;

    case "observations":
        await host.Services.GetRequiredService<ObservationsBackfill>().RunAsync(CancellationToken.None);
        return 0;

    case "forecasts":
        var stations = options.TryGetValue("stations", out var s)
            ? s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        await host.Services.GetRequiredService<ForecastsBackfill>().RunAsync(stations, CancellationToken.None);
        return 0;

    default:
        Console.Error.WriteLine(Usage);
        return 1;
}
