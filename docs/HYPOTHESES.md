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

## M5 — alpha search round 1 (recorded 2026-09-27, before running)

Starting from this round, multiple testing counts **every walk-forward study in the family (`meanrev.*`, etc.)**.

| ID | Strategy / conditions | Hypothesis | Origin | Expected |
|---|---|---|---|---|
| H7 | meanrev.intraday (z20 ≤ -2.5, next open→same-day close) | Most of the rebound after an extreme sell-off happens the next day. Daily capital recycling raises daily return | Derived from H6 (same data: weak evidence) | Round-trip cost per trade is the same, so it likely fails if the per-trade edge shrinks |
| H8 | breakout.volume (20-day high + volume ≥3x + bullish candle, 5 days/SMA10) | Information-driven buying continues | a-priori (spec Breakout/Volume group) | Korean theme-stock chasing tends to reverse, so negative is possible |
| H9 | meanrev.zscore v4 params, MaxPositions 20 (weight 10%) | More capital deployed when signals cluster on panic days raises daily return | Capacity analysis (M1 max-positions rejections) | Higher return, but MDD also rises |

### Round 1 results (2026-09-27)
H7 REJECTED (gross +0.33%, net -0.09%: the rebound is not first-day-only) · H8 HOLD (net +0.63%, t=0.88) · H9 net +0.96% (t=2.98) but daily average 0.020% is **below** H6 (0.040%), no improvement. H9 exposed a bug where a backtest study demoted a Paper version (fixed and corrected).

## M5 round 2 (recorded 2026-09-27, before running)

| ID | Strategy / conditions | Hypothesis | Origin | Expected |
|---|---|---|---|---|
| H10 | meanrev.capitulation (z20 ≤ -2 + volume ≥2x) | A heavy-volume sharp drop (forced selling) reverts more reliably. Captures more signals at the z -2 level at quality comparable to H6 | a-priori (forced-selling/liquidity-provision literature), meanrev family | Positive EV likely, but family variants are high (t threshold ~2.4) |
| H11 | breakout.volume grid: {60-day high, volume 5x} / {20-day, 5x} | Only stronger information shocks (volume 5x) continue | Refinement of H8 (a-priori grid, 2 variants) | Fewer trades |
| H12 | H6 + H8 50/50 combined OOS track record (analysis) | Combining low-correlation strategies improves Sharpe and utilization | Analysis (no status change) | — |

### Round 2 results (2026-09-27)
H10 HOLD (net +1.07%, t=2.14 < 2.45 family threshold) · H11 HOLD (net +0.23%) · H12 analysis: H6–H8 correlation 0.08, H6–H10 0.66. Capital split lowers daily average (0.026–0.031%); full-size combination upper bound 0.056–0.078%.

### Round 3 (recorded before running)
| ID | Strategy / conditions | Origin | Note |
|---|---|---|---|
| H13 | composite.portfolio [meanrev.zscore v4 params > breakout.volume default], shared capital, default risk (10 names×10%, gross ≤100%) | **post-hoc** (members chosen from OOS results) | Gate caps at HOLD. The point is to measure the actual shared-capital daily return |
