# M1 — Research Foundation 구현 계획 (짧은 버전)

목표 우선순위 (Owner 최신 지시):
비용·슬리피지 반영 / look-ahead 금지 / train·validation 분리 / walk-forward 필수 / MDD 제한 /
일일 손실 한도 / 단일 전략 집중 금지 / paper > backtest 신뢰 / regime 변화 시 중단 / 실주문 금지.
`일평균 +1%`는 측정 대상(Research Target)일 뿐 최적화 목표가 아니다.

## 0. 작업환경 확인 결과 (2026-09-27)

| 항목 | 상태 |
|---|---|
| .NET | SDK 10.0.401 (net10.0) |
| PostgreSQL | 12.22 바이너리. sudo 없음 → 사용자 소유 클러스터 `scripts/db.sh` (127.0.0.1:55432, trust) |
| KRX data.krx.co.kr | **로그인 필요** (`LOGOUT` 응답) — 현재 사용 불가 |
| KRX OpenAPI / OpenDART | API key 필요 — 환경에 key 없음 |
| Naver chart API | 동작. 수정주가 OHLC, **거래량은 비수정**, 상장폐지 종목 과거 데이터 유지 |
| KIND | 상장법인 목록(업종 포함), 상장폐지 목록(일자·사유) 조회 가능 |
| gh CLI / git remote | 없음 → PR 단위를 **stacked local branch** 로 만든다 |

## 1. Solution 구조

```
src/
  Investment.Domain        엔티티, enum, Bar/Signal, IStrategy, 비용 모델 계약
  Investment.Persistence   EF Core + Npgsql, DbContext, migrations
  Investment.MarketData    Naver/KIND 클라이언트, 수집, 데이터 품질 검사, point-in-time universe
  Investment.Strategies    전략 구현 (Momentum, MeanReversion, …)
  Investment.Risk          Risk Engine (포지션/노출/일손실/MDD/섹터/손절)
  Investment.Backtest      시뮬레이터, 체결·비용 모델, Metric 계산
  Investment.Research      (M2) OOS / walk-forward / promotion gate / 실패 기록 / regime
  Investment.PaperTrading  (M3)
  Investment.Cli           실행 진입점 (ingest / backtest / rerun / report)
tests/
  Investment.Backtest.Tests, Investment.Strategies.Tests,
  Investment.MarketData.Tests, Investment.Integration.Tests (실제 PostgreSQL)
```
API(ASP.NET Core)는 M1에서는 만들지 않는다 — CLI로 충분하고, Research Agent 단계에서 필요해질 때 추가.

## 2. 데이터 소스 — 결정 필요 사항

**Option A — Naver chart API + KIND (키 불필요)**
- 장점: 즉시 사용 가능, 상장폐지 종목 가격 포함(생존편향 제거 가능), 수정주가, JSON
- 단점: 비공식 API(변경 위험), 거래량 비수정(분할 전 거래대금 과소추정), 시가총액·상장주식수 이력 없음

**Option B — KRX OpenAPI / OpenDART (공식)**
- 장점: 공식, 일자별 전종목 시가총액·상장주식수·거래대금, 재무 데이터
- 단점: API key 필요(현재 없음), 발급 전까지 진행 불가

**Recommendation: A로 시작, `IPriceSource` 추상화로 B 추가 가능하게.**
근거: 지금 실제 결과를 낼 수 있는 유일한 경로. 데이터 소스와 버전을 BacktestRun에 기록하므로 나중에 B로 재검증 가능.
Owner가 KRX OpenAPI / OpenDART key를 제공하면 M2에서 교차검증 소스로 추가한다.

## 3. Universe — 결정 필요 사항

**Option A — point-in-time 유동성 상위 N (상장+상장폐지 전체에서, 그 날짜 이전 데이터로 선정)**
- 장점: 생존편향 제거, 당시 실제 거래 가능 종목
- 단점: 수집량 증가, 분할 이전 거래대금 과소추정 영향

**Option B — 현재 KOSPI200 등 고정 목록**
- 장점: 단순
- 단점: 생존편향 (살아남은 종목만 → 백테스트 과대평가)

**Recommendation: A.** 기본값은 월별 재선정, 직전 60거래일 거래대금 중앙값 상위 100, 우선주·스팩·ETF·리츠 제외.
수집 대상: 현재 상장 전종목 중 KOSPI/KOSDAQ 보통주 + 2017년 이후 상장폐지 종목.

## 4. 최소 DB schema

`securities`, `daily_prices`(PK ticker+date, halted flag), `index_prices`, `ingestion_runs`,
`strategies`, `strategy_versions`(파라미터 jsonb, code hash, 불변), `backtest_runs`(재현성 필드 전부),
`backtest_metrics`(gross/net 구분), `backtest_trades`, `backtest_equity`.
M2에서 `strategy_evaluations`, `strategy_promotions`, `experiment_failures`, `market_regimes` 추가.

## 5. Strategy interface

```csharp
interface IStrategy {
  StrategyDescriptor Descriptor { get; }      // id, family, version, hypothesis, parameters
  int WarmupBars { get; }
  IReadOnlyList<Signal> GenerateSignals(StrategyContext ctx);  // Buy/Sell/Hold + Score + Reason
}
```
`StrategyContext`는 날짜 D 종가까지의 데이터만 노출(배열 slice). 전략은 주문·저장·자금관리를 하지 않는다.

## 6. Backtest architecture

일봉, 이벤트 순서 고정:
1. D 장 마감 → 전략 신호 생성 (D까지의 데이터만)
2. Risk Engine이 신호 → 목표 포지션 (최대 종목 수, 종목당 비중, 총 노출, 섹터, 일손실/MDD 차단)
3. D+1 **시가** 체결 (+slippage). 거래정지·하한가/상한가 잠김은 체결 불가
4. D+1 장중 손절/익절 (갭 발생 시 시가 체결, 같은 날 둘 다 닿으면 손절 우선 — 보수적)
5. D+1 종가 평가

gross(비용 전)/net(비용 후)는 **같은 체결 경로**에서 비용만 분리해 계산 → 같은 거래 집합 비교.

## 7. 첫 두 전략

- **Momentum** (횡단면): lookback 60일 수익률(최근 5일 skip) 상위 K 매수, H일 보유 또는 순위 이탈 시 청산.
- **MeanReversion** (단기 과매도): 20일 z-score < -2 & 종가 > 200일선(장기 추세 유지 종목만), 종가가 5일선 회복 또는 최대 5일 보유 후 청산.

파라미터는 사전에 고정한 기본값만 사용 — M1에서는 파라미터 탐색을 하지 않는다(overfitting 방지).

## 8. 거래비용 모델

- 수수료: 매수·매도 각 0.015% (설정 가능)
- 매도 세금(거래세+농특세) 일자별 스케줄: ~2019-06-02 0.30%, 2019-06-03 0.25%, 2021 0.23%, 2023 0.20%, 2024 0.18%, 2025 0.15%, 2026 0.20%
- Slippage: 기본 편도 0.10% + 거래대금 대비 주문금액 비례항(설정 가능)

## 9. 평가 metric

Total Return, CAGR, Avg Daily Return, Win Rate, Profit Factor, Avg Profit/Loss, EV/trade(%, KRW),
EV t-stat, MDD, Sharpe, Sortino, #Trades, Turnover, Avg Holding, Exposure, 벤치마크(KOSPI) 대비. 모두 gross/net.

## 10. 테스트 전략

- 엔진: 수작업 계산 가능한 합성 데이터로 체결가·비용·PnL·equity 검증
- **Look-ahead 테스트**: D 이후 데이터를 무작위로 바꿔도 D까지의 신호·거래가 동일해야 함
- 미래 접근 시도 전략이 예외를 받는지
- 거래정지/상한가 잠김/상장폐지 처리, 손절 갭 처리
- Metric 공식 단위 테스트
- 재현성: 같은 입력 두 번 실행 → result hash 동일
- 통합: 실제 PostgreSQL에 저장/재조회

## 11. PR 분할 (stacked branches)

1. `m1/pr1-foundation` — solution, Domain, Persistence, DB script, 첫 migration
2. `m1/pr2-market-data` — Naver/KIND 수집, 품질 검사, universe
3. `m1/pr3-backtest-engine` — 엔진, 비용, Risk 기초, 엔진 테스트
4. `m1/pr4-strategies` — Momentum, MeanReversion + 테스트
5. `m1/pr5-metrics-reporting` — Metric, 결과 저장, CLI backtest/rerun, 재현성 검증

이후 M1 결과 보고서 → (Owner 최신 지시에 따라 멈추지 않고) M2 walk-forward / promotion gate, M3 paper trading 순으로 진행.
Live trading / 실제 주문 API는 구현하지 않는다.
