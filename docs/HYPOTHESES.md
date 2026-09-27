# Hypothesis register (recorded before running)

Rules:
- Record hypotheses, parameters, and judgment criteria **here before** running.
- Record every variant tried. `VariantsTried` raises the t threshold (Bonferroni, one-sided α=0.05).
- Mark the origin (a-priori vs post-hoc). Post-hoc hypotheses cannot be fully verified with the walk-forward over the same period. Paper trading from now on is the only clean evidence for them.
- Keep failures (`experiment_failures`).

Shared setup (M2 walk-forward, recorded 2026-09-27):
- folds: train 3 years / validation 1 year / OOS 1 year, rolling annually. First train 2017-01-01 → OOS 2021, 2022, 2023, 2024, 2025, 2026 (to 09-23)
- parameters are chosen by train net Sharpe. OOS trading only if validation net EV > 0 (otherwise that fold is flat)
- universe: point-in-time liquidity top 100 (monthly), costs/risk same as M1, **research mode (no MDD halt)**
- Gate (VALIDATED): OOS net EV>0, t ≥ RequiredT(variants), ≥100 trades, Sharpe ≥ 0.5, MDD ≤ 30%, positive folds ≥ 50%, beats random control
- Gate (REJECTED): OOS net EV ≤ 0, total return ≤ 0, or MDD > 30%

| ID | Strategy / conditions | Hypothesis | Origin | Variants | Expected |
|---|---|---|---|---|---|
| H1 | momentum.xsec v1 (defaults) | Top 60-day (skip 5) winners outperform | a-priori (M1 plan) | 1 | M1 full period shows negative EV even gross → expect REJECT |
| H2 | meanrev.zscore v1 (defaults) | Oversold (z≤-2) names in an uptrend (>SMA200) revert | a-priori (M1 plan) | 1 | Gross edge exists but costs erase it → REJECT or HOLD |
| H3 | composite.regime-filter(meanrev v1, Bull only) | H2 works only in KOSPI Bull regime | **post-hoc** (M1 by-year breakdown) | 1 | Weak evidence even if it passes; paper trading is required |
| H4 | meanrev.zscore v1, KOSPI-only universe | H2 works only on KOSPI (large, liquid) | **post-hoc** (M1 by-market breakdown) | 1 | Same as above |
| H5 | reversal.st v1 (5-day losers, 5-day hold) | Short-term reversal premium in liquid stocks | a-priori (literature: KRX short-term reversal) | 1 | Turnover ~50x/yr, so likely erased by costs |
| H6 | meanrev.zscore grid: EntryZ {-2,-2.5} × TrendLength {200, null} | Stricter entry cuts costs relative to edge | a-priori (cost analysis) | 4 | Required t rises to 2.24 |
