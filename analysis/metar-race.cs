#:property PublishAot=false
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

// Which free source shows a new METAR first? Polls several sources for a few stations and logs when each one first
// shows every report. Measured lag includes the polling interval (15-30 s), so differences below that mean nothing.
//   dotnet run metar-race.cs -- 180        poll for 180 minutes, then print the report
//   dotnet run metar-race.cs -- report     print the report from the log so far
var log = Path.GetFullPath(Path.Combine("..", "data", "metar-race.csv"));

if (args.FirstOrDefault() == "report")
{
    PrintReport();
    return;
}

var minutes = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 180;
string[] us = ["KLGA", "KORD", "KMIA", "KDAL", "KATL", "KSEA"];
string[] world = ["EGLC", "LFPB", "EDDM", "EPWA", "LTFM", "RKSI", "RJTT", "ZBAA", "SAEZ"];
string[] all = [.. us, .. world];

var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("polymarket-weather-research/0.1 (github.com/ChingizOralkhanov)");
var keyRe = new Regex(@"\b(\d{6})Z\b");
var seen = new ConcurrentDictionary<string, byte>();
var primed = new ConcurrentDictionary<string, byte>();
var writeLock = new object();
if (!File.Exists(log)) File.WriteAllText(log, "source,station,kind,report,first_seen_utc,extra\n");

// Reports already visible on a source's first full poll are skipped: their real publication time is unknown.
void Record(string source, string station, string kind, string report, string extra = "")
{
    if (!seen.TryAdd($"{source}|{station}|{kind}|{report}", 0) || !primed.ContainsKey(source)) return;
    lock (writeLock)
        File.AppendAllText(log, $"{source},{station},{kind},{report},{DateTimeOffset.UtcNow:O},{extra}\n");
}

void RecordRaw(string source, string station, string? raw, string extra = "")
{
    if (string.IsNullOrWhiteSpace(raw)) return;
    var m = keyRe.Match(raw);
    if (m.Success) Record(source, station, "metar", m.Groups[1].Value, extra);
}

async Task Loop(string source, TimeSpan every, Func<CancellationToken, Task> poll, CancellationToken ct)
{
    var errors = 0;
    while (!ct.IsCancellationRequested)
    {
        try { await poll(ct); errors = 0; primed.TryAdd(source, 0); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
        {
            if (++errors % 20 == 1) Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss} {source}: {ex.Message}");
        }
        try { await Task.Delay(every, ct); } catch (OperationCanceledException) { return; }
    }
}

using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(minutes));
var ct = cts.Token;

await Task.WhenAll(
    // aviationweather API: the source the markets resolve on and our collector polls.
    Loop("awc-api", TimeSpan.FromSeconds(15), async c =>
    {
        using var d = JsonDocument.Parse(await http.GetStringAsync($"https://aviationweather.gov/api/data/metar?ids={string.Join(',', all)}&format=json&hours=2", c));
        foreach (var r in d.RootElement.EnumerateArray())
            RecordRaw("awc-api", r.GetProperty("icaoId").GetString()!, r.GetProperty("rawOb").GetString(),
                r.TryGetProperty("receiptTime", out var t) ? t.GetString() ?? "" : "");
    }, ct),

    // aviationweather bulk cache file.
    Loop("awc-cache", TimeSpan.FromSeconds(30), async c =>
    {
        await using var gz = new GZipStream(await http.GetStreamAsync("https://aviationweather.gov/data/cache/metars.cache.csv.gz", c), CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        var wanted = all.ToHashSet();
        while (await reader.ReadLineAsync(c) is { } line)
        {
            var parts = line.Split(',');
            if (parts.Length > 1 && wanted.Contains(parts[1])) RecordRaw("awc-cache", parts[1], parts[0].Trim('"'));
        }
    }, ct),

    // NOAA text files.
    Loop("noaa-tgftp", TimeSpan.FromSeconds(15), async c =>
    {
        await Parallel.ForEachAsync(all, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = c }, async (s, t) =>
            RecordRaw("noaa-tgftp", s, await http.GetStringAsync($"https://tgftp.nws.noaa.gov/data/observations/metar/stations/{s}.TXT", t)));
    }, ct),

    // Iowa Environmental Mesonet: asks for about one request per second.
    Loop("iem", TimeSpan.FromSeconds(30), async c =>
    {
        foreach (var s in all)
        {
            using var d = JsonDocument.Parse(await http.GetStringAsync($"https://mesonet.agron.iastate.edu/api/1/currents.json?station={s}", c));
            foreach (var r in d.RootElement.GetProperty("data").EnumerateArray())
                if (r.TryGetProperty("raw", out var raw) && raw.ValueKind == JsonValueKind.String)
                    RecordRaw("iem", s, raw.GetString());
            await Task.Delay(1100, c);
        }
    }, ct),

    // Norwegian Met Institute: returns recent METARs, one per line.
    Loop("met-no", TimeSpan.FromSeconds(30), async c =>
    {
        foreach (var s in all)
        {
            var text = await http.GetStringAsync($"https://api.met.no/weatherapi/tafmetar/1.0/metar.txt?icao={s}", c);
            var last = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            RecordRaw("met-no", s, last);
            await Task.Delay(500, c);
        }
    }, ct),

    // NWS API, US stations: reports the sensor every few minutes, also between routine METARs.
    Loop("nws-api", TimeSpan.FromSeconds(30), async c =>
    {
        foreach (var s in us)
        {
            using var d = JsonDocument.Parse(await http.GetStringAsync($"https://api.weather.gov/stations/{s}/observations/latest", c));
            var p = d.RootElement.GetProperty("properties");
            var ts = p.GetProperty("timestamp").GetDateTimeOffset();
            var temp = p.GetProperty("temperature").GetProperty("value");
            var value = temp.ValueKind == JsonValueKind.Number ? temp.GetDouble().ToString("F1", CultureInfo.InvariantCulture) : "";
            Record("nws-api", s, "sensor", ts.UtcDateTime.ToString("ddHHmm", CultureInfo.InvariantCulture), value);
            RecordRaw("nws-api", s, p.GetProperty("rawMessage").GetString());
            await Task.Delay(500, c);
        }
    }, ct));

PrintReport();

void PrintReport()
{
    var rows = File.ReadLines(log).Skip(1).Select(l => l.Split(',')).Where(p => p.Length >= 5)
        .Select(p => (Source: p[0], Station: p[1], Kind: p[2], Report: p[3], Seen: DateTimeOffset.Parse(p[4], CultureInfo.InvariantCulture), Extra: p.Length > 5 ? p[5] : ""))
        .ToList();
    Console.WriteLine($"log rows {rows.Count}, {(rows.Count > 0 ? rows.Min(r => r.Seen).ToString("MM-dd HH:mm") : "-")}..{(rows.Count > 0 ? rows.Max(r => r.Seen).ToString("MM-dd HH:mm") : "-")} UTC");

    // Observation time from the report's ddHHmm group, in the month of first sight.
    double SinceObs((string Source, string Station, string Kind, string Report, DateTimeOffset Seen, string Extra) r)
    {
        var day = int.Parse(r.Report[..2]); var hour = int.Parse(r.Report[2..4]); var min = int.Parse(r.Report[4..6]);
        var obs = new DateTimeOffset(r.Seen.Year, r.Seen.Month, Math.Min(day, DateTime.DaysInMonth(r.Seen.Year, r.Seen.Month)), hour, min, 0, TimeSpan.Zero);
        if (obs > r.Seen.AddDays(1)) obs = obs.AddMonths(-1);
        return (r.Seen - obs).TotalSeconds;
    }
    static string Stats(List<double> v) { v.Sort(); return v.Count == 0 ? "-" : $"p10 {v[v.Count / 10],6:F0} | median {v[v.Count / 2],6:F0} | p90 {v[v.Count * 9 / 10],6:F0}"; }

    var metars = rows.Where(r => r.Kind == "metar").ToList();
    Console.WriteLine("\nseconds from observation time to first sight, per source:");
    foreach (var g in metars.GroupBy(r => r.Source).OrderBy(g => g.Key))
        Console.WriteLine($"  {g.Key,-11} reports {g.Count(),4} | {Stats(g.Select(SinceObs).ToList())}");

    Console.WriteLine("\nseconds compared with awc-api on the same report (negative = earlier than aviationweather API):");
    var awc = metars.Where(r => r.Source == "awc-api").ToDictionary(r => (r.Station, r.Report), r => r.Seen);
    foreach (var g in metars.Where(r => r.Source != "awc-api" && awc.ContainsKey((r.Station, r.Report))).GroupBy(r => r.Source).OrderBy(g => g.Key))
    {
        var diffs = g.Select(r => (r.Seen - awc[(r.Station, r.Report)]).TotalSeconds).ToList();
        Console.WriteLine($"  {g.Key,-11} reports {diffs.Count,4} | earlier by >60 s: {diffs.Count(d => d < -60),3} | {Stats(diffs)}");
    }

    Console.WriteLine("\nby region, compared with awc-api (median s):");
    foreach (var g in metars.Where(r => r.Source != "awc-api" && awc.ContainsKey((r.Station, r.Report))).GroupBy(r => (r.Source, Us: r.Station.StartsWith('K'))).OrderBy(g => g.Key.Source).ThenBy(g => g.Key.Us))
    {
        var d = g.Select(r => (r.Seen - awc[(r.Station, r.Report)]).TotalSeconds).Order().ToList();
        Console.WriteLine($"  {g.Key.Source,-11} {(g.Key.Us ? "US" : "non-US"),-7} reports {d.Count,4} | median {d[d.Count / 2],6:F0}");
    }

    var sensors = rows.Where(r => r.Kind == "sensor").ToList();
    if (sensors.Count > 0)
    {
        Console.WriteLine($"\nNWS sensor readings (US): {sensors.Count}, per station per hour {sensors.Count / Math.Max(1.0, sensors.Select(s => s.Station).Distinct().Count() * (rows.Max(r => r.Seen) - rows.Min(r => r.Seen)).TotalHours):F1}");
        Console.WriteLine($"  seconds from reading time to first sight: {Stats(sensors.Select(SinceObs).ToList())}");
    }
}
