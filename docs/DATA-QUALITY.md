# Data sources and data quality (as of 2026-09-27)

## Sources

| Data | Source | Notes |
|---|---|---|
| Listed companies (market, sector, listing date) | KIND `corpList.do` | Current snapshot only |
| Delisting history (date, reason) | KIND `delcompany.do` | 720 events since 2015; 84 market transfers (KONEX→KOSDAQ, KOSDAQ→KOSPI) are excluded |
| Daily OHLCV (stocks, KOSPI/KOSDAQ indices) | Naver `api.stock.naver.com/chart/domestic` | Unofficial API. Split-adjusted OHLC, **raw volume**, KRX session only (excludes NXT) |

KRX data.krx.co.kr requires a login, and KRX OpenAPI/OpenDART require API keys, so neither is used yet. With keys we can add them as a cross-check source.

Loaded (2026-09-27): 2,872 tickers, 6,389,180 bars (2015-01-02 to 2026-09-23), 211,458 halted bars.

## Known issues and how they are handled

| Issue | Scale | Handling |
|---|---|---|
| **Survivorship bias** | 335 delisted KOSPI/KOSDAQ common stocks | Delisted stocks are ingested too. The universe is rebuilt monthly with point-in-time data. The final liquidation-trading (정리매매) crash is kept as a real loss |
| Halted sessions (source reports O=H=L=0) | 211k bars | Normalized to O=H=L=C and flagged `is_halted`. No fills |
| OHLC rounding from adjustment (C > H by 1원, etc.) | >0.5% inconsistency in 11 tickers / 1,147 bars | Normalized at load: H=max(O,H,C), L=min(O,L,C) |
| L=0 (084440) | 1 ticker, 1,709 bars | Same normalization |
| **Discontinuities** (moves beyond the price limit outside the liquidation window) | 82 events, 66 tickers (e.g. 003060 +1,286% = unadjusted reverse split) | Backtest: a held position closes at the prior close (`DataDiscontinuity`) and new entries are blocked for 60 sessions. The jump is never counted as a return |
| Liquidation-trading moves (±30% cap does not apply) | 638 events | Real price moves, kept. New entries are blocked in the last 14 days before delisting (publicly known at the time) |
| **Unadjusted volume** (split/reverse split) | 650 tickers flagged by heuristic | TradingValue = adjusted close × raw volume, so pre-split trading value is **underestimated**. Liquidity rank/participation limits can be off → M2 item |
| Market transfers (KONEX→KOSDAQ) | Pre-transfer history is treated as KOSDAQ | KONEX names are illiquid and rarely enter the top 100. Needs point-in-time market data → M2 item |
| Historical market cap / shares outstanding | None | Not used in M1. Needs KRX OpenAPI |
| Designated/caution issues (관리종목/투자주의) history | None | Not used. Universe keeps a minimum price of 1,000원 as a partial filter |
| Source revisions | Full re-fetch each run; `ingestion_runs.notes` records how many rows changed | Each backtest stores `data_hash`, and rerun checks it matches |

Full report: `dotnet run --project src/Investment.Cli -- quality` → `reports/data-quality.json`
