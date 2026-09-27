# M2–M4 Results Report (2026-09-27)

> Bottom line: **one strategy (meanrev.zscore v4) passed walk-forward OOS validation and has started paper trading.**
> Everything so far is backtest/OOS evidence. The real verdict comes from paper-trading results that will accumulate from now on.
> The daily average +1% research target is **far out of reach** (validated strategy's OOS daily average +0.04%, gap of about 25x).

## What was built

| Stage | Contents |
|---|---|
| M2 Walk-forward | Train 3y / validation 1y / OOS 1y, rolled annually (OOS 2021–2026, 6 folds). Parameters selected on train only; OOS traded only if validation EV>0. Random-entry control runs on every OOS window |
| M2 Promotion Gate | REJECT: OOS net EV≤0, total return≤0, or MDD>30%. VALIDATED: t ≥ Bonferroni threshold (**cumulative study count per strategy**), ≥100 trades, Sharpe≥0.5, positive folds≥50%, beats random. Post-hoc hypotheses capped at HOLD |
| M2 Regime | KOSPI point-in-time: trend (SMA200 + slope) × volatility (20-day realized vol vs 1-year median). Per-regime OOS breakdown |
| M2 Records | `research_studies`, `strategy_evaluations` (every evaluation including holds, corrections append-only), `experiment_failures` (never deleted) |
| M3 Paper | Same `Simulator.Step` as the backtest (state persisted as JSON). Test proves data arriving over 3 days produces **identical** trades/equity to a backtest. VALIDATED-only start, no backfill, entries paused in regimes where evidence is negative, stored with decision context |
| M3 Risk | Per-session: position/sector/liquidity/daily loss/MDD 25%/stop loss. Book level: max 50% per strategy, **stop all sessions at 15% combined drawdown** |
| M4 Agent | observe → proposal generation (rules; with an API key, Claude `claude-opus-5` structured output) → duplicate-study block (fingerprint) → walk-forward + gate → `reports/research/cycle-*.md` |
| Robustness diagnostics | Parameter neighborhood, cost multipliers, universe size (no status change) |

## Hypothesis verdicts (docs/HYPOTHESES.md, OOS 2021-01 ~ 2026-09, net of costs)

| ID | Hypothesis | OOS trades | EV/trade | t | Sharpe | MDD | Verdict |
|---|---|---|---|---|---|---|---|
| H1 | momentum.xsec | 343 | -1.91% | -1.86 | -0.28 | 68.9% | **REJECTED** |
| H2 | meanrev z≤-2 + SMA200 | 379 | +0.22% | 0.46 | 0.11 | 25.6% | HOLD |
| H3 | meanrev, Bull only (post-hoc) | 261 | -0.10% | -0.19 | -0.04 | 18.7% | **REJECTED** |
| H4 | meanrev, KOSPI only (post-hoc) | 313 | +0.37% | 1.00 | 0.24 | 12.6% | HOLD |
| H5 | reversal.st (5-day losers) | 794 | -1.01% | -2.65 | -0.55 | 74.5% | **REJECTED** |
| H6 | meanrev grid 4 (z {-2,-2.5} × trend {200,none}) | 429 | **+1.41%** | **3.43** (req. 2.24) | 0.90 | 8.3% | **VALIDATED → PAPER** |
| A1 (agent) | meanrev z≤-3 (lower turnover) | — | — | 0.88 | 0.20 | — | HOLD |
| A2 (agent) | H6 excluding Bear/LowVol (post-hoc) | — | +1.44% | 4.03 | 1.03 | 9.6% | Promoted, then **corrected → HOLD** (see below) |

KOSPI over the same OOS period: +146%. H6's OOS total return is +69%. **It does not beat buy-and-hold the index on absolute return.** Exposure is low (holds positions only intermittently), so risk-adjusted it's Sharpe 0.90 with MDD 8.3%.

### H6 details (meanrev.zscore v4: z20 ≤ -2.5, exit when back above SMA5 or after 10 days)
- Fold selection: z=-2.5 chosen consistently; from 2024 the version without the trend filter was selected
- By fold: 2021 +5.7%, 2022 -1.6%, 2023 not traded (validation failed), 2024 +10.6%, 2025 +13.3%, 2026 (9 months) +29.9%
- Outlier dependence: median trade +1.49%, win rate 62.7%, **5% trimmed on both sides EV +0.97% (t=3.32)**. Removing only the top 5% gives +0.31% → fat right tail
- By regime: Bear/LowVol -1.39% (85 trades) is the only negative → paused in paper
- Largest winners checked against real events (HLB rebound after FDA rejections in 2024-05 and 2025-03, 씨씨에스 rebound after limit-down)

### Robustness diagnostics (fixed parameters, no status change)
| Case | EV/trade | t | Verdict |
|---|---|---|---|
| Base | +1.03% | 2.86 | pass |
| z -2.25 / -2.75 / -3.0 | **+0.02%** / +1.18% / +0.57% | 0.04 / 3.07 / 0.88 | fail / pass / hold |
| exit SMA 3 / 7 | +0.52% / +2.16% | 2.02 / 4.10 | hold / pass |
| band 15 / 25 | +1.16% / +0.67% | 2.47 / 2.24 | pass / pass |
| max hold 5 / 20 | +1.10% / +1.03% | 3.14 / 2.87 | pass / pass |
| SMA200 filter | +0.90% | 1.93 | pass |
| slippage ×2 / ×3 | +1.47% / +1.24% | 3.07 / 2.53 | pass (traded folds differ, not a like-for-like comparison; fixed-trade estimate ≈ +0.8% / +0.6%) |
| universe top 50 / **top 200** | +0.76% / **+0.09%** | 2.00 / 0.18 | pass / **fail** |

Interpretation: positive across most of the parameter neighborhood (a plateau, not a single point). But the **edge disappears at z=-2.25 and at universe top 200**, so the edge exists only in "extreme oversold + most liquid large caps". Expanding the universe is not allowed.

## Problems found and fixed (M2–M4)

| Problem | Fix |
|---|---|
| Post-hoc hypothesis (A2) was VALIDATED by the same OOS | Gate `CapPostHoc` added; existing promotion corrected with an append-only `correction` evaluation |
| Strategy-level status overwritten by a later HOLD on a different version | Changed to roll up from the most advanced active version |
| Agent-driven exploration inflates false discoveries | Cumulative walk-forward studies per strategy added to VariantsTried |
| Paper trade missing signal price when entry order was placed on a previous day | Preload existing orders from DB (caught by test) |
| Build-failed commit (my mistake: `&&` after `grep` in the command chain) | Fixed commit added, now commit only after checking build success |

## Current state

- Paper session `H6 meanrev z-2.5 (v4)`: started 2026-09-23, 100M virtual (50% of a 200M book), current regime Bull/LowVol, no positions/orders (no oversold signals)
- Strategy status: meanrev.zscore **Paper**, momentum.xsec / reversal.st **Rejected**, composite.regime-filter Backtested
- Paper → APPROVED requires: ≥60 sessions, ≥30 trades, EV>0, and ≥50% of the expected EV (1.41%). Disabled on MDD>20%, risk halt, or EV collapse (t≤-2). **APPROVED does not mean live trading**

## Running it (Owner action needed)

The daily job must run after the close (after 18:00 KST):
```bash
scripts/paper-daily.sh
```
cron is not running in this WSL, so registering with Windows Task Scheduler is an option (not registered — Owner decision):
```bash
schtasks.exe /Create /SC WEEKLY /D MON,TUE,WED,THU,FRI /ST 18:30 /TN InvestmentPaperDaily /TR "wsl.exe -d Ubuntu -- bash -lc ~/investment-ai/scripts/paper-daily.sh"
```

## Limitations and next decisions

1. **Paper evidence accumulates slowly** — at H6's trade frequency (~70/year) it takes about 5–6 months to reach the 30-trade threshold.
2. Only one validated strategy → the requirement "don't concentrate capital in one strategy" is currently met only via the 50%-of-book cap. Real diversification needs a second, **uncorrelated** strategy.
3. Data comes from an unofficial source (Naver) → providing KRX OpenAPI / OpenDART keys would enable cross-checking plus fundamental/disclosure (event-driven) strategies.
4. LLM hypothesis generation is only active with `ANTHROPIC_API_KEY` set (it calls a paid API).
5. Live trading (M7) is not implemented and not started without separate Owner approval.
