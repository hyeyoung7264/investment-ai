# Current status (2026-09-30, end of the delegated autonomous research)

## One-line summary
**+1%/day was not reached, and no legitimate path to it was found.** One strategy (H6) is running in paper trading every day; its verdict follows the pre-committed criteria (`DECISION-CRITERIA.md`).

## What was done during the delegation (M10–M12)
| Round | New information | Hypothesis | Result |
|---|---|---|---|
| M10 | KRX ETFs (no transaction tax), KOSPI200, VKOSPI | H21 index mean reversion / H22 VKOSPI fear spike / H23 index trend | HOLD / REJECTED / REJECTED |
| M11 | KOSPI200 futures basis | H24 negative-basis contrarian | HOLD (no edge, below random) |
| M12 | 520k type-I disclosures (preliminary earnings) | H25 earnings reaction drift (EAR) | REJECTED (EV −0.24%) |

Structural findings:
- Index timing trades only 6–28 times in 6 years → cannot meet the 100-trade minimum. It also shows no edge.
- The ETF tax exemption halves costs, but there is no edge left to preserve.
- Post-earnings drift does not exist in Korean liquid stocks in 2021–2026, whether dated to the filing or the preliminary release.

## Totals (25 hypotheses)
- **Validated → Paper**: 1 (H6 extreme oversold rebound: OOS EV +1.33%/trade on KRX, MDD 8.3%, ~0.04%/day)
- HOLD (positive but insufficient evidence): H19 value (0.041%/day, 79 trades), H10, H15, H16, etc.
- REJECTED: momentum, reversal, PEAD/EAR, VKOSPI, index trend, and more (all kept in `experiment_failures`)
- Best result with legitimate methods: **0.04–0.05%/day** (~20x short of +1%)

## Why research was stopped here
1. All new information sources accessible with the current data/APIs have been tried (prices, disclosures, financials, KRX official, ETFs, volatility, futures). Options (16GB) and intraday data are not practical.
2. More attempts on the same data are **data mining that the multiple-testing correction is designed to stop** (e.g., the meanrev family t threshold is now 2.5+).
3. The criteria agreed with the Owner (`DECISION-CRITERIA.md`) are "1–2 more rounds with new data only, then paper evidence decides" — 3 rounds were done in this delegation.

## Automated running (no action needed)
- Windows scheduled tasks `InvestmentPaperDaily` (08:10) / `InvestmentPaperDailyRetry` (08:45), weekdays → refresh data → advance the paper session.
- Check: `dotnet run --project src/Investment.Cli -- paper status` or `reports/paper/daily-YYYYMMDD.log`
- Stop/continue rules are enforced automatically in `PaperGate` (30 trades with EV ≤ 0 → disabled).

## What could change the result (Owner decisions)
- **Intraday (minute) data** source (brokerage API with read-only data permission): the one untested area is intraday/opening-auction behavior.
- **Options** data (KOSPI200 put/call ratio): possible with a days-long background collection (~16GB).
- `ANTHROPIC_API_KEY` for the LLM hypothesis generator: generates ideas but faces the same Gate/multiple-testing rules.
