# MICRO PULLBACK / MEAN REVERSION -- Design Footprint

Status: `research draft / minimal v1 defined / dry-run only`
Last updated: 2026-03-20

---

## Purpose

Ovaj dokument opisuje novu strategijsku familiju za `Bitfinex`:

- `BTCUSDT` only
- kratki `micro pullback / mean reversion`
- order book kao filter i veto
- dry-run first

Ideja nije da jurimo continuation, nego da cekamo kratku lokalnu
preekstenziju i da uzmemo povratak ka kratkorocnoj "fair value" zoni.

Radni opis:

- market ode kratko predaleko
- continuation pocne da slabi
- mi trazimo povratak, ne nastavak

---

## Why This Exists

Novi pravac nije izabran napamet, nego iz live dry-run logova trenutnog
`ScalpStrategy`.

Najvazniji nalazi iz `2026-03-20` dry-run prozora:

- vecina continuation entry-ja izgleda dobro pre ulaza, ali lose odmah posle ulaza
- mnogo trade-ova ima `MFE=0`
- `edge-lost` se aktivira brzo i cesto
- jaci `momentum / imbalance / microEdge` nije davao konzistentno bolji outcome
- `BTCUSDT` je jedini simbol koji izgleda iole spasivo
- `XRPUSDT` i `SOLUSDT` nisu pokazali dovoljno dobar profil za nastavak ovog puta

Zakljucak:

trenutni podaci vise lice na:

- `fade failed micro move`

nego na:

- `chase short-term continuation`

---

## Strategic Thesis

Osnovna hipoteza:

- na `Bitfinex BTCUSDT` kratki impuls cesto ne dobija lep continuation
- umesto toga dolazi do brzog smirivanja ili vracanja ka sredini
- zato je verovatnije da edge lezi u `micro pullback / mean reversion`
  nego u agresivnom taker continuation scalp-u

Ovo NIJE:

- full market making
- tape-reading / stop-hunt strategy
- 1m/5m candle pattern system

Ovo JESTE:

- kratkorocna mean reversion strategija
- zasnovana na quote + order book feature-ima
- sa vrlo brzom invalidacijom

---

## Scope

Prva verzija treba da bude uska i kontrolisana:

- simbol: `BTCUSDT`
- smer: long-only u v1
- mode: `dry-run`
- venue: `Bitfinex`

Namerno van scope-a za v1:

- `SOLUSDT`
- `XRPUSDT`
- short side
- maker quote management
- liquidity sweep / stop hunt
- multi-symbol rotation

---

## Minimal V1 Decisions

Da dokument ne bi ostao samo "dobar research", ovo su odluke koje vaze
za prvi implementation pass.

V1 je:

- `Bitfinex`
- `BTCUSDT only`
- `long-only`
- `dry-run only`
- `confirmed reclaim` kao pravi entry trigger
- `early reclaim` samo kao telemetry signal
- entry na `ask`
- `one shot per dislocation`

V1 namerno nije:

- maker / passive entry sistem
- multi-symbol sistem
- sweep / stop-hunt sistem
- partial exit / scale-out sistem

Razlog:

- cilj v1 je da dokazemo signal behavior
- ne da od prvog dana komplikujemo execution

---

## Market Model

Strategija treba da koristi tri sloja:

1. `Dislocation`
- cena se odvojila od kratke fair-value reference
- odvajanje mora biti dovoljno veliko da nije samo noise

2. `Exhaustion`
- continuation slabi
- book vise ne podrzava nastavak istim kvalitetom

3. `Reclaim / Stabilization`
- cena prestaje da se udaljava
- pojavljuje se prvi znak povratka

Trade ulazimo tek kada se sva tri sloja spoje.

---

## Fair Value

Strategija mora imati internu kratkorocnu referencu od koje meri:

- koliko je cena "otisla predaleko"
- koliko je blizu povratak

Za v1 kandidat fair-value reference:

- kratki rolling mid-price
- ili kratki EMA nad mid-price

Vazno:

- ne koristiti `Last` kao jedinu referencu
- koristiti mid / microstructure-aware price kad god je moguce

Radni predlog za v1:

- `fairValue = short EMA(mid-price)`
- `recentVolatility = EMA(abs(delta mid-price))`
- `effectiveVolatility = clamp(recentVolatility, minVol, maxVol)`

Time izbegavamo da dislocation bude fiksan i "slepo" vezan za isti broj bps.

Bitna napomena:

- bez `clamp` logike mikro-volatilnost moze da nas prevari
- kad market skoro stane, dobijamo lazne "velike" normalized move-ove
- kad market poludi, skoro sve moze da izgleda validno

Zato v1 ne treba da koristi "sirovu" volatility procenu, nego kontrolisanu.

---

## Critical Gaps

Ovo su tacke koje ce najverovatnije napraviti ili slomiti strategiju.

### 1. Dislocation ne sme biti fiksan

Los pristup:

- `DislocationMinBps = 5`

Problem:

- ako je prag premali -> hvata noise
- ako je prag prevelik -> ulazimo kasno

Zato v1 treba da koristi `normalized dislocation`.

Predlog:

- `dislocationBps = fairValue - price`
- `normalizedDislocation = dislocation / effectiveVolatility`

Prakticno:

- strategija ne pita samo "da li je cena 5 bps ispod fair value"
- nego "da li je cena dovoljno daleko u odnosu na trenutni micro volatility regime"

Minimalni radni dodatak:

- `effectiveVolatility = clamp(recentVolatility, minVol, maxVol)`

To treba da bude deo v1, ne kasniji polish.

### 2. Exhaustion mora biti konkretan

Fraza "continuation slabi" nije dovoljna.

Ako ovo nije dobro definisano:

- ulazimo u `falling knife`

Za v1 predlog je:

- entry kandidat postoji tek kada su zadovoljena najmanje `2 od 3` uslova

Uslovi:

- `Momentum decay`
  - downside slope je i dalje negativan, ali mu jacina opada
- `Imbalance recovery`
  - sell pressure ide ka neutralnijem book-u
- `Microprice recovery`
  - microprice vise ne nastavlja istim kvalitetom nize

Bitno:

- ne trazimo samo slabiji pad
- trazimo promenu karaktera micro move-a

### 3. Reclaim mora imati jasan trigger

Fraza "reclaim pocinje" takodje nije dovoljna.

Za v1 reclaim trigger mora biti konkretan.

Predlog:

- `price > local 2-3 quote high after local low`
  - ili
- `microprice flip` iz negativnog u neutralno/pozitivno

Poenta:

- entry nije dozvoljen na apstraktnom osecaju stabilizacije
- mora postojati jasan, proverljiv signal

Ali za v1 je korisno da razlikujemo 2 nivoa reclaim-a:

- `Early reclaim`
  - prvi uptick posle local low
  - ili prvi rast `bid/ask` kvaliteta / microprice-a
- `Confirmed reclaim`
  - `price > short-term local high`
  - ili jaci `microprice flip`

Poenta:

- `early reclaim` daje agresivniji ulaz
- `confirmed reclaim` daje sigurniji, ali kasniji ulaz

U dry-run-u vredi logovati oba, pa tek onda odluciti koji daje bolji profil.

Minimalna odluka za v1:

- `early reclaim` se loguje
- `confirmed reclaim` se koristi za stvarni dry-run entry

### 4. Exit ne sme da ceka "lep full revert"

Mean reversion na micro nivou cesto nije simetrican:

- cena ode `10 bps`
- vrati `4-6 bps`
- pa opet nastavi protiv nas

Zato v1 treba da razmatra:

- `micro TP`
- i opciono kasnije `scale-out`
- `profit protection`

Ako execution layer ostane jednostavan u prvoj iteraciji:

- bolje je uzeti manji, realniji target
- nego idealizovati full revert koji se retko zavrsi do kraja

Profit protection za v1 treba da ima makar:

- `MFE-based exit`
  - ako je trade vec imao smislen plus i znacajno ga vraca, izlaz
- `stall exit`
  - ako cena vise ne ide ka fair value i samo stoji, izlaz

### 5. Time je core edge, ne samo backup stop

Za ovu familiju `time` nije samo safety net.

Ako se mean reversion ne desi brzo:

- setup verovatno nije pravi

Zato v1 treba da ima:

- `expected reversion time`
- ako se pozitivan response ne pojavi brzo -> izlaz

Najvazniji telemetry derivat ovde je:

- `time to first positive MFE`

Radni princip:

- ako trade ne pokazuje zivot brzo, ne zasluzuje kapital ni vreme

---

## Candidate Inputs

Ovo su feature-i koje strategija moze da koristi bez uvodjenja novog
tehnoloskog sloja:

- `bid`
- `ask`
- `last`
- `spread bps`
- `recent average spread`
- `top-of-book liquidity`
- `top-of-book imbalance`
- `microprice edge`
- `book age`
- kratki quote-based momentum
- distanca od fair-value reference

To znaci da se strategija moze uklopiti u postojeci crypto flow bez
promene osnovnog strategy contract-a.

---

## Entry Concept

Long entry kandidat postoji kada:

- cena je ispod kratke fair-value reference za dovoljno veliki
  `normalized dislocation`
- prethodni down move je bio brz ili dovoljno jasan
- spread nije los
- liquidity nije losa
- book nije stale
- downside continuation slabi po konkretnim pravilima
- pojavljuje se prvi jasan reclaim signal nazad ka fair value

Prakticno:

- ne kupujemo kad market "puca gore"
- kupujemo kada je prethodni pad poceo da gubi dah

Minimalni v1 entry kostur:

1. `Dislocation`
- normalized dislocation iznad praga

2. `Exhaustion`
- bar `2 od 3`:
  - momentum decay
  - imbalance recovery
  - microprice recovery

3. `Reclaim`
- `early reclaim`
  - prvi uptick posle local low
  - ili prvi kvalitetniji bid/microprice oporavak
- `confirmed reclaim`
  - `price > local short-term quote high`
  - ili
  - `microprice flip`

Bez sva tri sloja nema entry-ja.

Napomena:

- v1 moze u dry-run-u da loguje i `early` i `confirmed` varijantu
- ne moramo odmah da tvrdimo koja je bolja bez podataka

---

## Exit Concept

Exit ne sme biti samo `TP / SL / time`.

V1 treba da ima:

- `micro-tp`
  - prvi mali, realan mean reversion target
- `reversion-hit`
  - cena se vratila do fair value / target zone
- `edge-lost`
  - reversion setup vise ne izgleda zdravo
- `stop`
  - odvajanje se nastavilo i invalidiralo ideju
- `time`
  - povratak se nije desio dovoljno brzo
- `mfe-protect`
  - trade je bio u plusu, ali ga znacajno vraca
- `stall`
  - trade ne ide ka fair value, nego stagnira

Bitna razlika u odnosu na continuation scalp:

- ovde je cilj mali i prirodan
- ne trazimo novi impuls, nego povratak

Napomena za v1:

- `scale-out` je vrlo verovatno koristan
- ali ako previse komplikuje execution, moze prvo da se simulira kroz
  telemetry i target layering, bez live partial management-a

Minimalni practical v1 exit set:

- `micro-tp`
- `edge-lost`
- `time-to-first-MFE fail`
- `mfe-protect`
- `hard stop`
- `max hold`

Za v1:

- `stall` moze biti poseban exit reason
- ili moze ostati pod `edge-lost`, ali mora biti logicki prepoznat u telemetry

---

## State Machine

Predlog za v1:

- `Idle`
- `Dislocated`
- `Armed`
- `InPosition`
- `Cooldown`

Znacenje:

`Idle`
- nema setupa

`Dislocated`
- cena je dovoljno daleko od fair value
- jos nema reclaim potvrde

`Armed`
- dislocation postoji
- continuation slabi
- reclaim pocinje
- entry je dozvoljen

`InPosition`
- long je otvoren
- cekamo mean reversion ili invalidaciju

`Cooldown`
- kratka pauza nakon exita
- da ne re-udaramo isti raspadajuci move

Prakticno:

- svaka dislocation epizoda ima najvise jedan trade
- novi trade je dozvoljen tek nakon povratka u `Idle` i posle `Cooldown`

---

## Integration Into Current System

Najcistiji nacin uklapanja u repo:

1. nova strategija u `Scalp` folderu
- npr. `MicroPullbackReversionStrategy.cs`

2. i dalje implementira `ITradingStrategy`

3. opcioni `OnOrderBook` helper kao i kod scalp-a

4. `CryptoTradingRunner` je pravi i povezuje novu strategiju kao sto vec
   povezuje `ScalpStrategy`

5. `AdaptiveStrategySelector` dobija novu granu ili zamenu za postojeceg
   scalp kandidata u dry-run rezimu

---

## Why Separate Strategy

Ovo ne treba gurati u postojeci `ScalpStrategy` kao jos jedan mode.

Razlozi:

- continuation i mean reversion imaju suprotnu logiku
- entry/exit razmisljanje je drugacije
- telemetry treba da bude cista i odvojena
- lakse se A/B testira
- manji je rizik da stari scalp kod postane nejasna mesavina dve ideje

---

## Why Ask Entry In V1

Iako je long entry na `ask` losiji execution model od pasivnog bid entry-ja,
za v1 je i dalje razuman izbor.

Razlog:

- zelimo da prvo dokazemo da signal uopste ima post-entry life
- ako i sa jednostavnim execution modelom dobijemo brz pozitivan `MFE`,
  signal je zdrav kandidat
- maker / passive entry mozemo dodati tek kad signal behavior bude potvrden

Drugim recima:

- v1 testira pre svega `signal quality`
- ne finalni execution alpha

---

## Config Shape

V1 bi trebalo da ima poseban config blok, nezavisan od trenutnog scalp-a:

- `UseMicroPullbackReversion`
- `MicroPullbackReversionDryRun`
- `MicroPullbackSymbols`
- `MicroPullbackStrategy`

Primer parametara za v1:

- `MaxSpreadBps`
- `MinLiquidityUsd`
- `MaxBookAgeMs`
- `FairValueEmaQuotes`
- `RecentVolatilityQuotes`
- `MinEffectiveVolatilityBps`
- `MaxEffectiveVolatilityBps`
- `MinNormalizedDislocation`
- `MaxNormalizedDislocation`
- `MaxContinuationMomentumBps`
- `MinMomentumDecayPct`
- `MinImbalanceRecovery`
- `MinMicropriceRecoveryBps`
- `ReclaimLookbackQuotes`
- `EnableEarlyReclaim`
- `EnableConfirmedReclaim`
- `MinReclaimMomentumBps`
- `MinImbalanceRecovery`
- `MinMicropriceRecoveryBps`
- `MicroTakeProfitBps`
- `ProfitTargetBps`
- `StopLossBps`
- `MaxTimeToFirstPositiveMfeSeconds`
- `MfeProtectMinBps`
- `MfeGivebackBps`
- `MaxStallSeconds`
- `ExpectedReversionSeconds`
- `MaxHoldSeconds`
- `CooldownSeconds`
- `OneShotPerMove`

Predlog `Minimal V1` default-a:

- `FairValueEmaQuotes = 20`
- `RecentVolatilityQuotes = 20`
- `MinNormalizedDislocation = 1.5`
- `MaxNormalizedDislocation = 4.0`
- `MicroTakeProfitBps = 2.0`
- `StopLossBps = 4.0`
- `MaxHoldSeconds = 6`
- `CooldownSeconds = 5`

Ovi brojevi nisu "sveti", ali su dobar prvi dry-run baseline.

Napomena:

- ukloniti duplikate kao sto su `MinImbalanceRecovery` i
  `MinMicropriceRecoveryBps` kada se config stvarno uvede u kod

---

## Minimal V1 Spec

Ovo je najkraci implementation-ready sloj strategije.

### Price Model

- `mid = (bid + ask) / 2`
- `fairValue = EMA(mid, N_fv)`
- `deltaMid = mid - prevMid`
- `recentVolatility = EMA(abs(deltaMid), N_vol)`
- `effectiveVolatility = clamp(recentVolatility, MinEffectiveVolatility, MaxEffectiveVolatility)`
- `dislocation = fairValue - mid`
- `normalizedDislocation = dislocation / effectiveVolatility`

### Entry

Dislocation:

- `MinNormalizedDislocation <= normalizedDislocation <= MaxNormalizedDislocation`

Filters:

- `spreadBps <= MaxSpreadBps`
- `topLiquidityUsd >= MinLiquidityUsd`
- `bookAgeMs <= MaxBookAgeMs`

Exhaustion:

- `2 of 3`
  - momentum decay
  - imbalance recovery
  - microprice recovery

Reclaim:

- v1 entry koristi `confirmed reclaim`
- `mid > highest(mid over last N)`
  - ili
- `microprice flip >= 0`

Execution:

- `enter LONG at ASK`

### Exit

- `hard stop`
- `micro take profit`
- `time-to-first-MFE fail`
- `MFE protection`
- `max hold`
- `edge lost`

### State Flow

- `Idle -> Dislocated -> Armed -> InPosition -> Cooldown`

### One Shot Rule

- `max 1 trade per dislocation`

---

## Telemetry

Nova strategija mora od starta da ima bogat logging, isto kao novi scalp.

Predlog log family:

- `MR-SNAPSHOT`
- `MR-BLOCKED`
- `MR-SETUP`
- `MR-ENTRY-DETAIL`
- `MR-RESULT`

Sta mora da se vidi u logu:

- distance from fair value
- normalized dislocation
- effective volatility
- spread
- avg spread
- liquidity
- imbalance
- microprice edge
- continuation momentum
- reclaim momentum
- reclaim mode (`early` / `confirmed`)
- whether reclaim was `observed-only` or `executed`
- book age
- hold time
- realized bps
- `MFE / MAE`
- `time to first positive MFE`
- `max MFE giveback`
- exit after `stall` or not

Bez ovoga cemo opet lutati kroz tuning bez dokaza.

---

## Dry-Run Success Criteria

Strategija nema smisla nastavljati ako u dry-run ne pokaze makar sledece:

- vecina trade-ova ima bar neki pozitivan `MFE`
- `edge-lost` nije dominantan u prvih nekoliko sekundi
- average `MAE` nije mnogo veci od average `MFE`
- gross expectancy nije ocigledno negativan
- `BTCUSDT` pokazuje ponovljiv obrazac, ne jedan izolovan dobar trade
- `time to MFE` dolazi brzo i ponovljivo
- `mfe-protect` ne vraca previse otvorenog profita
- `early` i `confirmed` reclaim mogu da se uporede bez mutnih kriterijuma

Prakticni v1 target:

- `> 60%` trade-ova sa `MFE > 0`
- `avg MFE > avg MAE`
- `expectancy >= 0` na dovoljno velikom sample-u

Klasicni failure signal bio bi:

- trade-ovi opet odmah idu protiv nas
- `MFE=0` dominira
- "jaci" setup feature-i opet ne prave bolji outcome
- time-based response ne dolazi brzo ni kad setup izgleda "dobro"
- volatility normalization daje previse laznih setup-a u dead marketu

---

## Non-Goals For V1

Ovo namerno NE radimo u prvoj verziji:

- liquidity sweep / stop hunt
- DOM/tape reading engine
- maker order management
- quote replace loop
- multi-symbol adaptive rotation
- live trading odmah

To je odvojena faza.

---

## Relation To Existing Strategies

`PullbackInUptrendStrategy`
- visi timeframe / trend-pullback logika
- moze ostati potpuno netaknut

`ScalpStrategy`
- trenutni continuation eksperiment
- moze ostati ugasen ili samo kao stari reference branch

`MicroPullbackReversionStrategy`
- novi kandidat za ultra-kratki BTC mean reversion

---

## Current Recommendation

Ako se ovaj pravac bude implementirao, preporuka je:

- samo `BTCUSDT`
- samo `dry-run`
- odvojena strategija
- bogata telemetry od prvog dana
- bez live execution dok se ne potvrdi da trade-ovi bar kratko dobijaju
  pozitivan `MFE`

---

## Open Questions

Pre implementacije treba razjasniti:

1. da li je bolja `EMA(mid)` ili drugi kratki fair-value proxy za v1
2. koji range `normalized dislocation` daje najbolji dry-run profil
3. da li `confirmed reclaim` zaista nadmasuje `early reclaim` kada se uporede na istom log setu
4. koliko brz mora biti `expected reversion time` da bi trade ostao validan
5. da li `SignalSlayer` treba da ostane samo kao filter ili da dobije poseban
   MR context
6. da li `micro TP` treba odmah da bude deo v1 execution-a ili prvo samo deo telemetry modela
7. koliki `volatility clamp` range je razuman za Bitfinex BTC
8. koliki `MFE giveback` je prihvatljiv pre `mfe-protect` exita

---

## Bottom Line

Ovo je trenutno najzdraviji novi pravac ako hocemo da ostanemo na `Bitfinex`
i da iskoristimo ono sto su nam logovi stvarno rekli:

- continuation scalp nije pokazao dovoljno dobar post-entry edge
- BTC je jedini simbol koji ima smisla dalje istrazivati
- novi eksperiment treba da ide ka `fade micro move`, ne ka `chase micro move`
