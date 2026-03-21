# Crypto 48h Baseline Review (Bitfinex)

Date: 2026-02-23
Window analyzed: last 48h from local review time (~2026-02-23 13:56 CET)
Mode: observation only (NO filter changes)

## Freeze rule (important)

- User decision: 30 days without changing any live filter / rule.
- This document is a baseline snapshot for periodic reviews.
- Review cadence from now: every 7 days, then final summary at day 30.

## Scope used in this review

Internal book / execution truth:

- `broker_orders`
- `trade_fills`
- `trade_signals`
- `swing_positions` (context for open positions / active exits)

Microstructure / context (for winner vs loser analysis):

- `signal_slayer_decisions`
- `crypto_snapshots` (`ticker`)
- `crypto_trades` (deduped by `trade_id`)
- `crypto_orderbooks` (used carefully; incremental updates, not always full book)

Excluded from integrity review:

- `crypto_orderbooks`, `market_ticks`, `crypto_trades` as primary truth sources

## 48h integrity check summary (internal book)

Result: internal book looked consistent / credible (`1/1`) in the reviewed window.

Counts (at review time):

- `broker_orders`: 51
- `trade_fills`: 33 (was 32 earlier; new XRP TP arrived during review)
- `trade_signals`: 86
- accepted signals: 16

Key consistency checks passed:

- accepted signals -> buy broker_orders: `16/16`
- accepted signals -> buy fills: `16/16`
- buy orders missing signal: `0`
- fills missing broker_order link: `0`
- filled broker_orders without fill row: `0`
- duplicate `corrId` / duplicate `broker_order_id` (48h window): `0`

Log correlation (batch grep):

- `51/51` broker orders had log trace (`corrId` and/or `broker_order_id`)
- `32/32` fills at that point had log trace
- later XRP TP (`exit-tp-831...`) also confirmed in logs and DB

## Important observed execution scenarios (confirmed as normal)

- OCO TP/SL lifecycle (submit -> one fills -> other canceled): working as expected
- `place-rolled-back` on BTC trail exits:
  - cause was exchange reject (`not enough exchange balance`)
  - local rollback happened correctly
  - later retry succeeded

This was treated as expected exchange sequencing / availability behavior, not a book corruption bug.

## Accepted signal cycle audit (48h)

Accepted signals analyzed: 16

Cycle mapping used:

- `accepted signal` -> `buy fill`
- first `sell fill` before next buy of same symbol = exit for that cycle

Outcome summary (updated with latest XRP TP):

- `winner`: 6
- `loser_sl`: 7
- `open`: 3

PnL summary for closed cycles:

- TP winners (5) + one `WIN-OTHER` (trail/manual style exit) produced positive group PnL
- SL group (7) produced expected negative group PnL

Open cycles at snapshot time:

- `ETHUSDT`
- `SOLUSDT`
- `BTCUSDT`

Each open cycle had both exits active (`TP sent` + `SL sent`).

## Winner vs loser_sl (microstructure/context) - what currently stands out

Sample size warning:

- small sample (`winner=6`, `loser_sl=7`)
- use as directional signal, not production change trigger

### Strongest separators seen (current 48h sample)

1. `regime` (from `signal_slayer_decisions`)

- winners: `6/6` in `NORMAL`
- losers_sl: `3/7` in `NORMAL`, `4/7` in `LOW`

2. `activity_ticks`

- winners average: `48.3`
- losers_sl average: `30.0`

3. `trade count` in 30s before entry (`crypto_trades`, deduped)

- winners average: `18.5`
- losers_sl average: `8.7`
- strongest symbol-normalized separator in this sample

4. `ticker depth imbalance` (`BidSize` vs `AskSize` from ticker snapshots)

- winners: near neutral / slightly positive on average
- losers_sl: more ask-heavy on average

5. Holding time (post-fact observation)

- winners tend to resolve faster than losers_sl
- winners median hold ~129 min
- losers_sl median hold ~294 min

### What did NOT separate well (in this sample)

- spread alone (`spread_bps`) was not a strong separator
- ATR fraction average was similar between groups (cross-symbol effects)
- raw `crypto_orderbooks` spread/depth is noisy without reconstructing full book state

### Interesting counter-signal

`buy_share` in 30s pre-entry (buy notional share) was often HIGHER in losers_sl than winners.

Interpretation hypothesis:

- "more buy flow" alone is not enough
- in some cases it may represent late entry / local exhaustion
- context (`regime + activity + structure`) matters more than one-sided flow alone

## Methodology notes (important for future comparisons)

1. `crypto_trades` duplication

- Table contains many duplicates.
- Trade-flow metrics were computed with deduplication by `trade_id`.
- Do not compare raw counts/notional from `crypto_trades` without dedupe.

2. `crypto_orderbooks` are incremental

- Many rows contain only bid or only ask updates.
- For stable spread/size at entry, `crypto_snapshots` (`ticker`) is safer.
- `crypto_orderbooks` can still be useful, but only with validation / pairing logic.

3. Cross-symbol comparability

- BTC/ETH/SOL/XRP scales differ.
- Symbol-normalized comparisons are preferred when ranking feature importance.

## What this suggests (without changing anything now)

No live changes now (30-day freeze remains).

But current evidence suggests that the most promising areas to monitor over the next weekly reviews are:

- `regime` (`LOW` vs `NORMAL`)
- `activity_ticks`
- pre-entry trade activity (`tr_cnt_30s`)
- ticker depth imbalance (`BidSize/AskSize`)
- interaction effects (not single metric only)

## Review plan (7-day cadence)

We continue with the same system settings, and only record observations.

### Checkpoint 1 (Day 7)

- Repeat internal book integrity checks
- Repeat accepted-cycle audit
- Recompute winner vs loser_sl features
- Compare stability of separators above

### Checkpoint 2 (Day 14)

- Same checks
- Start tracking per-symbol behavior consistency (BTC/ETH/SOL/XRP separately)
- Check if `LOW regime` remains disproportionately represented in stop-outs

### Checkpoint 3 (Day 21)

- Same checks
- Add simple threshold what-if tests (offline only, no config changes)
- Note false positive / false negative trade-offs

### Final checkpoint (Day 30)

- Consolidated report over full 30-day window
- Stable vs unstable signals
- Candidate changes (if any) ranked by confidence and expected impact
- Only then discuss filter changes

## Current stance (record)

- System remains unchanged.
- We are in observation / evidence collection mode.
- Decision quality is data-driven, not reaction-driven.

## Tracking note (2026-02-23): `crypto_trades` duplicates / tick inflation

Status: confirmed issue (data correctness), not execution-book issue.

### What was confirmed

- `crypto_trades` contains heavy duplication for Bitfinex trades (dominant pattern is ~`2x`, small amount `3x`).
- Root cause is consistent with Bitfinex WS public trades behavior (`te` + `tu` updates for the same trade), plus occasional snapshot/live overlap on reconnect.
- Our `BitfinexWebSocketFeed` currently emits `TradeReceived` for both paths, and `CryptoTradesRepository` inserts without dedupe guard.

### Why this matters (impact)

Direct / live impact:

- `activity_ticks` are inflated because they are counted from `TradeReceived` in-memory (`CryptoTradeTicksBatchWriter`), before DB insert.
- `SignalSlayer` micro-filter uses that inflated activity count (`MinActivityTicks` semantics drift).

Likely impact:

- Crypto trend context reads `crypto_trades` (raw), so duplicated trades can distort `TrendMinPoints` and trend quality scoring inputs.

No direct impact:

- `broker_orders`
- `trade_fills`
- `trade_journal`
- `daily_pnl_crypto`

### Decision taken (record)

- We do **not** need historical DB cleanup right now to continue.
- We **do** need to fix the pipeline going forward so new data is correct.
- `MinActivityTicks` recalibration is **mandatory** and must be data-driven (not heuristic `/2`).

### Fix direction (planned, not implemented yet)

1. Runtime dedupe on Bitfinex trade stream path (starting from WS message ingestion)

- Goal: correct `activity_ticks` in real time and prevent duplicate trade rows from being produced.
- Prefer dedupe by `(exchange, symbol, trade_id)` with short TTL protection for `te/tu` + reconnect overlap.

2. DB safety net for `crypto_trades`

- Add uniqueness protection for non-null `trade_id` (`exchange + symbol + trade_id`) and ignore duplicates on insert.
- Purpose: protect DB from regressions even if runtime duplicate slips through.

3. `MinActivityTicks` calibration (required)

- Recalculate threshold using collected historical data and deduped trade counts.
- Do not apply naive `/2`.
- Target: preserve intended filter semantics after dedupe fix.

4. Trend read-path review

- Verify / patch trend queries to avoid duplicate trade influence (especially while old rows remain duplicated).

### Tick usage map (from WS message to consumers) - review starting point

Bitfinex WS trade messages (`te` / `tu` / snapshot rows)

- `BitfinexWebSocketFeed` emits `TradeReceived`

Branches from `TradeReceived`:

- `CryptoTradeTicksBatchWriter.Add(...)`
  - maintains in-memory rolling counts (`IActivityTicksProvider`)
  - batches rows into `crypto_trades`
- periodic flush -> `CryptoTradesRepository.InsertBatchAsync(...)`

Consumers of trade tick outputs:

- `PullbackInUptrendStrategy` -> `IActivityTicksProvider.GetTicksInWindow(...)`
- `SignalSlayer` -> `ActivityTicks` (`MinActivityTicks` gate)
- `SignalSlayerDecisionRepository` (records observed activity values)
- `CryptoTrendContextProvider` -> `TrendMarketDataRepository` -> `crypto_trades` -> `TrendPriceMath`

### Next action set (agreed direction)

- Keep strategy filters frozen from a tuning perspective.
- Treat duplicate trade handling as data correctness / infra fix.
- Perform data-driven `MinActivityTicks` calibration using existing collected data before changing threshold.

### Helper SQL files (created for rollout / calibration)

- `src/Denis.TradingEngine.Strategy/Analysis/sql/01-crypto-trades-dedupe-rollout.sql`
- `src/Denis.TradingEngine.Strategy/Analysis/sql/02-min-activity-ticks-calibration.sql`
