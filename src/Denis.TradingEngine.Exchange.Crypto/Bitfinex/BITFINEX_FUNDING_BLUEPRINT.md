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
  - `funding_reconciliation_log`
- wallet delta classification metadata for funding wallet snapshots
- unique `ledger_id`-based funding payment deduplication

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

### Step 5. Add Opportunistic Layer

Introduce a second bucket with:

- moderately better target rate
- bounded waiting
- automatic fallback to Motor

Goal:

- improve yield without sacrificing utilization too much

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
