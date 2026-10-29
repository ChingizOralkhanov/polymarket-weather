#:property PublishAot=false
#:package Microsoft.Data.Sqlite@10.0.0
using System.Text.Json;
using Microsoft.Data.Sqlite;

// Runs every query of a Grafana dashboard JSON against the SQLite file, with fixed time-range macros.
var dashboard = JsonDocument.Parse(File.ReadAllText(args[1]));
using var c = new SqliteConnection($"Data Source={args[0]};Mode=ReadOnly");
c.Open();
var to = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
var from = to - 3 * 86400;
foreach (var panel in dashboard.RootElement.GetProperty("panels").EnumerateArray())
{
    if (!panel.TryGetProperty("targets", out var targets)) continue;
    var sql = targets[0].GetProperty("rawQueryText").GetString()!
        .Replace("$__unixEpochFrom()", from.ToString()).Replace("$__unixEpochTo()", to.ToString());
    try
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var rows = 0; string? first = null;
        while (r.Read())
        {
            if (rows++ == 0)
                first = string.Join(" | ", Enumerable.Range(0, r.FieldCount).Select(i => $"{r.GetName(i)}={(r.IsDBNull(i) ? "null" : r.GetValue(i))}"));
        }
        Console.WriteLine($"OK   {panel.GetProperty("title").GetString()}: {rows} rows; {first}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL {panel.GetProperty("title").GetString()}: {ex.Message}");
    }
}
