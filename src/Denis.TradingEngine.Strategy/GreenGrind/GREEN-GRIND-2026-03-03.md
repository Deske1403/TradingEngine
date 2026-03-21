# GreenGrind Change Log - 2026-03-03

## Goal
- Align crypto hard gate with strict GreenGrind logic used by state machine.

## Implemented
- Expanded DB snapshot logic in `MarketTickRepository.GetGreenGrindLatestSnapshotAsync(...)`:
  - Added strict metrics: `eff3h`, `pullback3h`, `spike3h`, `ctx_pct`.
  - Added strict gate checks: `breakdown`, `spike`, `context`, `flow`, activation thresholds.
  - Added detailed inactive reasons: `breakdown`, `spike`, `context`, `flow-fade`, `activation-*`.
  - Added safe fallback snapshot (`no-data`) instead of returning `null` when there is no row.
- Updated `CryptoTradingOrchestrator.TryGetGreenGrindDbSnapshot(...)`:
  - Now passes Watch + Activation thresholds and full strict config (pullback/spike/context/flow).
- Improved GG decision logs in `CryptoTradingOrchestrator`:
  - Added `eff3h`, `spike3h`, `ctx_pct` in suppress/block/dry-run logs.

## Expected Result
- Hard gate should reject choppy/weak contexts that previously passed with old simplified DB conditions.
- DB gate behavior should now match strict GreenGrind state logic much closer.
