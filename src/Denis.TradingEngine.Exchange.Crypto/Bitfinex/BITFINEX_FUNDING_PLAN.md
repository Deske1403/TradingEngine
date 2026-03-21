# Bitfinex Funding / Lending Plan

Date: 2026-03-20

## Goal

Add Bitfinex peer-to-peer funding/lending into the existing crypto engine without mixing it into the current spot order flow.

The existing Bitfinex integration is already well structured:

- `BitfinexTradingApi` handles authenticated REST
- `BitfinexPrivateWebSocketFeed` already authenticates private WS
- `CryptoExchangeSettings` gives us a clean place for exchange-specific config

Because of that, funding should be implemented as a parallel module, not as a hack inside `ICryptoTradingApi`.

## Agreed feature rules

These rules are mandatory for this repo.

### 1. `Enabled` must hard-gate the entire feature

- `Enabled = false` means the system behaves exactly as it does today
- no funding startup
- no funding background loop
- no funding REST calls
- no funding WS subscriptions
- no funding DB writes
- no funding side effects

In other words: when disabled, runtime should behave as if funding does not exist.

### 2. `DryRun` must hard-gate all write actions

- `DryRun = true` means the module may read state, compute decisions, and log what it would do
- `DryRun = true` must never submit, cancel, transfer, renew, or modify anything on Bitfinex
- `DryRun = false` enables full live behavior, but only if `Enabled = true`

Simple rule:

- `Enabled = false` -> feature absent
- `Enabled = true`, `DryRun = true` -> simulation with logs only
- `Enabled = true`, `DryRun = false` -> live feature

### 3. Maximum encapsulation

Funding must be implemented as a fully encapsulated feature.

That means:

- separate config section
- separate API layer
- separate runtime manager
- separate DTO/model types
- separate repository area if persistence is added
- separate logging prefix
- separate folder subtree

It must not leak funding-specific behavior into the current spot trading flow.

### 4. Dedicated folder subtree everywhere

Anything funding-specific must live in a dedicated folder/subtree wherever it is stored.

Examples:

- exchange adapter code -> `.../Bitfinex/Funding/...`
- data repositories -> `.../Repositories/Funding/...`
- core contracts if needed -> `.../Funding/...`

No scattered funding files across unrelated spot folders.

The only acceptable non-funding location is a very thin composition/startup hook that decides whether to start the funding module.

### 5. Fail-closed behavior

If the funding module fails:

- it must not break the current spot engine
- it must log clearly
- it must stop or self-disable safely
- the existing trading flow must continue normally

## Runtime truth model

Funding must not rely on REST only for live runtime truth.

Required model:

- private WS is the primary live source of truth for state changes
- REST is used for bootstrap snapshot
- REST is used for periodic reconciliation
- REST can remain the write path for submit/cancel operations

This mirrors the existing healthy pattern already used in the Bitfinex spot integration:

- live updates via private WS
- recovery and verification via REST

### Runtime responsibilities

#### WS responsibilities

Use authenticated WS funding events as the primary stream for:

- funding offer lifecycle
- funding credit lifecycle
- funding loan lifecycle
- funding trade execution updates
- wallet updates relevant to funding
- funding notifications

Relevant Bitfinex WS event families:

- `fos`, `fon`, `fou`, `foc`
- `fcs`, `fcn`, `fcu`, `fcc`
- `fls`, `fln`, `flu`, `flc`
- `fte`, `ftu`
- wallet / notify events when relevant

#### REST responsibilities

Use REST for:

- initial snapshot on startup
- state rebuild after reconnect
- periodic reconciliation
- command/write actions:
  - submit funding offer
  - cancel funding offer
  - cancel all funding offers
  - optional future wallet transfer actions

### Hard rule

Do not design funding as `REST-only polling truth`.

That would be weaker than the current Bitfinex trading architecture and would make it harder to:

- react to state changes quickly
- maintain an accurate local view
- recover correctly after partial fills / transitions
- audit timing between offer creation, match, payout, and close

## Official Bitfinex docs we can rely on

- REST auth overview: `https://docs.bitfinex.com/docs/rest-auth`
- WS auth overview: `https://docs.bitfinex.com/docs/ws-auth`
- Public funding ticker fields: `https://docs.bitfinex.com/reference/rest-public-ticker`
- Public funding stats: `https://docs.bitfinex.com/reference/rest-public-funding-stats`
- Public book: `https://docs.bitfinex.com/reference/rest-public-book`
- WS account info channel: `https://docs.bitfinex.com/reference/ws-auth-account-info`
- WS funding trades: `https://docs.bitfinex.com/reference/ws-auth-funding-trades`
- Abbreviation glossary: `https://docs.bitfinex.com/docs/abbreviations-glossary`

Important funding-related authenticated capabilities exposed by Bitfinex docs:

- Active Funding Offers
- Submit Funding Offer
- Cancel Funding Offer
- Cancel All Funding Offers
- Funding Offers History
- Funding Loans
- Funding Loans History
- Funding Credits
- Funding Credits History
- Funding Trades
- Funding Info
- Transfer Between Wallets
- Balance Available for Orders/Offers

## Important WS details

Bitfinex private WS already supports funding data once authenticated.

Useful channel filters:

- `funding`
- `funding-fUSD`
- `funding-fUST`
- `wallet`
- `notify`

Important event names from the official glossary:

- `fos`, `fon`, `fou`, `foc` -> funding offer snapshot/new/update/close
- `fcs`, `fcn`, `fcu`, `fcc` -> funding credit snapshot/new/update/close
- `fls`, `fln`, `flu`, `flc` -> funding loan snapshot/new/update/close
- `fte`, `ftu` -> funding trade execution/update

Wallet labels from the official WS auth docs:

- `exchange` = Exchange Wallet
- `trading` = Margin Wallet
- `deposit` = Funding Wallet

That `deposit` label is important. If we want to lend, we must not assume the current free `exchange` wallet balance is the lendable balance.

## Current public market snapshot

Source date: 2026-03-20

Queried public endpoints:

- `GET https://api-pub.bitfinex.com/v2/tickers?symbols=fUSD,fUST`
- `GET https://api-pub.bitfinex.com/v2/book/fUSD/P0?len=25`
- `GET https://api-pub.bitfinex.com/v2/book/fUST/P0?len=25`
- `GET https://api-pub.bitfinex.com/v2/funding/stats/fUSD/hist?limit=5`
- `GET https://api-pub.bitfinex.com/v2/funding/stats/fUST/hist?limit=5`

Observed top-of-book funding rates:

- `fUSD` around `0.0000966` to `0.0001630` per day
- `fUST` around `0.0000451` to `0.0000840` per day

Very rough simple annualized equivalents:

- `fUSD` about `3.5%` to `6.0%` gross simple APR
- `fUST` about `1.6%` to `3.1%` gross simple APR

This is only a market snapshot, not a guaranteed realized yield. Realized return depends on:

- whether your offer gets hit
- how long funds stay idle
- which currency you lend
- chosen duration and rate
- renew / auto-renew behavior
- platform fee and account conditions

For small balances, this really is a "slow drip" product, but it can still be worth automating if the logic is simple and low-maintenance.

## Recommended architecture

### 1. Separate funding API surface

Do not extend `ICryptoTradingApi` with funding methods.

Add a separate contract, for example:

- `IBitfinexFundingApi`, or
- `IFundingLendingApi`

Suggested responsibilities:

- read funding wallet balances
- read balance available for offers
- list active offers
- list active credits / loans
- submit funding offer
- cancel funding offer
- cancel all funding offers for a symbol
- optionally transfer funds between wallets

### 2. Separate funding models

Add dedicated models instead of reusing order types:

- `FundingOfferInfo`
- `FundingCreditInfo`
- `FundingLoanInfo`
- `FundingTradeInfo`
- `FundingWalletBalance`
- `FundingOfferRequest`

Core fields we will likely need:

- symbol (`fUSD`, `fUST`, ...)
- amount
- rate
- period
- id
- status
- renew flag
- timestamps

### 3. Separate funding runtime service

Add a scheduler/service such as:

- `BitfinexFundingManager`

Responsibilities:

- periodic read-only sync
- optional offer placement / replacement
- stale offer cancellation
- auto-renew handling
- logging and metrics

This should be independent from `CryptoTradingOrchestrator`.

### 4. Separate config block

Best fit is inside `CryptoExchangeSettings`, for example:

```json
"Funding": {
  "Enabled": false,
  "DryRun": true,
  "PreferredSymbols": [ "fUSD", "fUST" ],
  "MinOfferAmount": 50,
  "MaxOfferAmount": 250,
  "ReserveAmount": 100,
  "MinDailyRate": 0.00005,
  "MaxDailyRate": 0.00020,
  "DefaultPeriodDays": 2,
  "MaxPeriodDays": 30,
  "AutoRenew": true,
  "RepriceIntervalMinutes": 15,
  "UseFundingWalletOnly": true,
  "EnableWalletTransfers": false
}
```

This keeps funding behavior exchange-specific and avoids polluting the strategy config.

Minimal required semantics:

- `Enabled = false` -> funding module is not created
- `DryRun = true` -> all writes are converted into logs only

## Safe rollout plan

### Phase 0: Read-only

Implement only:

- funding wallet inspection
- active offers
- active credits
- active loans
- current public rate snapshot

No writes.

Output:

- logs
- metrics
- optional console summary

### Phase 1: Dry-run quoting

Engine computes what it would place:

- symbol
- amount
- daily rate
- period

But it does not submit offers.

Additionally in this phase:

- WS pipeline should already be active
- REST reconciliation should already exist
- all write intents should be persisted as dry-run decisions

### Phase 2: Real offer placement

Allow live submit/cancel with tight limits:

- one symbol only
- small size only
- dry-run off by explicit config
- min reserve amount always kept

Live mode still keeps:

- WS as primary runtime truth
- REST reconciliation loop
- full audit trail in DB

### Phase 3: Optional wallet transfers

Only after we are comfortable with the read/write flow.

This is the riskiest part operationally because it touches fund movement between wallet types.

## Best first implementation

The most practical first slice is:

1. `BitfinexFundingApi` with read-only endpoints
2. `BitfinexFundingSnapshotRunner` or `BitfinexFundingManager`
3. config block `Funding`
4. logging current lendable balance and market rates
5. dry-run recommendation for one currency

That gives us something useful immediately, with very low risk.

## Database and metrics plan

Funding is a different animal from trading.

It is slower, more stateful, more accrual-based, and more dependent on lifecycle tracking over time.

Because of that, we should store not only metrics, but three separate categories of information:

### 1. Exchange truth

What Bitfinex says is true right now or historically:

- wallet state
- active offers
- credits
- loans
- funding trades
- interest-related events / payouts when available

### 2. Engine decisions

What our module decided or intended to do:

- would place
- place
- would cancel
- cancel
- skip
- would transfer
- transfer

Including the reason for each decision.

### 3. Derived analytics

What we compute from truth + decisions:

- utilization
- idle cash
- realized interest
- average rate
- simple APR estimates
- fill efficiency
- time-to-fill

### Hard storage rule

Do not mix:

- exchange truth
- engine decisions
- derived analytics

into the same table.

They should remain conceptually separate.

## What to store in the database

### A. Wallet snapshots

Recommended table:

- `funding_wallet_snapshots`

Store:

- exchange
- wallet type
- currency
- total balance
- available balance
- reserved balance
- local timestamp
- exchange timestamp if available

Purpose:

- understand lendable capital over time
- separate exchange wallet vs funding wallet reality

### B. Market snapshots

Recommended table:

- `funding_market_snapshots`

Store:

- funding symbol (`fUSD`, `fUST`, ...)
- best bid rate
- best ask rate
- top period(s)
- top-of-book amount
- public funding ticker snapshot
- public funding stats snapshot
- local timestamp

Purpose:

- explain why the engine chose a given rate
- compare offered rate vs market reality

### C. Engine action log

Recommended table:

- `funding_offer_actions`

Store:

- action type
- dry-run flag
- live flag
- symbol
- intended amount
- intended rate
- intended period
- reason code
- reason text
- correlation id / run id
- created timestamp

Example actions:

- `would_place`
- `place`
- `would_cancel`
- `cancel`
- `skip`
- `would_transfer`
- `transfer`

Purpose:

- audit every funding decision
- compare dry-run intent vs live behavior

### D. Offer state

Recommended table:

- `funding_offers`

Store:

- exchange offer id
- symbol
- amount
- remaining amount
- rate
- period
- status
- renew flag
- create/update/close timestamps
- source mode (`dry-run` or `live`)

Purpose:

- reconstruct offer lifecycle
- query active and historical offers

### E. Credit state

Recommended table:

- `funding_credits`

Store:

- exchange credit id
- symbol
- amount
- rate
- period
- status
- open/update/close timestamps
- related offer id if known

Purpose:

- know when an offer actually became productive

### F. Loan state

Recommended table:

- `funding_loans`

Store:

- exchange loan id
- symbol
- amount
- rate
- period
- status
- open/update/close timestamps
- related offer id if known

Purpose:

- preserve the Bitfinex funding lifecycle model as-is

### G. Funding trades / executions

Recommended table:

- `funding_trades`

Store:

- exchange trade id
- symbol
- amount
- rate
- period
- timestamp
- related offer / credit / loan ids if known

Purpose:

- execution-level reconstruction
- fill analysis

### H. Interest ledger

Recommended table:

- `funding_interest_ledger`

Store:

- symbol / currency
- gross interest
- fee if applicable
- net interest
- payout timestamp
- related credit / loan / trade ids if known
- source event / source type

Purpose:

- answer the real business question:
  - how much did it actually drip

### I. Reconciliation log

Recommended table:

- `funding_reconciliation_log`

Store:

- reconciliation run id
- started / completed timestamps
- mismatch count
- corrected count
- summary details
- severity

Purpose:

- prove local state stayed aligned with the exchange

### J. Runtime health

Recommended table:

- `funding_runtime_health`

Store:

- WS connected/disconnected
- last WS event time
- last REST sync time
- error count
- degraded mode flag
- self-disabled flag
- local timestamp

Purpose:

- operational visibility

## Key business metrics

At minimum we should derive and track:

- `lendable_balance`
- `idle_balance`
- `offered_balance`
- `credited_balance`
- `utilization_pct`
- `avg_offered_rate`
- `avg_realized_rate`
- `interest_gross_day`
- `interest_net_day`
- `interest_mtd`
- `effective_simple_apr`
- `time_to_fill_avg`
- `offer_fill_ratio`
- `cancel_replace_count`
- `dryrun_decision_count`
- `live_action_count`

## Recommended DB rollout steps

### Step 1. Read-only observability foundation

Add only:

- `funding_wallet_snapshots`
- `funding_market_snapshots`
- `funding_offer_actions`

In this step:

- no live writes to Bitfinex
- only read-only collection and dry-run decisions

### Step 2. Offer lifecycle persistence

Add:

- `funding_offers`
- `funding_credits`
- `funding_loans`

In this step:

- local state can be reconstructed from WS + REST reconcile

### Step 3. Execution and income tracking

Add:

- `funding_trades`
- `funding_interest_ledger`

In this step:

- we can measure realized performance, not only intent

### Step 4. Reliability and operations

Add:

- `funding_reconciliation_log`
- `funding_runtime_health`

In this step:

- operations and debugging become much safer

### Step 5. Derived reporting

Build reporting/views/metrics from the stored data:

- utilization
- realized daily interest
- monthly accumulation
- effective simple APR
- idle capital analysis
- decision quality analysis

## Minimal useful V1 schema

If we want the smallest still-useful first DB slice, use these six tables first:

- `funding_wallet_snapshots`
- `funding_market_snapshots`
- `funding_offer_actions`
- `funding_offers`
- `funding_credits`
- `funding_interest_ledger`

That is enough to answer:

- how much capital was actually available
- what the engine wanted to do
- what Bitfinex actually opened
- how much interest was actually earned

## Foldering rule

Funding code should be grouped under a dedicated subtree from day one.

Recommended shape:

- `src/Denis.TradingEngine.Exchange.Crypto/Bitfinex/Funding/...`
- `src/Denis.TradingEngine.Data/Repositories/Funding/...` if DB storage is needed
- `src/Denis.TradingEngine.Core/Funding/...` only if a truly shared contract is required

Suggested examples:

- `Bitfinex/Funding/Config/BitfinexFundingOptions.cs`
- `Bitfinex/Funding/Api/IBitfinexFundingApi.cs`
- `Bitfinex/Funding/Api/BitfinexFundingApi.cs`
- `Bitfinex/Funding/Models/FundingOfferInfo.cs`
- `Bitfinex/Funding/Runtime/BitfinexFundingManager.cs`
- `Data/Repositories/Funding/BitfinexFundingRepository.cs`

## Notes for this repo

- Existing Bitfinex auth and nonce logic can be reused, but sharing one API key across multiple authenticated REST/WS clients can still produce intermittent `nonce: small` errors.
- Funding now supports dedicated credentials via:
  - `Funding.ApiKeyOverride`
  - `Funding.ApiSecretOverride`
- If dedicated funding credentials are supplied, funding uses its own nonce provider and auth scope.
- Existing private WS auth can be extended with funding filters.
- Existing config shape is already good for adding a funding section.
- Existing order abstractions should stay untouched unless there is a very strong reason to generalize them later.
- Funding implementation should remain visually and structurally isolated from spot trading code.

## Suggested next move

The initial safe slices are already done:

- read-only funding API
- funding config
- dry-run manager
- live submit/cancel support for funding offers
- private funding WS lifecycle tracking
- isolated startup/runtime integration

The next move is no longer "add funding at all", but:

- let the current live slice run and observe lifecycle behavior
- keep watching nonce stability in longer runtime
- keep funding persistence in dedicated tables, separate from spot trading tables
- extend lifecycle storage beyond offers into credits / loans / trades / interest / return flow

## Practical test recipe

Current implementation status in this repo:

- `Enabled=false` keeps runtime identical to today
- `DryRun=true` performs full read/decision flow and only logs + snapshots
- `DryRun=false` can now submit new funding offers and cancel/replace only offers created by the current funding process
- external/manual offers are left untouched by default

Recommended first dry-run test:

- set `Funding.Enabled=true`
- keep `Funding.DryRun=true`
- temporarily lower `Funding.RepriceIntervalMinutes` to `1`
- keep `Funding.AllowManagingExternalOffers=false`
- restart the app and watch `[BFX-FUND]` logs

What to verify in logs and snapshots:

- wallet snapshot arrives
- funding market snapshot arrives for each preferred symbol
- active offers snapshot is present
- decision is one of:
  - `would_place`
  - `would_cancel_for_replace`
  - `skip_external_active_offer_exists`
  - `skip_active_offer_ok`
  - `skip_reserved_balance`
  - `skip_live_requires_funding_wallet`
- `funding_runtime_health` snapshot is being written

Recommended first live test after dry-run looks sane:

- keep `Funding.Enabled=true`
- set `Funding.DryRun=false`
- use only one funding symbol first, for example `fUSD`
- keep small limits such as current `MinOfferAmount` / `MaxOfferAmount`
- keep `AllowManagingExternalOffers=false`
- make sure balance is in the Bitfinex funding/deposit wallet

Important live-safety rule in current slice:

- the manager will place a new offer if there is no active offer
- it will cancel/replace only offers it created during the current process lifetime
- after restart, previously opened offers are treated as external unless we later add persistent ownership tracking

## Achieved on 2026-03-20

The repo now has a working, encapsulated Bitfinex funding module.

What has been proven in live runtime:

- `Enabled=false` keeps runtime behavior identical to pre-funding behavior
- `DryRun=true` performs funding reads, decisions, logs, and snapshots without writes
- `DryRun=false` can successfully submit a live Bitfinex funding offer
- private funding WS receives and logs:
  - offer `NEW`
  - offer `UPDATE`
  - offer `CLOSE`
  - wallet updates
- active funding offer state survives restarts through WS/REST state rebuild
- single-symbol scope now works correctly when `PreferredSymbols = [ "fUSD" ]`
- funding can be limited to a single managed live offer flow without touching spot order flow
- dual-symbol live flow now works with `PreferredSymbols = [ "fUSD", "fUST" ]`
- the module has already placed one live offer on each funding symbol in the same runtime cycle
- basic dedicated funding persistence is now wired into funding-specific DB tables

What was specifically validated during the live test:

- a live `fUSD` offer was submitted successfully
- offer status was tracked as `ACTIVE`
- an earlier `fUSD` offer was also observed closing with `EXECUTED`
- manual cancellation of the unwanted `fUST` test offer was observed via funding WS
- after the scope fix, a clean restart created only one new `fUSD` offer and no `fUST` offer
- after re-enabling both symbols, a clean restart created both a live `fUSD` offer and a live `fUST` offer
- subsequent cycles correctly kept both managed offers as active and did not try to replace them prematurely
- an offer moving to Bitfinex UI state `Provided` was observed as offer close / execution on our side

## Important fixes already completed

### 1. Funding scope / config binding fix

`PreferredSymbols = [ "fUSD" ]` is now honored correctly.

Cause of the bug:

- `BitfinexFundingOptions.PreferredSymbols` originally had a default seeded list
- configuration binding appended JSON values instead of replacing the seeded list
- this caused `fUST` to remain active even when config only specified `fUSD`

Fix:

- remove the seeded mutable default list from options
- keep explicit fallback symbols only when config leaves the list empty

Result:

- funding startup now logs only `symbols=fUSD`
- funding WS auth now uses only `funding-fUSD`
- only one live `fUSD` offer is created in the current production test path

### 2. Fixed 2-day period policy for v1

Funding duration is intentionally fixed to `2` days for the current live slice.

Reason:

- simplest safe live behavior
- easier to reason about and audit
- avoids premature adaptive period logic before enough funding history exists

### 3. Funding auth topology improved

Funding now supports dedicated API credentials separate from spot trading credentials.

Current config fields:

- `Funding.ApiKeyOverride`
- `Funding.ApiSecretOverride`

Reason:

- Bitfinex authenticated REST/WS flows can still hit `nonce: small` when several auth clients share one API key
- separate credentials are the cleanest production-grade isolation path for the funding module

### 4. Nonce mitigation improved further

Funding authenticated REST calls are now serialized inside the funding API client.

Reason:

- even with dedicated funding credentials, parallel authenticated REST calls using the same funding REST key can still be processed out of order by Bitfinex
- wallet reads and active-offer reads in the same cycle were enough to reproduce `nonce: small`

Result:

- dedicated spot / funding REST / funding WS keys are now supported
- funding REST auth calls are gated to one in-flight request per funding REST client
- latest runtime validation no longer shows recurring `nonce: small` during normal funding operation

### 5. Dedicated funding persistence basics are now implemented

Funding is no longer limited to generic snapshot storage only.

Dedicated repository layer has been added for funding state and events.

Tables now populated by the funding module:

- `funding_wallet_snapshots`
- `funding_market_snapshots`
- `funding_offer_actions`
- `funding_offers`
- `funding_offer_events`
- `funding_runtime_health`

Important boundary:

- `crypto_snapshots` still remains as auxiliary telemetry / debug storage
- dedicated funding tables are now the intended home for funding-specific state
- full business lifecycle persistence still needs credits / loans / trades / interest

## Current production-like state

Current state of the funding module:

- encapsulation: strong
- dry-run confidence: high
- single-symbol live submit confidence: high
- dual-symbol live submit confidence: high
- spot/funding isolation: strong
- DB isolation from spot domain tables: basic layer implemented
- nonce robustness with dedicated spot / funding REST / funding WS keys: looks healthy in current runtime
- offer lifecycle tracking: good
- full funding accounting lifecycle: still incomplete

Practical meaning:

- the module is already good enough for controlled multi-symbol live validation
- the basics are in place: live offers, WS truth, DB persistence basics, and isolated runtime behavior
- it is not yet "finished" for complete funding accounting until credits / loans / trades / interest / return-from-loan lifecycle are first-class persisted

## Remaining open items

### 1. Full funding lifecycle persistence

The basic dedicated funding persistence layer now exists, but the full funding business lifecycle is not complete yet.

Already covered:

- `funding_wallet_snapshots`
- `funding_market_snapshots`
- `funding_offer_actions`
- `funding_offers`
- `funding_offer_events`
- `funding_runtime_health`

Still required next:

- `funding_credits`
- `funding_loans`
- `funding_trades`
- `funding_interest_ledger`
- `funding_reconciliation_log`

Rule:

- spot trading truth stays in spot tables
- funding truth stays in funding tables
- generic `crypto_snapshots` can remain only as auxiliary telemetry, not the main funding source of truth

What still must be tracked explicitly:

- when an offer is submitted
- when it becomes active
- when it becomes provided / executed
- when a credit or loan is opened
- when interest accrues or gets paid
- when capital returns from the loan/funding cycle
- when a new offer is created again from returned capital

### 2. Longer-run nonce confidence

Latest runtime behavior looks healthy after:

- dedicated funding REST key
- dedicated funding WS key
- serialized funding authenticated REST requests

But we should still continue to watch for:

- `["error",10114,"nonce: small"]`

especially during:

- long runtimes
- reconnects
- simultaneous offer refresh + wallet refresh + live actions

### 3. Complete funding business accounting

Offer lifecycle is already tracked and partially persisted, but full funding accounting still needs:

- credits
- loans
- funding trades
- interest / payout ledger
- reconciliation between offer closure and resulting productive funding state

That is the point where the engine moves from "offer automation" to "complete funding business accounting".
