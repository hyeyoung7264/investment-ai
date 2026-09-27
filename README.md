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

Plan: [docs/M1-PLAN.md](docs/M1-PLAN.md)
