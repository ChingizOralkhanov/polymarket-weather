using PolymarketWeather.Backfill;
using PolymarketWeather.Core.Polymarket.Clob;
using PolymarketWeather.Core.Polymarket.Gamma;
using PolymarketWeather.Core.Storage;

const string Usage = """
    Usage:
      backfill markets [series-slug-filter]
      backfill prices [--fidelity=<minutes>] [--kind=Max|Min] [--cities=nyc,london]
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
builder.Services.AddSingleton(_ => new WeatherDb(
    builder.Configuration.GetConnectionString("Weather")
    ?? throw new InvalidOperationException("ConnectionStrings:Weather is not configured")));
builder.Services.AddTransient<MarketsBackfill>();
builder.Services.AddTransient<PricesBackfill>();

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
        var kind = options.GetValueOrDefault("kind");
        var cities = options.TryGetValue("cities", out var c)
            ? c.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        await host.Services.GetRequiredService<PricesBackfill>().RunAsync(fidelity, kind, cities, CancellationToken.None);
        return 0;

    default:
        Console.Error.WriteLine(Usage);
        return 1;
}
