# M5 — Report on the search toward +1%/day (2026-09-27)

Instruction: "Keep trying by any means until the goal is reached." Leverage, look-ahead, overfitting, concentrating in high-risk names, and live orders were excluded per the original spec's prohibitions.
All hypotheses were recorded in `docs/HYPOTHESES.md` **before running** and judged by the same walk-forward OOS (2021-01 ~ 2026-09, net of costs) and Gate.

## Results

| ID | Idea | Net daily avg | EV/trade (t) | MDD | Verdict |
|---|---|---|---|---|---|
| H6 (existing) | Extreme oversold rebound z≤-2.5 | 0.040% | +1.41% (3.43) | 8.3% | Validated → Paper |
| H7 | Same signal, same-day close exit (daily capital recycling) | -0.002% | -0.09% | 7.9% | REJECTED |
| H8 | Volume breakout | 0.016% | +0.63% (0.88) | 22.8% | HOLD |
| H9 | H6 with 20 positions | 0.020% | +0.96% (2.98) | 10.2% | Worse than H6 |
| H10 | Capitulation (high-volume sharp drop) | 0.022% | +1.07% (2.14, threshold 2.45) | 14.0% | HOLD |
| H11 | Strong breakout (5x volume) | 0.004% | +0.23% | 13.9% | HOLD |
| H12 | Combination analysis | 50/50 split 0.026–0.031%, full-size upper bound 0.056–0.078% | | | Analysis |
| H13 | H6 + breakout, shared capital (no leverage) | **0.049%** | +0.86% (2.22) | 27.1% | HOLD (post-hoc) |

**Distance to +1%/day: the best legitimate result is 0.049%, about 20x short.** 1%/day is about 12x per year (1.01^250).

## Why it doesn't get closer (evidence-based)

1. **Costs**: a round trip is about 0.45–0.55%. Short-term strategies need a large edge per trade, and H7 (same-day close) failed because the gross edge was only +0.33%.
2. **Signal scarcity**: good signals (extreme oversold in the most liquid names) number ~70–100 a year. Widening the universe (top 200) kills the edge (robustness diagnostics).
3. **Capital utilization**: the only way to raise daily return is to combine several uncorrelated strategies (H13). Without leverage, the upper bound is roughly "sum of each strategy's daily average".
4. **Multiple testing**: the more ideas tried on the same 2021–2026 data, the higher the required t (the meanrev family is now at 2.45). This mechanism is what separates luck from edge.

## Engine and gate changes made during the search
- Day-trade (same-day close exit) support, capitulation/breakout/intraday strategies, shared-capital portfolio (ownership tags)
- Multiple testing counted per strategy family
- **Bug fix**: a backtest study (H9) demoted a Paper-stage version → backtest studies can no longer change a Paper/Approved status (corrected)
- Official M1 runs still reproduce with identical hashes after all changes

## Honest assessment and next steps

Further re-testing on the same daily-bar data risks becoming data mining. What could actually move the needle:
1. **Forward evidence**: H6 paper trading (the more trustworthy verdict, from ~5–6 months out).
2. **New information sources**: OpenDART disclosures (earnings, buybacks, contracts: event-driven), KRX investor-type flows (foreign/institutional net buying). Needs API keys.
3. **Intraday data**: the open→close rebound failed on daily bars, but minute data would allow testing open-auction/intraday behavior (needs a paid data source).
4. **Strategies that start with fresh data**: judge newly generated hypotheses by paper trading from here on rather than by past data.

## M6 — OpenDART disclosure events (2026-09-27)

- 10 years of type B (major events) disclosures collected: 75,155 filings (buybacks 1,936, rights offerings 5,918, CB 5,415, …). Receipt date only → entry at the next session's open.
- Strategies see only events dated up to the as-of date (test-verified); existing strategies reproduce with identical data hashes.

| ID | Idea | Trades | EV/trade | t (threshold) | Daily avg | Verdict |
|---|---|---|---|---|---|---|
| H14 | Buyback announcement drift, top 100 | 47 | +2.12% | 0.66 (1.64) | 0.007% | HOLD |
| H15 | Buyback announcement drift, top 300 | 163 | +1.80% | 1.24 (1.96) | 0.021% | HOLD |
| H16 | H6 + exclude names with dilutive financing in 30 days | 529 | +1.10% | 3.05 (2.24) | 0.038% | HOLD (post-hoc) |

Interpretation: disclosure events give **large per-trade edges but few events**, so their contribution to daily return is small. The overall best remains 0.049%/day (H13).

Next candidates: (1) earnings surprise (PEAD) — DART multi-company key accounts API (`fnlttMultiAcnt`, 100 companies per call) to compute YoY operating income surprise, event date = filing date; (2) type I exchange disclosures (supply contracts, preliminary earnings) — ingest takes about 2–3 hours; (3) a shared-capital combination of buybacks and H16.
