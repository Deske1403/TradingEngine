# IBKR Filter Tuning Reference

Date: 2026-02-21

Context:
- Universe expanded to 100 IBKR symbols.
- Trend gate already strict (Up-only, Neutral/Unknown blocked).
- Goal: stronger signal quality by tightening non-trend filters.

Scope:
- File: `src/Denis.TradingEngine.Strategy/pullback-config.json`
- Section: `Exchanges.IBKR.Defaults`

## Changes (Before -> After)

| Parameter | Before | After |
|---|---:|---:|
| `MinTicksPerWindow` | 20 | 26 |
| `MaxSpreadBps` | 30.0 | 24.0 |
| `MinPullbackDepthPct` | 0.00010 (implicit default) | 0.00013 |
| `BreakoutBufferPct` | 0.00035 | 0.00040 |
| `MicroFilterMinSlope20Bps` | -0.8 | -0.5 |

## Why

- Fewer low-quality entries when universe is large.
- Better liquidity quality (`spread`, `ticks`).
- Cleaner breakout confirmation (`depth`, `buffer`).
- Stronger micro-momentum requirement (`slope`).

## Rollback (if signal count drops too much)

Revert these values:
- `MinTicksPerWindow`: `26 -> 20`
- `MaxSpreadBps`: `24.0 -> 30.0`
- `MinPullbackDepthPct`: `0.00013 -> 0.00010`
- `BreakoutBufferPct`: `0.00040 -> 0.00035`
- `MicroFilterMinSlope20Bps`: `-0.5 -> -0.8`

