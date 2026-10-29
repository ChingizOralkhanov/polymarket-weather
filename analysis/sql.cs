#:package Microsoft.Data.Sqlite@10.0.0
using Microsoft.Data.Sqlite;
using var c = new SqliteConnection($"Data Source={args[0]};Mode=ReadOnly");
c.Open();
foreach (var sql in args.Skip(1))
{
    using var cmd = c.CreateCommand(); cmd.CommandText = sql;
    using var r = cmd.ExecuteReader();
    Console.WriteLine(string.Join(" | ", Enumerable.Range(0, r.FieldCount).Select(r.GetName)));
    while (r.Read()) Console.WriteLine(string.Join(" | ", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "∅" : r.GetValue(i)?.ToString())));
    Console.WriteLine();
}
