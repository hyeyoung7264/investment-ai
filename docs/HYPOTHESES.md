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

### Round 3 results
H13 HOLD: shared capital **0.049%/day** (highest so far), EV +0.86%/trade (t=2.22), but MDD 27.1%, positive folds 2/6.

## M6 — DART disclosure events (recorded 2026-09-27, before running)

Events use the receipt date (no time) → decision after the close, **entry at the next session's open**. Corrections are not new events.

| ID | Strategy / conditions | Hypothesis | Origin | Note |
|---|---|---|---|---|
| H14 | event.buyback (buyback decision filed ≤3 days ago, hold 20 days), universe top 100 | Positive drift after buyback announcement | a-priori (literature) | event family variant 1 |
| H15 | Same, universe top 300 | Smaller caps have stronger buyback signal / more events | a-priori | family variant 2 |
| H16 | composite.event-filter(meanrev v4 params, block rights offering/CB/BW within 30 days) | Sharp drops caused by dilution news are information, not overreaction | a-priori idea, but **base parameters were chosen on OOS → post-hoc** | Gate capped at HOLD |

### M6 results (2026-09-27)
H14 HOLD (47 trades, EV +2.12%, t=0.66) · H15 HOLD (163 trades, EV +1.80%, t=1.24 < 1.96, 4/5 traded folds positive, 0.021%/day) · H16 HOLD by post-hoc cap (EV +1.10%, t=3.05, 0.038%/day; slightly better than fixed-parameter H6 1.03%).

## M7 — post-earnings drift PEAD (recorded 2026-09-29, before running)

Event date = periodic report receipt date (the day the figures became public; if preliminary earnings came earlier, only the remaining drift is captured). SUE = (quarterly OI − same quarter last year) / std of that change over up to 8 prior quarters **filed earlier**. Late amendments are excluded.

| ID | Strategy / conditions | Hypothesis | Origin | Note |
|---|---|---|---|---|
| H17 | event.pead SUE≥2, grid holding {20, 60} days, universe top 300 | Under-reaction to large positive surprises → drift | a-priori (PEAD literature) | event family cumulative → t threshold ~2.24 |
| H18 | Same, universe top 100 | Is the drift present in large caps too | a-priori | t threshold ~2.33 |

### M7 results (2026-09-29)
H17 REJECTED (top 300: EV +1.61%, t=0.64, MDD 30.9% > 30%) · H18 REJECTED (top 100: EV −1.85%). Using the periodic report filing date as the event date means the reaction to the earlier preliminary earnings is already priced in. PEAD would need the preliminary earnings date (type I) and its numbers.

## M8 — official KRX data + using idle capital (recorded 2026-09-29, before running)

Background: since Nextrade (2025-03), ~2% of Naver prices differ from KRX official → **new studies use `--price-source krx`**. H6 re-check on KRX (diagnostic): EV +1.33%/trade (t=3.27), MDD 8.3% — holds. H6 uses only **13.5%** of capital on average (flat on 47% of days).

| ID | Strategy / conditions | Hypothesis | Origin | Note |
|---|---|---|---|---|
| H19 | value.ey (TTM operating income / market cap top 10, monthly rebalance, sell below rank 30), universe top 300, grid TrendFilter {off, on} | Value premium; low turnover suits idle capital | a-priori (value premium literature, Faber trend filter) | new family, 2 variants → t 1.96 |
| H20 | composite.portfolio [H6 params (members limited to liquidity top 100) > value.ey (H19 selected variant)], universe top 300, shared capital | Filling H6's idle capital with value raises daily return | **post-hoc** (H6 base) | Gate capped at HOLD. The point is measuring actual daily return |

H20 details (fixed before running): value.ey member = final-fold selection of H19 (TrendFilter=false) with **TopK 7** (≈70% capital) so H6 keeps ~30% capacity; H6 member limited to liquidity top 100; risk MaxPositions 12, weight 10%, gross ≤100%.

### M8 results (2026-09-29)
H19 HOLD: value.ey top 300 (KRX) **0.041%/day**, EV +7.24%/trade (t=2.01 ≥ 1.96), Sharpe 0.67, MDD 18.5% — only 79 trades < 100 minimum (criterion not changed after the fact) · H20 REJECTED: MDD 41.3%, 0.036%/day (value without trend filter buying through the 2022 bear) · H20b (diagnostic, post-hoc, info only): trend-filtered combination 0.046%/day, MDD 23.2% — no breakthrough.

## M10 — ETF and derivatives-index data (recorded 2026-09-30, before running; Owner away, full delegation)

New information: KRX ETF daily records (2015–, 1.57M rows), KOSPI200, **VKOSPI** (implied volatility). Key cost difference: **domestic equity ETFs pay no securities transaction tax** → cost per round trip drops from ~0.5% to ~0.25% (conservative slippage 0.1% kept).
Common settings: instrument **KODEX 200 (069500, 1x)**, KRX official prices, one position at up to 100% (a 200-stock basket), stop loss 10%, no leverage/inverse. Benchmark = KOSPI.

| ID | Strategy / conditions | Hypothesis | Origin | Note |
|---|---|---|---|---|
| H21 | meanrev.zscore on KODEX 200, grid EntryZ {-1.5, -2.0}, no trend filter, exit SMA5 / 10 days | Index short-term oversold reversal; ETF low cost preserves the edge | a-priori (index short-term reversal, Connors) | **Same code as meanrev family → family multiple testing applied (high t threshold)** |
| H22 | index.volspike (VKOSPI ≥ 1.3× 60-day median → buy, exit when back at median or 20 days) | Mean reversion after fear spikes (forced de-risking) | a-priori (volatility risk premium / fear-spike literature) | new index family, 1 variant |
| H23 | index.trend (hold KODEX 200 above SMA200) | Keep index return while avoiding deep drawdowns (time-series momentum) | a-priori (Faber 2007) | Few trades → the 100-trade minimum may not be met (criterion unchanged) |

### M10 results (2026-09-30)
H21 HOLD (22 trades, EV +0.15%, t=0.16 < 2.54) · H22 REJECTED (EV −0.35%, MDD 34.9%) · H23 REJECTED (0.047%/day but MDD 40.8%, 6 trades). Structural finding: index timing trades only 6–22 times in 6 years → cannot meet the Gate's 100-trade minimum.

## M11 — futures basis (recorded 2026-09-30, before running; last round of new-data research)
Options (5.5MB/day → 16GB total) are excluded as too heavy. Data: front-month KOSPI 200 futures (day session, highest open interest) close vs spot → basis = (F−S)/S.

| ID | Strategy / conditions | Hypothesis | Origin | Note |
|---|---|---|---|---|
| H24 | index.basis: basis z (60 days) ≤ −2 → buy KODEX 200, exit when z ≥ 0 or 10 days | Deep negative basis = excessive hedging/pessimism → contrarian rebound | a-priori (sentiment contrarian) | index family (variants accumulate); few trades expected |
