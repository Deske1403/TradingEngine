## IBKR Pullback Hardening - 2026-03-21

Context:
- Goal: harden IBKR pullback entries before Monday open.
- Main issue from week `2026-03-16` to `2026-03-20`: engine was entering weak midday reclaim/fade setups and often buying while short-term tape was already rolling over.
- `LocalTrendQuality` was intentionally excluded from this change-set.

### What Was Changed

1. Trading window is now DST-safe and tied to New York session logic.
- Replaced legacy fixed UTC start behavior with explicit NY-open offset.
- Current setting:
  - `TradeStartOffsetFromOpenNy = 01:30:14`
  - `TradeEndLocalNy = 16:00:00`
- Meaning:
  - do not trade first `90 min + 14 sec` after `09:30 ET`
  - stop at regular market close

2. Daily risk knobs restored to prior values.
- `PerSymbolBudgetUSD = 1000`
- `MaxTradesTotal = 3`

3. Daily trade slots are no longer treated as pure FIFO quality-blind capacity.
- Added signal-priority gating for later daily slots.
- Current thresholds:
  - `MinSignalPriorityScoreAfterFirstTrade = 56`
  - `MinSignalPriorityScoreForLastTradeSlot = 68`
- Intent:
  - first slot is allowed to be normal-quality
  - second and especially third slot must be cleaner

4. Pullback micro filter now uses live entry-time short momentum.
- Before:
  - `slope5` used for decision context was effectively frozen from pullback start
  - this could allow entry after the reclaim was already losing momentum
- Now:
  - `slope5` and `slope20` are evaluated live at reclaim / entry time

5. Added optional hard gate for `slope5`.
- New config field:
  - `MicroFilterMinSlope5Bps`
- Current IBKR default:
  - `MicroFilterMinSlope5Bps = 0.0`
- Intent:
  - buy entries must have non-negative live short-term momentum

6. Tightened IBKR base and micro liquidity filters.
- IBKR defaults changed:
  - `MinTicksPerWindow: 26 -> 35`
  - `MaxSpreadBps: 24.0 -> 18.0`
- IBKR micro defaults changed:
  - `MicroFilterMinSlope20Bps: -0.5 -> -0.25`
  - `MicroFilterMaxSpreadBps: 22.0 -> 18.0`

7. Tightened midday-specific quality further.
- New IBKR midday micro thresholds:
  - `MicroFilterMiddayMaxSpreadBps = 8.0`
  - `MicroFilterMiddayMinTicksPerWindow = 120`
- Intent:
  - midday entries should only pass when tape is still reasonably liquid and active

### Files Changed

- `src/Denis.TradingEngine.App/appsettings.json`
- `src/Denis.TradingEngine.App/Trading/TradingSettings.cs`
- `src/Denis.TradingEngine.App/Trading/TradingOrchestrator.cs`
- `src/Denis.TradingEngine.Core/Trading/TradeSignal.cs`
- `src/Denis.TradingEngine.Strategy/Filters/TickerProfiler.cs`
- `src/Denis.TradingEngine.Strategy/Pullback/PullbackInUptrendStrategy.cs`
- `src/Denis.TradingEngine.Strategy/Pullback/PullbackSymbolConfig.cs`
- `src/Denis.TradingEngine.Strategy/pullback-config.json`

### Build / Publish Status

Verified locally:
- `Denis.TradingEngine.Core` build passed
- `Denis.TradingEngine.Strategy` build passed
- `Denis.TradingEngine.App` build passed
- `Denis.TradingEngine.App` release publish passed

Publish output:
- `src/Denis.TradingEngine.App/bin/Release/net9.0/win-x64/publish`

Notes:
- Full solution build can show file-lock errors if a live `Denis.TradingEngine.Exchange.Crypto` process is running and holding DLLs.
- App / Strategy / Core changes themselves compiled successfully.

### Monday Watchlist

Watch first live session for:
- whether entries still appear on obvious late-rollover reclaim attempts
- whether signal count drops too much after tighter midday quality gates
- whether second / third daily slots are now reserved for materially cleaner setups

If still too loose:
- raise `MicroFilterMinSlope5Bps` from `0.0` to `0.05` or `0.10`
- tighten midday spread further if needed
- tighten signal-priority thresholds again
