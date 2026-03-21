# Trend Analysis - Koraci i Scope

Namena:
- zajednicki trend filter za IBKR + Crypto
- jedan izvor istine za trend odluku pre entry signala
- bez dupliranja RTH/session pravila koja vec postoje

## Dogovorene odluke
- Ne diramo `pullback-config.json`.
- Svaki exchange koristi svoj config i dobija flag `EnableTrendAnalysis`.
- RTH/open block ostaje gde je sada (orchestrator/session guard), ne duplira se u trend filteru.

## Status tabla
- `[ ]` nije uradjeno
- `[~]` u toku
- `[x]` uradjeno

## Faza 0 - Priprema (analiza bez koda)
- [x] Definisan cilj i granice izmene
- [x] Potvrdjen config pristup po exchange fajlovima
- [x] Potvrdjeno: nema duplog open-block pravila

## Faza 1 - Data sloj (shared za IBKR + Crypto)
Cilj: zajednicki model + repository kontrakt.

- [x] Dodati model u `src/Denis.TradingEngine.Data/Models`
Tip:
- `MarketTrendSnapshot` (predlog)
Polja (minimalno):
- `Source` (`IBKR` / `CRYPTO`)
- `Exchange`
- `Symbol`
- `WindowMinutes`
- `Direction` (`Up` / `Down` / `Neutral`)
- `Score`
- `ComputedAtUtc`

- [x] Dodati repository u `src/Denis.TradingEngine.Data/Repositories`
Tip:
- `MarketTrendRepository` (predlog)
Metode (minimalno):
- `UpsertSnapshotAsync(...)`
- `GetLatestAsync(exchange, symbol, windowMinutes, ct)`
- `GetLatestBySourceAsync(source, symbol, windowMinutes, ct)`

Done kriterijum:
- model i repo rade za oba source-a bez posebnih duplih klasa

## Faza 2 - Config (po exchange)
Cilj: feature toggle po marketu.

- [x] Dodati `EnableTrendAnalysis` u IBKR config (`src/Denis.TradingEngine.App/appsettings.json`)
- [x] Dodati `EnableTrendAnalysis` u crypto confige:
- `src/Denis.TradingEngine.Exchange.Crypto/appsettings.crypto.bitfinex.json`
- `src/Denis.TradingEngine.Exchange.Crypto/appsettings.crypto.kraken.json`
- `src/Denis.TradingEngine.Exchange.Crypto/appsettings.crypto.deribit.json`
- `src/Denis.TradingEngine.Exchange.Crypto/appsettings.crypto.bybit.json`

Done kriterijum:
- [x] svaki runtime cita svoj flag bez fallback konfuzije

Dogovoreni props:
- Svi marketi:
- `EnableTrendAnalysis`
- `TrendTimeWindowMinutes` (npr. 180 crypto, 60 ibkr)
- IBKR dodatno:
- `TrendUseExplicitRange`
- `TrendRangeStartLocal`
- `TrendRangeEndLocal`
- `TrendRangeTimeZone`

## Faza 3 - Strategy Trend folder (core kontrakti)
Cilj: uvodimo trend API koji strategija poziva.

- [x] Dodati u `src/Denis.TradingEngine.Strategy/Trend`:
- `TrendDirection` enum
- `TrendContext` model
- `ITrendContextProvider` interfejs

Predlog metode:
- `GetTrendContextAsync(exchange, symbol, quoteTsUtc, ct)`

Done kriterijum:
- [x] strategija moze da trazi trend kontekst bez znanja da li je source IBKR ili Crypto

## Faza 4 - Provider implementacije
Cilj: jedan provider za svaki tok podataka, isti izlaz.

- [x] Dodati IBKR provider (cita osnovu iz `market_ticks` / shared repo)
- [x] Dodati Crypto provider (cita osnovu iz trade/snapshot toka / shared repo)
- [x] Normalizovati rezultat na isti `TrendContext`

Done kriterijum:
- [x] oba providera vracaju isti format sa istom semantikom

## Faza 5 - Integracija trend gate-a u signal tok
Cilj: trend odluka pre order submission, uz upis u `trade_signals.reject_reason`.

- [x] U orchestrator tok dodati trend check pre risk/order faze
- [x] Pravilo:
- ako je trend `Down` -> blok entry
- ako je `Up` -> dozvoli (ako ostali filteri prolaze)
- ako je `Neutral` -> dozvoli (default fail-open)
- [x] Reject reason standard:
- `macro-trend-block`

Done kriterijum:
- [x] signal ulazi samo kad trend gate dozvoli
- [x] trend reject razlog ide u `trade_signals.reject_reason`

## Faza 6 - Wiring (App + Crypto)
Cilj: provajderi i repo ubodeni u postojeci startup tok.

- [x] `Denis.TradingEngine.App/Program.cs`:
- registracija IBKR trend provider-a i prosledjivanje u strategiju
- [x] `Denis.TradingEngine.Exchange.Crypto/Program.cs` i/ili `Trading/CryptoTradingRunner.cs`:
- registracija Crypto trend provider-a i prosledjivanje u strategiju
- [ ] Potvrda da se ne menja postojece:
- day-guard
- RTH guard
- risk sizing

Done kriterijum:
- [x] oba runtime toka koriste trend gate bez regresije postojece logike

## Faza 7 - Validacija i rollout
Cilj: bezbedno uvodjenje velike izmene.

- [ ] Test scenariji:
- trend `Down` blokira entry
- trend `Up` prolazi
- `EnableTrendAnalysis=false` potpuno gasi trend gate
- [ ] Paper verifikacija prvo:
- IBKR paper
- Crypto paper
- [ ] Tek posle toga real rollout

Done kriterijum:
- nema duplih istina i nema neocekivanih blokova entry-ja

## Rizici koje pratimo
- Previse agresivan trend filter smanjuje broj validnih ulaza.
- Nekonzistentan source time (`quoteTs` vs DB timestamp) moze dati pogresan trend.
- Ako config fallback nije cist, moguce je da jedan exchange cita tudji flag.

## Pravilo rada dalje
- Svaki novi korak krece tek kad odobris prethodni.
- Posle svake faze oznacavamo status ovde (`[ ]` -> `[x]`).

## Trenutni status (live)
- [x] Faza 0 - priprema
- [x] Faza 1 - data model + repository
- [x] Faza 2 - config flag + trend window props
- [x] Faza 3 - strategy trend kontrakti
- [x] Faza 4 - provider implementacije
- [x] Faza 5 - trend gate + reject reason u trade_signals
- [x] Faza 6 - app/crypto wiring
- [ ] Faza 7 - validacija i rollout

Sledece odmah:
- Faza 7 (validacija i rollout)

## Implementacioni log (sta je stvarno dodato)

### Data
- `src/Denis.TradingEngine.Data/Models/TrendMarketDataPoint.cs`
- `src/Denis.TradingEngine.Data/Repositories/TrendMarketDataRepository.cs`

### Strategy Trend core
- `src/Denis.TradingEngine.Strategy/Trend/TrendDirection.cs`
- `src/Denis.TradingEngine.Strategy/Trend/TrendContext.cs`
- `src/Denis.TradingEngine.Strategy/Trend/ITrendContextProvider.cs`
- `src/Denis.TradingEngine.Strategy/Trend/TrendAnalysisSettings.cs`
- `src/Denis.TradingEngine.Strategy/Trend/TrendPriceMath.cs`
- `src/Denis.TradingEngine.Strategy/Trend/CryptoTrendContextProvider.cs`
- `src/Denis.TradingEngine.Strategy/Trend/IbkrTrendContextProvider.cs`

### Orchestrator trend gate (reject reason ide u trade_signals)
- `src/Denis.TradingEngine.App/Trading/TradingOrchestrator.cs`
- `src/Denis.TradingEngine.Exchange.Crypto/Trading/CryptoTradingOrchestrator.cs`

### Wiring
- `src/Denis.TradingEngine.App/Program.cs`
- `src/Denis.TradingEngine.Exchange.Crypto/Program.cs`
- `src/Denis.TradingEngine.Exchange.Crypto/Trading/CryptoTradingRunner.cs`

### Config (Trading sekcija)
- `src/Denis.TradingEngine.App/appsettings.json`
- `src/Denis.TradingEngine.Exchange.Crypto/appsettings.crypto.bitfinex.json`
- `src/Denis.TradingEngine.Exchange.Crypto/appsettings.crypto.kraken.json`
- `src/Denis.TradingEngine.Exchange.Crypto/appsettings.crypto.deribit.json`
- `src/Denis.TradingEngine.Exchange.Crypto/appsettings.crypto.bybit.json`

## Finalna pravila (dogovoreno)
- `EnableTrendAnalysis=false` -> trend filter OFF.
- `EnableTrendAnalysis=true` -> trend filter ON.
- `TrendUseExplicitRange=false` -> koristi `TrendTimeWindowMinutes`.
- `TrendUseExplicitRange=true` -> koristi `TrendRangeStartLocal/TrendRangeEndLocal/TrendRangeTimeZone`.
- `Unknown/null trend` -> allow (fail-open).
- `Down` -> block.
- `Up` -> allow.
- `Neutral` -> allow.
- Reject razlog ide samo u `trade_signals.reject_reason`:
- `macro-trend-block:down:score=...`

## Quality scoring (dodato)
- Endpoint + slope + drawdown penalty model.
- Parametri:
- `TrendUseQualityScoring`
- `TrendMinPoints`
- `TrendNeutralThresholdFraction`
- `TrendEndpointWeight`
- `TrendSlopeWeight`
- `TrendDrawdownPenaltyWeight`
- `TrendMaxDrawdownClampFraction`

## Napomena o validaciji
- Dogovoreno je live-first (bez paper faze).
- Operativno pratiti:
- `trade_signals.reject_reason` (posebno `macro-trend-block:*`)
- broj blokiranih vs prihvacenih signala po simbolu.
