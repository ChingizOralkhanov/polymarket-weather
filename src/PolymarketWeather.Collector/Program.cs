using PolymarketWeather.Collector;
using PolymarketWeather.Core.Polymarket.Gamma;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddHttpClient<GammaClient>(c =>
{
    c.BaseAddress = new Uri(GammaClient.DefaultBaseAddress);
    c.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.Configure<CollectorOptions>(builder.Configuration.GetSection(CollectorOptions.Section));
builder.Services.AddHostedService<MarketCollectorWorker>();

await builder.Build().RunAsync();
