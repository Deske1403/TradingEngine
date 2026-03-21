# GREEN GRIND -- Design Footprint

Status: `v1.2 implemented + live validation (DryRun) + hard-gate decision pending`
Last updated: 2026-03-01

---

## Purpose

Regime detection system: detektuje period "green grind" -- stabilan, postepen rast cene
kroz vise sati. Koristi se kao GATE za trade signale.

Kljucna ideja: ne predvidjamo trend, vec detektujemo kad je POTVRDJENI grind u toku
i samo tada dozvoljavamo trading.

Crypto-specific (Bitfinex). Ne koristi se za IBKR.

---

## v1 Implementation (Feb 2026)

### Files
- `SymbolGreenGrindRegimeService.cs` -- state machine, evaluation, metrics
- `GreenGrindSettings.cs` -- config, thresholds, symbol overrides
- `CryptoTradingOrchestrator.cs` -- integration, logging, Discord notifications
- `MarketTickRepository.cs` -- DB-canonical GreenGrind snapshot query

### State Machine
```
OFF -> WATCH -> ACTIVE -> STRONG
         |         |
         v         v
        OFF       OFF
```
- OFF: nema grinda
- WATCH: metrike prelaze Watch pragove, ali ne i Active
- ACTIVE: potvrdjen grind (3h+), hysteresis zadovoljen
- STRONG: jak grind, sve metrike visoke

### Hysteresis
- ActivationThresholds (strozi): za ulazak u Active iz Off/Watch
- DeactivationThresholds (blazi): za ostanak u Active/Strong
- Cooldown timer: min 15 minuta izmedju state promena

### Source of Truth (runtime gating)
- Trade gate koristi DB-canonical snapshot (market_ticks + crypto_trades, isti metric shape kao SQL)
- In-memory state machine ostaje za runtime state/observability (OFF/WATCH/ACTIVE/STRONG)
- Discord GreenGrind alert ima DB parity safeguard (suppress ako DB snapshot ne potvrdi Active)

### Key Metrics (3h rolling window, 5min bars)
- `net_bps`: neto pomak cene u basis points
- `up_ratio`: procenat barova koji su gore
- `path_efficiency`: net_move / total_path (anti-chop)
- `pullback`: (max - close) / max
- `spike_concentration`: max_single_bar_delta / total_positive_delta (v1.1)
- `trades_3h`, `imb_3h`: flow confirmation (optional)

### Configured Thresholds (appsettings.crypto.bitfinex.json)
```
Watch:      net >= 60 bps,  up >= 0.54, eff >= 0.35
Active:     net >= 100 bps, up >= 0.56, eff >= 0.45
Strong:     net >= 180 bps, up >= 0.62, eff >= 0.55
Activation: net >= 120 bps, up >= 0.58, eff >= 0.48
Deactivation: net >= 60 bps, up >= 0.52, eff >= 0.35
MaxSpikeConcentration: 0.25
```

### Spike Filter (v1.1, added Feb 28)
Problem: spike (1-2 bara sa ogromnim pomerajem) moze da napumpa 3h metriku i izgleda
kao grind, ali nije. Primer: XRPUSDT Feb 28 -- 2 bara od 106+131 bps = 72% net move.

Resenje: `spike_concentration = max_bar_positive_delta / total_positive_delta`
- Pravi grind: ~0.05-0.10 (ravnomerno)
- Spike: > 0.25 (koncentrisano)
- Threshold: 0.25

---

## Live Testing Results (Feb 28 - Mar 1, 2026)

### Feb 28 Recap (14 trades, $14/position)
- Bez GG: 10W / 4L = +$0.81
- Sa GG hard gate: 1 trade (XAUTUSDT Strong) = -$0.54
- GG bi blokirao 9 winnera (ali sve sitnis: $0.03-$0.20)
- Jedini propusteni trade bio LOSS

### Mar 1 Recap (4 trades)
- XRPUSDT (Active) -> SL -$0.14
- ETHUSDT (Active) -> SL -$0.12
- SOLUSDT (Watch/blocked) -> SL -$0.13
- BTCUSDT (Watch/blocked) -> SL (expected)
- GG Active: 0/2 (oba loss)

### Key Observations
1. GG Active/Strong ne garantuje win -- samo znaci da je 3h window OK
2. Winneri izvan grinda su moguce (50:50), ali nisu konzistentni
3. Vecina "Active" signala u ova 2 dana su bili FALSE POSITIVES

---

## Identified Problems

### Problem 1: Bounce After Dump (KRITICAN)
3h prozor nema memoriju. Ne zna sta je bilo pre.

Primer: LTCUSDT Feb 28 vece
- LTC pao sa 54.50 na 51.50 (-550 bps) od 06:00-15:00
- Zatim se vratio na 53.90 od 18:00-20:00
- 3h prozor vidi: "lepo ide gore, eff OK" -> Active
- Realnost: dead cat bounce, ne pravi grind

Primer: BTCUSDT Mar 1
- BTC spike od 66200 na 68000, pa nazad na 67000
- 3h prozor vidi pozitivan net -> Active signal
- Realnost: spike + testera

### Problem 2: 3h Window Too Short for Context
Korisnik vidi razliku na grafu odmah:
- PRAVI GRIND: cena na NOVOM HIGH-u, nikad nije bila visa
- LAZNI BOUNCE: cena se VRACA gde je bila pre pada

3h prozor ne moze ovo da razlikuje jer nema kontekst.

### Problem 3: Complexity
Trenutno 3 filtera + sub-filteri:
- Macro Trend (active) -- smer
- LQ / Local Trend Quality (DryRun) -- chop
- Green Grind (DryRun) -- regime
- Spike filter (deo GG)

Previse slojeva koji se preklapaju.

---

## v2 Design: Context Window ("Dvoriste" Filter)

### Core Insight
Razlika izmedju pravog grinda i bouncea: gde je cena u odnosu na SIRI kontekst.

Analogija: 3h prozor = gledas iz sobe. 12h kontekst = izasao si u dvoriste.

### Formula
```
pct_of_12h_high = current_price / max(high, last 12h)
```

Klasifikacija:
- `>= 99.8%` -> NEW_HIGH: pravi grind, novi territory -> TRADE
- `98.5-99.8%` -> NEAR_HIGH: blizu ali ne novi -> OPREZ
- `< 98.5%` -> BELOW: bounce/recovery -> NE TRGUJ

### Validation (BTC Feb 25 - Mar 1)

Feb 25 (PRAVI GRIND):
- 8/12 signala = NEW_HIGH (>= 99.8%)
- Cena konstantno pravi nove 12h highs
- Svi tradovi u ovoj zoni = winneri

Feb 28 15:00 (BOUNCE):
- pct_of_12h_high = 98.28% -> BELOW
- 3h net = +120 bps (izgleda kao grind!)
- Formula ispravno kaze: NE, bounce

Mar 1 03:00-05:00 (DEGRADACIJA):
- Pocinje 100.18% (NEW_HIGH) -> pada na 99.13% (NEAR_HIGH)
- Gubi momentum -- korektno signalizira slabljenje

### Implementation Plan (implemented in v1.2)
Dodati u Green Grind evaluation:
1. Query max(high) from market_ticks za poslednjih 12h PRE rolling window
2. Compute `pct_of_context_high = current_mid / context_high`
3. Dodati kao uslov za Active/Strong aktivaciju
4. Config: `MinContextHighPct` (default 0.998)
5. Log: `ctx_high_pct` u snapshot za dijagnostiku

### Simplified Filter Stack (Target)
```
1. Green Grind (sa context window) -- jedini gate
   - 3h metrike (net, eff, up_ratio, spike)
   - 12h kontekst (pct_of_high)
   - State machine sa hysteresis
2. Macro Trend -- samo blokira clear downtrend (score < -X)
3. LQ -- DryRun / monitoring only
```

---

## Data Sources
- `market_ticks`: bid, ask -> mid price, bucketed 5min
- `trade_fills`: realized PnL, win/loss analysis
- `crypto_trades`: trade count, buy/sell imbalance (flow confirmation)

---

## User's Visual Pattern Recognition

Korisnik prepoznaje na grafu:
1. PRAVI GRIND: stepenast rast, svaka sveca zelena, volume raste, NOVI HIGHS
2. TESTERA: cik-cak, gore-dole, svi idu u stop loss
3. BOUNCE: pad pa recovery -- izgleda kao grind ali cena jos ispod prethodnog nivoa
4. SPIKE: jedan skok pa nazad -- koncentrisano kretanje

Razliciti timeframe-ovi:
- 1 dan (15min): vidi opsti pravac ali ne detalje
- 6h (5min): vidi testera jasno
- 1h (1min): vidi tacan smer i timing

Kljucno: pravi grind se desava 4-5 dana mesecno. Ostatak vremena = testera/flat.
Bolje ne trgovati 25 dana i zaraditi na 5, nego gurati 50:50 svaki dan.

---

## Open Questions

- Da li 12h kontekst treba biti fiksan ili adaptivan (ATR-based)?
- Da li koristiti high_12h ili high_24h za kontekst?
- Threshold 99.8% -- treba li kalibracija po simbolu (XAUT vs BTC)?
- Da li potpuno ukloniti Macro Trend kad GG bude hard gate?
- Da li STRONG state treba zahtevati NEW_HIGH a Active moze NEAR_HIGH?
- Per-symbol MinContextHighPct overrides?

---

## Rollout Timeline

- Phase 0 (DONE): SQL analysis, feature validation
- Phase 1 (CURRENT): DryRun, live log monitoring, threshold calibration
- Phase 1.1 (DONE): Spike filter dodat
- Phase 1.2 (DONE): Context window (12h high) implementiran + testiran na live feed-u
- Phase 2 (NEXT): Hard gate aktivacija (nakon stabilne validacije false-positive rate-a)
- Phase 3: Macro Trend simplification / removal

---

## Non-Goals
- Ne uvoditi ML/modeling u v1/v2
- Ne mesati sa IBKR logikom
- Ne dodavati kompleksnost koja duplira postojece filtere
- Ne forsirati hard gate pre dovoljno live podataka
