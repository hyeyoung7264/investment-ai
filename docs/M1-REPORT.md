# M1 결과 보고 (2026-09-27)

> 결론 먼저: **M1 파이프라인은 완성·재현 가능하다. 검증된 양의 기대값 전략은 아직 없다.**
> Momentum은 비용 전에도 음(-)의 기대값, Mean Reversion은 비용 전 유의한 양(+)의 기대값(t=3.8)이 있으나
> 비용 후 기대값은 +0.13%/trade(t=0.8)로 통계적으로 0과 구분되지 않는다. 일평균 +1% 목표와의 거리는 두 자릿수 배다.

## 1. 현재 구조

```
Investment.Domain        Security/DailyPrice/Bar, BarSeries(미래 접근 시 예외), IStrategy, 전략 상태·버전, 백테스트 레코드
Investment.Persistence   EF Core + Npgsql, migrations 2개(InitialSchema, SplitEvents), COPY 기반 대량 upsert
Investment.MarketData    KIND(상장/상장폐지), Naver(수정 OHLCV, 원주가 페이지), 분할 검증, 품질 검사, point-in-time universe
Investment.Strategies    momentum.xsec, meanrev.zscore, control.liquidity-leaders, control.random
Investment.Risk          RiskEngine(종목수/비중/노출/섹터/유동성/일손실 차단/MDD halt/손절)
Investment.Backtest      일봉 이벤트 시뮬레이터, 비용 모델(연도별 거래세), gross/net Metric
Investment.Research      데이터셋 로더, 실행·저장·재실행 검증, 리포트
Investment.Cli           migrate / ingest / quality / backtest / rerun / runs
```
테스트 69개 (엔진 28, 데이터 30, 전략 7, 통합 4 — 통합은 실제 PostgreSQL).
PR 단위 branch: `m1/pr1-foundation` → `pr2-market-data` → `pr3-backtest-engine` → `pr4-strategies` → `pr5-metrics-reporting` (stacked, merge 안 함).

## 2. 수집 데이터

| 항목 | 규모 |
|---|---|
| 종목 마스터 | KOSPI/KOSDAQ 보통주 상장 2,532 + 상장폐지 335 (2015년 이후, 이전상장 84건 제외) |
| 일봉 | 2,872 종목, 6,389,180 bar, 2015-01-02 ~ 2026-09-23 |
| 지수 | KOSPI, KOSDAQ 각 2,879 세션 |
| 분할/병합 검증 | 후보 3,419 → 실제 이벤트 551, 거래량 보정 필요 240 |

## 3. 실험 조건 (모든 전략 동일)

- 기간 2017-01-02 ~ 2026-09-23 (2,385 세션), 초기자본 1억
- Universe: 매월 전월 말 기준 60세션 거래대금 중앙값 상위 100 (상장폐지 종목 포함, 우선주·스팩·리츠·펀드 제외, 1,000원 미만 제외)
- 체결: 신호 D 종가 → D+1 시가, 슬리피지 0.10% + 0.1×참여율, 수수료 0.015%, 매도세 연도별(0.30%→0.15%→2026 0.20%)
- Risk: 최대 10종목, 종목당 10%, 섹터 30%, 참여율 5%, 손절 -10%, 일손실 -3% 시 다음날 신규진입 금지
- 두 모드: **research**(MDD halt 끔 — 전 기간 증거 확보) / **risk-managed**(MDD 25% 도달 시 전량 청산·중단)
- 파라미터는 사전에 고정. 튜닝 0회.

## 4. Backtest 결과 (research 모드, 비용 후 net / 비용 전 gross)

| 전략 | Net 총수익 | Net CAGR | Net 일평균 | Net MDD | Net Sharpe | 거래수 | 승률 | EV/trade gross (t) | EV/trade net (t) |
|---|---|---|---|---|---|---|---|---|---|
| momentum.xsec v1 | **-96.9%** | -29.9% | -0.111% | 97.3% | -0.68 | 1,879 | 27.1% | -1.33% (-2.79) | -1.81% (-3.80) |
| meanrev.zscore v1 | +13.6% | +1.3% | +0.011% | 49.0% | 0.16 | 2,037 | 58.5% | **+0.60% (+3.81)** | +0.13% (+0.81) |
| control.liquidity-leaders | +403% | +18.1% | +0.094% | 53.1% | 0.65 | 271 | 18.8% | +18.1% (1.21) | +17.4% (1.17) |
| control.random | -87.6% | -19.3% | -0.077% | 90.0% | -0.86 | 3,420 | 42.0% | -0.08% (-0.50) | -0.55% (-3.68) |
| KOSPI (benchmark) | +249% | | | | | | | | |

risk-managed 모드: **4개 전략 모두 MDD 25% halt** (momentum 2017-02, random 2018-06, liquidity-leaders 2018-10, meanrev 2020-03).
→ 현재 Risk 설정(단일 전략, 최대 10종목, 25% MDD)으로는 패시브 대형주 보유조차 생존하지 못한다. Risk 파라미터 자체가 M2 논의 대상.

재현성: 공식 8개 run 모두 `rerun` → **data hash / params / result hash 일치(REPRODUCED)**.
Run id와 hash는 `reports/m1-official.txt`, DB `backtest_runs`.

### M1 DoD 질문에 대한 답

> 특정 Universe·기간에서 Momentum과 Mean Reversion은 실제로 어떤 수익률, EV, MDD, Sharpe가 나왔는가?

위 표. 동일 조건 재실행 시 동일 결과(hash 일치).

### 해석 (주의: 아래는 사후 분석 = 가설이지 증거가 아니다)

- **Momentum**: gross부터 음수. 손절 1,103회(평균 -10.6%). 거래대금 상위 종목 중 60일 상승률 상위는 테마주 과열 구간을 사는 결과가 됨.
  한국 시장의 단기 반전 경향과 일치. **Promotion Gate 기준으로 REJECT 대상.**
- **Mean Reversion**: gross 우위(t=3.8)는 있지만 연 20회전 × 왕복 약 0.5% 비용이 대부분을 소멸.
  연도별 gross EV: 2017 +1.20%, 2023 +0.88%, 2025 +1.98%, 2026 +1.48% (강세장) vs 2020~2022 ≈ 0.
  시장별: KOSPI gross +0.70%(t=4.33) / net +0.23%, KOSDAQ net -0.04%.
  → "강세 regime + KOSPI에서만 작동" 가설. **사후에 본 것이므로 M2에서 사전 등록 후 walk-forward로만 검증.**
- **Control**: random 전략 gross EV ≈ 0 (t=-0.5) → 엔진이 체계적 편향을 만들지 않는다는 sanity check 통과.
  liquidity-leaders +403% > KOSPI +249%: 2025~26 반도체·조선·방산 등 대형 주도주 랠리 영향(연 +172%, +150%).

### 일평균 +1% 목표 대비

| | Net 일평균 |
|---|---|
| 목표(측정 대상) | +1.000% |
| 최고 관측치 (control, 패시브) | +0.094% |
| 최고 관측치 (알파 전략 후보) | +0.011% |

목표와 약 10~90배 차이. 이 차이를 레버리지·파라미터 튜닝으로 메우지 않는다.

## 5. 발견·수정한 문제 (M1 중)

| 문제 | 영향 | 조치 |
|---|---|---|
| Naver 거래량이 **정방향 분할에서 비수정** (삼성 2018 50:1 등) | 분할 전 거래대금 최대 50배 과소 → universe 왜곡 | 원주가 API와 비교해 이벤트 551건 검증, 240건 보정 (`split_events`, 로드 시 적용) |
| 비수정 감자/이전상장 가격 점프 (예: 003060 +1,286%) | 가짜 수익 | 82건 discontinuity로 표시 → 직전 종가 청산, 60세션 재진입 금지 |
| 조정 반올림 OHLC 불일치, L=0 | 체결가 오류 | 로드 시 정규화 |
| KIND 분류: 메리츠→리츠, VC→펀드 오분류 | universe 오염 | 규칙 수정 + 회귀 테스트 |
| gross 곡선 = net + 누적비용 (복리 무시) | 장기 gross MDD/변동성 왜곡 | 세션별 비용 가산 수익률 복리로 변경 (engine 1.1.0) |
| result hash가 decimal scale에 의존 | 재실행 불일치 | 고정 포맷 hash + 자본 scale 정규화, rerun 시 첫 불일치 거래 표시 |

## 6. 남은 데이터 품질 문제

1. **가격 소스가 비공식 API 하나** — 교차검증 불가. KRX OpenAPI key가 있으면 교차검증 가능.
2. 상장폐지 종목은 원주가 API가 없음 → 이들의 분할 거래량 보정 불가.
3. 감자 후 수정주가가 커져 **1,000원 최소가격 필터가 원주가 기준이 아님** (저가 종목 일부 통과).
4. 시가총액·상장주식수 이력 없음, 관리종목/투자경고 이력 없음.
5. KONEX→KOSDAQ 이전 종목의 이전 전 이력이 KOSDAQ처럼 취급됨.
6. 원천 데이터 수정 시 과거 run 재현은 hash로 **감지만** 가능 (스냅샷 보관 없음).
7. 2025-03 이후 NXT(대체거래소) 거래는 KRX 일봉에 미포함 — 체결 가능 유동성 과소 추정.

## 7. Overfitting 위험

- M1은 파라미터 튜닝을 하지 않았으므로 **파라미터 과최적화는 없음**. 그러나:
- 같은 2017~2026 데이터를 본 뒤 "강세장/KOSPI 한정" 가설을 얻었다 → 이 데이터로 그 가설을 확인하면 순환논리. M2는 **사전 등록 + walk-forward OOS만** 증거로 인정해야 함.
- 전략 후보가 늘수록 다중검정 문제 → 시도한 모든 변형을 기록하고 t-stat 기준을 시도 횟수에 맞춰 상향(Deflated Sharpe / Bonferroni 류) 필요.
- 10년 중 강세장 비중이 결과를 지배(2025~26) → 기간별·regime별 분해 없이 전체 수치로 판단 금지.

## 8. 성능

| 작업 | 시간 |
|---|---|
| 전체 가격 수집 (2,883 종목) | ~2분 |
| 분할 검증 | ~18초 |
| 데이터셋 로드 (universe 2-pass) | ~12초 |
| 백테스트 1회 (10년, ~770종목) | ~2~2.5초 |

walk-forward(폴드×전략×변형)에서도 병목은 아님. 데이터셋 1회 로드 후 메모리 재사용으로 충분.

## 9. M2에서 해결해야 할 것 (제안)

1. **Walk-forward / OOS 엔진**: train/validation/OOS 창 이동, 폴드별 결과 저장, OOS만 합쳐 평가.
2. **Promotion Gate 구현**: 증거 기반 상태 전이 + 실패 기록(`experiment_failures`) — momentum v1은 첫 REJECT 사례.
3. **사전 등록된 가설 검증**: (a) meanrev를 강세 regime 한정(예: KOSPI > 200일선) / (b) KOSPI 한정 / (c) 비용 절감형(진입 임계 강화) — 각각 walk-forward OOS에서만 판단.
4. **Regime 라벨링** (Bull/Bear/Sideways × 변동성) 및 regime별 성과 분해.
5. **Risk 설정 재검토**: 단일 전략 25% halt가 패시브 전략도 중단시킴 → 전략별/포트폴리오별 한도 분리, 여러 전략 분산.
6. 다중검정 보정 지표(시도 횟수 대비 t-stat 기준).
7. (Owner 결정 필요) KRX OpenAPI / OpenDART key 발급 여부 → 데이터 교차검증·재무 데이터.

## Owner 결정 필요 사항

**Option A — MDD halt를 전략 단위로 유지 (현재)**
장점: 단순, 보수적. 단점: 이 시장에서는 모든 전략이 조기 중단 → 증거 축적 불가.

**Option B — 연구(research) 평가는 halt 없이, 페이퍼/실전 Risk는 포트폴리오 단위 halt**
장점: 전략 통계적 평가와 자본 보호를 분리. 단점: 설정이 두 벌.

**Recommendation: B.** 근거: 전략의 기대값 판단에는 전 기간 증거가 필요하고, 자본 보호는 실제 운용 계층(Paper)에서 포트폴리오 단위로 하는 것이 목표(“하나의 전략에 자본 집중 금지”)와도 맞다. Promotion Gate는 research 모드 MDD도 기준으로 사용한다.
