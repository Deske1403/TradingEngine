# Green Grind Revision Plan - 2026-03-08

Status: `proposal only - no code changes yet`
Scope: `Bitfinex crypto GreenGrind redesign review`

---

## Why this doc exists

After reviewing:
- existing GreenGrind docs,
- raw DB evidence from `market_ticks` / `crypto_trades`,
- chart examples marked by the user,

it became clear that the current `v1.2` logic captures part of the intended idea, but formalizes it too narrowly.

The main issue is not that `GreenGrind` is useless.
The main issue is that the current implementation is too strict in the wrong place, so it can miss good early-to-mid continuation opportunities.

---

## Current understanding

The user's visual model is not simply:

`trade only when price is at a fresh 12h high`

It is closer to:

`trade when market transitions from dead/choppy regime into an organized continuation regime`

This regime often has these properties:
- reclaim after weakness or flush,
- higher lows and higher highs,
- steady continuation over multiple bars,
- limited pullback damage,
- no single spike carrying the whole move,
- entry opportunity appears before the very late extension phase.

This means:
- `new high` is often a strong confirmation,
- but `new high` should not be mandatory for every valid GreenGrind entry.

---

## What seems wrong in v1.2

### 1. Active regime is too dependent on context-high logic

Current design leans toward:
- if price is not near context high, it is likely a bounce,
- therefore do not trade.

This protects against bad recoveries, but it also blocks healthy reclaim continuations that are still early.

### 2. Duration is conceptually important, but not enforced strongly enough

The docs describe GreenGrind as a multi-hour regime.
In practice, current logic can fragment one visual grind into several short active windows.

This creates two problems:
- late recognition,
- noisy state changes.

### 3. The model lacks a clear distinction between:
- bad bounce,
- healthy reclaim continuation,
- late/extended grind.

The charts suggest these are not the same thing and should not be treated the same way.

---

## Revised conceptual model

### Red zone

Do not trade.

Typical characteristics:
- chop,
- weak reclaims,
- no continuation,
- failed breakouts,
- high stop-loss probability.

### Green zone

Primary trade zone.

Typical characteristics:
- organized reclaim,
- steady continuation,
- repeated higher lows / higher highs,
- acceptable pullbacks,
- decent directional efficiency,
- still early or mid-stage, not fully stretched.

### Blue zone

Late zone.

Usually not the best place to start a new trade.

Typical characteristics:
- already extended,
- close to resistance / prior high / local exhaustion,
- upside continuation still possible,
- but reward-to-risk is worse than in the green zone.

---

## Proposed GreenGrind regime definitions

### WATCH

Meaning:
- market is improving,
- but structure is not yet strong enough.

Expected behavior:
- monitor only,
- no hard trade permission.

### ACTIVE

Meaning:
- healthy continuation regime is present,
- reclaim grind is organized enough to trade.

Requirements should emphasize:
- continuity,
- multi-bar structure,
- positive net move,
- acceptable efficiency,
- controlled pullback,
- anti-spike protection.

Important:
- `near/new context high` should not be a hard requirement for `ACTIVE`.

### STRONG

Meaning:
- continuation regime is not only healthy, but also dominant.

Requirements should emphasize:
- stronger net move,
- stronger efficiency,
- stronger up ratio,
- stronger context confirmation.

Important:
- `near/new context high` belongs here much more naturally.

### LATE / EXTENDED

Proposal:
- keep this as diagnostic state or metadata first,
- not necessarily a full runtime state on day one.

Meaning:
- trend may still be up,
- but new entries are lower quality because move is already stretched.

This is useful for:
- avoiding chasing,
- later entry suppression,
- explaining why a setup was visually "good trend" but not good timing.

---

## Proposed design direction

### Direction A: Separate context usage by regime strength

Use context differently:
- `ACTIVE`: context is informative or lightly permissive,
- `STRONG`: context is strict confirmation.

This would preserve:
- bounce protection,
- without killing early continuation.

### Direction B: Make continuity a first-class condition

GreenGrind should behave more like a regime, less like a single rolling snapshot.

Planned focus:
- stronger span enforcement,
- better handling of short interruptions,
- less fragmentation of one visual grind into multiple tiny activations.

### Direction C: Keep anti-fake protections

These still look useful and should remain:
- pullback control,
- spike concentration,
- efficiency,
- optional flow confirmation.

The problem is not that these filters exist.
The problem is mostly where strict context is applied.

---

## Proposed change plan

### Phase 1 - Definition lock

Before code changes:
- confirm revised meaning of `WATCH`, `ACTIVE`, `STRONG`,
- confirm whether `LATE/EXTENDED` is only diagnostic or real state,
- confirm that early reclaim continuation must be tradable under `ACTIVE`.

### Phase 2 - Logic changes in strategy runtime

Planned code areas:
- `SymbolGreenGrindRegimeService.cs`
- `GreenGrindSettings.cs`

Planned changes:
- enforce real duration / continuity more explicitly,
- relax hard context requirement for `ACTIVE`,
- keep stronger context requirement for `STRONG`,
- improve state transitions so one visual grind is not split too aggressively.

### Phase 3 - DB-canonical parity update

Planned code area:
- `MarketTickRepository.cs`

Planned changes:
- align SQL gate with revised runtime logic,
- keep same metrics shape,
- expose enough reason codes to debug misses,
- avoid mismatch between runtime state and DB gate.

### Phase 4 - Orchestrator behavior

Planned code area:
- `CryptoTradingOrchestrator.cs`

Planned changes:
- keep DB-canonical gate as source of truth,
- improve logs so missed opportunities are easier to diagnose,
- optionally log when setup is blocked because regime is `late/extended` rather than `inactive`.

### Phase 5 - Rollout mode

Recommended rollout:
- first `DryRun` or detailed logging review,
- compare blocked vs passed setups,
- then re-enable hard gate once behavior matches visual intent better.

---

## Concrete implementation ideas to validate before coding

These are candidate ideas, not final decisions:

1. `ACTIVE` should not require `MinContextHighPct = 0.998`.
2. `STRONG` may keep strict context-high confirmation.
3. Current duration enforcement should be tightened in both runtime and DB SQL.
4. Short breaks inside one organized grind should not immediately destroy regime identity.
5. A late-extension marker may be more useful than treating all non-new-high situations as invalid.

---

## Open decisions for review

Before implementation, confirm:

1. Should `ACTIVE` allow reclaim continuation even if price is below strict `99.8%` of context high?
2. Should `STRONG` remain the only state that requires `near/new high` behavior?
3. Do we want a true `LATE` state, or only a `late-entry` diagnostic flag?
4. Should continuity be defined by:
   - minimum span,
   - minimum number of valid buckets,
   - maximum tolerated local interruption,
   - all of the above?

---

## Working conclusion

The current docs were directionally correct about fake bounce protection.
However, the charts and DB review suggest the next version should shift from:

`GreenGrind = mostly new-high grind`

toward:

`GreenGrind = organized continuation regime`

with `STRONG` reserved for the cleaner near/new-high variant.

No code should be changed until this revised definition is explicitly approved.
