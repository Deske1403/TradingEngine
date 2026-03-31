# Bitfinex Funding Engine Blueprint

## Purpose

This document defines the target direction for the Bitfinex funding engine.

The goal is not to blindly chase the highest rate.
The goal is to optimize for:

- rate
- execution probability
- capital utilization over time
- stable and auditable behavior

Core belief:

- idle capital is the biggest enemy
- flow beats perfection
- consistency beats peaks

## Current Proven Baseline

What is already proven in live runtime:

- funding is fully encapsulated as a separate feature
- `Enabled` and `DryRun` gates work
- live funding offers can be submitted
- offers can move to `Provided`
- returned capital is visible in funding wallet state
- funding writes its basic operational state to dedicated funding tables
- reserve logic can intentionally block auto-reinvestment

What is now also implemented in code and schema, pending next runtime verification:

- REST lifecycle sync for `credits`, `loans`, `funding trades`, and `ledger` history
- dedicated persistence into:
  - `funding_credits`
  - `funding_loans`
  - `funding_trades`
  - `funding_interest_ledger`
  - `funding_interest_allocations`
  - `funding_capital_events`
  - `funding_reconciliation_log`
- joined reporting view:
  - `v_funding_book`
- wallet delta classification metadata for funding wallet snapshots
- unique `ledger_id`-based funding payment deduplication
- normalized principal/interest business events layered on top of raw exchange truth
- layered allocation scaffold in shadow mode:
  - `Motor`
  - `Opportunistic`
  - regime classification `LOW / NORMAL / HOT`
  - `funding_shadow_plan` telemetry snapshots
  - dedicated shadow persistence table:
    - `funding_shadow_plans`
  - comparison/reporting view:
    - `v_funding_shadow_vs_actual`
  - shadow next-step policy layer:
    - `funding_shadow_actions`
    - `v_funding_shadow_action_vs_actual`
  - stateful shadow session layer:
    - `funding_shadow_action_sessions`
    - `v_funding_shadow_session_vs_actual`

This means the basic funding runtime is already real and working.

## Target Philosophy

The final engine should behave like a layered yield system:

- `Motor` = baseline utilization
- `Higher layers` = yield enhancement
- `Sniper` = optional edge, never the foundation

The engine must prefer:

- staying active
- falling back safely
- surviving wrong assumptions

It must not prefer:

- waiting forever for a perfect rate
- long lockups at weak rates
- cleverness without measurable edge

## Capital Journey We Must Fully Track

The complete funding journey is:

1. Capital becomes available in the funding wallet.
2. Engine decides allocation, bucket, rate, duration, and wait tolerance.
3. Offer is posted.
4. Offer becomes active / updated / canceled / replaced.
5. Offer is executed and moves into real funding state.
6. Funding lives through credit / loan / trade lifecycle.
7. Interest is accrued and later realized.
8. Principal returns to the funding wallet.
9. Engine decides whether to re-offer, reroute, or hold.

Until this whole chain is persisted and linked, the engine is not considered complete.

## Source-of-Truth Layers

The final system should have four clear layers:

### 1. Raw Exchange Events

Store what Bitfinex said, without interpretation.

Examples:

- wallet snapshot / update
- offer snapshot / new / update / close
- credit events
- loan events
- funding trade events
- notifications relevant to funding lifecycle

### 2. Normalized Runtime State

Store the latest understood state of the engine and exchange objects.

Examples:

- current open offers
- current active credits
- current active loans
- current wallet state
- current market snapshot

### 3. Accounting Ledger

Store the financial truth of what happened.

Examples:

- principal deployed
- principal returned
- gross interest
- fees
- net interest
- lifecycle timestamps and linked identifiers

### 4. Analytics and Allocation

Store derived metrics used for decision making and reporting.

Examples:

- utilization
- average execution time
- idle capital
- realized yield
- bucket performance
- regime performance

## Target Allocation Model

The long-term design is a layered engine:

### Motor

- purpose: keep capital constantly working
- priority: highest
- target behavior: low/normal rate, fast execution
- default duration: 2 days
- role: baseline performance and fail-safe

### Opportunistic

- purpose: capture above-average rate without sacrificing too much fill probability
- behavior: moderate waiting
- duration: usually 2-3 days

### Aggressive

- purpose: improve yield when market is clearly strong
- behavior: higher target rate with stricter wait budget
- duration: usually 3-4 days

### Sniper

- purpose: catch rare spikes
- behavior: small allocation, strict max wait, strict disable rules
- duration: usually 4-5 days
- rule: optional edge only, never core capital

## Important Implementation Decision

The target model has four layers, but the first intelligent version should not start with all four.

Recommended rollout:

- `Phase 1`: complete the accounting journey
- `Phase 2`: implement `Motor + Opportunistic`
- `Phase 3`: add regime-aware `Aggressive`
- `Phase 4`: add `Sniper` only if data proves it helps

This keeps the engine measurable and prevents fake sophistication.

## Duration Logic

Duration should not be based on rate alone.

Bad rule:

- high rate automatically means long duration

Better rule:

- rate influences duration
- regime influences duration
- execution probability influences duration
- lock risk influences duration

Safe starting rule:

- weak / uncertain conditions -> 2 days
- stronger conditions -> 3 days
- only proven strong edge -> 4-5 days

Never lock long duration on weak rate.

## FRR Usage

FRR should be used as:

- benchmark
- fallback
- sanity check

FRR should not become the whole strategy by itself.

Healthy use:

- compare bucket target against FRR
- use FRR as fallback if waiting too long
- use FRR to classify regime quality

## Max-Wait Rule

No offer should wait indefinitely.

Each bucket must have:

- target rate
- max wait
- fallback behavior

If wait budget is exceeded, the engine must do one of these:

- lower rate
- move capital down a bucket
- fall back to Motor / FRR-like behavior
- cancel and reprice

This rule is mandatory because utilization matters more than theoretical peak pricing.

## Market Regimes

The engine should eventually classify the market into simple regimes:

- `LOW`
- `NORMAL`
- `HOT`

Regime should influence:

- bucket allocation
- rate aggressiveness
- max wait
- duration

Safe interpretation:

- `LOW` -> mostly Motor
- `NORMAL` -> Motor plus some Opportunistic
- `HOT` -> more Opportunistic / Aggressive

## Safety Rules

These rules are non-negotiable:

1. Funding must remain fully encapsulated from spot trading.
2. `Enabled=false` must behave like funding does not exist.
3. `DryRun=true` must run decisions without live writes.
4. Funding failures must never crash the spot engine.
5. The engine must fail closed, not invent state.
6. Reserve logic must always be respected.
7. No bucket may absorb all capital.
8. Sniper logic must never control baseline capital.

## Metrics That Matter

Primary metrics:

- capital utilization percent
- idle capital percent
- average execution time
- real realized yield
- net interest over time
- time from return to reallocation
- bucket-level performance

Metrics that do not matter by themselves:

- highest seen rate
- prettiest ask quote
- isolated one-off spike

## What Must Exist Before We Claim "Full Journey"

The structural pieces now exist in code.

What still needs to be proven in runtime:

- first clean restart with the new lifecycle slice enabled
- first full post-return sync proving credits / trades / interest rows land correctly
- validation that ledger entries and wallet-return behavior line up over a real cycle
- validation of link quality when multiple same-currency funding chunks overlap
- validation that `funding_interest_allocations`, `funding_capital_events`, and `v_funding_book` stay coherent over consecutive real returns

Until that is confirmed, we have a very strong funding book implementation, but not yet a fully verified funding book.

## Steps To Follow

### Step 1. Finish the Book

Implement and persist:

- credits
- loans
- trades
- interest ledger
- return-of-principal events

Goal:

- exact accounting of funding lifecycle

Status:

- implemented in code and DB schema
- raw lifecycle truth now persists into:
  - `funding_credits`
  - `funding_loans`
  - `funding_trades`
  - `funding_interest_ledger`
  - `funding_interest_allocations`
  - `funding_capital_events`
- next task is live verification after restart and real return cycles

### Step 2. Build the Funding Book View

Create linked visibility for:

- offer posted
- offer executed
- capital deployed
- interest earned
- capital returned
- final net outcome

Goal:

- one clear story for every funding chunk

Status:

- implemented in schema as `v_funding_book`
- next task is runtime validation against real post-return cycles

## Current Validation Window

The remaining live returns scheduled for Sunday, March 22, 2026 are ideal for validating the new accounting layer without changing engine behavior:

- `USDt` chunk expected around `2026-03-22 21:30:32`
- `USD` chunk expected around `2026-03-22 22:10:32`

These returns should be used to confirm:

- `principal_returned` events
- `interest_paid` allocations
- correct updates in `v_funding_book`
- continued reserve-based blocking of auto-reinvestment

### Step 3. Keep Reinvestment Simple

Before intelligent allocation, keep reinvestment conservative:

- reserve-based safety
- 2-day default duration
- clean re-entry only after confirmed wallet return

Goal:

- stable baseline behavior

### Step 4. Introduce Motor

Formalize baseline allocation rules:

- minimum capital always assigned to Motor
- fast execution preference
- shallow wait tolerance

Goal:

- capital keeps flowing

Status:

- first implementation is now added in `shadow mode`
- live placement is no longer limited to a hardcoded raw book-ask rule
- live now supports bounded rate-selection modes:
  - `BookAsk`
  - `SmartRegime`
  - `ShadowMotor`
  - `ShadowOpportunistic`
- intended safe live mode is currently `SmartRegime`:
  - anchor from book ask/bid
  - optional `FRR` floor
  - `LOW / NORMAL / HOT` classification
  - bounded premium via regime multipliers
  - clamp to configured rate band
- live now also supports symbol-specific runtime profiles so `fUSD` and `fUST` can diverge safely on:
  - `Enabled`
  - `PauseNewOffers`
  - `MinOfferAmount`
  - `MaxOfferAmount`
  - `ReserveAmount`
- symbol-specific profiles now also support advanced smart/shadow tuning on:
  - `MinDailyRate`
  - `MaxDailyRate`
  - `LiveRateMode`
  - `ManagedOfferTargetMode`
  - `LiveUseFrrAsFloor`
  - `LiveLowRegimeRateMultiplier`
  - `LiveNormalRegimeRateMultiplier`
  - `LiveHotRegimeRateMultiplier`
- `MotorAllocationFraction`
- `OpportunisticAllocationFraction`
- `SniperAllocationFraction`
- `MotorRateMultiplier`
- `OpportunisticRateMultiplier`
- `SniperRateMultiplier`
- `MotorMaxWaitMinutesLow/Normal/HotRegime`
- `OpportunisticMaxWaitMinutesLow/Normal/HotRegime`
- `SniperMaxWaitMinutesLow/Normal/HotRegime`
- startup now logs the effective runtime profile per symbol so live behavior is auditable before the first funding cycle
- managed-offer ownership now survives restart through persisted `managed_by_engine` state plus submit-history recovery
- this closes the old gap where the same live offer became `external` after process restart
- live now also supports a promotion gate for managed offers:
  - new placement can stay on `SmartRegime`
  - managed active offers can separately target `Live`, `ShadowMotor`, or `ShadowOpportunistic`
  - replace now runs as a same-cycle `replace_offer` flow instead of a bare cancel-only step
- this gives us a practical intermediate promotion path:
  - stable live placement remains conservative
  - managed repricing can start following `Motor`
  - full shadow wait/fallback policy can stay shadow-only until later
- practical promotion sequence is now explicit:
  - first promote managed repricing with `ManagedOfferTargetMode = ShadowMotor`
  - then, when ready, promote fresh live entries too with `LiveRateMode = ShadowMotor`
  - that avoids the split where a replaced offer follows `Motor`, but the next post-fill fresh placement jumps back to `SmartRegime`
- the engine now computes and logs a `Motor / Opportunistic` funding plan per symbol for observation only
- shadow plans now persist into `funding_shadow_plans`
- latest shadow intent can now be compared against realized book outcomes through `v_funding_shadow_vs_actual`
- the next shadow slice now also computes per-bucket next-step actions:
  - `would_place_now`
  - `would_wait_for_better_rate`
  - `would_reprice_active_offer`
  - `would_keep_active_offer`
  - `would_wait_then_fallback`
- those actions persist into `funding_shadow_actions`
- latest shadow policy can now be compared against live actions and book outcomes through `v_funding_shadow_action_vs_actual`
- the shadow layer now also groups consecutive action-policy observations into stateful sessions
- those sessions persist into `funding_shadow_action_sessions`
- latest session state can now be compared against live actions and book outcomes through `v_funding_shadow_session_vs_actual`
- live now also supports a first bounded placement-policy gate for fresh entries:
  - `Immediate`
  - `MotorWaitFallback`
  - `OpportunisticWaitFallback`
- `MotorWaitFallback` is a narrow live promotion of the blueprint idea:
  - if there is no active offer and regime is not `HOT`, the engine can wait for a bounded `Motor` window instead of placing immediately
  - after the wait budget expires, it falls back to the `Motor` rate derived from the current market anchor
  - active-offer repricing still follows the separate managed-offer promotion path
- this gives us the first real live `wait -> fallback` behavior without promoting the whole shadow action system into live mutation logic
- `OpportunisticWaitFallback` is the next narrow fresh-entry promotion:
  - target rate comes from the `Opportunistic` multiplier
  - wait budget comes from the `Opportunistic` wait profile
  - if the wait expires, runtime falls back to the `Motor` rate
  - `HOT` still places immediately
  - `LOW` collapses into the `NORMAL` opportunistic wait profile to stay conservative
- live runtime now also supports bounded parallel capacity through `MaxActiveOffersPerSymbol`:
  - `1` keeps the old single-offer behavior
  - `2+` allows another managed offer while an older managed offer is still active
  - external offers still block mutation
  - if capacity is full, runtime emits `skip_active_offer_capacity_reached`
  - extra parallel slots reuse the current live policy:
    - immediate target if the policy already wants to place now
    - fallback request if the policy would otherwise open another wait state
- this is the first real live capital-deployment step beyond the one-offer baseline:
  - returned capital no longer has to sit idle just because one managed offer is still waiting
  - scaling stays bounded and explicit instead of flooding the order book
- shadow analysis is now also multi-offer aware:
  - it no longer treats `2/2` managed slots as a generic ambiguous state
  - it can now show:
    - open parallel slot ready
    - open parallel slot waiting/fallback
    - capacity-full keep state
- live runtime now also supports the first deterministic `Motor + Opportunistic` split:
  - live slot budget is recalculated from lendable balance, `MinOfferAmount`, active offers, and `MaxActiveOffersPerSymbol`
  - if only one live slot exists, it always belongs to `Motor`
  - if two live slots exist and `Opportunistic` is enabled:
    - slot `1/2` is `Motor`
    - slot `2/2` is `Opportunistic`
  - `Sniper` still does not consume live capital in this slice
  - live reasons now emit:
    - `slotRole=Motor|Opportunistic`
    - `slotIndex=N/M`
    - `liveSplit=Motor:X/Opportunistic:Y`
  - shadow summaries mirror the same live split shape so `live vs shadow` comparison stays readable
  - if live capacity grows above `2`, slot counts no longer stay hardcoded:
    - first slot still stays `Motor`
    - second slot still prefers `Opportunistic`
    - any additional slots are distributed by `MotorAllocationFraction` / `OpportunisticAllocationFraction`
- the shadow layer now also supports a third bucket:
  - `Sniper`
  - small allocation fraction
  - highest target-rate multiplier
  - long bounded wait window
  - fallback into `Opportunistic`, then indirectly into `Motor`
- `Sniper` remains shadow-only for now:
  - it does not mutate live funding offers
  - it exists so we can observe spike-capture intent before allowing it to touch capital
- once `Sniper` is promoted into live in a bounded way, the next useful promotion is adaptive ceiling control:
  - `Sniper` should not stay hard-capped at the same fixed maximum as the baseline buckets
  - if the visible funding market offers more, `Sniper` should be allowed to float its own cap upward inside an explicit safety ceiling
  - fallback still remains conservative and collapses back into `Opportunistic` / `Motor`
- managed active offers now also support `KeepThenMotorFallback`:
  - keep the current live offer while it is still young
  - once the replace-age window passes, allow a controlled repricing down toward the `Motor` fallback target
  - this adds the first live `keep -> wait -> lower -> replace` path for stale managed offers
- immediate post-fill fresh re-entry now also keeps a short carry-forward memory of that fallback target so a just-lowered managed offer does not instantly bounce back to the old `HOT` placement ceiling
- next task is to validate both fresh-entry and managed-offer fallback behavior over real return cycles before allowing more aggressive live promotion
- with this slice in place, the next useful real-cycle check is to catch:
  - `OpportunisticWaitFallback -> wait_for_target_rate`
  - or `OpportunisticWaitFallback -> place_after_wait_fallback`
- managed active offers are now also evaluated as explicit live slot roles once capacity is full:
  - full symbols no longer stop at a generic `capacity_reached` summary
  - active offers are classified into effective `Motor` vs `Opportunistic` roles
  - managed keep / wait / replace now runs against that role-aware target
  - multi-offer replace is targeted to a concrete active `offerId`, so one slot can be repriced without assuming the symbol only has a single active offer

### Step 5. Add Opportunistic Layer

Introduce a second bucket with:

- moderately better target rate
- bounded waiting
- automatic fallback to Motor

Goal:

- improve yield without sacrificing utilization too much

Status:

- first shadow allocation scaffold now exists alongside `Motor`
- current implementation is measurement-first and not yet allowed to mutate live offers
- the shadow layer now has both:
  - telemetry snapshots
  - dedicated funding-table storage and joined reporting against actual realized outcomes
- the shadow layer now also emits explicit action policy, so we can measure the next safe promotion step before lowering reserve and re-enabling live re-entry
- the shadow layer now also keeps short-lived stateful sessions so `wait -> fallback -> placed/closed` can be observed as a coherent mini-journey instead of isolated rows
- `Sniper` is now added as the third shadow-only bucket so `Motor / Opportunistic / Sniper` can be compared in the same runtime telemetry
- live runtime now also promotes the first deterministic split:
  - baseline slot stays `Motor`
  - second live slot can become `Opportunistic`
  - `Sniper` remains shadow-only

### Step 6. Measure Before Expanding

Only after enough real data:

- compare Motor vs Opportunistic
- analyze execution speed
- analyze realized yield
- analyze idle time

Goal:

- prove edge before adding complexity

### Step 7. Add Regime Logic

Introduce low / normal / hot classification.

Goal:

- smarter capital distribution

### Step 8. Add Aggressive Layer

Only after measurement proves the engine can support it.

Goal:

- controlled yield enhancement

### Step 9. Add Sniper Layer Last

Only after the whole system is measurable and trustworthy.

Goal:

- optional spike capture with small capital

Status:

- first `Sniper` implementation now exists in `shadow mode` only
- it is intentionally not promoted into live mutation yet
- next promotion rule stays the same:
  - only after repeated live cycles show that the measured `Sniper` edge is real
- first live promotion gate now also exists:
  - disabled by default
  - when enabled, `Sniper` can consume a small live slot only after `Motor` and `Opportunistic` are already available
  - current live planner keeps the promotion intentionally narrow:
    - at most one live `Sniper` slot
    - fallback path is `Sniper -> Opportunistic -> Motor` when the symbol profile supports it
  - this keeps `Sniper` as a controlled test feature rather than a full live default

## What We Should Avoid

- starting with all four layers at once
- dynamic reallocation without ledger truth
- long-duration offers on weak conviction
- overfitting to short-term spikes
- treating wallet movement alone as profit truth
- building Discord profit notifications before interest ledger exists

## Final Rule

The funding engine should become:

- observable
- auditable
- conservative by default
- adaptive only when data justifies it

The order is:

- book first
- engine second
- optimization third

That is the footprint we should follow.
