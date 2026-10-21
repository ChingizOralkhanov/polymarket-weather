using PolymarketWeather.Collector;
using PolymarketWeather.Core.Musk;
using PolymarketWeather.Core.Observations;
using PolymarketWeather.Core.Polymarket.Clob;
using PolymarketWeather.Core.Polymarket.Gamma;
using PolymarketWeather.Core.Storage;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddHttpClient<GammaClient>(c =>
{
    c.BaseAddress = new Uri(GammaClient.DefaultBaseAddress);
    c.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddHttpClient<ClobClient>(c =>
{
    c.BaseAddress = new Uri(ClobClient.DefaultBaseAddress);
    c.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHttpClient<XTrackerClient>(c =>
{
    c.BaseAddress = new Uri(XTrackerClient.DefaultBaseAddress);
    c.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHttpClient<AviationWeatherClient>(c =>
{
    c.BaseAddress = new Uri(AviationWeatherClient.DefaultBaseAddress);
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("polymarket-weather-collector/0.1");
});
builder.Services.AddWeatherDb(
    builder.Configuration.GetConnectionString("Weather")
    ?? throw new InvalidOperationException("ConnectionStrings:Weather is not configured"));
builder.Services.Configure<CollectorOptions>(builder.Configuration.GetSection(CollectorOptions.Section));

builder.Services.AddHostedService<MarketCollectorWorker>();
builder.Services.AddHostedService<BookSnapshotWorker>();
builder.Services.AddHostedService<PostsCollectorWorker>();
builder.Services.AddHostedService<MetarCollectorWorker>();
builder.Services.AddHostedService<WbPaperWorker>();

var host = builder.Build();
await host.Services.GetRequiredService<WeatherDb>().EnsureSchemaAsync();
await host.RunAsync();
