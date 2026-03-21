# Dynamic Trailing (REAL) - Analiza i Predlog Implementacije

## 📋 Pregled

**Stavka 7: Dynamic trailing (REAL managed SL)** - trenutno **0%** (Planned)

**Cilj:**
- Implementirati dinamički trailing stop loss u REAL modu koristeći IBKR API
- Umesto statičkog SL-a, automatski ažurirati stop loss order kada se trailing aktivira
- Koristiti IBKR napredne opcije za order management

---

## 🔬 IBKR API - Tehnički Detalji (Istraživanje)

### **1. Kako IBKR API Modifikuje Order**

**Ključna činjenica:**
- IBKR API **NEMA** posebnu `modifyOrder()` metodu
- Modifikacija se vrši pozivom `placeOrder()` sa **ISTIM order ID-jem**
- Kada se pozove `placeOrder(orderId, contract, order)` sa order ID-jem koji već postoji, IBKR automatski **modifikuje** postojeći order
- Sve properties Order objekta se mogu modifikovati: `AuxPrice`, `LmtPrice`, `TotalQuantity`, itd.

**Iz postojećeg koda (`RealIbkrClient.cs`):**
```csharp
// Line 411: placeOrder poziva se sa order ID-jem
_wrapper.ClientSocket.placeOrder(twsOrderId, ibContract, ibOrder);
```

**Važno:**
- Order ID mora biti **isti** kao originalni order
- IBKR automatski detektuje da je order ID već korišćen i modifikuje postojeći order
- OCO grupa se **čuva** - modifikacija SL ordera ne utiče na TP order u OCO grupi

### **2. IBKR Order Objekat - Dostupne Properties**

Iz postojećeg koda (`RealIbkrClient.cs`, linije 373-386) vidimo da se koriste:

```csharp
ibOrder = new Order
{
    Action = side,                    // "BUY" ili "SELL"
    TotalQuantity = qtyDec,           // Količina
    OrderType = type,                 // "MKT", "LMT", "STP", "STP LMT"
    LmtPrice = px,                    // Limit cena (za LMT i STP LMT)
    AuxPrice = stopPx,                // Stop cena (za STP i STP LMT) ⭐ OVO MODIFIKUJEMO
    OcaGroup = ocaGroup,              // OCO grupa ID
    OcaType = hasOca ? 1 : 0,        // OCO tip
    Tif = tif,                        // "DAY" ili "GTC"
    Transmit = transmit,              // Da li da se odmah pošalje
    OutsideRth = outsideRth,          // Da li van RTH
    OrderRef = correlationId          // Correlation ID
};
```

**IBKR Order objekat takođe podržava (ali ne koristimo u kodu):**
- `TrailingAmount` - iznos za trailing stop (npr. 0.50 za $0.50 trailing)
- `TrailingStopType` - tip trailing stop-a (0 = fixed amount, 1 = percentage)
- **ALI**: Ovo se koristi za **Trailing Stop** order tip (`OrderType = "TRAIL"`), ne za modifikaciju postojećeg STP ordera

### **3. Dve Opcije za Dynamic Trailing:**

#### **Opcija A: Modifikacija Postojećeg STP Ordera** ⭐ **PREPORUČENO**
- Koristimo postojeći STP order iz OCO grupe
- Modifikujemo `AuxPrice` pozivom `placeOrder` sa istim order ID-jem
- **Prednost**: Jednostavno, direktno, OCO i dalje radi
- **Prednost**: Potpuna kontrola nad trailing logikom (naša logika, ne IBKR-ova)

#### **Opcija B: Trailing Stop Order Tip** (Alternativa)
- Koristimo IBKR `TrailingAmount` i `TrailingStopType` properties
- Kreira se **Trail** order tip umesto STP
- **Problem**: Ne može se kombinovati sa OCO grupom (TP order)
- **Problem**: Trailing se dešava automatski na IBKR strani, ne možemo kontrolisati logiku (ATR-based trailing)
- **Problem**: Ne možemo koristiti našu custom trailing logiku (ATR-based, activation price, itd.)

**Zaključak:** Opcija A je bolja jer omogućava potpunu kontrolu nad trailing logikom i kompatibilna je sa OCO grupom.

---

## 🔍 IBKR Opcije (iz Trader Workstation-a)

Iz screenshot-a vidimo da IBKR podržava:

### 1. **OCO (One Cancel Another)** ✅ **VEĆ KORISTIMO**
- Kada se jedan order fill-uje, drugi se automatski cancel-uje
- Trenutno koristimo za TP/SL parove

### 2. **Bracket Orders** (Stop Loss/Profit Taker)
- IBKR automatski kreira TP i SL nakon entry fill-a
- **Problem**: Bracket orders se ne mogu lako modifikovati (kompleksnije)

### 3. **Modifikacija Postojećeg Ordera** ⭐ **NAJBOLJA OPCIJA**
- IBKR API podržava modifikaciju ordera tako što se pozove `placeOrder` sa **istim order ID-jem**
- Možemo modifikovati `AuxPrice` (stop price) na postojećem SL orderu
- **Prednost**: Jednostavno, direktno, bez dodatnih kompleksnosti

### 4. **Conditional Orders**
- Order se aktivira kada se ispuni određeni uslov
- **Problem**: Kompleksnije za trailing stop logiku

### 5. **Iceberg Orders**
- Ne relevantno za trailing stop

### 6. **Hedge Orders**
- Ne relevantno za trailing stop

---

## 💡 Predlog Implementacije

### **Opcija 1: Modifikacija Postojećeg SL Ordera** ⭐ **PREPORUČENO**

**Kako radi:**
1. Nakon BUY fill-a, kreiramo OCO grupu (TP + SL) - **već postoji**
2. Čuvamo `brokerOrderId` za SL order u `PositionRuntimeState`
3. Kada se trailing aktivira (bestPrice >= activationPrice):
   - Pronađemo SL order preko `brokerOrderId`
   - Izračunamo novi trailing stop: `bestPrice - TrailDistanceAtrMultiple * atr`
   - Modifikujemo SL order pozivom `PlaceOrderAsync` sa **istim order ID-jem** i novim `AuxPrice`
4. IBKR automatski ažurira stop price na postojećem orderu

**Prednosti:**
- ✅ Jednostavno - samo modifikacija `AuxPrice`
- ✅ Direktno - koristi postojeći SL order
- ✅ Nema dodatnih ordera - samo update
- ✅ OCO i dalje radi - TP se ne dira

**Implementacija:**

```csharp
// 1. Dodati u PositionRuntimeState
public int? SlBrokerOrderId { get; set; }  // IBKR order ID za SL order

// 2. Nakon OCO kreiranja, sačuvati SL broker order ID
slReq = new OrderRequest(...);
var slBrokerId = await _orderService.PlaceAsync(slReq);
_posRuntime[sym].SlBrokerOrderId = int.Parse(slBrokerId);

// 3. U EvaluateRealExitsOnQuote, kada se trailing aktivira:
if (rt.TrailingArmed && rt.SlBrokerOrderId.HasValue)
{
    var newTrailStop = rt.BestPrice - TrailDistanceAtrMultiple * atr;
    
    // Modifikuj postojeći SL order
    var modifyReq = new OrderRequest(
        symbol: sym,
        side: OrderSide.Sell,
        type: OrderType.Stop,
        quantity: pos.Quantity,
        limitPrice: null,
        tif: TimeInForce.GTC,  // ili DAY za intraday
        correlationId: $"exit-sl-modify-{Guid.NewGuid():N}",
        timestampUtc: now,
        stopPrice: newTrailStop,
        isExit: true
    );
    
    // IBKR modifikacija: pozovi PlaceOrderAsync sa ISTIM order ID-jem
    await _orderService.ModifyOrderAsync(
        brokerOrderId: rt.SlBrokerOrderId.Value.ToString(),
        newStopPrice: newTrailStop
    );
}
```

**Nova metoda u `IbkrOrderService`:**

```csharp
/// <summary>
/// Modifikuje postojeći order na IBKR-u.
/// IBKR API modifikacija se vrši pozivom placeOrder sa ISTIM order ID-jem.
/// </summary>
public async Task ModifyOrderAsync(string brokerOrderId, decimal? newStopPrice = null, decimal? newLimitPrice = null)
{
    if (!int.TryParse(brokerOrderId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ibId))
    {
        _log.Warning("[IB-MODIFY] Cannot parse brokerOrderId={Id}", brokerOrderId);
        return;
    }

    OrderRequest? original;
    lock (_sync)
    {
        if (!_byIbOrderId.TryGetValue(ibId, out original))
        {
            _log.Warning("[IB-MODIFY] Order not found id={Id}", ibId);
            return;
        }
    }

    // Kreiraj modifikovanu verziju sa novim stop/limit cenom
    var modified = new OrderRequest(
        symbol: original.Symbol,
        side: original.Side,
        type: original.Type,
        quantity: original.Quantity,
        limitPrice: newLimitPrice ?? original.LimitPrice,
        tif: original.Tif,
        correlationId: original.CorrelationId + "-modify",  // Nova correlation ID za tracking
        timestampUtc: DateTime.UtcNow,
        ocoGroupId: original.OcoGroupId,  // ⭐ VAŽNO: Očuvaj OCO grupu!
        stopPrice: newStopPrice ?? original.StopPrice,
        isExit: original.IsExit
    );

    // IBKR modifikacija: pozovi PlaceOrderAsync sa ISTIM order ID-jem
    // IBKR automatski detektuje da order ID već postoji i modifikuje postojeći order
    var contract = IbkrMapper.ToContract(modified.Symbol);
    var order = IbkrMapper.ToOrder(modified);
    
    // ⭐ KLJUČNO: Koristimo ISTI ibId (ne kreiramo novi order ID)
    await _ib.PlaceOrderAsync(ibId, contract, order).ConfigureAwait(false);
    
    // Update internal tracking
    lock (_sync)
    {
        _byIbOrderId[ibId] = modified;  // Update sa modifikovanom verzijom
        _orderTouchedUtc[ibId] = DateTime.UtcNow;
    }
    
    _log.Information(
        "[IB-MODIFY] id={Id} {Sym} newStop={Stop} newLimit={Limit} oco={Oco}",
        ibId, modified.Symbol.Ticker, newStopPrice, newLimitPrice, modified.OcoGroupId ?? "null");
}
```

**Važne napomene:**
- ⭐ Koristimo **ISTI** `ibId` (ne kreiramo novi order ID)
- ⭐ IBKR automatski detektuje da order ID već postoji i modifikuje postojeći order
- ⭐ OCO grupa se **čuva** - `OcoGroupId` se prosleđuje u modifikovani order
- ⭐ Update-ujemo internal tracking (`_byIbOrderId`) sa modifikovanom verzijom

---

### **Opcija 2: Bracket Orders** (Alternativa)

**Kako radi:**
- IBKR automatski kreira TP i SL nakon entry fill-a
- **Problem**: Bracket orders se ne mogu lako modifikovati
- **Rešenje**: Cancel + recreate, ali to je kompleksnije i može dovesti do gap-a

**Ne preporučuje se** zbog kompleksnosti.

---

### **Opcija 3: Conditional Orders** (Alternativa)

**Kako radi:**
- Order se aktivira kada se ispuni uslov (npr. "cena >= X")
- **Problem**: Kompleksnije za trailing stop logiku
- **Rešenje**: Potrebno je kreirati nove conditional ordere za svaki trailing update

**Ne preporučuje se** zbog kompleksnosti i potrebe za više ordera.

---

## 🎯 Finalna Preporuka

### **Koristiti Opciju 1: Modifikacija Postojećeg SL Ordera**

**Razlozi:**
1. ✅ **Jednostavno** - samo modifikacija `AuxPrice`
2. ✅ **Direktno** - koristi postojeći SL order
3. ✅ **Efikasno** - nema dodatnih ordera
4. ✅ **Pouzdano** - IBKR API direktno podržava modifikaciju
5. ✅ **OCO i dalje radi** - TP se ne dira

**Implementacija:**
1. Dodati `SlBrokerOrderId` u `PositionRuntimeState`
2. Sačuvati SL broker order ID nakon OCO kreiranja
3. Dodati `ModifyOrderAsync` metodu u `IbkrOrderService`
4. U `EvaluateRealExitsOnQuote`, kada se trailing aktivira, modifikovati SL order
5. Rate-limit modifikacije (max 1 po sekundi, kao u PAPER modu)

---

## 📝 Detalji Implementacije

### **1. PositionRuntimeState proširenje**

```csharp
public sealed class PositionRuntimeState
{
    // ... postojeća polja ...
    
    // Trailing stop state (PAPER)
    public bool TrailingArmed { get; set; } = false;
    public DateTime? LastTrailUpdateUtc { get; set; }
    public decimal? LastTrailStop { get; set; }
    
    // NOVO: REAL trailing - IBKR order ID za SL order
    public int? SlBrokerOrderId { get; set; }
}
```

### **2. Čuvanje SL broker order ID nakon OCO kreiranja**

U `TradingOrchestrator.ApplyFillCore`, nakon OCO kreiranja:

```csharp
// Nakon što se SL order pošalje
var slBrokerId = await PlaceRealAsync(slReq);
if (int.TryParse(slBrokerId, out var slId))
{
    lock (_sync)
    {
        if (_posRuntime.TryGetValue(sym.Ticker, out var rt))
        {
            rt.SlBrokerOrderId = slId;
        }
    }
}
```

### **3. Modifikacija SL ordera u trailing logici**

U `TradingOrchestrator.EvaluateRealExitsOnQuote`:

```csharp
// Kada se trailing aktivira i imamo SL order ID
if (rt.TrailingArmed && rt.SlBrokerOrderId.HasValue)
{
    var newTrailStop = rt.BestPrice - TrailDistanceAtrMultiple * atr;
    
    // Proveri da li se trail stop pomera naviše (update)
    var shouldUpdate = !rt.LastTrailStop.HasValue || newTrailStop > rt.LastTrailStop.Value;
    var canUpdate = !rt.LastTrailUpdateUtc.HasValue || 
                   (now - rt.LastTrailUpdateUtc.Value) >= TimeSpan.FromSeconds(1);
    
    if (shouldUpdate && canUpdate)
    {
        rt.LastTrailStop = newTrailStop;
        rt.LastTrailUpdateUtc = now;
        
        // Modifikuj SL order na IBKR-u
        await _orderService.ModifyOrderAsync(
            brokerOrderId: rt.SlBrokerOrderId.Value.ToString(),
            newStopPrice: newTrailStop
        );
        
        _log.Information(
            "[TRAIL-UPDATE-REAL] {Sym} best={Best:F2} stop={Stop:F2} entry={Entry:F2} atr={Atr:F4} brokerId={Id}",
            sym, rt.BestPrice, newTrailStop, entry, atr, rt.SlBrokerOrderId.Value);
    }
}
```

### **4. Nova metoda u IbkrOrderService**

```csharp
public async Task ModifyOrderAsync(string brokerOrderId, decimal? newStopPrice = null, decimal? newLimitPrice = null)
{
    if (!int.TryParse(brokerOrderId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ibId))
    {
        _log.Warning("[IB-MODIFY] Cannot parse brokerOrderId={Id}", brokerOrderId);
        return;
    }

    OrderRequest? original;
    lock (_sync)
    {
        if (!_byIbOrderId.TryGetValue(ibId, out original))
        {
            _log.Warning("[IB-MODIFY] Order not found id={Id}", ibId);
            return;
        }
    }

    // Kreiraj modifikovanu verziju
    var modified = new OrderRequest(
        symbol: original.Symbol,
        side: original.Side,
        type: original.Type,
        quantity: original.Quantity,
        limitPrice: newLimitPrice ?? original.LimitPrice,
        tif: original.Tif,
        correlationId: original.CorrelationId + "-modify",
        timestampUtc: DateTime.UtcNow,
        ocoGroupId: original.OcoGroupId,
        stopPrice: newStopPrice ?? original.StopPrice,
        isExit: original.IsExit
    );

    var contract = IbkrMapper.ToContract(modified.Symbol);
    var order = IbkrMapper.ToOrder(modified);
    
    // IBKR modifikacija: pozovi PlaceOrderAsync sa ISTIM order ID-jem
    await _ib.PlaceOrderAsync(ibId, contract, order).ConfigureAwait(false);
    
    _log.Information(
        "[IB-MODIFY] id={Id} {Sym} newStop={Stop} newLimit={Limit}",
        ibId, modified.Symbol.Ticker, newStopPrice, newLimitPrice);
}
```

---

## ⚠️ Napomene i Edge Cases

### **1. SL Order već fill-ovan**
- Proveriti status ordera pre modifikacije
- Ako je fill-ovan, ne modifikovati

### **2. SL Order cancel-ovan (TP fill-ovao)**
- Ako je TP fill-ovao, SL je automatski cancel-ovan (OCO)
- Proveriti status pre modifikacije

### **3. Rate-limiting**
- Max 1 modifikacija po sekundi (kao u PAPER modu)
- IBKR može imati svoje rate-limiting zahteve

### **4. Error handling**
- Ako modifikacija ne uspe, logovati i nastaviti
- Ne rušiti trading engine zbog modifikacije

### **5. DB tracking**
- Opciono: dodati `broker_orders` update za modifikacije
- Ili samo logovati modifikacije

---

## 🧪 Testiranje

### **Test Scenariji:**

1. **Trailing aktivacija**
   - BUY fill → OCO kreiranje → trailing aktivacija → SL modifikacija
   - Proveriti da li se SL order modifikuje na IBKR-u

2. **Trailing update**
   - Trailing aktiviran → cena raste → SL se pomera naviše
   - Proveriti da li se SL order modifikuje više puta

3. **TP fill pre trailing**
   - BUY fill → OCO kreiranje → TP fill → SL cancel
   - Proveriti da li trailing pokušava da modifikuje cancel-ovan order

4. **SL fill pre trailing**
   - BUY fill → OCO kreiranje → SL fill (gubitak)
   - Proveriti da li trailing pokušava da modifikuje fill-ovan order

---

## 📊 Prometheus Metrike

Dodati metrike za REAL trailing:

```csharp
// U StrategyMetrics.cs
_trailingUpdateRealTotal = Metrics.CreateCounter(
    "strategy_trailing_update_real_total",
    "Number of times trailing stop was updated in REAL mode",
    new[] { "symbol", "method" }
);

_trailingModifyFailedTotal = Metrics.CreateCounter(
    "strategy_trailing_modify_failed_total",
    "Number of times trailing stop modification failed",
    new[] { "symbol", "reason" }
);
```

---

## ✅ Checklist za Implementaciju

- [ ] Dodati `SlBrokerOrderId` u `PositionRuntimeState`
- [ ] Sačuvati SL broker order ID nakon OCO kreiranja
- [ ] Dodati `ModifyOrderAsync` metodu u `IbkrOrderService`
- [ ] Implementirati trailing logiku u `EvaluateRealExitsOnQuote`
- [ ] Dodati rate-limiting (max 1 po sekundi)
- [ ] Dodati error handling
- [ ] Dodati Prometheus metrike
- [ ] Dodati logove za modifikacije
- [ ] Testirati u paper trading modu (ako je moguće)
- [ ] Testirati u real trading modu sa malim size-om

---

## 🎯 Zaključak

**Preporuka: Koristiti Opciju 1 (Modifikacija Postojećeg SL Ordera)**

- ✅ Jednostavno i direktno
- ✅ Koristi postojeći IBKR API
- ✅ Nema dodatnih kompleksnosti
- ✅ OCO i dalje radi

**Sledeći korak:** Implementirati prema predlogu iznad.

