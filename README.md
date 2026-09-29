# Investment AI — Korean equity research system

A research system that finds, validates, and rejects strategies with positive expected value in the Korean stock market (KOSPI/KOSDAQ).
**No live trading. No real orders.** The scope ends at paper trading.

## Quick start

```bash
scripts/db.sh init          # user-owned PostgreSQL 12 cluster (127.0.0.1:55432)
dotnet tool restore
dotnet run --project src/Investment.Cli -- migrate
dotnet test
```

## Layout

| Project | Responsibility |
|---|---|
| Investment.Domain | Entities, `IStrategy`, `BarSeries` (look-ahead guard) |
| Investment.Persistence | EF Core / Npgsql, migrations |
| Investment.MarketData | Data collection, quality checks, point-in-time universe |
| Investment.Strategies | Strategy implementations |
| Investment.Risk | Risk Engine |
| Investment.Backtest | Simulator, cost model, metrics |
| Investment.Cli | Entry point |
| Investment.Research | Walk-forward, Promotion Gate, regimes, Research Agent |
| Investment.PaperTrading | Forward paper trading (same simulator as backtest) |

## Research workflow

```bash
dotnet run --project src/Investment.Cli -- ingest all && dotnet run --project src/Investment.Cli -- ingest splits
dotnet run --project src/Investment.Cli -- backtest --strategy all --from 2017-01-01
dotnet run --project src/Investment.Cli -- walkforward --strategy meanrev.zscore --hypothesis "..."
dotnet run --project src/Investment.Cli -- robustness --strategy meanrev.zscore --params '{...}'
dotnet run --project src/Investment.Cli -- agent cycle --max-studies 3
dotnet run --project src/Investment.Cli -- paper status        # scripts/paper-daily.sh after the close
dotnet run --project src/Investment.Cli -- evaluations | failures | runs
```

Documents: [M1 plan](docs/M1-PLAN.md) · [data quality](docs/DATA-QUALITY.md) · [M1 results](docs/M1-REPORT.md) · [hypotheses](docs/HYPOTHESES.md) · [M2–M4 results](docs/M2-M4-REPORT.md) · [M5 +1% search](docs/M5-REPORT.md) · **[Continuation criteria](docs/DECISION-CRITERIA.md)**
