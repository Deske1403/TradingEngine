# Green Grind / Micro-Filter Tuning (2026-03-04)

## Context
- Morning session showed `GREEN_GRIND Active/Strong` on:
  - `XRPUSDT`, `LINKUSDT`, `SOLUSDT`, `ETHUSDT`, `ADAUSDT`, `SUIUSDT`, `LTCUSDT`, `XMRUSDT`
- At the same time, several pullback entries were blocked by `micro_filter_rejected`.
- Analysis of `signal_slayer_decisions` + market data indicated strict micro thresholds were cutting valid continuation setups for part of this basket.

## Config changes made
File updated:
- `src/Denis.TradingEngine.Strategy/pullback-config.json`
- runtime copy: `src/Denis.TradingEngine.Exchange.Crypto/bin/Debug/net9.0/pullback-config.json`

Per symbol changes:
- `ETHUSDT`
  - `MicroFilterMaxSpreadBps: 4.2 -> 4.6`
- `SOLUSDT`
  - Added explicit micro overrides:
  - `MicroFilterMinSlope20Bps: -0.25`
  - `MicroFilterMaxSpreadBps: 6.0`
  - `MicroFilterMinTicksPerWindow: 6`
- `XRPUSDT`
  - Added explicit micro overrides:
  - `MicroFilterMinSlope20Bps: -0.25`
  - `MicroFilterMaxSpreadBps: 6.0`
  - `MicroFilterMinTicksPerWindow: 6`
- `XMRUSDT`
  - `MicroFilterMaxSpreadBps: 12.0 -> 14.0`
- `LTCUSDT`
  - `MicroFilterMaxSpreadBps: 10.0 -> 11.0`
- `ADAUSDT`
  - `MicroFilterMinTicksPerWindow: 4 -> 3`
- `LINKUSDT`
  - `MicroFilterMaxSpreadBps: 12.0 -> 14.0`
- `SUIUSDT`
  - no numeric change in this pass (kept current micro profile)

## Notes
- This is a controlled relaxation, not disabling micro-filter.
- Range-slope gate (`slope20bps < -0.07` in `LOW/NORMAL`) is still active in code.
- Next review should compare:
  - accepted/rejected ratio by symbol,
  - stop-loss rate for newly admitted setups,
  - whether spread relaxations on `LINK/XMR/LTC` increase low-quality fills.
