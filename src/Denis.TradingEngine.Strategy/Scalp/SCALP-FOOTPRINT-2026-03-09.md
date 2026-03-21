# SCALP -- Design Footprint

Status: `draft / dry-run foundation implemented / live scalp not started`
Last updated: 2026-03-10

---

## Purpose

Scalp strategija treba da bude mikrostrukturna, brza i strogo kontrolisana
crypto strategija za kratke trade-ove u trenucima kada market pokazuje:

- tight spread
- dovoljnu likvidnost
- jak short-term momentum
- zdrav order book
- brz i cist exit kada setup oslabi

Cilj nije da "trguje stalno", nego da prepozna mali broj kvalitetnih
intraday/scalp prozora i da u ostalim uslovima ostane potpuno tih.

---

## Current State In Repo

Trenutno `Scalp` deo postoji samo kao pocetni proof-of-concept:

- postoji samo `ScalpStrategy.cs`
- entry logika je previse jednostavna
- order book se prosledjuje direktno konkretnoj klasi, ne kroz strategijski contract
- strategija lokalno vodi `InPosition`, ali execution layer vodi stvarnu poziciju
- exit signal trenutno nije modelovan kao pun first-class exit flow kroz orchestrator

Zakljucak:
trenutni kod je dobar kao skica ideje, ali nije jos dobra osnova za live scalp.

---

## Implementation Progress

Status preseka na dan `2026-03-10`.

### Current DryRun Calibration

Na osnovu prvih Bitfinex dry-run logova, inicijalni `AdaptiveScalpSelection`
thresholds su bili previsoki za realni feed i nisu davali ni jedan scalp regime switch.

Zato je uradjena prva kalibracija samo na nivou selector-a:

- `MinAtrFraction: 0.001 -> 0.00035`
- `MinTicksPerWindow: 100 -> 25`
- `MaxSpreadBps: 10.0 -> 8.0`

Bitno:

- menjan je samo `AdaptiveScalpSelection`
- `ScalpStrategy` pragovi nisu dirani
- cilj ove promene je da dry-run pocne da pokazuje kada bi selector uopste birao scalp regime
- ovo NIJE live tuning scalp entry logike

Posle toga je otkriven jos jedan bitan tehnicki problem:

- `ScalpStrategy` je za prvi entry koristila `BestPrice`, iako se `BestPrice`
  postavlja tek nakon ulaza
- to je znacilo da prvi scalp entry prakticno nije mogao da okine

Zato je uradjena korekcija:

- pre-entry momentum sada koristi `LastObservedPrice`
- `AdaptiveStrategySelector` sada loguje throttled `scalp-rejected` razlog
  sa svojim stvarnim internim `atr/ticks/spread` uslovima

Ovo je vazno jer sada mozemo da razlikujemo:

- selector nije izabrao scalp
- selector jeste spreman, ali strategija nije mogla da napravi prvi entry

Nakon dodatnog dry-run posmatranja pokazalo se da je glavni realni bloker
na Bitfinex feed-u i dalje `ticks`, pa je uradjena druga kalibracija:

- `MinAtrFraction: 0.00035 -> 0.00030`
- `MinTicksPerWindow: 25 -> 5`
- `MaxSpreadBps: 8.0` ostaje isto

Razlog:

- `ticks` je bio dominantan reject reason
- `BTCUSDT` i `SOLUSDT` su cesto bili blizu scalp rezima, ali su padali na activity gate
- cilj ove promene je da dry-run konacno proizvede stvarne `switching to SCALP`
  i `SCALP-DRYRUN` evente za najlikvidnije simbole

Kada je adaptive selector poceo stvarno da ulazi u `Scalp` regime, sledeci korak
je bio da se uvede dodatna observability unutar same `ScalpStrategy`.

Dodato je throttled `SCALP-BLOCKED` logovanje za glavne entry reject razloge:

- `missing-orderbook`
- `empty-book`
- `ticks`
- `spread`
- `liq`
- `missing-last`
- `missing-reference-price`
- `momentum<=0`

Time sada mozemo precizno da razlikujemo:

- selector nije prebacio simbol na scalp
- selector jeste prebacio simbol na scalp
- scalp strategy i dalje nije napravila `would-enter`, i znamo tacan razlog zasto

Posle toga je dry-run prosiren i na osnovne microstructure feature gate-ove,
bez uvodjenja live execution complexity:

- stale-book guard (`MaxBookAgeMs`)
- top-of-book imbalance gate (`MinImbalanceRatio`)
- microprice edge gate (`MinMicropriceEdgeBps`)
- minimalni momentum u bps (`MinMomentumBps`)
- average spread observability iz kratke istorije book-a

To znaci da dry-run sada nije vise samo:

- spread
- liquidity
- ticks
- prost price momentum

nego vec koristi siri skup scalp-signala i loguje ih u `SCALP-ENTRY`
reason string-u ili u `SCALP-BLOCKED` razlozima.

Posle prvog prosirenog dry-run loga pokazalo se da je glavni strategy-side
bloker i dalje `MinTicksForEntry=5`, dok je realni feed vrlo cesto na `4`.

Zato je za sledeci observe korak uradjeno:

- `ScalpStrategy.MinTicksForEntry: 5 -> 4`

Ovo je uradjeno namerno izolovano:

- selector pragovi nisu menjani
- ostali scalp feature gate-ovi nisu menjani

Cilj je da vidimo da li strategija sada prelazi iz `SCALP-BLOCKED`
u `SCALP-ENTRY / SCALP-DRYRUN` bez dodatnog menjanja vise stvari odjednom.

Sledeci identifikovani bloker bio je `empty-book`.
Na realnom Bitfinex feed-u povremeno dolaze prazni orderbook snapshot-i bas u trenutku
evaluacije, pa je strategija prerano odbacivala setup.

Zato je uradjena sledeca korekcija:

- `ScalpStrategy` sada ne pregazuje poslednji validni non-empty book praznim snapshot-om
- poslednji validni book ostaje aktivan dok ga `stale-book` guard ne proglasi prestarim

Time dry-run vise ne pada odmah na prolazni `empty-book`, nego:

- koristi poslednji validni orderbook ako je jos svez
- i dalje bezbedno blokira ako book stvarno zastari

Kako bismo prestali da gledamo samo `blocked` razlog bez feature konteksta,
dodato je i throttled `SCALP-SNAPSHOT` logovanje.

Snapshot sada belezi:

- `ticks`
- `spread`
- `avgSpread`
- `liq`
- `imbalance`
- `microEdge`
- `momentum`
- `bookAgeMs`
- `stage`

To omogucava da za svaki scalp candidate vidimo ne samo da li je blokiran,
nego i kompletan feature state u trenutku evaluacije.

Kako se u praksi pokazalo da strategy cesto bude odbijena jos na `ticks`,
`empty-book` ili `stale-book`, snapshot logging je dodatno pomeren ranije
u entry flow.

To znaci da `SCALP-SNAPSHOT` sada treba da postoji i za:

- `pre-missing-orderbook-block`
- `pre-ticks-block`
- `pre-empty-book-block`
- `pre-stale-book-block`

Cilj ove dopune nije menjanje scalp logike, nego bolja observability:
da iz jednog log reda vidimo i block stage i sav feature kontekst koji je
u tom trenutku bio dostupan.

Kako su `BTCUSDT`, `SOLUSDT` i `ETHUSDT` poceli redovno da ulaze u `Scalp`
regime, ali bez ijednog `SCALP-ENTRY / SCALP-DRYRUN / SCALP-BLOCKED` eventa
za ta tri simbola, dodat je i uski handoff debug u `AdaptiveStrategySelector`.

Novi log:

- `ADAPTIVE-HANDOFF`

trenutno je ogranicen samo na:

- `BTCUSDT`
- `SOLUSDT`
- `ETHUSDT`

Svrha:

- da potvrdimo da li `scalp.OnQuote(...)` stvarno biva pozvan
- da vidimo `preferredStrategy`, `ticks`, `atrFrac`, `spread` i `last`
  u trenutku slanja quote-a ka scalp sloju
- da izolujemo da li je preostali problem u wiring-u ili u samoj `ScalpStrategy`

### Done So Far

Do sada je uradjena minimalna infrastruktura do `DryRun` nivoa:

1. uveden je `ScalpDryRun` config flag u crypto config
2. `UseScalp=false` ostaje potpuno bezbedan baseline i ne menja postojeci flow
3. `UseScalp=true` + `ScalpDryRun=true` sada drzi `Pullback` kao live execution owner
4. `Scalp` u dry-run modu radi paralelno kao observer i ne salje execution signale dalje
5. `AdaptiveStrategySelector` sada zna za scalp dry-run i loguje scalp intent kao observability event
6. dodat je jasniji runtime logging za `disabled / dry-run / live` scalp mode
7. uvedeno je razdvajanje `AdaptiveScalpSelection` pragova i `ScalpStrategy` pragova u kodu i config smeru

### Files Changed

Ovo su fajlovi koji su do sada menjani za ovu fazu:

- `src/Denis.TradingEngine.Strategy/Adaptive/AdaptiveStrategySelector.cs`
- `src/Denis.TradingEngine.Strategy/Scalp/ScalpStrategy.cs`
- `src/Denis.TradingEngine.Exchange.Crypto/Program.cs`
- `src/Denis.TradingEngine.Exchange.Crypto/Trading/CryptoTradingRunner.cs`
- `src/Denis.TradingEngine.Exchange.Crypto/appsettings.crypto.bitfinex.json`
- `src/Denis.TradingEngine.Strategy/Scalp/SCALP-FOOTPRINT-2026-03-09.md`

### What Works Now

Prakticno ponasanje trenutno treba da bude:

- `UseScalp=false` -> sistem radi kao i do sada
- `UseScalp=true` + `ScalpDryRun=false` -> koristi se postojeci scalp/live wiring
- `UseScalp=true` + `ScalpDryRun=true` -> `Pullback` trguje, `Scalp` samo loguje `would-enter / would-exit`

### Not Done Yet

Ove stvari jos nisu uradjene:

1. posebna DB telemetry tabela ili upis za scalp dry-run evente
2. reason-code model za `would_hold / would_block` u strukturisanom obliku
3. state machine refactor za scalp (`Flat -> Armed -> PendingEntry -> Open -> PendingExit -> Cooldown`)
4. pravi execution-aware scalp exit flow
5. order-book-aware contract umesto direktnog vezivanja na konkretnu klasu
6. static symbol assignment tipa `BTCUSDT -> Scalp`, ostali `Pullback`
7. replay/test harness za scalp dry-run validaciju

### Next Recommended Step

Sledeci najlogicniji korak je:

1. posle restarta proveriti da li sada postoje `switching to SCALP` i `SCALP-DRYRUN` eventi
2. tek onda definisati koje scalp dry-run evente hocemo da cuvamo
3. odluciti da li to ide samo u log ili i u DB
4. zatim dodati structured telemetry za `would_enter / would_exit / would_block / would_hold`

---

## Operating Modes

Scalp mora imati jasne runtime modove, da ne remetimo postojeci sistem dok jos gradimo.

### Locked Decision

Ovo je trenutno dogovorena smernica razvoja:

- `Pullback` ostaje nepromenjen i radi kao do sada
- `Scalp` u `DryRun` modu radi paralelno samo za observability
- `Scalp` u `Live` modu ne sme deliti ownership nad istim simbolom sa pullback strategijom

Kratko:

- `DryRun = parallel observe`
- `Live = single-owner execution`

Ovo pravilo postoji da ne mesamo postojeci stabilni flow sa novim scalp eksperimentom.

### Mode 1 -- Disabled

Ako je scalp u `appsettings` iskljucen:

- `UseScalp = false`

onda sistem mora da radi identicno kao danas:

- samo postojeca strategija
- bez promene execution flow-a
- bez dodatnog scalp uticaja na ulaz/izlaz
- bez rizika da novi modul menja ponasanje live sistema

Ovo je "safe baseline" i mora ostati netaknut.

### Mode 2 -- Enabled + DryRun

Ako je scalp ukljucen, ali je:

- `UseScalp = true`
- `ScalpDryRun = true`

onda scalp NE SME da salje real entry/exit naloge.

Treba samo da:

- evaluira setup-e
- belezi sta bi uradio
- cuva reason kodove
- loguje entry/exit intent
- omoguci analizu i poredjenje sa pullback/adaptive tokom live rada

Drugim recima:
scalp tada radi kao "observer strategy", ne kao execution strategy.

### Mode 3 -- Enabled + Live

Tek kada je scalp validiran:

- `UseScalp = true`
- `ScalpDryRun = false`

tada scalp moze da ucestvuje u stvarnom execution flow-u.

Ovaj mod dolazi poslednji, ne na pocetku razvoja.

---

## Target Outcome

Hocemo scalp modul koji ima:

1. jasan contract prema market data i execution layer-u
2. odvojene feature-e za entry, exit i market health
3. state machine po simbolu
4. realan exit flow vezan za fill/position truth
5. replay/test harness za kalibraciju pre live paljenja
6. dovoljno observability logova i metrika da mozemo da dokazemo zasto je signal nastao

---

## Core Principles

### 1. Source Of Truth

Strategija ne sme biti jedini source of truth za poziciju.
Strategy state moze da drzi "intent" i lokalni context, ali:

- fills
- open position
- pending entry
- pending exit

moraju biti uskladjeni sa orchestrator/execution slojem.

### 2. Order Book Is First-Class Input

Scalp bez order book-a nije pravi scalp.
Quote-only signal moze biti pomocni filter, ali ne i glavni signal source.

### 3. Flat Is A Valid Decision

Ako spread nije zdrav, book je tanak, momentum je nejasan ili context nije dobar,
strategija ne radi nista.

### 4. Exit Is More Important Than Entry

Kod scalp-a entry moze biti "dobar", ali ako exit nije striktan i brz,
cela strategija gubi smisao.

### 5. DryRun Must Be Observability-First

DryRun nije "skoro live".
DryRun treba da postoji da bismo videli:

- kada bi scalp usao
- kada bi scalp izasao
- zasto bi to uradio
- da li adaptive selector prebacuje rezim smisleno
- koliko bi scalp setup-a bilo po simbolu i po satu

Ako ovo ne mozemo jasno da pratimo, DryRun nema vrednost.

---

## Proposed State Machine

Po simbolu:

`Flat -> Armed -> PendingEntry -> Open -> PendingExit -> Cooldown -> Flat`

Opis:

- `Flat`: nema setup-a
- `Armed`: market conditions su skoro spremne
- `PendingEntry`: signal emitovan, ceka se order/fill potvrda
- `Open`: pozicija otvorena
- `PendingExit`: exit signal poslat, ceka se fill/close
- `Cooldown`: kratka zabrana novog ulaza nakon trade-a ili failed setup-a

Napomena:
ne zelimo vise prost `bool InPosition` model.

---

## Entry Model v1

Prva verzija scalp entry modela treba da koristi kombinaciju:

- spread filter
- top-of-book liquidity
- order book imbalance
- microprice / bid-ask pressure
- short-term momentum
- stale-book guard
- minimum activity / tick velocity

Entry ne treba da se zasniva samo na tome da je `Last` porastao.

### Initial Direction

Predlog za v1:

- long-only
- jedan open scalp po simbolu
- bez averaging-a
- bez short-a u prvoj fazi

To smanjuje complexity i ubrzava validaciju.

---

## Adaptive Strategy Requirement

Ako je scalp `enabled`, arhitektura treba da ostane adaptive.

To znaci:

- sistem i dalje koristi adaptive selector / regime izbor
- scalp se ne aktivira stalno, nego samo kada market conditions to opravdavaju
- pullback ostaje fallback kada scalp uslovi nisu zadovoljeni

### Adaptive + DryRun

Kada je:

- `UseScalp = true`
- `ScalpDryRun = true`

adaptive logika i dalje treba da odlucuje:

- da li bi simbol bio u `Pullback` modu
- da li bi simbol bio u `Scalp` modu

ali scalp strana tada samo belezi:

- `would_enter`
- `would_exit`
- `would_hold`
- `would_block`

bez slanja naloga.

To je veoma dobra ideja zato sto:

1. cuvamo postojeci live behavior
2. dobijamo real runtime evidenciju da li scalp selector ima smisla
3. mozemo uporediti scalp i pullback bez rizika
4. mozemo videti da li adaptive switch pravi previse buke ili hvata dobre prozore

Predlog:
u dry-run modu i dalje logovati kada adaptive selector prebaci simbol na scalp regime,
cak i ako se ni jedan real trade ne izvrsi.

---

## Exit Model v1

Exit mora imati vise nezavisnih razloga:

- hard stop loss
- take profit
- max hold time
- adverse microstructure shift
- spread expansion
- momentum failure

Exit signal mora biti pravi execution-aware exit,
a ne samo "sell side signal".

---

## Required Architecture Changes

Pre pune implementacije treba dogovoriti sledece:

1. Da li uvodimo `IOrderBookAwareStrategy` ili slican contract.
2. Kako scalp dobija order book, quote i eventualno raw trade feed.
3. Kako se mapira `entry` naspram `exit` kroz `TradeSignal` i `OrderRequest`.
4. Ko drzi canonical state otvorene scalp pozicije.
5. Kako se resava cooldown i max trades per symbol/session.
6. Kako DryRun signali i reason-codes idu u log/DB bez pokretanja execution-a.
7. Kako adaptive selector razlikuje `scalp available`, `scalp dry-run`, i `scalp live`.

---

## Implementation Plan

### Phase 1 -- Contract And State

Cilj:
zatvoriti arhitekturu pre logike.

Deliverables:

- scalp footprint/spec approved
- strategijski contract za order book aware flow
- definisan state machine
- definisan entry/exit signal contract
- odluka ko je source of truth za open position

### Phase 2 -- Market Features

Cilj:
napraviti feature layer koji je citljiv i testabilan.

Deliverables:

- spread health
- liquidity snapshot
- imbalance
- microprice / pressure
- activity window
- stale-book detection

### Phase 3 -- Entry/Exit Rules

Cilj:
napraviti cistu i dokazivu signal logiku.

Deliverables:

- entry conditions
- exit conditions
- cooldown
- no-trade conditions
- structured reason codes za svaki signal

### Phase 4 -- Execution Integration

Cilj:
da scalp radi sa realnim orchestrator pravilima, ne sa lokalnim pretpostavkama.

Deliverables:

- pending entry sync
- exit order path
- fill reconciliation
- broker/orchestrator position parity

### Phase 5 -- Replay, Metrics, Calibration

Cilj:
da validiramo setup pre live ukljucivanja.

Deliverables:

- replay harness za quote/orderbook sekvence
- test cases za entry i exit
- metrics i logging
- inicijalna kalibracija pragova po exchange-u i simbolu

---

## Non-Goals For v1

U prvoj verziji NE jurimo:

- short scalp
- multi-leg scaling
- machine learning signal
- ultra-complex queue position simulation
- cross-exchange arb

Ako v1 ne radi kako treba, complexity nece pomoci.

---

## Success Criteria

Scalp v1 je "dovoljno dobar" tek kad ispunjava sledece:

1. signal moze jasno da se objasni iz logova
2. exit path je tacan i uskladjen sa realnim fills
3. nema duplih entry/exit stanja po simbolu
4. replay pokazuje da strategija zna kad treba da CUT-uje trade
5. live dry-run daje mali broj, ali kvalitetne setup-e

---

## Open Decisions

Pre koda treba finalno potvrditi:

1. Da li v1 ostaje long-only.
2. Da li koristimo samo order book + quote, ili dodajemo i raw trades.
3. Da li scalp radi samo na top simbolima ili na celom universe-u.
4. Koliki je max hold time target za v1.
5. Da li adaptive selector bira scalp, ili scalp ima svoj poseban gate.
6. Da li koristimo jedan global config ili per-symbol overrides.
7. Koji je minimalni set DryRun telemetry podataka koji mora da se cuva.
8. Da li u DryRun modu cuvamo samo logove ili i posebne DB evente za `would_trade`.

---

## Decision Log

Ovo su odluke koje su trenutno dovoljno stabilne da ih tretiramo kao dogovorene:

1. `Pullback` ostaje netaknut dok gradimo scalp.
2. `ScalpDryRun` je obavezan korak pre live scalp-a.
3. U `DryRun` modu `Pullback` ostaje execution owner.
4. U `DryRun` modu `Scalp` samo posmatra i belezi intent.
5. U `Live` modu ne zelimo dve execution strategije nad istim simbolom bez jasnog owner-a.
6. Prva verzija scalp-a ide ka manjem scope-u, ne ka sirokom universe-u i ne ka visokoj complexity.
7. Pragovi za `Adaptive` izbor i pragovi za samu `Scalp` strategiju moraju biti odvojeni.

Ako neka od ovih odluka bude promenjena kasnije, treba je eksplicitno zapisati ovde.

---

## Threshold Separation

Ovo je sada eksplicitno zakljucano:

- `Pullback` ima svoje pragove i svoje filtere
- `Scalp` ima svoje pragove i svoje filtere
- `Adaptive selector` ima svoje pragove samo za izbor rezima

Ne sme da postoji mesanje tipa:

- da `Pullback` metrika odredjuje scalp entry
- da `Scalp` entry pragovi glume selector pragove
- da jedan hardkodovan threshold pokusava da resi i regime selection i signal generation

To su tri razlicita sloja:

1. `Regime selection`
2. `Strategy-specific filtering`
3. `Strategy-specific signal generation`

### Layer 1 -- Adaptive Selection Thresholds

Ovo sluzi samo da sistem odluci:

- da li simbol ostaje `Pullback`
- da li simbol ide u `Scalp`

Ovi pragovi ne smeju direktno da budu isto sto i scalp entry uslovi.

Primer:

- `MinTicksForScalpSelection`
- `MaxSpreadBpsForScalpSelection`
- `MinAtrFractionForScalpSelection`

### Layer 2 -- Scalp Strategy Thresholds

Ovo su pravi scalp uslovi za entry/exit logiku.

Primer:

- `ScalpMaxSpreadBps`
- `ScalpMinLiquidityUsd`
- `ScalpMinBookImbalance`
- `ScalpProfitTargetPct`
- `ScalpStopLossPct`
- `ScalpMaxHoldSeconds`

### Layer 3 -- Pullback Thresholds

Pullback ostaje potpuno odvojen sa svojim parametrima:

- EMA / slope
- ATR warmup
- pullback depth
- reclaim uslovi
- spread/activity filteri vezani za pullback logiku

Zakljucak:
selector bira rezim, strategija bira signal.
To ne sme biti ista stvar.

---

## Draft Config Contract

Minimalni config koji sad vec ima smisla:

```json
"Trading": {
  "UseScalp": false,
  "ScalpDryRun": false
}
```

Verovatan sledeci nivo config-a:

```json
"Trading": {
  "UseScalp": true,
  "ScalpDryRun": true,
  "ScalpSymbols": ["BTCUSDT"],
  "AdaptiveScalpSelection": {
    "Enabled": true,
    "MinTicksPerWindow": 30,
    "MaxSpreadBps": 8.0,
    "MinAtrFraction": 0.00035
  },
  "ScalpStrategy": {
    "Enabled": true,
    "MaxSpreadBps": 10.0,
    "MinLiquidityUsd": 1000.0,
    "ProfitTargetPct": 0.002,
    "StopLossPct": 0.001,
    "MaxHoldSeconds": 300
  },
  "ScalpMaxSpreadBps": 10.0,
  "ScalpMinLiquidityUsd": 1000.0,
  "ScalpProfitTargetPct": 0.002,
  "ScalpStopLossPct": 0.001
}
```

Napomena:
`ScalpSymbols` je jak kandidat za prvi live rollout jer daje cist ownership po simbolu.

Napomena 2:
gornji blok treba posmatrati kao prelazni draft.
Pravi smer je da se stare ravne `Scalp*` vrednosti prebace u poseban `ScalpStrategy` config objekat.

---

## Minimum DryRun Telemetry

Ako budemo uvodili structured telemetry, minimum koristan skup je:

- `utc`
- `symbol`
- `preferred_strategy`
- `event_type` = `would_enter | would_exit | would_hold | would_block`
- `side`
- `price`
- `reason_code`
- `spread_bps`
- `ticks_per_window`
- `mode` = `dry-run`
- `exchange`

Opcioni ali korisni podaci:

- `imbalance`
- `top3_liquidity_usd`
- `microprice_bias`
- `hold_time_sec`
- `selector_state`

Ovo je dovoljno da kasnije mozemo da pitamo:

- koliko puta bi scalp usao
- na kojim simbolima
- pod kojim uslovima
- koliko cesto selector bira scalp

---

## Likely File Map For Next Steps

Najverovatniji fajlovi za sledece faze:

- `src/Denis.TradingEngine.Strategy/Scalp/ScalpStrategy.cs`
- `src/Denis.TradingEngine.Strategy/Adaptive/AdaptiveStrategySelector.cs`
- `src/Denis.TradingEngine.Exchange.Crypto/Program.cs`
- `src/Denis.TradingEngine.Exchange.Crypto/Trading/CryptoTradingRunner.cs`
- `src/Denis.TradingEngine.Exchange.Crypto/Trading/CryptoTradingOrchestrator.cs`
- `src/Denis.TradingEngine.Data/db.sql`

Moguci novi fajlovi:

- `ScalpSettings.cs`
- `ScalpReasonCodes.cs`
- `ScalpTelemetryRepository.cs`
- `IOrderBookAwareStrategy.cs`
- replay/test fajlovi za scalp

---

## Risks To Watch

Najveci trenutni rizici:

1. da scalp dry-run ostane samo "lep log", bez dovoljno podataka za analizu
2. da se prerano ode u live scalp pre nego sto exit path bude cist
3. da adaptive selector prebukira scalp regime i napravi previse buke
4. da se ownership pozicije zamagli kada se predje sa dry-run na live
5. da se config previse rano rasiri bez jasnog rollout plana

---

## Resume Checklist

Kad nastavimo rad, prvo proveriti:

1. da li i dalje vazi `Pullback live / Scalp dry-run`
2. da li uvodimo samo log telemetry ili i DB telemetry
3. da li zelimo `ScalpSymbols` static assignment kao sledeci korak
4. da li ostajemo `long-only` za v1
5. da li prvo vadimo pragove iz `AdaptiveStrategySelector` u poseban config
6. da li je fokus sledece faze `telemetry`, `state machine`, ili `symbol ownership`

Ako ovo nije potvrdeno na startu sledece sesije, lako odemo u kodiranje bez jasnog cilja.

---

## Working Agreement

Dalji razvoj scalp-a radimo ovim redom:

1. spec
2. contract
3. state machine
4. signal rules
5. execution integration
6. replay/test
7. tek onda live enable

Ovo treba da bude "calm and precise" razvoj, bez brzog lepljenja logike u jedan fajl.
