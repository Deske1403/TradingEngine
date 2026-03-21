# Bitfinex Fixes – Dokumentacija

Dokumentacija svih implementiranih fixova za Bitfinex spot trading.

---

## 1. UST/USDT normalizacija (wallet balance)

### Problem
Bitfinex koristi "UST" kao interni ticker za USDT na endpoint-u `/v2/auth/r/wallets`. Ako tražiš samo "UST", možeš propustiti balance ako exchange vrati "USDT".

### Rešenje
Dodate helper metode u `CryptoTradingOrchestrator`:

```csharp
private static string NormalizeCurrency(string raw)
{
    if (string.IsNullOrWhiteSpace(raw)) return raw;
    if (raw.Equals("UST", StringComparison.OrdinalIgnoreCase))
        return "USDT";
    return raw;
}

private static BalanceInfo? FindUsdtBalance(IReadOnlyList<BalanceInfo> balances)
{
    return balances.FirstOrDefault(b =>
        NormalizeCurrency(b.Asset).Equals("USDT", StringComparison.OrdinalIgnoreCase));
}
```

- Uvek traži **USDT** (normalizovano)
- Ako dođe "UST" ili "USDT" – oba se mapiraju
- U logu ispisujemo raw currency za debug (`[PLACE-BALANCE-CHECK] raw={Raw} normalized=USDT`)

**Fajl:** `Trading/CryptoTradingOrchestrator.cs`

---

## 2. Stop trigger logika: Bid vs Last

### Problem
- Za **TP** (sell limit): fill zavisi od **Bid** strane
- Za **SL** (stop trigger): berža često gleda **Last**, ne Bid

Ako se oba checkuju na Bid, može doći do mismatch-a – ti vidiš "SL hit", a broker još nije trigerovao.

### Rešenje
Razdvojene reference cene:

```csharp
// TP (sell limit): Bid je relevantan
var tpRefPx = q.Bid ?? q.Last ?? 0m;

// SL (stop trigger): Last je relevantan
var slRefPx = q.Last ?? q.Bid ?? 0m;
```

- **TP check:** `if (tpRefPx >= tpPx)`
- **SL check:** `if (slRefPx <= slPx)`
- **TRAILING STOP:** koristi `slRefPx` (Last)

U exit logovima ispisujemo sva tri: `bid={Bid} last={Last}` radi debug-a.

**Fajl:** `Trading/CryptoTradingOrchestrator.cs`

---

## 3. TryParseBfxError – parsiranje Bitfinex error odgovora

### Problem
Kad Bitfinex vrati error (npr. "not enough exchange balance"), odgovor je tipa:
```json
["error", 10001, "Invalid order: not enough exchange balance for -0.06 SOLUST at 103.1085"]
```

Ako se ne parsira, korisnik dobija generičku poruku "Order rejected".

### Rešenje
Helper u `BitfinexTradingApi.cs`:

```csharp
private static bool TryParseBfxError(string json, out string message)
{
    message = string.Empty;
    try
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array &&
            root.GetArrayLength() >= 3 &&
            root[0].ValueKind == JsonValueKind.String &&
            string.Equals(root[0].GetString(), "error", StringComparison.OrdinalIgnoreCase))
        {
            message = root[2].ValueKind == JsonValueKind.String ? root[2].GetString() ?? "" : "";
            return !string.IsNullOrWhiteSpace(message);
        }
        return false;
    }
    catch { return false; }
}
```

U `PlaceLimitOrderAsync` i `PlaceStopOrderAsync`, odmah posle `json`:

```csharp
if (TryParseBfxError(json, out var err))
    return new PlaceOrderResult(false, null, err);
```

**Fajl:** `Bitfinex/BitfinexTradingApi.cs`

---

## 4. PLACE-STOP log – usklađivanje sa payload-om

### Problem
Log je pokazivao `limit=null`, a u payload-u se uvek šalje `price_aux_limit = (limitPrice ?? stopPrice)`.

### Rešenje
Dodata stvarna vrednost u log:

```csharp
var effectiveLimitPrice = stopLimitPrice ?? stopPrice;

_log.Information(
    "[CRYPTO-ORD] PLACE-STOP {Exchange} {Side} {Symbol} x{Qty} stop={Stop} limit={Limit} (effective={Effective})",
    ..., stopPrice, stopLimitPrice, effectiveLimitPrice);
```

**Fajl:** `Adapters/CryptoOrderService.cs`

---

## 5. ReservedUsd za exit naloge (BUG)

### Problem
Za SELL/exit naloge u `ReservedUsd` se upisivao fee umesto 0. To nije rezervacija keša – za sell ne rezervišemo USD.

### Rešenje
Za exit naloge:
- `ReservedUsd = 0m`
- Fee se upisuje u `LastFeeUsd` (metadata)

```csharp
// Pre (BUG):
_orders.TryAdd(new PendingOrder(tpReq, tpFee, utcNow));

// Posle (FIX):
_orders.TryAdd(new PendingOrder(tpReq, 0m, utcNow, LastFeeUsd: tpFee));
```

Primenjeno u:
- OCO kreiranje u `ApplyFillCore`
- Sync OCO kreiranje
- `SendExit` metoda

**Fajl:** `Trading/CryptoTradingOrchestrator.cs`

---

## 6. TP/SL dupla rezervacija (KRITIČAN BUG)

### Problem
Slanje 2 SELL naloga za isti qty na Bitfinex spot:
1. TP: Limit Sell qty = 0.06 ✅
2. SL: Stop Sell qty = 0.06 ❌

Bitfinex rezerviše base asset (SOL) za prvi SELL. Za drugi nema dovoljno slobodnog balance-a → "Invalid order: not enough exchange balance".

### Rešenje: samo SL na broker, TP "soft"

- **SL nalog** → šalje se na broker (hard protection)
- **TP nalog** → NE šalje se na broker, radi se "soft" u engine-u (`EvaluateCryptoExitsOnQuote`)

```csharp
// Šaljemo SAMO SL na broker - TP će raditi soft logika
_log.Information("[OCO-SOFT] Placing only SL on broker, TP will be handled soft...");
_ = PlaceRealAsync(slReq);  // Samo SL!
// PlaceRealAsync(tpReq) - UKLONJENO
```

TP nalog ostaje u pending store za tracking, ali se ne šalje na broker.

**Fajl:** `Trading/CryptoTradingOrchestrator.cs`

---

## 7. Cancel SL kada se soft TP/TRAIL/TIME aktivira

### Problem
Kad soft TP (ili TRAIL, TIME) ode na izvršavanje, SL nalog i dalje živi na brokeru. Treba ga otkazati da se ne izvrši duplo.

### Rešenje
U `EvaluateCryptoExitsOnQuote`:
1. Pronađe se aktivni SL nalog na brokeru (`activeSLBrokerOrderId`)
2. Pre slanja bilo kog soft exit-a, canceluje se SL

```csharp
if (!string.IsNullOrWhiteSpace(activeSLBrokerOrderId))
{
    _log.Information("[EXIT-TP-CANCEL-SL] Canceling active SL order before TP exit...");
    FireAndForgetCancel(activeSLBrokerOrderId, sym);
}
SendExit(...);
```

Implementirano za:
- TP exit
- TIME exit
- TRAIL exit

**Fajl:** `Trading/CryptoTradingOrchestrator.cs`

---

## 8. Dozvoljena soft TP provera uz aktivni SL

### Problem
Ranije je `hasActiveExitOrders` blokirao sve soft exit-e čim postoji bilo koji exit nalog (uključujući SL).

### Rešenje
- SL i TP nalozi iz OCO grupe **ne blokiraju** soft TP check
- Blokiraju samo **drugi** aktivni exit nalozi (TIME-EXIT, TRAIL-EXIT u toku)

```csharp
// Proveri da li postoje DRUGI aktivni exit nalozi (ne SL/TP iz OCO)
var hasOtherActiveExitOrders = _orders.Snapshot()
    .Any(po => ... && 
               !po.Req.CorrelationId.StartsWith("exit-sl-", ...) &&
               !po.Req.CorrelationId.StartsWith("exit-tp-", ...) && ...);
```

**Fajl:** `Trading/CryptoTradingOrchestrator.cs`

---

## 9. No-price guard (quote glitch protection)

### Problem
Fallback na `0m` ako nema Bid/Last može lažno trigerovati SL (`0 <= slPx`).

### Rešenje
- Ako nema ni Bid ni Last → preskoči evaluaciju
- TP check: samo ako ima Bid
- SL check: samo ako ima Last
- TRAIL check: samo ako ima Last

**Fajl:** `Trading/CryptoTradingOrchestrator.cs`

---

## 10. NormalizeCurrency – raw.Trim()

### Problem
Whitespace može maskirati bug pri pretrazi balansa.

### Rešenje
`return raw.Trim()` umesto `return raw` – nikad ne vraćaj whitespace kao "normalizovano".

**Fajl:** `Trading/CryptoTradingOrchestrator.cs`

---

## 11. TryParseBfxError – error code u message

### Problem
Samo message bez error code-a otežava debug (rate-limit, invalid-args, balance).

### Rešenje
Uključi `root[1]` (error code) u message: `"[{code}] {msg}"`.

**Fajl:** `Bitfinex/BitfinexTradingApi.cs`

---

## 12. Native OCO (One-Cancels-Other) – zamenjuje soft TP

### Problem
Soft TP (samo SL na brokeru, TP u engine-u) nije potreban: Bitfinex podržava OCO par (TP limit + SL stop sa istim `gid` i `flags=16384`). Berza automatski otkazuje drugi nalog kad se jedan izvrši.

### Rešenje
- **ICryptoTradingApi / BitfinexTradingApi:** `PlaceLimitOrderAsync` i `PlaceStopOrderAsync` primaju opciono `flags` i `gid`. Za OCO: `flags=16384`, `gid=Math.Abs(ocoGroupId.GetHashCode())`.
- **CryptoOrderService:** kada `request.OcoGroupId` je set, prosleđuje `flags=16384` i `gid` u API.
- **CryptoTradingOrchestrator:** šalje **oba** TP i SL na broker (isti `ocoGroupId`), bez `BrokerOrderId="SOFT"`. TP/SL fill dolaze preko `OnOrderUpdated`; TIME/TRAIL exit i dalje otkazuju sve aktivne exit naloge za simbol pre slanja market exit-a.
- Uklonjeni: `BrokerOrderId="SOFT"`, `MarkSlInvalidated`, `IsSlInvalidated`, `ClearSlInvalidated`, soft TP/SL blokovi u `EvaluateCryptoExitsOnQuote`.

**Fajlovi:** `Abstractions/ICryptoTradingApi.cs`, `Bitfinex/BitfinexTradingApi.cs`, `Adapters/CryptoOrderService.cs`, `Trading/CryptoTradingOrchestrator.cs`, Bybit/Deribit/Kraken API (signature match).

---

## 13. Cancel dedupe (throttling) – broj sekcije ostao

### Problem
Više quote tick-ova može poslati više cancel-a za isti brokerOrderId → burst taskova.

### Rešenje
`_cancelRequestedAt[brokerOrderId]` – ne šalji dupli cancel u roku 5 sekundi. Dedupe po brokerOrderId.

**Fajl:** `Trading/CryptoTradingOrchestrator.cs`

---

## 15. Cancel idempotency (Bitfinex API)

### Problem
Ponovljen cancel za već canceled order može baciti grešku.

### Rešenje
U `BitfinexTradingApi.CancelOrderAsync`: ako `TryParseBfxError` vrati "not found" / "already" / "canceled" → tretiraj kao success (idempotent).

**Fajl:** `Bitfinex/BitfinexTradingApi.cs`

---

## Preostali bugovi (Phase 2)

1. **Sync duplikacija pozicije** – `ApplyBuyFillCrypto` + `ApplyFillCore` = dupla pozicija
2. **NormalizePrice()** – zaokruživanje na tick size za Bitfinex parove
3. **Timeout u PlaceRealAsync** – `cts.Token` se ne prosleđuje u `PlaceAsync`

---

## Kratak pregled

| # | Fix | Fajl | Status |
|---|-----|------|--------|
| 1 | UST/USDT normalizacija | CryptoTradingOrchestrator | ✅ |
| 2 | Bid vs Last za TP/SL | CryptoTradingOrchestrator | ✅ |
| 3 | TryParseBfxError | BitfinexTradingApi | ✅ |
| 4 | PLACE-STOP log | CryptoOrderService | ✅ |
| 5 | ReservedUsd=0 za exit | CryptoTradingOrchestrator | ✅ |
| 6 | No-price guard | CryptoTradingOrchestrator | ✅ |
| 7 | NormalizeCurrency Trim | CryptoTradingOrchestrator | ✅ |
| 8 | TryParseBfxError error code | BitfinexTradingApi | ✅ |
| 9 | Native OCO (TP+SL na broker) | CryptoTradingOrchestrator, CryptoOrderService, BitfinexTradingApi | ✅ |
| 10 | Cancel dedupe | CryptoTradingOrchestrator | ✅ |
| 11 | Cancel idempotency | BitfinexTradingApi | ✅ |
