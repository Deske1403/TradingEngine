# Denis Trading Engine – Status & Plan (V1.8 → V2)

**Datum:** 2025-12-26  
**Last Updated:** 2026-01-27 (US market calendar – NYSE/NASDAQ praznici + early close)

## Status

- V1.8 je stabilan u PAPER modu.
- SWING core (4/4 taskova) je implementiran – praktično "V1.8.5 SWING baseline".
- **SignalSlayer 2.0 je sada 100% završen** ✅
- Sledeći korak: LIVE VALIDATION (logovi + DB) i posle toga V2 (REAL trailing, profiling, reporting).

## Implementirano (ključne stvari)

- TrendSlope, SMA, PA analytics, ATR-floor
- TP/SL + trailing (PAPER)
- RTH guard, weekend gap guard, **US market calendar (NYSE/NASDAQ praznici + early close)** ✅
- **SignalSlayer 2.0 (HARD-REJECT) + micro-filter + per-day cap + DB persistence + Prometheus metrics + Replay harness** ✅
- Robustan pending/recovery sloj
- SWING baseline (REAL OCO + auto-exit engine + External/IBKR zaštita)

---

## 0. Trenutno stanje – V1.8 (stabilno, precizno ponašanje)

### 0.0 PullbackInUptrendStrategy

**Trend logika:**
- Uptrend = EMA_fast > EMA_slow i EMA_slow ne pada.
- Korišćeni indikatori:
  - EMA20 / EMA50
  - TrendSlope(5)
  - TrendSlope(20)
  - SMA(10) / SMA(30)

**Pullback sistem:**
- pbHi / pbLo / pbDur
- Validan PB = cena ispod EMA_fast ali ne previše ispod EMA_slow.
- Predugačak PB → abort (regime-sensitive).

**Reclaim uslov:**
- Breakout iznad PB-high + režimski buffer.
- LOW režim → stroži uslovi.

**Filtri u strategiji:**
- stale quote guard
- minimum ticks-per-window
- spread sanity check
- ATR warmup
- min/max PB dubina
- min/max PB duration
- signal throttling

**PA / mikro indikatori (bez L2):**
- TrendSlope(5/20)
- SMA(10/30)
- distance to EMA
- distance to PB high/low
- [PA] i [ANALYTICS] logging

### 0.1 Risk & TP/SL sistem

- Fiksni dollar-risk po trade-u.
- ATR sizing sa ATR-floor (min ~0.2% cene).
- TP/SL:
  - ATR-multiple
  - fallback procentualni limit
- Exit engine (PAPER):
  - TP
  - SL
  - dynamic trailing regulator
  - time-exit

### 0.2 Volatility režimi

- atrFrac = ATR / Price.
- Režimi: LOW / NORMAL / HIGH.

Režimi kontrolišu:
- minimalni PB duration
- breakout buffer
- minimalnu PB dubinu
- agresivnost entry-ja

Režim se zamrzava kada PB počne (snapshot state).

### 0.3 Time-of-day guard (RTH) + weekend guards + US market calendar

**TradingSessionGuard** (`Denis.TradingEngine.Strategy.Pullback.TimeGate`):

- **Belgrade radni prozor:** `IsWeekendGapClosed(utcNow)` – vikend zatvoren, petak 22:00 gas, ponedeljak 14:20 paljenje.
- **US berza (NYSE/NASDAQ):**
  - **`IsUsMarketHoliday(dateEt)`** – da li je datum US praznik (berza zatvorena): New Year, MLK Jr, Presidents Day, Good Friday, Memorial Day, Juneteenth, Independence Day, Labor Day, Thanksgiving, Christmas (+ observed kada padne vikend).
  - **`GetUsSessionEndTimeEt(dateEt)`** – kraj sesije za taj dan: **16:00 ET** normalno, **13:00 ET** na early-close danu (dan posle Thanksgiving, Christmas Eve 24. dec).
  - **`IsInsideUsRth(utcNow)`** – da li je trenutak unutar US RTH: vikend (ET) = closed, US praznik = closed, inače 9:30–16:00 ET (ili 9:30–13:00 ET na early-close dan).

U orchestratoru:
- kada su `RthStartUtc` / `RthEndUtc` setovani, koristi se **`TradingSessionGuard.IsInsideUsRth(now)`** (umesto lokalne RTH logike);
- blokira signale van RTH, na praznike i posle early close;
- i dalje koristi **`IsWeekendGapClosed(quoteTs)`** za Belgrade radni prozor;
- loguje razloge i/ili upisuje u DB.

### 0.4 UseMidPrice i MinQuantity (IBKR) – cena i minimum količine

**Config:** `appsettings.json` → sekcija **Trading**:
- **`UseMidPrice`** (bool, default `false`) – kako se računa limit cena za entry.
- **`MinQuantity`** (int, default `3`) – minimalna dozvoljena količina za entry; ispod toga se order blokira.

**Tok:**

1. **Program.cs** – učitava `Trading.UseMidPrice` i `Trading.MinQuantity`, loguje `[TRADING-CONFIG] UseMidPrice=… MinQuantity=…`, prosleđuje **UseMidPrice** u konstruktor **PullbackInUptrendStrategy**; **MinQuantity** ide i u strategiju (opciono) i u **TradingSettings** (za orchestrator).
2. **PullbackInUptrendStrategy** – pri emitovanju signala:
   - **Limit cena:** `suggestedLimit = UseMidPrice ? px : (Ask ?? px)`:
     - **UseMidPrice = true** → limit = **MID** (px = last/mid), manji slippage na skupim akcijama.
     - **UseMidPrice = false** → limit = **ASK** (klasično).
   - Signal nosi `SuggestedLimitPrice`; orchestrator koristi tu cenu za sizing i order.
3. **TradingOrchestrator** – nakon risk/sizing i normalizacije količine (korak 8.5):
   - ako je **MinQuantity > 0** i **qty < MinQuantity** → blokira entry, `MarkBlocked("min-quantity")`, upis u DB sa razlogom npr. `min-quantity:2.000000<3`.

**Rezime:** UseMidPrice određuje da li je entry limit na MID ili ASK (u strategiji); MinQuantity u orchestratoru sprečava order kada je količina ispod praga (npr. skupi simboli sa malim brojem akcija).

---

## 1. SignalSlayer 2.0 – HARD REJECT logika ✅ **100% COMPLETE**

**Status:** ✅ **ZAVRŠENO** (2025-12-26)

Slayer radi unutar strategije i odlučuje da li je signal validan.

**Ulaz:** SignalSlayerContext
- symbol
- cena
- ATR + atrFractionOfPrice
- spreadBps
- activityTicks
- regime
- slope5 / slope20
- ime strategije
- UtcNow

**Hard-reject pravila:**

1) ATR sanity
   - atrFrac < 0.00001 → reject
   - atrFrac > 0.05    → reject

2) Spread sanity
   - spread > 30 bps → reject

3) Aktivnost
   - ticks < 10 → reject

4) Per–day cap
   - > 15 signala po simbolu dnevno → reject

5) Micro-filter (PA proxy, SignalSlayerConfig)
   - MinSlope20
   - MinAtrFractionOfPrice = 0.00002
   - MaxSpreadBps = 20
   - MinTicksPerWindow = 150
   - Enable/Disable flag

Ako ne prođe → razlog: MicroFilterRejected.

**Log tagovi:**
- [SLAYER-CTX] – snapshot ulaza
- [SLAYER-MICRO] – micro-filter rezultat
- [SLAYER-REJECT] – reject + razlozi
- [SLAYER-ACCEPT] – validan signal

### ✅ Implementirano (100%):

1. **Stabilni reason codes** ✅
   - `ATR_TOO_LOW`, `ATR_TOO_HIGH`, `SPREAD_TOO_WIDE`, `TICKS_TOO_LOW`, `CAP_REACHED`, `MICRO_FILTER_REJECTED`, `ACCEPTED`
   - **NOVO (2026-01-12):** `REJECTION_SPEED`, `OPEN_FAKE_BREAKOUT` (Distribution Protection filters)
   - Koriste se u metrics i DB

2. **Prometheus metrics** ✅
   - Counter: `strategy_signal_slayer_decision_total` sa labels: `strategy`, `symbol`, `reason_code`
   - Omogućava "Top 5 razloga reject-a" queries

3. **Database persistence** ✅
   - Tabela: `signal_slayer_decisions`

4. **Distribution Protection (anti-manufactured-spike filters)** ✅ **NOVO (2026-01-12)**
   - **Time-of-day hard rule:** Detektuje "open trap" scenarije (fake breakouts u open_1h fazi)
     - Uslov: `< MaxMinutesFromOpen` minuta od RTH open-a + move > `MinMovePct` + nema valid pullback strukture
     - Default pragovi: 8 minuta, 0.7% move, require valid PB
   - **Rejection Speed filter:** (TODO - za early exit guard u TradingOrchestrator)
     - Detektuje brz pad posle lokalnog max-a (distribution pattern)
   - **Config:** `SignalSlayer:DistributionProtection` u appsettings.json
     - `Enabled: false` (master switch)
     - `LogWhenDisabled: true` (loguje čak i kada je disabled za analizu)
     - Po filteru: `RejectionSpeed.Enabled`, `TimeOfDay.Enabled`
   - **Logging:** `[DIST-PROT]` tag sa reason code-om i parametrima
   - Repository: `SignalSlayerDecisionRepository`
   - Non-blocking inserts (fire-and-forget)
   - Snima sve decisions sa full context (price, ATR, spread, activity, regime, slopes, run_env)

4. **Replay harness** ✅
   - `SignalSlayerReplayHarness` klasa
   - Determinističko testiranje
   - Summary statistics (accept/reject counts, top rejection reasons)

**DONE kad:**
- ✅ možeš da rangiraš "Top 5 razloga reject-a" (Prometheus + DB)
- ✅ vidiš trend kroz vreme (DB queries)
- ✅ testiraš deterministički (replay harness)

---

## 2. "V1.8.5" – SWING BASELINE (REAL readiness, 4/4 završeno)

Četiri glavne stavke za SWING baseline:

1) Ispraviti semantiku auto_exit + exit_reason
   - SL/TP ≠ AUTO-EXIT, jasno razdvojiti.
2) Ukinuti duple fill/journal/pnl upise
   - idempotentni handler za real fills + eventualni UNIQUE key u DB.
3) Dovršiti auto-exit engine (time + weekend)
   - selektor kandidata, CancelAllExitsForSymbol, SendExit override, MarkClosed sa auto_exit=true.
4) Zaštititi External/IBKR pozicije od bilo kakvog automatizovanog exita
   - filtar po strategiji / flagu, i sanity check oko toga.

**Sve 4 stavke su sada implementirane u kodu, u statusu "DONE, WAITS FOR TEST".**

### 2.1 Semantika exit_reason + auto_exit (razlikovanje TP/SL/Auto/Manual)

Funkcija: SwingHelpers.InferSwingExitReason(OrderRequest req)

Mapiranje po correlationId:

- OCO TP / SL – normalni exits (autoExit=false)
  - "exit-tp-*" → TakeProfit, autoExit = false
  - "exit-sl-*" → StopLoss,  autoExit = false

- SWING auto-exit (REAL + AutoExitReal=true)
  - "exit-swing-max-weekend-*" → SwingMaxDays, autoExit = true (dominantan razlog: max days)
  - "exit-swing-max-*"         → SwingMaxDays, autoExit = true
  - "exit-swing-weekend-*"     → SwingWeekend, autoExit = true
  - "exit-swing-*"             → SwingMaxDays, autoExit = true (fallback)

- Ostali exit-* (manual / other)
  - "exit-*" (sve ostalo)      → Manual, autoExit = false

Ako uopšte nije exit-* (za close ne bi trebalo da se desi) → (null, false).

**Efekat:**
- TP/SL se jasno knjiže kao TakeProfit/StopLoss (auto_exit=false).
- Auto-exit (max-days / weekend) ide sa auto_exit=true i odgovarajućim ExitReason.
- Manual/ostali exit-i su uredno odvojeni.

### 2.2 REAL OCO nakon BUY fill-a (TP/SL, nije auto-exit)

U ApplyFillCore, posle BUY fill-a:

- Kad prelaziš iz stanja:
  - prevQty <= 0
  - newQty > 0
- Engine:
  - kreira TP limit Sell Order:  correlationId = "exit-tp-*"
  - kreira SL stop Sell Order:   correlationId = "exit-sl-*"
  - dodeljuje isti OCO/OCA group id za oba naloga.

TIF:
- Swing mode → GTC
- Intraday mode → DAY

DB:
- oba naloga se upisuju u broker_orders INSERT SUBMITTED (best-effort)
- pending:
  - oba se ubacuju u _orders pending store radi TTL/cancel/recovery.

**Napomena:**
- Ovo su "primary exits" i NISU auto-exit, semantika je čista na nivou DB + kod.

### 2.3 SWING auto-exit engine (time + weekend)

Radi samo ako:
- _orderService != null   (REAL)
- _swingConfig != null
- _swingConfig.Mode == Swing
- _swingConfig.AutoExitReal == true

EvaluateSwingAutoExits(nowUtc):

- snapshot lokalnih pozicija: _positionBook.Snapshot()
- filtrira live pozicije: qty != 0
- za svaku poziciju proverava:
  - External/IBKR flag (IsExternalSwingSymbol) → ako je true, preskače (ne dira external).
  - da li je vreme za:
    - AutoExitOnMaxHoldingDays + MaxHoldingDays
    - AutoExitBeforeWeekend + CloseBeforeWeekend cutoff logika

Kada odluči da zatvara:
1) CancelAllExitsForSymbol(sym)
   - čisti sve postojeće exit naloge (TP/SL/OCO/time exit) za simbol:
     - lokalni pending (PendingOrder sa IsExit == true ili corrId "exit-*")
     - ako postoji brokerOrderId → šalje cancel ka brokeru (rate-limited)
     - unreserve fee, čisti cumFilled tracking
     - uklanja iz _exitPending

2) SendExit(sym, qty, refPx, reason, corrPrefix: "exit-swing-max-" / "exit-swing-weekend-"/"exit-swing-max-weekend-")
   - kreira OrderRequest sa:
     - isExit = true
     - korektan correlationId prefix:
       - exit-swing-max-*
       - exit-swing-weekend-*
       - exit-swing-max-weekend-*
   - rezerviše fee (best-effort)
   - beleži u _exitPending
   - šalje u PAPER ili REAL (_orderService / _paperSim)

Time se obezbeđuje:
- da OCO TP/SL ne ostanu visiti kad auto-exit "overriduje" poziciju.
- da MarkClosed u SwingPositionRepository dobije tačan exitReason + autoExit.

### 2.4 Zaštita External/IBKR pozicija (nema auto-exit)

**Cilj:**
- pozicije koje su došle iz IBKR / eksternog sveta ne smeju biti "ubijene" od engine-a.

SyncExternalPosition(symbol, quantity, averagePrice):

- poziva se prilikom sync-a IBKR → lokalni PositionBook + Swing DB.
- radi:
  1) PositionBook.Override(quantity, averagePrice)
  2) runtime state (_posRuntime) update za monitoring (EntryUtc = now, EntryPrice = avg)
  3) Swing DB sync:
     - quantity > 0:
       - UpsertOpenExternalAsync(SwingPositionSnapshot):
         - Strategy = "External/IBKR"
         - ExitPolicy = PriceOrTime
         - otvorena pozicija u swing_positions (is_open = true)
         - opened_utc NE resetuje ako već postoji open red.
     - quantity == 0:
       - MarkClosedAsync(symbol, closedUtc=now, exitReason=ExternalSync, autoExit=false)

SwingPositionRepository.UpsertOpenExternalAsync:
- koristi on conflict (symbol) do update sa:
  - quantity, entry_price
  - opened_utc / strategy / correlation_id NE resetuju ako je is_open = true
  - exit_policy, planned_holding_days refresh

IsExternalSwingSymbol(symbol):

- čita open swing_positions za symbol (GetOpenBySymbol ili ekvivalent), i gleda:
  - strategy STARTS WITH "External/" (npr. "External/IBKR")
- EvaluateSwingAutoExits preskače simbol ako IsExternalSwingSymbol == true.

**Efekat:**
- auto-exit engine NE dirne External/IBKR pozicije.
- zatvaranje eksternih pozicija ide isključivo preko IBKR / manuelno / sync, sa exitReason=ExternalSync.

### 2.5 Recovery / pending disciplina (REAL)

RecoverOnStartupAsync:
- vraća open orders iz DB (broker_orders) u lokalni _orders.
- ako correlationId počinje sa "exit-":
  - vraća i u _exitPending.

StartPendingExpiryWatcher:
- EXIT nalozi:
  - ako imaju brokerOrderId → ne TTL-cancel (broker je izvor istine).
  - ako NEMA brokerOrderId → čisti brzo (da ne ostane _exitPending zaglavljen).
- ENTRY nalozi:
  - standard TTL + cancel request ka brokeru.

### 2.6 Application Restart Behavior – šta se čuva vs resetuje

**Status:** ⚠️ **TRENUTNO STANJE** (2026-01-10)

#### Šta se ČUVA u bazi i učitava pri restartu:

1. **Open Swing Positions** ✅
   - Svi zapisi iz `swing_positions` sa `is_open=true`
   - Učitava se pri startup-u preko `externalPositions.GetOpenPositionsAsync()` (IBKR) ili direktno iz baze (crypto)
   - Entry prices, quantity, strategy, correlation_id, opened_utc – sve je očuvano

2. **Open Broker Orders** ✅
   - `RecoverOnStartupAsync()` učitava sve open orders iz `broker_orders` tabele
   - Učitava samo **ENTRY orders** (ne EXIT TP/SL jer se oni prave dinamički)
   - Status, limit/stop prices, brokerOrderId – sve je očuvano
   - Restore-ovani orderi se NE broje ponovo u DayGuards (već su se brojali kada su prvi put postavljeni)

3. **Trade History** ✅
   - `trades_signals`, `trades_fills`, `trades_journal`, `broker_orders`
   - `daily_pnl` – dnevni PnL po danu
   - `signal_slayer_decisions` – sve SignalSlayer odluke
   - Svi trade-ovi su trajno očuvani u bazi

4. **Cash State (refresh)** ✅
   - Za IBKR: čita se iz IBKR account snapshot-a pri startupu (live izvor)
   - Za Crypto: čita se iz `appsettings.crypto.{exchange}.json` (`StartingCashUsd`)
   - **Nije persistent u bazi** (refreshuje se iz live source-a pri restartu)

#### Šta se RESETUJE (memory-only state):

1. **DayGuards** ✅ **SADA SE RESTORIRA IZ BAZE PRI RESTARTU** (2026-01-12)
   ```csharp
   // DayGuards je IN-MEMORY, ali se restorira iz baze pri startupu
   private int _tradesTotal = 0;  // ← Restorira se iz trade_signals
   private Dictionary<string, int> _tradesPerSymbol = new();  // ← Restorira se iz trade_signals
   private decimal _realizedPnlUsd = 0m;  // ← Restorira se iz daily_pnl
   ```
   **Implementacija:**
   - `DayGuards.RestoreState()` – metoda za restauraciju stanja
   - `RecoverOnStartupAsync()` učitava:
     - Trade counts po simbolu iz `trade_signals` (samo `accepted=true` i `side='Buy'`)
     - Ukupan broj tradeova za današnji dan (COUNT DISTINCT correlation_id)
     - Realizovani PnL iz `daily_pnl` tabele
   - Helper metode u repozitorijumima:
     - `TradeSignalRepository.GetTodayTradeCountsPerSymbolAsync()`
     - `TradeSignalRepository.GetTodayTradeCountTotalAsync()`
     - `DailyPnlRepository.GetTodayRealizedPnlAsync()`
   
   **Efekat:** DayGuards sada pravilno učitava stanje iz baze pri restartu, pa se ne mogu prekoračiti dnevni limiti.

2. **Position Book (memory)** ✅
   - `_positionBook` se učitava iz external positions pri startupu (IBKR)
   - **Problem (crypto):** Crypto app trenutno **nema recovery logiku** za pozicije – ako restartuješ crypto app, open positions se gube iz memory-a (ali su i dalje u bazi)

3. **Pending Orders (memory)** ✅
   - `_orders` se učitava iz `broker_orders` tabele pri startupu
   - Samo ENTRY orders (EXIT orders se prave dinamički)

#### IBKR App Recovery Flow:

1. Učitava open positions iz IBKR-a → syncuje sa `swing_positions` ✅
2. Učitava open orders iz `broker_orders` → vraća u `_orders` ✅
3. Refreshuje cash iz IBKR account snapshot-a ✅
4. **DayGuards se restorira iz baze** ✅ (učitava trade counts i PnL za današnji dan)

#### Crypto App Recovery Flow:

1. Učitava cash iz config-a (`StartingCashUsd`) ✅
2. **Ne učitava open positions iz baze** ⚠️ (nema recovery logiku)
3. **DayGuards se restorira iz baze** ✅ (učitava trade counts i PnL za današnji dan)

#### Problemi koje treba popraviti:

1. ~~**DayGuards ne čuva state**~~ ✅ **REŠENO** (2026-01-12)
   - ✅ Učitava trade counts i PnL za današnji dan pri startupu
   - ✅ Ne može da prekorači dnevne limite posle restart-a
   - ✅ Implementirano: `DayGuards.RestoreState()` + recovery logika u `RecoverOnStartupAsync()`

2. **Crypto positions se ne učitavaju pri restartu** ⚠️
   - Ako restartuješ crypto app, open positions se gube iz memory-a (ali su i dalje u bazi sa `is_open=true`)
   - **Rešenje:** Dodati recovery logiku u `CryptoTradingOrchestrator.RecoverOnStartupAsync()` da učitava open positions iz `swing_positions` tabele

#### Preporuka:

Za sada:
- ~~**DayGuards reset**~~ ✅ **REŠENO** (2026-01-12) – Sada se restorira iz baze
- **Crypto pozicije** moraš pratiti iz baze (`swing_positions` sa `is_open=true`)

Kada bi trebalo dodati:
- ~~**DayGuards recovery**~~ ✅ **IMPLEMENTIRANO** (2026-01-12) – Učitavanje broja trade-ova iz `trade_signals` za današnji dan pri startupu
- **Crypto positions recovery:** Učitavanje open positions iz `swing_positions` pri startupu u `CryptoTradingOrchestrator`

---

## 3. PAPER vs REAL – razlika u exit engine-u

**PAPER:**
- EvaluatePaperExitsOnQuote:
  - TP
  - SL
  - trailing
  - time exit
- paper simulator popunjava LIMIT-e.

**REAL:**
- oslanja se na:
  - OCO TP/SL kao primarni exits
  - swing auto-exit override (max-hold / weekend)
- REAL trailing (managed SL sa cancel/replace) je planiran za V2 (nije još implementiran).

---

## 4. Test Plan – LIVE VALIDATION (kritično)

**Cilj:**
- ništa novo ne dirati dok se ne potvrdi da lifecycle radi korektno u REAL svetu.

**Fokus testiranja:**
- fillovi (full/partial)
- slippage
- commissions
- stabilnost pending/recovery sloja
- ponašanje SignalSlayer-a na real feed-u
- OCO kreiranje + ponašanje kad auto-exit override-uje
- performance & latencija
- Swing auto-exit: da ne duplira exits i da Swing DB dobija korektan exitReason/autoExit.

**Plan:**
- prvo manji LIVE test sa ograničenim brojem simbola i malim size-om.
- posle toga, ako logovi deluju zdravo → postepeno dizanje AutoExitReal i pun SWING režim.

---

## 5. Progres tabela (osvežena)

| Stavka                                | Progres | Status |
|---------------------------------------|--------:|--------|
| 1. Time-of-day profiling              | **100%** ✅ | ✅ **COMPLETE** |
| 2. SignalSlayer 2.0                   | **100%** ✅ | ✅ **COMPLETE** |
| 3. ATR sizing (polish)                | **100%** ✅ | ✅ **COMPLETE** |
| 4. PA micro-pullback                  | **100%** ✅ | ✅ **COMPLETE** |
| 5. Entry micro-filter                 | **100%** ✅ | ✅ **COMPLETE** |
| 6. Dynamic trailing (PAPER)          | **100%**  ✅ | ✅ **COMPLETE** |
| 7. Dynamic trailing (REAL managed SL) |    0%   | ⏳ Planned |
| 8. Tick profiler                      | **100%** ✅ | ✅ **COMPLETE** |
| 9. Hold-time model (baseline)         | **100%** ✅ | ✅ **COMPLETE** |
| 10. Multi-day swing (full)            | **100%** ✅ | ✅ **COMPLETE** |
| 11. OCO + auto-exit + recovery        | **100%**    | ✅  **COMPLETE** |
| 12. Market vs Limit za exit ordere    |    0%   | 💡 Idea/Planned |

---

## 6. Šta znači "100%" za otvorene stavke

### 1) Time-of-day profiling ✅ **100% COMPLETE**

**Status:** ✅ **ZAVRŠENO** (2025-12-30)

**Cilj:**
- Jedna istina o performansama i ponašanju po fazama dana.

**Implementirano:**

1. **TradingPhase helper klasa** ✅
   - Određuje fazu dana (NY vreme): `preRTH`, `open_1h`, `midday`, `power_hour`, `close`, `afterhours`, `off_hours`
   - Helper metode: `GetPhase()`, `ToString()`, `Parse()`, `GetDescription()`

2. **Phase tagging u DB** ✅
   - Dodata `trading_phase` kolona u `trade_signals` i `signal_slayer_decisions`
   - Auto-detekcija faze u repository-ju (ako nije eksplicitno prosleđena)
   - Indexi za efikasno query-ovanje po fazi

3. **Per-phase Prometheus metrike** ✅
   - `strategy_signals_generated_by_phase_total` - signals generated po fazi
   - `strategy_signals_accepted_by_phase_total` - signals accepted po fazi
   - `strategy_signals_blocked_by_phase_total` - signals blocked po fazi i razlogu
   - `strategy_signal_slayer_rejected_by_phase_total` - SignalSlayer rejections po fazi i reason code

4. **Phase-aware metrike integracija** ✅
   - TradingOrchestrator koristi phase-aware metrike za blocked signals
   - PullbackInUptrendStrategy koristi phase-aware metrike za generated/accepted signals
   - SignalSlayer koristi phase-aware metrike za rejections

5. **Daily report generator** ✅
   - `DailyReportRepository` za generisanje report-a po fazama
   - Statistika: signals po fazama, top blockers, SignalSlayer rejections
   - SQL queries za analizu performansi po fazama

**DONE kad:**
- ✅ možeš da kažeš: "najviše me ubija spread-too-wide u power_hour" (Prometheus + DB queries)
- ✅ vidiš statistiku po fazama (daily report generator)
- ✅ svi signali i decisions su tagovani sa trading_phase u DB

### 2) SignalSlayer 2.0 ✅ **100% COMPLETE**

**Status:** ✅ **ZAVRŠENO** (2025-12-26)

**Implementirano:**
- ✅ Stabilni reason codes (ATR_TOO_LOW, SPREAD_TOO_WIDE, etc.)
- ✅ Prometheus metrics counter po reason-u
- ✅ Database persistence (signal_slayer_decisions tabela)
- ✅ Replay harness za determinističko testiranje

**DONE kad:**
- ✅ možeš da rangiraš "Top 5 razloga reject-a" (Prometheus + DB)
- ✅ vidiš trend kroz vreme (DB queries)
- ✅ testiraš deterministički (replay harness)

### 4) PA micro-pullback ✅ **100% COMPLETE**

**Status:** ✅ **ZAVRŠENO** (2025-12-30)

**Cilj:**
- PB state machine koji ne osciluje i nema tihe regresije.

**Implementirano:**

1. **Freeze state** ✅
   - Od momenta kad PB počne, regime/slope snapshot se ne menja
   - Zamrznuti: `RegimeAtStart`, `Slope5AtStart`, `Slope20AtStart`, `AtrAtStart`, `AtrFractionAtStart`
   - Zamrznute vrednosti se koriste u SignalSlayer umesto live vrednosti

2. **Edge-case guards** ✅
   - **Gap tick detection**: abort ako cena skoči > 3x ATR bez kontinuiteta
   - **Double PB guard**: abort tracking kad trend pukne tokom PB-a
   - **Thin liquidity guard**: abort ako spread postane > 2x threshold tokom PB-a

3. **Offline evaluacija** ✅
   - Prometheus metrike:
     - `strategy_pullback_detected_total` - koliko PB-a je detektovano
     - `strategy_pullback_aborted_total` - koliko je abortovano (sa razlogom: gap_tick, thin_liquidity, trend_broken, too_long, signal_slayer_rejected)
     - `strategy_pullback_valid_breakout_total` - koliko je završilo u valid breakout
   - Abort tracking sa razlogom u `SymbolState` (`WasAborted`, `AbortReason`)

**DONE kad:**
- ✅ imaš pouzdan PB state machine sa frozen snapshot-om
- ✅ znaš statistiku: detect → abort → valid breakout (Prometheus metrics)

### 6) Dynamic trailing (PAPER) ✅ **100% COMPLETE**

**Status:** ✅ **ZAVRŠENO** (2026-01-02)

**Cilj:**
- PAPER trailing kao verna simulacija realnog ponašanja.

**Implementirano:**

1. **Trailing state tracking** ✅
   - Dodata polja u `PositionRuntimeState`: `TrailingArmed`, `LastTrailUpdateUtc`, `LastTrailStop`
   - Praćenje stanja trailing stop-a (armed, update, fire)

2. **TRAIL-ARMED log** ✅
   - Loguje se kada se trailing prvi put aktivira (bestPrice >= activationPrice)
   - Prikazuje entry, best, activation price, i ATR/metodu

3. **TRAIL-UPDATE log sa rate-limit-om** ✅
   - Loguje se kada se trail stop pomera naviše
   - Rate-limit: max 1 update po sekundi (da liči na real)
   - Prikazuje best, stop, entry, i ATR/metodu

4. **TRAIL-FIRE log** ✅
   - Loguje se kada se trailing exit izvršava
   - Prikazuje best, stop, now, limit, entry, i ATR/metodu

5. **Politika fill-a za trailing exit** ✅
   - Agresivni limit za trailing exit: koristi `bid - 0.01` (1 tick slippage) ako je bid dostupan
   - Fallback na `refPx` ako nema bid
   - Limit order se kreira sa agresivnijim limit-om za brži fill

6. **Prometheus metrike** ✅
   - `strategy_trailing_armed_total` - broj puta kada je trailing aktiviran
   - `strategy_trailing_update_total` - broj puta kada je trailing ažuriran
   - `strategy_trailing_fire_total` - broj puta kada je trailing exit izvršen
   - `strategy_trailing_pnl_percent` - P&L % distribucija posle trailing exit-a
   - Sve metrike labelovane sa `symbol` i `method` (ATR/PCT)

**DONE kad:**
- ✅ znaš koliko trailing "spašava" vs "seče prerano" u PAPER statistici (Prometheus metrike)
- ✅ nema phantom fill-ova (agresivni limit sa slippage-om)
- ✅ jasni logovi za armed/update/fire sa svim detaljima
- ✅ rate-limit za trailing update-e (max 1 po sekundi)

### 8) Tick profiler ✅ **100% COMPLETE**

**Status:** ✅ **ZAVRŠENO** (2026-01-02)

**Cilj:**
- imati "truth" po simbolu: koliko je živ, koliko je noisy.

**Implementirano:**

1. **TickProfiler klasa** ✅
   - Praćenje statistike po simbolu i fazi dana (TradingPhase)
   - Rolling window za tick rate (5-minutni prozori)
   - Distribucije za quote age, spread bps, atrFrac
   - Percentilne metrike (p50, p95) za sve distribucije
   - Auto-suggestion logika za MinTicksPerWindow i MaxSpreadBps
   - Preporuka za disable symbol u fazi (ako je likvidnost vrlo loša)

2. **TickProfilerMetrics (Prometheus)** ✅
   - Histogrami: `tick_profiler_quote_age_seconds`, `tick_profiler_spread_bps`, `tick_profiler_atr_frac`
   - Gauge metrike: `tick_profiler_ticks_per_second_p50`, `tick_profiler_ticks_per_second_p95`
   - Sve metrike labelovane sa `symbol` i `phase`

3. **Integracija u TradingOrchestrator** ✅
   - Handler `OnQuoteForTickProfiler` koji automatski snima svaki quote
   - Automatsko računanje quote age, spread bps, atrFrac
   - Integracija sa TradingPhase helper-om za fazu dana

4. **Auto-suggestion logika** ✅
   - Predlog za `MinTicksPerWindow` na osnovu tick rate-a (p50/p95)
   - Predlog za `MaxSpreadBps` na osnovu spread distribucije (p50/p95)
   - Preporuka za disable symbol u fazi (ako je tick rate < 0.05/s, spread > 50bps, quote age > 10s)

**DONE kad:**
- ✅ možeš automatski da kažeš: "SBUX je loš u open zbog spread/ticks, ali posle 16:00 ok"
- ✅ vidiš percentilne metrike (p50/p95) za ticks/sec, spread, quote age, atrFrac po simbolu i fazi
- ✅ dobijaš auto-suggestions za MinTicksPerWindow i MaxSpreadBps
- ✅ sve metrike su dostupne u Prometheus-u za monitoring i alerting

### 9) Hold-time model (baseline) ✅ **100% COMPLETE**

**Status:** ✅ **ZAVRŠENO** (2025-12-30)

**Cilj:**
- hold-time da prestane da bude one-size-fits-all.

**Implementirano:**

1. **PositionRuntimeState proširenje** ✅
   - Dodata polja: `RegimeAtEntry` (LOW/NORMAL/HIGH), `SymbolBaseline` (slow/normal/fast), `AtrAtEntry`
   - Zamrznuti snapshot pri entry-ju (ne menja se tokom hold-a)

2. **Per-regime hold policy** ✅
   - LOW regime → 25% kraći max hold (agresivniji exit)
   - HIGH regime → 25% duži hold
   - NORMAL regime → bez promene
   - Swing mode: 20% adjustment (LOW → 0.8x, HIGH → 1.2x)
   - Intraday mode: 25% adjustment (LOW → 0.75x, HIGH → 1.25x)

3. **Per-symbol baseline** ✅
   - slow symbols → 15-20% duži hold
   - fast symbols → 15-20% kraći hold
   - normal symbols → bez promene
   - Baseline određen na osnovu ATR fraction pri entry-ju:
     - slow: ATR fraction < 0.0002
     - normal: 0.0002 ≤ ATR fraction ≤ 0.0005
     - fast: ATR fraction > 0.0005

4. **Outcome logging** ✅
   - Prometheus metrike:
     - `strategy_time_exit_occurred_total` - broj time-exit-ova po mode/regime/baseline
     - `strategy_time_exit_pnl_percent` - P&L % distribucija posle time-exit-a
   - Logovi sa detaljima: regime, baseline, adjusted hold time, P&L

**DONE kad:**
- ✅ hold-time deluje sistematski (regime/symbol aware), a ne nasumično
- ✅ znaš statistiku: koliko time-exit-ova po regime-u/baseline-u i P&L distribuciju

### 10) Multi-day swing (full) ✅ **100% COMPLETE**

**Status:** ✅ **ZAVRŠENO** (2026-01-02)

---

### 12) Market vs Limit za exit ordere 💡 **IDEA/PLANNED**

**Status:** 💡 **IDEA** (2026-01-02)

**Cilj:**
- Omogućiti izbor između Market i Limit order tipa za exit ordere
- Optimizovati fill brzinu vs cenu zavisno od tipa exit-a

**Trenutno stanje:**
- `SendExit()` uvek koristi `OrderType.Limit` (linija 3207 u TradingOrchestrator.cs)
- Entry order može biti Limit ili Market (zavisi od strategije)
- Exit order je uvek Limit

**Predlog implementacije:**

1. **Dodati parametar u `SendExit()`:**
   ```csharp
   private void SendExit(string symbol, decimal qty, decimal px, string reason, 
                        string? corrPrefix = null, bool useMarket = false)
   ```

2. **Logika za izbor Market vs Limit:**
   - **Trailing exit** → Market (brži fill, cena je već "loša" jer je pala ispod trailing stop-a)
   - **TP exit** → Limit (bolja cena, nema hitnosti)
   - **SL exit** → Limit (bolja cena, nema hitnosti)
   - **Time exit** → Market (brži fill, hitno je)
   - **Gap protection exit** → Market (brži fill, hitno je)

3. **Konfiguracija:**
   - Dodati u `appsettings.json` ili `pullback-config.json`:
     ```json
     "ExitOrderType": {
       "TrailingExit": "Market",
       "TakeProfitExit": "Limit",
       "StopLossExit": "Limit",
       "TimeExit": "Market",
       "GapExit": "Market"
     }
     ```

4. **Implementacija:**
   - U `SendExit()`, proveriti `reason` string ili dodati enum za exit tip
   - Ako je `useMarket = true` ili exit tip zahteva Market → `OrderType.Market`
   - Inače → `OrderType.Limit` (trenutno ponašanje)

**Prednosti:**
- ✅ Brži fill za hitne exit-e (trailing, time, gap)
- ✅ Bolja cena za TP/SL exit-e (Limit order)
- ✅ Fleksibilnost - možemo optimizovati po tipu exit-a

**Nedostaci:**
- ⚠️ Market order može imati veći slippage
- ⚠️ Potrebno je pažljivo testirati za svaki tip exit-a

**DONE kad:**
- ✅ `SendExit()` podržava Market i Limit order tip
- ✅ Konfiguracija omogućava izbor po tipu exit-a
- ✅ Testirano u paper trading modu
- ✅ Prometheus metrike za Market vs Limit exit performanse

---

### 10) Multi-day swing (full) ✅ **100% COMPLETE**

**Status:** ✅ **ZAVRŠENO** (2026-01-02)

**Cilj:**
- Kompletna podrška za multi-day swing trading sa gap protection-om i automatskim exit-om.

**Implementirano:**

1. **Gap protection sa auto-exit-om** ✅
   - Automatski exit kada je overnight gap loss > MaxOvernightGapLossPct
   - Proverava se u heartbeat-u za pozicije starije od 1 dana
   - Loguje se sa detaljima: loss %, age, entry, now, maxLoss, P&L
   - GapExitExecuted flag sprečava dupliranje exit-a

2. **Gap detection na open-u** ✅
   - Detektuje gap između prethodnog close-a i trenutnog open-a
   - Proverava se u Open1H fazi (prvi sat trgovanja)
   - LastClosePrice se čuva u close fazi za gap detection sledeći dan
   - Automatski exit ako je gap loss > MaxOvernightGapLossPct

3. **PositionRuntimeState proširenje** ✅
   - Dodata polja: `LastClosePrice`, `LastCloseUtc`, `GapExitExecuted`
   - Praćenje gap-a kroz više dana

4. **Prometheus metrike** ✅
   - `strategy_gap_exit_executed_total` - broj puta kada je gap exit izvršen
   - `strategy_gap_detected_on_open_total` - broj puta kada je gap detektovan na open-u
   - `strategy_gap_exit_pnl_percent` - P&L % distribucija posle gap exit-a
   - Sve metrike labelovane sa `symbol`

**DONE kad:**
- ✅ gap protection automatski izvršava exit kada je gap loss prekoračen
- ✅ gap detection na open-u radi za multi-day pozicije
- ✅ Prometheus metrike prate gap statistiku
- ✅ jasni logovi za gap exit sa svim detaljima

### 3) ATR sizing ✅ **100% COMPLETE** (polish završen)

**Status:** ✅ **ZAVRŠENO** (2025-12-26)

**Implementirano:**
- ✅ Izbacen hardcoded minAtrFrac (0.002) → sada u RiskLimits config
- ✅ Dodato `MinAtrFraction` u RiskLimits (default 0.002 = 0.2%)
- ✅ Dodate kolone u `trade_journal`: `risk_fraction`, `atr_used`, `price_risk`
- ✅ Proširen `TradeJournalEntry` sa novim poljima
- ✅ ATR sizing metadata se sada upisuje u DB za svaki trade

**DONE kad:**
- ✅ minAtrFrac je u config-u (može se menjati po simbolu ili globalno)
- ✅ riskFraction, atrUsed, priceRisk se upisuju u DB za analizu

---

## SAŽETAK

- ✅ **V1.8 je stabilan u PAPER modu**, sa dobrim guard-ovima i logging-om.
- ✅ **SWING baseline** (OCO + auto-exit + External/IBKR zaštita + recovery) je implementiran (4/4 taskova).
- ✅ **SignalSlayer 2.0 je 100% završen** sa DB persistence, Prometheus metrics, i replay harness.
- ✅ **Tick profiler je 100% završen** sa per-symbol/per-phase tracking-om, Prometheus metrikama, i auto-suggestion logikom.
- ✅ **Dynamic trailing (PAPER) je 100% završen** sa trailing state tracking-om, jasnim logovima (ARMED/UPDATE/FIRE), rate-limit-om, agresivnom fill politikom, i Prometheus metrikama.
- ✅ **Multi-day swing (full) je 100% završen** sa gap protection-om, gap detection-om na open-u, automatskim exit-om, i Prometheus metrikama.
- ✅ **US market calendar (NYSE/NASDAQ)** – praznici i early close u TradingSessionGuard; orchestrator koristi `IsInsideUsRth` za RTH prozor.
- **Sledeći korak:** LIVE VALIDATION u REAL modu (mali size, limitirani simboli).
- **Posle potvrde:**
  - REAL managed trailing (V2 feature)
  - profiling/reporting sloj za brže iteracije (time-of-day ✅, slayer reasons ✅, tick profiler ✅, hold-time model ✅, trailing stats ✅).

---

### 13) Macro Trend Gate (IBKR + Crypto) ✅ **100% COMPLETE**

**Status:** ✅ **ZAVRŠENO** (2026-02-16)

**Cilj:**
- dodati globalni trend filter koji blokira BUY ulaze samo kada je širi trend `Down`
- raditi jedinstveno za IBKR i Crypto, sa odvojenim config kontrolama po marketu

**Implementirano:**

1. **Unified trend data sloj** ✅
   - `TrendMarketDataPoint` model (poklapa `market_ticks` + `crypto_trades`)
   - `TrendMarketDataRepository` sa:
     - `GetIbkrMarketTicksByWindowAsync`
     - `GetIbkrMarketTicksByRangeAsync`
     - `GetCryptoTradesByWindowAsync`
     - `GetCryptoTradesByRangeAsync`

2. **Trend core u Strategy namespace** ✅
   - `TrendDirection`, `TrendContext`, `ITrendContextProvider`
   - `TrendAnalysisSettings` (feature flag + window + explicit range + quality parametri)
   - provideri:
     - `IbkrTrendContextProvider`
     - `CryptoTrendContextProvider`

3. **Trend quality scoring (blagi režim)** ✅
   - score = endpoint + slope - drawdown penalty
   - quality parametri su u configu (`Trading` sekcija):
     - `TrendUseQualityScoring`
     - `TrendMinPoints`
     - `TrendNeutralThresholdFraction`
     - `TrendEndpointWeight`
     - `TrendSlopeWeight`
     - `TrendDrawdownPenaltyWeight`
     - `TrendMaxDrawdownClampFraction`

4. **Config po marketu (bez diranja pullback-config.json)** ✅
   - IBKR (`appsettings.json`):
     - `EnableTrendAnalysis`
     - `TrendTimeWindowMinutes`
     - `TrendUseExplicitRange`
     - `TrendRangeStartLocal`
     - `TrendRangeEndLocal`
     - `TrendRangeTimeZone`
   - Crypto (`appsettings.crypto.*.json`):
     - `EnableTrendAnalysis`
     - `TrendTimeWindowMinutes`
     - + quality parametri

5. **Integracija u signal tok (orchestrator nivo)** ✅
   - trend gate dodan pre risk/order faze:
     - `TradingOrchestrator` (IBKR)
     - `CryptoTradingOrchestrator` (Crypto)
   - pravilo:
     - `Down` → block
     - `Up` / `Neutral` / `Unknown` → allow (fail-open)

6. **Reject logging (jedna tabela istine)** ✅
   - trend reject NE ide u `signal_slayer_decisions`
   - trend reject ide u `trade_signals.reject_reason`:
     - `macro-trend-block:down:score=...`

**DONE kad:**
- ✅ trend filter radi na oba marketa
- ✅ config je po marketu
- ✅ reject razlog je vidljiv u `trade_signals`
- ✅ build prolazi za App + Strategy + Crypto

## Changelog

### 2026-02-16
- ✅ Macro Trend Gate (IBKR + Crypto) completed to 100%
  - Added unified trend data model/repository (`market_ticks` + `crypto_trades`)
  - Added Strategy Trend core (`TrendDirection`, `TrendContext`, `ITrendContextProvider`, providers, settings)
  - Added quality scoring (endpoint + slope - drawdown penalty)
  - Added per-market config keys in `Trading` sections (IBKR + all crypto appsettings)
  - Integrated trend gate in orchestrators (block only on `Down`)
  - Trend reject is logged only to `trade_signals.reject_reason` as `macro-trend-block:down:score=...`

### 2026-01-27
- ✅ US market calendar (NYSE/NASDAQ) – praznici + early close
  - **TradingSessionGuard** proširen: `IsUsMarketHoliday(dateEt)`, `GetUsSessionEndTimeEt(dateEt)`, `IsInsideUsRth(utcNow)`
  - Praznici (berza zatvorena): New Year, MLK Jr, Presidents Day, Good Friday, Memorial Day, Juneteenth, Independence Day, Labor Day, Thanksgiving, Christmas (+ observed kada padne vikend)
  - Early close (13:00 ET): dan posle Thanksgiving, Christmas Eve (24. dec)
  - Orchestrator koristi `TradingSessionGuard.IsInsideUsRth(now)` umesto lokalne RTH logike; uklonjena hardkodirana Dec 25 provera iz orchestratora
  - Izvori: [NYSE Holidays & Trading Hours](https://www.nyse.com/trade/hours-calendars), [NASDAQ Stock Market Holiday Schedule](https://www.nasdaq.com/market-activity/stock-market-holiday-schedule)

### 2026-01-02
- ✅ Tick profiler completed to 100%
  - TickProfiler klasa sa per-symbol/per-phase tracking-om (ticks/sec, quote age, spread, atrFrac)
  - Rolling window za tick rate (5-minutni prozori) sa percentilnim metrikama (p50/p95)
  - Prometheus metrike: histogrami za quote age, spread bps, atrFrac; gauge metrike za tick rate percentilne
  - Auto-suggestion logika za MinTicksPerWindow i MaxSpreadBps na osnovu statistike
  - Preporuka za disable symbol u fazi (ako je likvidnost vrlo loša)
  - Integracija u TradingOrchestrator sa automatskim snimanjem svakog quote-a

- ✅ Dynamic trailing (PAPER) completed to 100%
  - Trailing state tracking u PositionRuntimeState (TrailingArmed, LastTrailUpdateUtc, LastTrailStop)
  - TRAIL-ARMED log kada se trailing aktivira (bestPrice >= activationPrice)
  - TRAIL-UPDATE log sa rate-limit-om (max 1 update po sekundi) kada se trail stop pomera naviše
  - TRAIL-FIRE log kada se trailing exit izvršava
  - Agresivni limit za trailing exit (bid - 0.01 tick slippage) za brži fill
  - Prometheus metrike: trailing_armed_total, trailing_update_total, trailing_fire_total, trailing_pnl_percent

- ✅ Multi-day swing (full) completed to 100%
  - Gap protection sa auto-exit-om kada je MaxOvernightGapLossPct prekoračen (u heartbeat-u)
  - Gap detection na open-u (proverava gap između prethodnog close-a i trenutnog open-a)
  - PositionRuntimeState proširenje (LastClosePrice, LastCloseUtc, GapExitExecuted)
  - Prometheus metrike: gap_exit_executed_total, gap_detected_on_open_total, gap_exit_pnl_percent

### 2026-01-12
- ✅ Distribution Protection (anti-manufactured-spike filters) - MVP implemented
  - Added Time-of-day hard rule (open trap detection)
  - Added DistributionProtectionConfig with Enabled/LogWhenDisabled flags
  - Added new reason codes: REJECTION_SPEED, OPEN_FAKE_BREAKOUT
  - Extended SignalSlayerContext with MovePctFromEntry and HasValidPullbackStructure
  - Config in appsettings.json: SignalSlayer:DistributionProtection
  - Logging: [DIST-PROT] tag with reason codes and parameters
  - Default: Enabled=false, LogWhenDisabled=true (for analysis phase)

### 2025-12-26
- ✅ SignalSlayer 2.0 completed to 100%
  - Added stable reason codes (SignalBlockReasonCode)
  - Added Prometheus metrics (strategy_signal_slayer_decision_total)
  - Added database persistence (signal_slayer_decisions table + repository)
  - Added replay harness for deterministic testing
  - All decisions now tracked in DB with full context

- ✅ ATR sizing polish completed
  - Removed hardcoded minAtrFrac (0.002) → now in RiskLimits config
  - Added MinAtrFraction to RiskLimits (default 0.002 = 0.2%)
  - Added columns to trade_journal: risk_fraction, atr_used, price_risk
  - Extended TradeJournalEntry with new fields
  - ATR sizing metadata now persisted to DB for analysis

- ✅ Entry micro-filter completed to 100%
  - Added per-symbol MicroSignalFilterConfig to pullback-config.json
  - Extended PullbackSymbolConfig with micro-filter settings
  - SignalSlayer now uses per-symbol micro-filter config (via provider function)
  - Micro-filter can be configured per symbol (Enabled, MinSlope20Bps, MinAtrFractionOfPrice, MaxSpreadBps, MinTicksPerWindow)
  - Defaults defined in pullback-config.json, per-symbol overrides optional

- ✅ PA micro-pullback completed to 100%
  - Freeze state: regime, slope5, slope20, ATR snapshot zamrznuti od momenta kad PB počne
  - Edge-case guards: gap tick detection (3x ATR threshold), double PB guard, thin liquidity guard
  - Offline evaluacija: Prometheus metrike za detect/abort/valid breakout statistiku
  - Abort tracking sa razlogom (gap_tick, thin_liquidity, trend_broken, too_long, signal_slayer_rejected)
  - Zamrznute vrednosti se koriste u SignalSlayer umesto live vrednosti

- ✅ Time-of-day profiling completed to 100%
  - TradingPhase helper klasa za određivanje faze dana (preRTH, open_1h, midday, power_hour, close, afterhours)
  - Phase tagging u DB: trading_phase kolone u trade_signals i signal_slayer_decisions
  - Per-phase Prometheus metrike: signals generated/accepted/blocked po fazi
  - Phase-aware metrike integracija u TradingOrchestrator, PullbackInUptrendStrategy, SignalSlayer
  - Daily report generator (DailyReportRepository) za analizu performansi po fazama

- ✅ Hold-time model completed to 100%
  - PositionRuntimeState proširenje: RegimeAtEntry, SymbolBaseline, AtrAtEntry (frozen snapshot)
  - Per-regime hold policy: LOW → kraći (25%), HIGH → duži (25%), NORMAL → bez promene
  - Per-symbol baseline: slow → duži (15-20%), fast → kraći (15-20%), normal → bez promene
  - Outcome logging: Prometheus metrike za time-exit count i P&L distribuciju po regime/baseline
  - Regime određen na osnovu ATR fraction pri entry-ju (LOW < 0.00015, NORMAL 0.00015-0.0005, HIGH > 0.0005)

- ✅ Tick profiler completed to 100%
  - TickProfiler klasa sa per-symbol/per-phase tracking-om (ticks/sec, quote age, spread, atrFrac)
  - Rolling window za tick rate (5-minutni prozori) sa percentilnim metrikama (p50/p95)
  - Prometheus metrike: histogrami za quote age, spread bps, atrFrac; gauge metrike za tick rate percentilne
  - Auto-suggestion logika za MinTicksPerWindow i MaxSpreadBps na osnovu statistike
  - Preporuka za disable symbol u fazi (ako je likvidnost vrlo loša)
  - Integracija u TradingOrchestrator sa automatskim snimanjem svakog quote-a

