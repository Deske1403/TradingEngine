# IBKR EOD Skim Order - Upgrade Plan (V1)

Date: 2026-02-22

## Goal

Add optional end-of-day IBKR profit-skimming behavior that closes already-profitable positions near session close when TP was not hit.

- Purpose: reduce overnight risk while locking in profit.
- If disabled, behavior remains exactly as today.

## Agreed V1 Scope

Keep V1 intentionally simple:

- Separate config section (do not mix with `SwingTrading`)
- Real mode only (no paper-mode support in scope)
- Limit-based exit (no market order fallback in V1)
- Minimal number of config parameters
- Include explicit **minimum net-profit** guard in JSON (to cover fees/slippage buffer)

## Desired Behavior (V1)

When `IbkrEodSkim.Enabled=true`:

1. In final `StartMinutesBeforeClose` before market close, scan open IBKR positions.
2. For each symbol:
   - position must be in profit,
   - estimated net profit must be at least `MinNetProfitUsd`,
   - quote must be fresh and spread acceptable (internal safety checks).
3. If eligible:
   - cancel existing exit orders (TP/SL) for that symbol,
   - place SELL limit intended to execute near current best executable price,
   - retry/reprice up to `MaxRetries` if still open.
4. `DryRun=true`:
   - do not place/cancel orders,
   - only log what would happen.

When `IbkrEodSkim.Enabled=false`:

- Existing logic remains unchanged.

## Pricing Approach (V1)

- Use **limit orders**, not market orders.
- Price should be based on current executable market data (typically anchored to bid-side for SELL).
- Repricing can become more aggressive near close, but still stays limit-based.
- Exact reprice interval and quote/spread thresholds can be hardcoded internally in V1 (not exposed in JSON yet).

## Suggested Config (appsettings.json)

Recommended separate section: `IbkrEodSkim`

```json
"IbkrEodSkim": {
  "Enabled": false,
  "DryRun": true,
  "StartMinutesBeforeClose": 60,
  "MaxRetries": 3,
  "MinNetProfitUsd": 2.00,
  "ReasonTag": "EOD-SKIM"
}
```

Notes:

- `MinNetProfitUsd` is the explicit net-profit guard (after fee/slippage buffer estimate).
- `DryRun` is required for safe rollout and observability before live activation.
- Session window should be calculated from the **actual market close** (avoid hardcoding BGD clock times in logic due to DST changes).

## Current Code Flow Inventory (What We Have / What We Don't)

This section captures the current implementation reality in `TradingOrchestrator` + IBKR order stack, to reduce risk when implementing EOD skim.

### What already exists (reuse candidates)

- Real order place/cancel flow in orchestrator:
  - `PlaceRealAsync(...)`
  - `FireAndForgetCancel(...)`
  - central `OnOrderUpdated(...)` status/commission/fill handling
- Existing helper to cancel all exit orders for one symbol:
  - `CancelAllExitsForSymbol(symbol, nowUtc, reason)`
- Existing quote safety checks (good for EOD skim V1):
  - `TryGetQuote(...)` with quote freshness + NBBO + spread guard
  - hardcoded quote age / spread thresholds already exist in orchestrator
- Existing swing auto-exit pattern similar to EOD skim:
  - flow already does `CancelAllExitsForSymbol(...)` + `SendExit(...)`
- Existing IBKR recovery mapping for open orders:
  - `reqOpenOrders` snapshot -> `OrderRef -> TWS orderId`
  - registration of recovered exit orders into `IbkrOrderService`

### What does NOT exist yet (gaps)

- No `Modify/Replace` order API in abstractions:
  - `IOrderService` currently supports only `PlaceAsync` and `CancelAsync`
  - `IIbkrClient` currently supports only place/cancel
- No EOD skim scheduler/state machine/config model yet
- No dedicated `ReasonTag` field on `OrderRequest` (V1 should reuse `CorrelationId` prefix + DB `last_msg` logging)
- No runtime `IbkrOpenOrdersProvider` service wired into orchestrator loop (currently used in startup/recovery flow)

## Sensitive Area (Important for EOD Skim)

### External IBKR positions are currently blocked by `SendExit(...)`

Current code has an explicit protection that blocks `SendExit(...)` for positions marked as `External/IBKR`.

Implication:

- EOD skim **cannot** simply call existing `SendExit(...)` as-is.
- We need one of these approaches:
  - add a dedicated EOD skim exit method (recommended for clarity), or
  - extend `SendExit(...)` with a very explicit override flag (e.g. internal-only path for EOD skim).

### Modify vs Cancel+New (V1 decision)

Given current codebase shape:

- `cancel + new` is lower-risk for V1
- `modify` is not currently supported end-to-end through `IOrderService` / `IIbkrClient`
- existing event/recovery/idempotency plumbing is already built around place/cancel lifecycle

Therefore V1 should use:

1. cancel existing TP/SL exits for symbol
2. place skim limit
3. on retry/reprice: cancel current skim order + place new skim order

## Runtime Execution Flow (When Enabled)

This is the concrete V1 runtime flow based on the current codebase shape.

### `Enabled=false` (current behavior preserved)

- EOD skim path returns immediately (no-op)
- System behavior remains the same as today

### `Enabled=true` (new mode)

1. `TradingOrchestrator` calls EOD skim evaluation from the existing periodic/heartbeat loop
   - natural integration point is the same place where swing auto-exit monitoring runs

2. EOD skim service validates global prerequisites
   - real mode only (`_orderService` available)
   - IBKR path active
   - feature flag enabled

3. Check session window
   - run only in final `StartMinutesBeforeClose`
   - calculate against actual market close (not hardcoded BGD hour)

4. Read current open positions
   - use local position snapshot (`_positionBook`)
   - process live long positions (`qty > 0`)

5. Per-symbol idempotency/state check
   - skip symbols already in active skim flow unless retry/reprice is due
   - track per-symbol skim state (`retryCount`, `lastActionUtc`, `activeSkimCorr`, `activeBrokerOrderId`)

6. Quote safety checks
   - reuse existing `TryGetQuote(...)`
   - requires fresh quote + NBBO + acceptable spread

7. Build candidate skim SELL limit price
   - limit-only
   - price anchored to current executable market data (typically bid-side for SELL)
   - V1 may use hardcoded tick/aggression rules

8. Net-profit eligibility check
   - estimated net profit (after fee/slippage buffer estimate) must be `>= MinNetProfitUsd`
   - if not eligible: log skip reason and continue

9. `DryRun=true` behavior (initial rollout mode)
   - log exact intended actions (`cancel exits`, `place skim`, `retry/reprice`)
   - no real cancel/place requests
   - no side effects

10. `DryRun=false` eligible path: cancel existing exits
   - cancel TP/SL exits for symbol using existing cancel pipeline
   - reuse `CancelAllExitsForSymbol(...)`

11. Place skim exit limit
   - use a dedicated EOD skim exit path (recommended)
   - do not rely on current `SendExit(...)` as-is because it blocks `External/IBKR` positions

12. Update skim state after submit
   - store correlation id / broker order id / retry count / price / timestamp

13. Retry/reprice (if still open and not filled)
   - when retry interval is due and `retryCount < MaxRetries`
   - cancel current skim order + place new skim order (V1 `cancel + new`)
   - update skim state

14. Cleanup/reset skim state
   - position closed
   - skim order filled / canceled / rejected
   - session end / new trading day
   - reconnect + resync completed

## Encapsulation / Class Design (Recommended)

To avoid spreading EOD skim logic across `TradingOrchestrator`, use a dedicated class and keep orchestrator as the caller/coordinator only.

### Recommended structure (V1)

- `IbkrEodSkimOptions`
  - strongly typed config model (`Enabled`, `DryRun`, `StartMinutesBeforeClose`, `MaxRetries`, `MinNetProfitUsd`, `ReasonTag`)

- `IbkrEodSkimCoordinator` (or `IbkrEodSkimEngine`)
  - main EOD skim decision + execution flow
  - orchestrator calls this service from heartbeat

- internal per-symbol state (can be inside coordinator for V1)
  - dictionary keyed by symbol
  - stores retry/status/action timestamps and active skim order references

- optional decision/action model (nice-to-have)
  - e.g. `IbkrEodSkimDecision` / `IbkrEodSkimPlan`
  - useful for `DryRun` logging and easier testing

### Why this approach

- better encapsulation (less risk in `TradingOrchestrator`)
- easier to test `DryRun` behavior and eligibility logic
- easier to iterate later (modify pricing/retries/state machine) without touching core orchestrator flow

### Integration rule

- `TradingOrchestrator` should only:
  - call EOD skim evaluator in periodic loop
  - pass required dependencies/context (positions, quote access, cancel/place adapters, clock/time)
  - forward relevant order updates for skim-state cleanup if needed

- Existing behavior must remain unchanged when `Enabled=false`
- Initial rollout should be `Enabled=true` + `DryRun=true`

## Delivery Plan (Implementation Steps To Add)

Recommended implementation order (small, testable increments):

1. Add config model + config binding (no behavior change)
   - add `IbkrEodSkim` section to `appsettings.json`
   - add strongly typed `IbkrEodSkimOptions` class
   - bind + log values in startup
   - validate ranges (`StartMinutesBeforeClose > 0`, `MaxRetries >= 0`, `MinNetProfitUsd >= 0`)

2. Add EOD skim coordinator skeleton (no execution yet)
   - create `IbkrEodSkimCoordinator` class
   - define public `EvaluateAsync(...)` entrypoint
   - define internal per-symbol state structure
   - define dependency adapters/delegates used by orchestrator

3. Wire coordinator into `TradingOrchestrator` in no-op mode
   - create coordinator instance (if config present)
   - call it from periodic/heartbeat loop
   - return immediately when `Enabled=false`
   - verify no behavior changes

4. Implement `DryRun` decision flow only (no real orders)
   - session window check
   - position snapshot scan
   - `TryGetQuote(...)` reuse
   - candidate price calculation
   - net-profit check (`MinNetProfitUsd`)
   - log decisions / skip reasons / intended actions

5. Implement real cancel path (`DryRun=false`) for eligible symbols
   - reuse `CancelAllExitsForSymbol(...)`
   - add skim state transitions (`canceling` / `ready-to-place`)
   - ensure idempotency (no duplicate cancel spam)

6. Implement skim limit place path (dedicated EOD skim exit path)
   - do not reuse `SendExit(...)` as-is (external IBKR block)
   - create dedicated submit helper for EOD skim exits
   - store skim order correlation + broker id in state

7. Implement retry/reprice (`cancel + new`)
   - retry interval (hardcoded V1)
   - if still open and retry due: cancel current skim order, then place new skim limit
   - stop after `MaxRetries`

8. Implement state cleanup hooks
   - cleanup on fill/cancel/reject (from `OnOrderUpdated`)
   - cleanup on position close
   - cleanup on session end / new day
   - reconnect/resync safety cleanup

9. Add focused logging/tracing
   - `eod-skim-start`, `eligible`, `skip`, `dryrun`, `cancel`, `place`, `reprice`, `done`
   - include symbol, qty, candidate price, estimated net profit, retry count

10. Rollout sequence
   - deploy with `Enabled=true`, `DryRun=true`
   - observe multiple sessions
   - switch to `DryRun=false`

## Proposed Class Skeleton (No Implementation Yet)

The goal is to keep `TradingOrchestrator` thin and move EOD skim behavior into one isolated component.

### Config model (example)

```csharp
public sealed class IbkrEodSkimOptions
{
    public bool Enabled { get; set; } = false;
    public bool DryRun { get; set; } = true;
    public int StartMinutesBeforeClose { get; set; } = 60;
    public int MaxRetries { get; set; } = 3;
    public decimal MinNetProfitUsd { get; set; } = 2.00m;
    public string ReasonTag { get; set; } = "EOD-SKIM";
}
```

### Coordinator skeleton (example)

```csharp
public sealed class IbkrEodSkimCoordinator
{
    private readonly IbkrEodSkimOptions _options;
    private readonly ILogger _log;
    private readonly object _sync = new();
    private readonly Dictionary<string, SkimSymbolState> _stateBySymbol =
        new(StringComparer.OrdinalIgnoreCase);

    public IbkrEodSkimCoordinator(IbkrEodSkimOptions options, ILogger log)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public Task EvaluateAsync(IbkrEodSkimContext context, CancellationToken ct = default);

    public void OnOrderUpdated(IbkrEodSkimOrderUpdate update);

    public void OnSessionBoundary(DateTime utcNow);

    public void ResetSymbol(string symbol, string reason, DateTime utcNow);

    private Task EvaluateEnabledAsync(IbkrEodSkimContext context, CancellationToken ct);

    private bool IsWithinSkimWindow(DateTime utcNow, DateTime marketCloseUtc);

    private IEnumerable<IbkrEodSkimPositionCandidate> GetEligiblePositionCandidates(IbkrEodSkimContext context);

    private bool TryBuildDecision(
        IbkrEodSkimContext context,
        IbkrEodSkimPositionCandidate candidate,
        DateTime utcNow,
        out IbkrEodSkimDecision decision,
        out string? skipReason);

    private decimal ComputeCandidateSellLimit(IbkrEodSkimContext context, string symbol, MarketQuote quote, int retryCount);

    private decimal EstimateNetProfitUsd(IbkrEodSkimPositionCandidate candidate, decimal candidateSellPx);

    private Task ExecuteDecisionAsync(IbkrEodSkimContext context, IbkrEodSkimDecision decision, CancellationToken ct);

    private Task ExecuteDryRunAsync(IbkrEodSkimContext context, IbkrEodSkimDecision decision, CancellationToken ct);

    private Task ExecuteLiveAsync(IbkrEodSkimContext context, IbkrEodSkimDecision decision, CancellationToken ct);

    private Task CancelExistingExitsAsync(IbkrEodSkimContext context, IbkrEodSkimDecision decision, CancellationToken ct);

    private Task PlaceSkimLimitAsync(IbkrEodSkimContext context, IbkrEodSkimDecision decision, CancellationToken ct);

    private bool IsRetryDue(SkimSymbolState state, DateTime utcNow);

    private void UpsertStateAfterPlace(string symbol, string correlationId, string? brokerOrderId, decimal limitPx, DateTime utcNow);

    private void MarkStateCancelRequested(string symbol, DateTime utcNow);

    private void CleanupClosedOrTerminalSymbols(IbkrEodSkimContext context, DateTime utcNow);

    private void LogSkip(string symbol, string reason, DateTime utcNow);
}
```

### Supporting models (example)

```csharp
public sealed class IbkrEodSkimContext
{
    public DateTime UtcNow { get; init; }
    public DateTime MarketCloseUtc { get; init; }
    public bool IsRealMode { get; init; }

    public IReadOnlyList<IbkrEodSkimPositionCandidate> OpenPositions { get; init; } =
        Array.Empty<IbkrEodSkimPositionCandidate>();

    public Func<string, DateTime, (bool Ok, MarketQuote? Quote, string? Reason)> TryGetQuote { get; init; } =
        default!;

    public Func<string, DateTime, string, Task> CancelAllExitsForSymbolAsync { get; init; } =
        default!;

    public Func<IbkrEodSkimPlaceRequest, CancellationToken, Task<IbkrEodSkimPlaceResult>> PlaceSkimExitAsync { get; init; } =
        default!;
}

public sealed record IbkrEodSkimPositionCandidate(
    string Symbol,
    decimal Quantity,
    decimal AveragePrice,
    bool IsExternalIbkrPosition);

public sealed record IbkrEodSkimDecision(
    string Symbol,
    decimal Quantity,
    decimal AveragePrice,
    decimal CandidateLimitPrice,
    decimal EstimatedNetProfitUsd,
    int RetryCount,
    string ReasonTag,
    bool IsRetry);

public sealed record IbkrEodSkimPlaceRequest(
    string Symbol,
    decimal Quantity,
    decimal LimitPrice,
    string CorrelationId,
    string ReasonTag);

public sealed record IbkrEodSkimPlaceResult(
    string CorrelationId,
    string? BrokerOrderId);

public sealed record IbkrEodSkimOrderUpdate(
    string? BrokerOrderId,
    string Status,
    string? CorrelationId);

internal sealed class SkimSymbolState
{
    public string Symbol { get; set; } = string.Empty;
    public string? ActiveCorrelationId { get; set; }
    public string? ActiveBrokerOrderId { get; set; }
    public decimal? LastLimitPrice { get; set; }
    public int RetryCount { get; set; }
    public DateTime? LastActionUtc { get; set; }
    public DateTime? LastCancelRequestUtc { get; set; }
    public bool CancelRequested { get; set; }
}
```

### Integration note for orchestrator

- `TradingOrchestrator` should create the context and call `coordinator.EvaluateAsync(...)`
- `TradingOrchestrator.OnOrderUpdated(...)` can optionally forward relevant events to `coordinator.OnOrderUpdated(...)`
- Existing `Enabled=false` path must remain a strict no-op

## Files To Change

1. `src/Denis.TradingEngine.App/appsettings.json`
   - Add `IbkrEodSkim` section.

2. `src/Denis.TradingEngine.Core/...` config model file (new or existing shared config class)
   - Add strongly typed properties for `IbkrEodSkim`.
   - Do not attach to `SwingTradingConfig` unless there is a strong codebase constraint.

3. `src/Denis.TradingEngine.App/Trading/TradingOrchestrator.cs`
   - Add EOD skim scheduler/check.
   - Add symbol-level skim flow:
     - detect eligible open positions,
     - estimate net profit,
     - cancel current exits,
     - place skim limit,
     - retry/reprice up to max retries.
   - Ensure idempotency (no duplicate skim orders per symbol).

4. `src/Denis.TradingEngine.App/Program.cs`
   - Log loaded `IbkrEodSkim` values at startup.
   - Validate config ranges and warn on invalid values.

5. `src/Denis.TradingEngine.App/Trading/IBKR_EOD_SKIM_UPGRADE_PLAN.md`
   - This reference file (current document).

## Implementation Steps (V1)

1. Config and model
   - Add `IbkrEodSkim` keys in `appsettings.json`.
   - Add matching typed config class.

2. Eligibility checks in orchestrator
   - IBKR only.
   - Real mode only.
   - Feature flag `Enabled=true`.
   - Position must be in profit and pass `MinNetProfitUsd`.
   - Fresh quote and acceptable spread required (internal safety guards).

3. Order flow
   - Cancel existing exits for symbol via current cancel pipeline.
   - Submit skim SELL limit with `ReasonTag` / correlation tag.
   - Track skim state per symbol to avoid duplicate actions.
   - Retry/reprice while still open and retry count < `MaxRetries`.
   - Keep logic limit-only (no market fallback in V1).

4. Logging and traceability
   - Use clear tags:
     - `eod-skim-start`
     - `eod-skim-eligible`
     - `eod-skim-cancel-exits`
     - `eod-skim-place`
     - `eod-skim-reprice`
     - `eod-skim-skip`
     - `eod-skim-dryrun`
   - Persist `ReasonTag` in signal/order logs for analytics.

5. State handling / idempotency
   - Track per symbol:
     - skim started flag,
     - active skim order id,
     - retry count,
     - last action timestamp.
   - Reset state when:
     - position closes,
     - skim order fills/cancels/rejects,
     - session ends / new trading day,
     - reconnect resync completes.

## Safety Rules

- Never run skim outside configured window.
- Never skim symbols without open position.
- Never submit if quote is stale or spread is too wide.
- Never skim if estimated net profit is below `MinNetProfitUsd`.
- Do not break existing cooldown/day-guard behavior.
- If cancel/place step fails, keep or restore protection logic (no silent loss of exits).
- `DryRun=true` must be side-effect free (no real cancel/place requests).

## Design Principle (Most Important Rule)

EOD skim must behave as a **fail-safe, transaction-like override**:

- If all preconditions and steps succeed -> proceed (`commit` behavior)
- If any critical step fails -> abort skim attempt and keep baseline behavior/protections (`rollback` behavior)

Practical meaning:

- EOD skim is **opportunistic**, not mandatory.
- If anything is uncertain, invalid, or fails, the system should `skip` and continue normal operation.
- Existing protections (TP/SL / current exit setup) must not be silently lost because of skim logic.

### Fail-safe rules for real mode (`DryRun=false`)

- Eligibility/profit/quote check fails -> `skip`, no changes
- Cancel existing exits fails -> abort skim, do not place skim order
- Place skim order fails -> abort skim attempt, do not continue with reprice chain
- Any unexpected exception -> catch, log full context, reset local skim state if needed, continue baseline flow

### Transaction analogy (design intent)

Treat one skim attempt like a small transaction:

1. Validate inputs and safety guards
2. Attempt controlled state transition (cancel old exits, then place skim)
3. If full sequence cannot complete safely -> stop and preserve/restore baseline protections

This is the intended behavior for the future real mode implementation.

## Logging & Audit Journey Contract (Required)

For `DryRun=true` and later `DryRun=false`, log the **full journey** from entry to final decision/outcome.

### Required journey stages to log

1. Procedure entry
   - feature flags (`Enabled`, `DryRun`)
   - current time, market close, minutes-to-close
   - count of open positions / candidate symbols

2. Per-symbol evaluation
   - symbol, quantity, average price
   - external/IBKR marker
   - local skim state (retry count / active skim refs if present)

3. Quote and liquidity snapshot
   - bid / ask / last
   - quote age
   - spread / spread quality
   - skip reason if quote invalid

4. Pricing decision
   - candidate SELL limit price
   - pricing rule used (e.g. bid, bid-1tick on retry)
   - retry/reprice context

5. Profit estimation
   - gross profit
   - estimated fee buffer
   - estimated slippage buffer
   - estimated net profit
   - compare vs `MinNetProfitUsd`

6. Final decision
   - `eligible` or `skip`
   - exact reason (`outside-window`, `stale-quote`, `net-profit-below-min`, etc.)

7. Intended/real actions
   - `DryRun=true`: log what would be canceled/placed/repriced
   - `DryRun=false`: log actual cancel/place calls and returned IDs

8. Order/DB journey (real mode later)
   - correlation id / broker order id
   - broker status updates (`sent`, `canceled`, `filled`, `rejected`, etc.)
   - DB audit/status updates and reasons (`last_msg`)

### Dry-run rule (strict)

- `DryRun=true` is logging/audit only.
- No broker side effects.
- No DB side effects for skim execution attempts.

## DB Write Contract (Live Phase Blueprint)

This section defines how EOD skim should be tracked in the database once `DryRun=false` is implemented.

### Core principle

- Reuse existing tables and status lifecycle where possible (V1)
- Avoid schema changes unless required
- Track EOD skim identity via:
  - `correlation_id` / `broker_orders.id` prefix
  - `broker_orders.last_msg`
  - `ReasonTag` (`EOD-SKIM`)

### DryRun vs Real (DB behavior)

- `DryRun=true`
  - no real DB writes for skim attempts
  - log virtual DB journey only (`would-insert`, `would-mark-sent`, etc.)

- `DryRun=false`
  - real writes happen through existing repositories and order lifecycle flows

## Correlation / IDs Convention (EOD skim)

### Attempt-level correlation id (used as `broker_orders.id`)

Recommended V1 format:

- first attempt: `exit-eod-skim-r0-<guid>`
- first reprice: `exit-eod-skim-r1-<guid>`
- next retry: `exit-eod-skim-r2-<guid>`

Notes:

- one broker submit = one `broker_orders` row
- each reprice (`cancel + new`) creates a new `broker_orders` row
- retries are grouped by prefix (`exit-eod-skim-r*`)

### Broker order id

- `broker_orders.broker_order_id` is populated only after broker accept (`sent`)
- assigned by existing `IOrderService` / `IbkrOrderService` flow

## Table-by-Table Tracking Plan

### 1) `broker_orders` (main order lifecycle table)

This is the primary table for EOD skim tracking.

For a skim SELL order:

- `id` = EOD skim correlation id (e.g. `exit-eod-skim-r0-...`)
- `symbol` = target symbol (e.g. `PEP`)
- `side` = `sell`
- `qty` = position quantity (or remaining quantity if partial logic later)
- `order_type` = `limit`
- `limit_price` = skim limit price
- `exchange` = `SMART` (IBKR equities)
- `status` = existing broker lifecycle statuses

### Status path (reuse existing statuses, no new V1 statuses required)

Typical path:

- `submitted`
- `sent`
- `partially_filled` (optional)
- `filled`

Reprice path:

- `submitted`
- `sent`
- `cancel-requested`
- `canceled`
- then new attempt row (`r1`, `r2`, ...)

Failure path:

- `submitted`
- `place-timeout` or `place-error` or `rejected`

### `last_msg` usage (important for audit)

Use `last_msg` to store skim-specific context for analytics/debugging.

Examples:

- `eod-skim place retry=0 dry=false tag=EOD-SKIM estNet=7.42`
- `eod-skim reprice retry=1 dry=false tag=EOD-SKIM estNet=6.15`
- `eod-skim cancel-before-skim`
- `eod-skim abort cancel-exits-failed`

## 2) `trade_fills` (real fill events)

No direct EOD skim special write path needed.

When the skim order actually fills, existing fill flow should write:

- `correlation_id` = same EOD skim correlation (`exit-eod-skim-rN-...`)
- `broker_order_id` = real broker order id
- `is_exit = true`
- `is_paper = false`
- `symbol`, `side`, `quantity`, `price`, `realized_pnl`, etc.

This gives exact execution trace for bookkeeping.

## 3) `trade_journal` (journal / accounting trace)

No direct EOD skim special write path needed.

On fill, existing journaling should write:

- `correlation_id` = EOD skim correlation id
- `broker_order_id` = real broker order id
- `is_exit = true`
- `strategy` / `reason` context should allow later identification (via correlation prefix and logs)

V1 can rely on correlation prefix + logs; later we can enrich journal reason tagging if needed.

## 4) `swing_positions` (position state table)

EOD skim should not write here directly at order placement time.

Expected behavior:

- no close/update on skim submit
- update/close only when real fill happens (existing position/fill flow)

### Exit reason note (future clarity)

Today, swing exit reason inference may not explicitly classify `exit-eod-skim-*`.

V1 can still work (fallback reason path), but for cleaner analytics later it is recommended to add:

- new `SwingExitReason` value (e.g. `EodSkim`)
- mapping in `SwingHelpers.InferSwingExitReason(...)` for `exit-eod-skim-*`

This is a code-level analytics improvement, not a DB schema blocker.

## 5) `daily_pnl`

No special EOD skim table/columns needed.

Existing flow should handle:

- realized PnL on fill
- fees on commission events

EOD skim simply contributes to daily PnL like any other real exit.

## 6) `trade_signals`

V1 plan: no EOD skim writes to `trade_signals`.

Reason:

- EOD skim is an execution/exit management flow, not a strategy entry signal.
- Keep signal table focused on strategy signal generation.

## What is new for bookkeeping (without schema change)

Required (V1):

- correlation prefix convention: `exit-eod-skim-rN-*`
- consistent `last_msg` format in `broker_orders`
- `ReasonTag = EOD-SKIM`
- dry-run virtual DB journey logs (already implemented)

Optional later (not required for V1):

- `SwingExitReason.EodSkim` for cleaner `swing_positions.exit_reason` analytics
- explicit EOD skim audit table if we want persistent dry-run analytics in DB

## Test Checklist

1. `Enabled=false`
   - Confirm identical behavior to baseline.

2. `Enabled=true`, `DryRun=true` in real environment
   - Eligible position near close logs skim actions.
   - Ineligible position logs skip reason (`not profitable`, `net-profit too low`, `stale quote`, etc.).
   - No orders are actually canceled/placed.

3. `Enabled=true`, `DryRun=false` (real mode)
   - Existing TP/SL gets replaced safely.
   - Skim limit is placed with `ReasonTag`.
   - Retry/reprice occurs as configured.
   - No duplicate exits for same symbol.

4. Failure paths
   - Cancel failure.
   - Place failure.
   - Reconnect during skim window.
   - Ensure no orphaned pending skim state.

## Rollback

Immediate rollback: set

```json
"IbkrEodSkim": {
  "Enabled": false
}
```

Optional safe rollout sequence:

1. `Enabled=true`, `DryRun=true`
2. Observe logs for several sessions
3. Switch to `DryRun=false`

## Current Progress Status (2026-02-22)

This section tracks what is already implemented vs what is still pending.

### Implemented Today (DryRun milestone)

- Documentation plan/runtime flow/design rules updated (this file)
- New isolated folder created for EOD skim code:
  - `src/Denis.TradingEngine.App/Trading/EodSkim/`
- Skeleton classes added:
  - `IbkrEodSkimOptions`
  - `IbkrEodSkimCoordinator`
  - `IbkrEodSkimContext`
  - supporting models/state records
- `Program.cs` now binds and logs `IbkrEodSkim` config at startup
- `TradingOrchestrator` now creates EOD skim coordinator and calls it from heartbeat loop
- `DryRun` evaluation path is wired and active
  - scans local open positions
  - filters external/IBKR candidates
  - reuses quote guard (`TryGetQuote`)
  - computes candidate price (dry-run estimation)
  - computes estimated net profit
  - logs full decision journey
- Full dry-run journey logging added (entry -> quote -> pricing -> profit -> decision -> intended actions)
- Fail-safe guard added in orchestrator:
  - if `Enabled=true` and `DryRun=false` (before live wiring exists), skim is skipped with warning (no side effects)
- `appsettings.json` updated for rollout start:
  - `IbkrEodSkim.Enabled = true`
  - `IbkrEodSkim.DryRun = true`
- Project build passes after dry-run wiring changes

### Current Runtime Status (as of today)

- EOD skim is **enabled in config** but runs in **DryRun mode only**
- No broker `cancel/place` calls are executed by skim
- No skim DB writes are executed (dry-run logs audit intent only)
- Baseline trading behavior remains unchanged

### Still Pending (next phases)

- Real skim execution (`DryRun=false`) cancel/place integration
- Dedicated EOD skim exit submit path (for External/IBKR positions)
- Retry/reprice live state machine (`cancel + new`)
- `OnOrderUpdated` -> skim state cleanup integration
- Session/day boundary cleanup integration for coordinator state
- Optional DB audit standardization (`last_msg` format / correlation conventions)

### Important note

- Current implementation is intentionally **DryRun-first**.
- Live execution is not enabled yet.
- This milestone is focused on observability, full journey tracing, and safe rollout before any real order actions.
