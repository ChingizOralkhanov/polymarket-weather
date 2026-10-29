# polymarket-weather

Bot for Polymarket daily temperature markets.

- `src/PolymarketWeather.Core` — API clients (Gamma, CLOB, Open-Meteo), models, storage
- `src/PolymarketWeather.Collector` — console app that collects markets, prices and forecasts on a schedule

## Dashboard (Grafana)

Grafana over `data/weather.db`, with the datasource and the "Prexpect" dashboard provisioned from `grafana/`:

```
docker compose up -d
```

Then open http://localhost:3000 (admin / admin on first login). Panels: collector health, W-B dead buckets and
would-buy signals, latest METAR per station, Musk posts per hour. Edits made in the UI are kept in the Grafana
volume; to keep one in the repo, export the dashboard JSON into `grafana/dashboards/prexpect.json`.

## Analysis

One-file C# scripts for every check done so far: see `analysis/README.md`.
