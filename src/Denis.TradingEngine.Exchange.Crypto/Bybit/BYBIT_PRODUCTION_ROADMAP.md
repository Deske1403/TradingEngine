# Bybit Production Roadmap for Denis.TradingEngine

## Context
Bitfinex integration is currently the most mature exchange path, but liquidity is limited for the target strategy universe.
Bybit has better market breadth/liquidity, so this document defines what is needed to move Bybit from current state to stable production.

## Current State (What Already Exists)

### Implemented
- Public market data pipeline exists for Bybit:
  - WebSocket feed for ticker/trades/orderbook (`BybitWebSocketFeed.cs`)
  - Market data adapter to `IMarketDataFeed` (`BybitMarketDataFeed.cs`)
- REST trading API exists (`BybitTradingApi.cs`) with:
  - place limit
  - place stop
  - cancel order
  - fetch open orders
  - fetch balances
- Exchange wiring exists in both startup paths:
  - `Program.cs`
  - `Trading/CryptoTradingRunner.cs`
- Generic crypto order adapter already supports Bybit through `ICryptoTradingApi`:
  - `Adapters/CryptoOrderService.cs`
- Config and symbol metadata support exist:
  - `appsettings.crypto.bybit.json`
  - `CryptoSymbolMetadataProvider.cs`
- Fees and exchange-level trading params include Bybit defaults:
  - `Config/CryptoFeeProvider.cs`
  - `Config/CryptoExchangeTradingParams.cs`

### Important Limitations vs Bitfinex
- No Bybit equivalent of `BitfinexOrderManager` (private order stream + reconciliation manager).
- Runtime order lifecycle updates are less robust (fills/partials/cancels are not forwarded with the same reliability path as Bitfinex).
- Startup position sync path in launch flow is currently Bitfinex-specific condition logic.
- OCO/TP/SL behavior is not hardened with Bybit-specific edge-case handling to the same level as Bitfinex.
- Operational hardening (rate-limit strategy, idempotency, reconciliation semantics, alerting specifics) is not yet complete.

## Critical Gap Analysis

### Gap 1: Order Lifecycle Reliability (Highest Priority)
Without a Bybit private-order lifecycle manager, engine state can drift from exchange state during:
- partial fills
- cancel/replace races
- reconnect windows
- restart recovery windows

### Gap 2: Startup and Recovery Generalization
Current sync/recovery flow is strongest on Bitfinex branching.
Bybit needs first-class startup recovery behavior, not fallback behavior.

### Gap 3: Exchange-Specific Edge Cases
Bybit API-specific response/error semantics need explicit handling in production paths:
- retCode-based classification
- retry policy for transient responses
- cancellation idempotency
- precision/minQty/notional validation strictness

### Gap 4: Risk and Ops Readiness
Need production controls for:
- canary symbol rollout
- exchange-specific alerting
- deeper monitoring of order state lag and reconcile drift

## Delivery Plan (Step-by-Step)

## Phase 0 - Security and Configuration Hygiene (0.5 to 1 day)
1. Move Bybit API credentials from json files to environment/secret store.
2. Ensure no real credentials are committed to repository.
3. Add config validation at startup (missing keys, invalid category, disabled exchange mismatch).
4. Define explicit environments:
- paper
- bybit testnet
- bybit mainnet

Acceptance:
- Engine starts only with valid env-specific credentials.
- No plaintext production keys in tracked config files.

## Phase 1 - Bybit Paper/Readiness Baseline (1 to 2 days)
1. Enable Bybit in paper mode for 2-3 high-liquidity symbols.
2. Run soak session (24h+) with:
- reconnects
- websocket interruptions
- quote gaps
3. Verify strategy signal quality and fill simulation behavior.
4. Validate DB persistence (signals, orders, fills, snapshots) and Discord alerts.

Acceptance:
- Stable runtime with no critical state corruption.
- Clean restart/recovery in paper mode.

## Phase 2 - Real-Mode MVP (3 to 5 days)
1. Implement Bybit order lifecycle manager (same role as BitfinexOrderManager):
- subscribe private order/execution updates
- map events to `OrderResult`
- emit to orchestrator (`OnOrderUpdated`)
2. Add REST reconciliation backup loop for Bybit open/final states.
3. Generalize startup sync logic so Bybit and Bitfinex use same recovery contract.
4. Wire balance->position sync for Bybit as first-class real-mode path.
5. Add per-exchange reconcile metrics/log markers.

Acceptance:
- Real mode can place/cancel and correctly process partial/filled/canceled statuses.
- Restart recovery restores pending orders and positions correctly.

## Phase 3 - Production Hardening (4 to 7 days)
1. Add robust error policy by Bybit retCode class:
- retryable
- terminal reject
- manual attention required
2. Add idempotent cancel and duplicate-event protection in Bybit flow.
3. Validate precision/step/min-notional checks against live symbol rules.
4. Stress test:
- burst order activity
- reconnect storms
- delayed websocket delivery
5. Add explicit alerts for:
- reconcile mismatch
- prolonged pending orders
- repeated API failures

Acceptance:
- No unresolved order-state drift in test runs.
- Alerting catches operational degradation quickly.

## Phase 4 - Controlled Production Rollout (3 to 5 days)
1. Canary deployment with 1 symbol and strict limits.
2. Expand to 3-5 symbols after stable period.
3. Expand to target universe in steps with rollback checkpoints.
4. Freeze release criteria:
- max tolerated reconcile mismatches/day
- max tolerated failed order operations/day
- max tolerated restart recovery discrepancies

Acceptance:
- Meets reliability SLOs before full rollout.

## Estimated Effort
- Fast MVP (tradable with supervision): 3 to 5 working days
- Production-grade parity with Bitfinex robustness: 2 to 4 weeks

Assumptions:
- One senior engineer, focused delivery
- No major Bybit API behavior change during implementation
- Existing orchestration architecture remains unchanged

## Test Strategy

### Functional
- place limit buy/sell
- place stop/exit paths
- partial fill progression
- full fill progression
- cancel before fill
- cancel after partial fill

### Recovery
- restart during open pending orders
- restart during partial fill state
- recover from stale pending records
- recover with transient API unavailability

### Operational
- websocket disconnect/reconnect loops
- API rate-limit pressure tests
- notification verification (Discord)
- DB consistency checks across `broker_orders`, fills, and position state

## Risks and Mitigations

1. Risk: Order state drift between engine and exchange.
- Mitigation: private stream + periodic reconciliation + idempotent updates.

2. Risk: Category/account-mode mismatch (spot vs linear, unified account semantics).
- Mitigation: strict startup validation + explicit env config profiles.

3. Risk: Wrong fee assumptions degrade PnL/risk controls.
- Mitigation: keep fee config external and account-tier aware; validate against account-level effective fees.

4. Risk: Hidden precision/min-notional rejects in live flow.
- Mitigation: symbol-rule pre-validation and reject telemetry.

## Bybit Fee and API Notes (for this project)

### Fee Model Note
Bybit fee tables are tier/account dependent and can change.
Do not hardcode one static fee assumption globally.
Use configurable maker/taker per market type and validate with live account effective fees.

### API Design Notes Relevant to Implementation
- V5 API unifies Spot/Linear/Inverse/Options under category-based routing.
- Order creation uses `POST /v5/order/create` with product `category`.
- Bybit docs also describe cancellation flows by settlement currency (`settleCoin`) for derivatives use cases.
- For market data load, WebSocket usage is preferred operationally over heavy REST polling.

## Code Areas to Touch for Full Bybit Production
- `src/Denis.TradingEngine.Exchange.Crypto/Bybit/BybitTradingApi.cs`
- `src/Denis.TradingEngine.Exchange.Crypto/Bybit/BybitWebSocketFeed.cs`
- `src/Denis.TradingEngine.Exchange.Crypto/Program.cs`
- `src/Denis.TradingEngine.Exchange.Crypto/Trading/CryptoTradingRunner.cs`
- `src/Denis.TradingEngine.Exchange.Crypto/Trading/CryptoTradingOrchestrator.cs`
- `src/Denis.TradingEngine.Exchange.Crypto/Adapters/CryptoOrderService.cs`
- `src/Denis.TradingEngine.Exchange.Crypto/appsettings.crypto.bybit.json`

## External References (Reviewed)
- Bybit fee page: https://www.bybit.com/en/announcement-info/fee-rate/
- Bybit developer portal: https://www.bybit.com/future-activity/en/developer
- Bybit V5 intro (incl. cancel by settlement currency note):
  https://bybit-exchange.github.io/docs/v5/intro#cancellation-of-orders-by-settlement-currency

Reviewed on: 2026-03-03 (Europe/Belgrade).
