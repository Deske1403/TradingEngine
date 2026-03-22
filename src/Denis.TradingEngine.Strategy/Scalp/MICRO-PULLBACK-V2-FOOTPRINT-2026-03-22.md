# MICRO PULLBACK V2 -- Design Footprint

Status: `research draft / v1 paused / not implemented`
Last updated: 2026-03-22

---

## Purpose

Ovaj dokument opisuje sledeci korak posle `MicroPullbackReversionStrategy v1`.

V1 je bio koristan jer je odgovorio na dva pitanja:

- engine/wiring/logging sada rade
- `ask-entry micro mean reversion` u sadasnjem obliku nije pokazao dovoljno dobar post-entry life

Zato je cilj `v2`:

- ne jos jedan mali parametarski tweak
- nego promena `entry behavior` logike

Radni opis:

- `v1` je kupovao prvi znak reclaim-a
- `v2` treba da kupuje tek mali dokaz da se reclaim odrzao

---

## Why V1 Is Paused

Najvazniji nalazi iz `2026-03-22` dry-run testova:

- u sirokom rezimu bilo je trade-ova, ali bez pozitivnog `MFE`
- u zategnutom rezimu spread handicap je smanjen, ali sample je skoro nestao
- `early reclaim` je dao raniji ulaz, ali i dalje nije doneo pozitivan post-entry zivot
- problem vise nije samo spread
- problem je sto entry i dalje cesto hvata `too early bounce that never lives`

Najkraca istina:

- `v1` je ili prebrz, ili prerano veruje prvom odbijanju

---

## Strategic Thesis

`MicroPullback v2` treba da zadrzi istu familiju:

- `BTCUSDT`
- `Bitfinex`
- `dry-run first`
- quote + orderbook aware

Ali treba da promeni glavni entry princip:

- ne ulazimo na `first reclaim`
- ulazimo na `reclaim that survives briefly`

To znaci da signal treba da bude:

1. `dislocated`
2. `exhausted`
3. `reclaimed`
4. `stabilized`

V1 je uglavnom radio samo prva tri.

---

## Core Idea

V2 uvodi `stabilization window`.

Prakticno:

- posle validne dislocation i exhaustion faze
- reclaim mora da se pojavi
- i onda mora kratko da prezivi

Ne trazimo veliki bounce.

Trazimo:

- nema novog lower low odmah
- nema novog raspada microprice-a odmah
- nema novog sirenja spread-a odmah
- reclaim se ne gasi odmah na sledecem update-u

Jedna recenica:

- `v1 kupuje prvi blink`
- `v2 kupuje blink koji nije odmah nestao`

---

## What Changes From V1

### 1. Reclaim vise nije dovoljan sam po sebi

V1:

- `early reclaim` ili `confirmed reclaim` moze direktno u entry

V2:

- reclaim samo otvara vrata
- ne ulazi se odmah

### 2. Dodaje se stabilization faza

Posle reclaim-a cekamo vrlo kratak prozor:

- `2-3` uzastopna quote/book update-a
- ili mali vremenski prozor `1-2s`

Tokom tog prozora:

- nema novog lower low
- `microprice edge` ne sme da se raspadne
- `imbalance` ne sme da se vrati jako protiv nas
- `spread` ne sme da ode preko entry limita

### 3. Entry tek posle reclaim + hold

Entry kandidat postoji tek kada:

- dislocation i dalje postoji
- exhaustion ostaje validan
- reclaim je detektovan
- stabilization window je prezivljen

### 4. Book quality mora biti makar neutralna

V2 ne trazi ekstremno bullish book.

Ali ne ulazi ako je neposredno pre entry-ja:

- imbalance opet jako negativan
- microprice opet jako negativan
- spread ponovo siri

---

## Candidate State Machine

Predlog za `v2`:

- `Idle`
- `Dislocated`
- `Armed`
- `ReclaimObserved`
- `Stabilizing`
- `InPosition`
- `Cooldown`

Znacenje:

`Idle`
- nema setupa

`Dislocated`
- cena je dovoljno odvojena od fair value

`Armed`
- exhaustion je validan

`ReclaimObserved`
- reclaim je detektovan
- ali jos nije dozvoljen entry

`Stabilizing`
- reclaim pokusava da prezivi
- cekamo mini potvrdu

`InPosition`
- ulaz tek posle stabilization uslova

`Cooldown`
- kratka pauza posle exita

---

## Minimal V2 Entry Rules

V2 entry treba da zahteva sve ovo:

1. `Dislocation`
- `normalizedDislocation` u dozvoljenom opsegu

2. `Exhaustion`
- i dalje `2 of 3`
  - momentum decay
  - imbalance recovery
  - microprice recovery

3. `Reclaim observed`
- `early` ili `confirmed`, zavisno od rezima

4. `Stabilization`
- kratki prozor bez:
  - novog lower low
  - novog negativnog microprice flip-a
  - novog spread deterioration

5. `Entry`
- tek tada ulaz

---

## Exit Philosophy

Exit logika iz `v1` je dovoljno dobra da ostane startna osnova.

Za `v2` fokus nije na novom exit-u, nego na boljem entry-u.

Zadrzati:

- `micro-tp`
- `edge-lost`
- `time-to-first-MFE fail`
- `mfe-protect`
- `hard stop`
- `max hold`

Ali pratiti:

- da li bolji entry konacno daje `MFE > 0`

---

## Telemetry Additions

V2 mora da loguje i novu fazu izmedju reclaim-a i entry-ja.

Dodati:

- `MRV2-SETUP`
- `MRV2-STABILIZE`
- `MRV2-STABILIZE-FAIL`
- `MRV2-ENTRY-DETAIL`
- `MRV2-RESULT`

Najvaznije nove metrike:

- `time from reclaim to stabilized-entry`
- `stabilization quotes count`
- `did lower low happen during stabilization`
- `did spread widen during stabilization`
- `did microprice relapse during stabilization`

---

## Config Ideas

Ako se v2 bude implementirao, trebace novi parametri:

- `StabilizationQuotes`
- `MinStabilizationSeconds`
- `MaxLowerLowToleranceBps`
- `MaxSpreadDeteriorationBps`
- `MaxMicropriceRelapseBps`
- `RequireNeutralBookAtEntry`

Poenta:

- v2 ne treba da bude samo `v1 + random tweaks`
- treba da ima svoj eksplicitan stabilization sloj

---

## Success Criteria

V2 nema smisla samo ako “lepse izgleda”.

Treba da pokaze:

- vise trade-ova sa `MFE > 0`
- manje `instant negative` trade-ova
- manju dominaciju `mr-edge-lost` odmah posle entry-ja
- zdraviji odnos `avg MFE` vs `avg MAE`

Najvazniji test:

- da li trade prvi put pokazuje zivot posle entry-ja

Ako ni `v2` ne uspe da izvuce pozitivan `MFE` profil, treba ozbiljno razmotriti da ova familija nije pravi edge za ovaj venue.

---

## Non-Goals

Ovo namerno nije `v2`:

- maker entry sistem
- full top-of-book strategy
- liquidity sweep / stop hunt
- 1m/5m candle breakout system
- multi-symbol rotation

To su drugi pravci.

---

## Current Recommendation

Trenutno stanje:

- `MicroPullback v1` je parkiran
- feature treba ostati `disabled`
- novi rad, ako ga bude, treba da ide kao `v2 redesign`, ne kao dalje sitno stezanje v1

---

## Bottom Line

`v1` je odgovorio na pitanje da li prvi reclaim vredi juriti.

Za sada odgovor izgleda:

- `ne dovoljno`

`v2` treba da odgovori na drugo pitanje:

- `da li reclaim koji kratko prezivi ima bolji post-entry life`

To je trenutno najzdraviji sledeci eksperiment za ovu strategijsku familiju.
