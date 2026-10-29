# Analysis scripts

One-file C# apps (.NET 10). Run from this folder:

```
dotnet run <script>.cs
```

Most read `../data/weather.db` (absolute path inside the script) and call the public Polymarket / xtracker APIs.
Results of each check are summarised in `docs/SPEC.md`.

| Script | What it checks |
|---|---|
| `wb-history.cs` | W-B on history: price of buckets already "dead" by METAR, 5–60 min after the observation |
| `wb-paper-results.cs` | W-B live paper signals (`wb_signals`) against how the markets resolved |
| `weather-tails.cs` | Calibration of cheap weather buckets (≤ 30c): win rate and ROI of buying them |
| `musk-xtracker-lag.cs` | Does the post-count price move when xtracker imports late posts |
| `musk-v0.cs` | Musk weekly markets: our count vs the winner, favourite at 90–99c, pace model vs market |
| `musk-near-certain.cs` | How often 99–99.9c "near-certain" bets on Musk markets lost |
| `musk-prexpect.cs` | Prexpect's closed positions by entry price and month |
| `traders.cs` | Weather traders from public articles: P&L, buy prices, timing |
| `trader-onlylucknobrain.cs` | One trader in detail: timing vs model runs, cities, resolved ROI |
| `sql.cs` | Ad-hoc read-only queries: `dotnet run sql.cs -- <db> "<sql>" ["<sql>" ...]` |
| `dashcheck.cs` | Runs every query of a Grafana dashboard JSON against the database |
