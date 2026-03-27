# IBKR Entry Telemetry Exact Join (2026-03-27)

## Context

Tokom weekly review-a za nedelju `2026-03-23` do `2026-03-27` pojavilo se nekoliko prividnih telemetry gap-ova:

- `MRVL`
- `TEAM`

Na prvi pogled je delovalo da `signal_slayer_decisions` nema accepted red koji odgovara entry-ju.

## Root Cause

Problem nije bio gubitak podataka u `signal_slayer_decisions`.

Problem je bio u analizi:

- entry cohort je bio spajan sa `signal_slayer_decisions` preko uskog vremenskog prozora oko fill vremena
- accepted slayer odluka moze da se desi minutima pre samog fill-a
- zato timestamp-only join lako promasi pravi accepted signal

Drugim recima:

- `signal_slayer_decisions` = strategija / filter odluka
- `trade_signals` = kanonski signal koji je orchestrator prihvatio za order flow
- `trade_journal` = stvarni fill / execution audit

Za entry review najtacniji put je:

- `trade_journal.correlation_id -> trade_signals.correlation_id`

## What Changed

1. `trade_signals.utc` se sada upisuje sa `signal.TimestampUtc`, ne sa trenutkom kada orchestrator naknadno obradjuje signal.
2. Dodat je view `v_entry_signal_context` koji tacno spaja:
   - accepted `trade_signals`
   - entry `trade_journal` redove
   - preko istog `correlation_id`
3. Dodat je i SQL template:
   - `src/Denis.TradingEngine.Strategy/Analysis/sql/03-ibkr-entry-cohort-exact-join.sql`

## Expected Usage

Za buduce weekly footprint / cohort analize:

1. prvo uzeti entry cohort iz `v_entry_signal_context`
2. tek onda, po potrebi, dodatno povezivati `signal_slayer_decisions`
3. ne koristiti vise "najblizi accepted slayer row oko fill vremena" kao primarni join

## Files

- `src/Denis.TradingEngine.App/Trading/TradingOrchestrator.cs`
- `src/Denis.TradingEngine.Exchange.Crypto/Trading/CryptoTradingOrchestrator.cs`
- `src/Denis.TradingEngine.Data/db.sql`
- `src/Denis.TradingEngine.Data/migrations/2026-03-27_add_entry_signal_context_view.sql`
- `src/Denis.TradingEngine.Strategy/Analysis/sql/03-ibkr-entry-cohort-exact-join.sql`
