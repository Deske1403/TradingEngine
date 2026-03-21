# Trend Observations (2026-02-24)

## Why this note exists

Current trend filter behavior is technically consistent, but several real trades show a gap between:

- `macro trend = Up` (what the current filter measures)
- `good local entry quality` (what we visually want on chart)

This note captures that distinction and the observed anti-patterns.

## Scope and intent (important)

This document is **not** a quick-fix note.

It is a feature-design note for a missing capability:

- local trend quality classification before entry

Goal:

- design a solid, testable feature
- avoid ad-hoc threshold tweaks as the primary solution (especially for crypto)
- use explicit "must-block" and "must-allow" examples during validation

## Current trend filter (what it actually answers)

The current trend filter answers:

- "Is the broader context up enough?" (macro trend)

It does **not** directly answer:

- "Is this specific entry moment a strong local uptrend / clean push?"

### Current mechanics (high level)

Trend score is computed from:

- endpoint return
- slope return
- drawdown penalty

Direction is then classified using `TrendNeutralThresholdFraction`:

- `Up`
- `Neutral`
- `Down`

`TradingOrchestrator` allows long entries only when trend direction is `Up`.

## Key observation: macro Up can still produce bad local entries

We observed repeated cases where:

- macro trend score says `Up`
- micro filters pass
- chart visually shows a weak local structure (plateau / rollover / poor rebound)
- trade still enters and then stops out

This is not necessarily a bug in score calculation. It is a mismatch between:

- macro trend qualification
- local entry-quality qualification

## IBKR examples (borderline vs cleaner trend)

### `NOW` (accepted, later stopped out)

- Replay trend score (IBKR macro trend): `0.001345`
- Old threshold: `0.001`
- Result: `Up` (accepted)

Why it feels wrong visually:

- endpoint component was enough to push score above threshold
- slope component was weak/negative in that case
- chart looked borderline / flat-to-weak instead of clean uptrend

### `PEP` (accepted, cleaner trend)

- Replay trend score (IBKR macro trend): `0.002906`
- Result: clearly stronger `Up` than `NOW`

This led to a practical IBKR change:

- `TrendNeutralThresholdFraction` increased to `0.0025`

Rationale:

- blocks `NOW`-like borderline cases
- still allows `PEP`-like stronger trend cases

## Crypto examples (important difference)

We also observed a crypto cluster case (Bitfinex, majors entered close together and stopped out):

- `BTCUSDT`
- `ETHUSDT`
- `SOLUSDT`

These looked visually like weak local entries ("debilni uptrendovi"), but replayed macro trend scores were still solid:

- `BTCUSDT` score `0.005423`
- `ETHUSDT` score `0.002628`
- `SOLUSDT` score `0.004618`

Important consequence:

- Raising crypto macro trend threshold alone would **not** reliably remove these cases.
- At least some of them would still pass.

## Main conclusion

The problem we want to solve is not only:

- "Threshold too low"

It is more specifically:

- "Macro trend may be Up, but local entry structure is poor"

## Core feature hypothesis (refined)

The local trend-quality feature should not be based on only one signal (for example `short_slope > 0`).

The stronger hypothesis is:

- **Trend magnitude + chop/noise quality**

In practical terms:

- **big trend + smaller chop = GO**
- **small trend + chop = NO GO**

This matches observed chart behavior better than a simple "is slope positive?" rule.

### What the eye is seeing (and what we should model)

Visual observation can be translated into two separate dimensions:

1. `Trend magnitude`
- how much price actually moved from point `B` (start of window) to point `A` (entry time)

2. `Chop / noise`
- how unstable / broken the path was inside the same window

The decision should depend on the relationship between the two, not either one alone.

Examples:

- small but smooth move -> can still be weak (`NO GO`)
- large but chaotic move -> suspicious / unstable
- large move with controlled chop -> preferred (`GO`)

## What is missing (proposed concept)

We need a second gate for **local trend quality** before entry.

### Proposed idea: local 15m trend quality filter (crypto-first candidate)

Use a short pre-entry window (e.g. `15m`) and require local quality in addition to macro `Up`.

Candidate measurements (concept):

- `net_move_15m` (magnitude): endpoint move from start of window to entry
- `chop_15m` (noise): path instability inside the window
- `drawdown_15m`: adverse movement inside the window
- `efficiency_15m = abs(net_move_15m) / path_length_15m` (optional but strong candidate)

Candidate acceptance rule families:

- `net_move_15m >= X`
- `chop_15m <= Y` (or normalized chop cap)
- `efficiency_15m >= Z`
- and `local direction` must still be positive

Minimal first version (least invasive starting point):

- `macro trend (180m) == Up`
- `net_move_15m > 0`
- `net_move_15m >= X`
- `chop_15m <= Y`

This targets exactly the observed anti-pattern:

- macro still looks up
- local structure is flat/rolling over or too noisy relative to its move

## Alternative / complementary protection (crypto majors)

For repeated correlated losses (`BTC/ETH/SOL` entering close together), a separate risk-control idea was discussed:

- majors cluster/correlation guard (crypto-only)

Example concept:

- limit new entries from `BTC/ETH/SOL` group within a short time window

This does not replace trend-quality logic, but can reduce serial losses during the same local regime failure.

It should be treated as a separate risk feature, not a substitute for local trend quality.

## Current decision (as of 2026-02-24)

- IBKR: threshold tightened to `0.0025` (already applied)
- Crypto: no immediate threshold change yet
- Crypto trend issue is tracked as "macro trend vs local entry quality" problem

## Implementation status (2026-02-24, end of day)

Local trend-quality feature scaffold has been implemented in **shadow mode** (`dry-run`) for both:

- IBKR
- Crypto (Bitfinex)

What is implemented now:

- new `Trading.LocalTrendQuality` config section
- `30m` magnitude metrics
- `15m` chop/stability metrics
- dry-run evaluation (`wouldPass / wouldBlock`)
- telemetry logging only (no trade blocking yet)

Important:

- existing entry logic is unchanged
- this is telemetry/validation phase, not enforcement phase

Current live usage mode:

- `Enabled = true`
- `DryRun = true`

Log marker to track:

- `[TREND-LQ-DRYRUN]`

This gives us live evidence before turning the feature into a hard gate.

Next step (analysis-driven, feature-first):

- test local 15m quality metrics against known bad examples and good XRP/BTC/ETH/SOL winners
- explicitly compare `magnitude`, `chop`, and `magnitude/chop` style ratios
- define acceptance criteria for the feature (`must-block` / `must-allow`)
- then decide whether to add:
  - local quality gate
  - cluster guard
  - or both

## Reference anti-pattern (visual)

User-marked chart examples showed:

- entry on weak local rebound / plateau
- no clean continuation
- rollover and stop-out

Those examples should be used later as explicit "must-block" test cases when evaluating local trend quality rules.

The target outcome is a stable feature that explains decisions clearly (why a local trend was accepted/rejected), not a one-off patch for a single trade.

## Where we paused (for next session)

We intentionally paused after enabling dry-run telemetry.

Tomorrow's first review should answer:

1. Are `[TREND-LQ-DRYRUN]` logs appearing for both IBKR and Crypto?
2. Do the metrics line up with known "bad local entry" examples?
3. Which provisional thresholds are obviously too strict / too loose?

Only after that should we decide on:

- keeping shadow mode longer
- soft enable
- or hard gate with calibrated thresholds
