# Partial Fill Discord Aggregation (2026-03-24)

## Incident

Prod Discord je danas pokazao vise poruka za isti broker order:

- `PSX` BUY `broker_order_id=1246`
  - fill slice `3 @ 184.22`
  - fill slice `2 @ 184.22`
- `OXY` SELL `broker_order_id=1245`
  - fill slice `1 @ 60.88`
  - fill slice `5 @ 60.88`
  - fill slice `10 @ 60.87`

## Verified In Prod DB

I `trade_fills` i `trade_journal` imaju iste slice-ove po istom:

- `correlation_id`
- `broker_order_id`

To znaci da DB audit nije bio "duplikat", nego realan partial-fill trace.

## Root Cause

`TradingOrchestrator.ApplyFillCore(...)` je slao Discord notifikaciju po svakom fill slice-u:

- BUY: odmah posle `ApplyBuyFill(...)`
- SELL: odmah posle `ApplySellFill(...)`

Zato je jedan parent order mogao da napravi vise Discord poruka.

## Fix

User-facing Discord notifikacije su agregirane po `correlation_id`:

1. Svaki real fill slice se sabira u memoriji.
2. `trade_fills` i `trade_journal` ostaju slice-level zbog audita.
3. Discord se salje tek kad order postane terminalan:
   - `filled`
   - `canceled`
   - `rejected`

## Expected Behavior After Fix

- `PSX 3 + 2` => jedna BUY Discord poruka za ukupno `5`
- `OXY 1 + 5 + 10` => jedna SELL Discord poruka za ukupno `16`

## File Changed

- `src/Denis.TradingEngine.App/Trading/TradingOrchestrator.cs`

## Verification

- `dotnet build src/Denis.TradingEngine.App/Denis.TradingEngine.App.csproj -c Release`
