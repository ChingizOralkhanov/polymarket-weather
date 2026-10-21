using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PolymarketWeather.Core.Storage;

/// <summary>Lets "dotnet ef migrations add" build the context without a host; it never connects.</summary>
internal sealed class DesignTimeWeatherDbContextFactory : IDesignTimeDbContextFactory<WeatherDbContext>
{
    public WeatherDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<WeatherDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
