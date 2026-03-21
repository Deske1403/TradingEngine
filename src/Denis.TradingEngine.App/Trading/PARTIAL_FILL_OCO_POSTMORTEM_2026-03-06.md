# Partial Fill OCO Postmortem (2026-03-06)

## Incident Summary

On 2026-03-05 (EOG), one BUY order was submitted as `qty=7`, but IBKR filled it in two slices:

- fill #1: `deltaQty=3`
- fill #2: `deltaQty=4`

Discord and DB correctly showed two BUY fill rows (`3` and `4`), but they belong to the same parent order:

- `broker_order_id=1081`
- `correlation_id=sig-EOG-ad30b5078cd648219dcd7279a7836bb0`

## What Was Wrong Before

Previous OCO scale-in logic in `TradingOrchestrator.ApplyFillCore`:

1. For first fill (`prevQty<=0`), create OCO for current `newQty` (here: `3`).
2. For next fill (`newQty>prevQty`), if any pending exit orders exist, skip OCO creation.

That means second slice (`+4`) stayed without its own exits, because pending exits for the first slice already existed.

Observed log line:

- `[OCO-SCALE-IN] Skipping OCO creation - found 3 pending exit orders for EOG`

Result:

- TP/SL covered only 3 shares.
- After TP sold 3 shares, position tail (4 shares) remained open.

## Fix Implemented

File changed:

- `src/Denis.TradingEngine.App/Trading/TradingOrchestrator.cs`

Behavior now:

1. On new position (`prevQty<=0`), OCO covers full `newQty`.
2. On scale-in (`newQty>prevQty`), orchestrator computes:
   - `protectedQty` from existing pending exit orders (grouped by OCO group to avoid TP/SL double counting),
   - `missingQty = newQty - protectedQty`.
3. If `missingQty>0`, create delta OCO only for missing quantity.
4. If already protected, skip with explicit log.
5. `broker_orders` inserts for TP/SL/SL-ORTH now use `ocoQty` (delta-safe), not always full `newQty`.

## Expected Outcome After Fix

For a partial fill `3 + 4`:

1. First slice creates exits for `3`.
2. Second slice creates additional exits for `4` (delta OCO).
3. Total protected quantity = full position (`7`).

## Verification Checklist

In logs after deploy, for partial-fill symbol:

1. One parent BUY order id.
2. Two `IB-FILL` buy slices (`deltaQty`).
3. New log:
   - `[OCO-SCALE-IN] Creating delta OCO ... protectedQty=... missingQty=...`
4. `broker_orders` has exit rows whose quantities sum to full open position.
5. No leftover unprotected tail in `swing_positions` after partial TP/SL activity.

