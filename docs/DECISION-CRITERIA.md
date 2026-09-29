# Project continuation criteria (agreed with the Owner 2026-09-29)

These criteria are **fixed in advance**. They are not changed after results are in. Any change requires Owner agreement and applies only to evidence collected after the change.

## 1. Redefined goal

| Item | Target |
|---|---|
| Annual return (net) | 10–15% |
| Max drawdown | ≤ 15% |
| Comparison | Better risk-adjusted return (Sharpe, drawdown) than holding the index |
| `+1%/day` | Kept only as a measuring stick (the realistic path is judged not to exist, see M5 report) |

The strategy is treated as a **low-risk addition** to an index holding (on raw return over 2021–2026, holding KOSPI came out ahead).

## 2. Operating mode (from 2026-09-30)

- Paper trading runs automatically every trading day: Windows Task Scheduler `InvestmentPaperDaily` (08:10) + `InvestmentPaperDailyRetry` (08:45).
  - KRX official data is published on the next business day → decisions use day D's close, orders go to D+1's open (runs before 09:00).
  - If a day is missed, the next run processes several sessions at once (the log shows `CATCH-UP`: orders are simulated after the fact).
- No more hunting for strategies on the same data. Extra research only with **new information sources** (e.g. KRX investor flows), at most 1–2 rounds.
- No live orders or broker connection (live trading needs separate Owner approval).

## 3. Verdict criteria (paper session `H6 meanrev z-2.5 (v4)`, KRX official prices)

Validated expectation: net EV **+1.41%/trade** (walk-forward OOS; +1.33% on the KRX re-check).

| Verdict | Condition | Action |
|---|---|---|
| **Stop** | After 30 paper trades, average net return per trade **≤ 0** | Strategy DISABLED; Owner discussion on stopping the project |
| **Stop** | Paper drawdown **> 20%**, or risk engine halt (drawdown 25% / book 15%) | Strategy DISABLED immediately |
| **Stop** | After 20+ trades, EV significantly negative (t ≤ −2) | Strategy DISABLED immediately |
| **Continue** | After 30 trades and 60 sessions, average net return **≥ +0.7%/trade** (≥50% of expected) | APPROVED → discuss **small-scale live trading** with the Owner (separate approval) |
| **Hold** | Anything else (EV between 0 and +0.7%, or not enough trades) | Keep collecting |

- Decision point: when 30 paper trades are reached (at about 70 signals a year, **around 2027-03 to 04**). Signals are rare in a strong market, so it may take longer.
- All of the above are **encoded as rules in `PaperGate`** (`src/Investment.PaperTrading/PaperTradingService.cs`); every evaluation is recorded in `strategy_evaluations`.
- The Owner checks with `paper status`, or `reports/paper/daily-YYYYMMDD.log`.
