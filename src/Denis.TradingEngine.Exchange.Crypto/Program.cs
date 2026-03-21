#nullable enable
using Denis.TradingEngine.Core.Trading;
using Denis.TradingEngine.Data;
using Denis.TradingEngine.Data.Repositories;
using Denis.TradingEngine.Data.Repositories.Funding;
using Denis.TradingEngine.Data.Runtime;
using Denis.TradingEngine.Core.Interfaces;
using Denis.TradingEngine.Core.Risk;
using Denis.TradingEngine.Core.Swing;
using Denis.TradingEngine.Exchange.Crypto.Trading;
using Denis.TradingEngine.Exchange.Crypto.Abstractions;
using Denis.TradingEngine.Strategy.Filters;
using Denis.TradingEngine.Strategy.Pullback;
using Denis.TradingEngine.Strategy.Adaptive;
using Denis.TradingEngine.Strategy.Scalp;
using Denis.TradingEngine.Strategy.Trend;
using Denis.TradingEngine.Core.Accounts;
using Denis.TradingEngine.Risk;
using Denis.TradingEngine.Core.Crypto;
using Denis.TradingEngine.Exchange.Crypto.Adapters;
using Denis.TradingEngine.Exchange.Crypto.Bitfinex;
using Denis.TradingEngine.Exchange.Crypto.Bitfinex.Funding.Api;
using Denis.TradingEngine.Exchange.Crypto.Bitfinex.Funding.Runtime;
using Denis.TradingEngine.Exchange.Crypto.Bitfinex.Funding.Stream;
using Denis.TradingEngine.Exchange.Crypto.Bybit;
using Denis.TradingEngine.Exchange.Crypto.Bybit.Config;
using Denis.TradingEngine.Exchange.Crypto.Common;
using Denis.TradingEngine.Exchange.Crypto.Config;
using Denis.TradingEngine.Exchange.Crypto.Deribit;
using Denis.TradingEngine.Exchange.Crypto.Kraken;
using Denis.TradingEngine.Exchange.Crypto.Kraken.Config;
using Denis.TradingEngine.Exchange.Crypto.Monitoring;
using Denis.TradingEngine.Logging;
using Denis.TradingEngine.Logging.Discord;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace Denis.TradingEngine.Exchange.Crypto
{
    //    Kako pokrećeš:
    //.\Denis.TradingEngine.Exchange.Crypto.exe all --rest
    public static class CryptoTestProgram
    {
        public static async Task Main(string[] args)
        {
            AppLog.Init(appName: "CryptoTest", env: "dev");
            var log = AppLog.ForContext(typeof(CryptoTestProgram));

            // Parsiranje argumenata (opciono - ako nema argumenata, pokreni sve)
            var useRest = args.Any(a => a.Equals("--rest", StringComparison.OrdinalIgnoreCase));
            var exchangeArg = args.FirstOrDefault()?.Trim().ToLowerInvariant() ?? "bitfinex";

            log.Information("==============================================");
            log.Information("[CRYPTO] Starting Crypto Trading Engine");
            log.Information("[CRYPTO] Exchange: {Exchange} | REST sanity: {Rest}", exchangeArg, useRest);
            log.Information("==============================================");

            // Učitaj config za bazu (proba sve exchange-e dok ne nađe connection string)
            var pgConnStr = GetPostgresConnectionString(log);

            // Inicijalizacija baze za market data (orderbooks, ticks, snapshots)
            CryptoOrderBookService? orderBookService = null;
            BoundedTickQueue? tickQueue = null;
            CryptoSnapshotRepository? snapshotRepo = null;

            if (!string.IsNullOrWhiteSpace(pgConnStr))
            {
                var dbFactory = new PgConnectionFactory(pgConnStr, log);

                // Orderbooks
                // orderBookService = new CryptoOrderBookService(
                //     dbFactory,
                //     queueCapacity: 1000,
                //     batchSize: 50,
                //     maxBatchDelay: TimeSpan.FromMilliseconds(1000),
                //     log: log);
                // Quick fix: privremeno gasimo DB persistence za crypto orderbook-e.
                // In-memory orderbook tok ostaje aktivan preko exchange feed-ova i koristi se za scalp/runtime.
                // Kada finalizujemo novi analytics storage model, vraticemo dedicated orderbook writer.
                orderBookService = null;
                log.Information("[CRYPTO-DB] OrderBook DB persistence is temporarily disabled; runtime/scalp cache remains active");
                if (orderBookService is not null)
                {
                log.Information("[CRYPTO-DB] OrderBookService initialized - orderbooks će se snimati u bazu");

                }

                // Market ticks
                var tickRepo = new MarketTickRepository(dbFactory, log);
                tickQueue = new BoundedTickQueue(
                    capacity: 5000,
                    onTickAsync: _ => Task.CompletedTask,
                    tickRepo: tickRepo,
                    batchSize: 100,
                    maxBatchDelay: TimeSpan.FromMilliseconds(200),
                    log: log);
                log.Information("[CRYPTO-DB] BoundedTickQueue initialized - market ticks će se snimati u bazu");

                // Snapshots (trades, tickers)
                snapshotRepo = new CryptoSnapshotRepository(dbFactory, log);
                log.Information("[CRYPTO-DB] CryptoSnapshotRepository initialized - snapshots će se snimati u bazu");
            }
            else
            {
                log.Warning("[CRYPTO-DB] Postgres:ConnectionString nije konfigurisan - orderbooks, ticks i snapshots se neće snimati u bazu");
            }

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                log.Information("[CRYPTO] Shutdown requested...");
            };

            try
            {
                // POKRENI SVE: Market data + Trading zajedno
                log.Information("[CRYPTO] Starting MARKET DATA + TRADING mode");

                if (exchangeArg == "all")
                {
                    // Pokreni sve exchange-e paralelno
                    await Task.WhenAll(
                        RunCryptoTradingAsyncSafe("kraken", useRest, log, orderBookService, tickQueue, snapshotRepo, cts.Token),
                        RunCryptoTradingAsyncSafe("bitfinex", useRest, log, orderBookService, tickQueue, snapshotRepo, cts.Token),
                        RunCryptoTradingAsyncSafe("deribit", useRest, log, orderBookService, tickQueue, snapshotRepo, cts.Token),
                        RunCryptoTradingAsyncSafe("bybit", useRest, log, orderBookService, tickQueue, snapshotRepo, cts.Token)
                    ).ConfigureAwait(false);
                }
                else
                {
                    // Pokreni jedan exchange
                    await RunCryptoTradingAsync(exchangeArg, useRest, log, orderBookService, tickQueue, snapshotRepo, cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                log.Information("[CRYPTO] Shutdown completed");
            }
            catch (Exception ex)
            {
                log.Error(ex, "[CRYPTO] Fatal error u CryptoTestProgram.");
            }
            finally
            {
                log.Information("[CRYPTO] Cleaning up resources...");
                orderBookService?.Dispose();
                tickQueue?.Dispose();
                log.Information("[CRYPTO] Shutdown complete");
            }
        }

        private static string? GetPostgresConnectionString(ILogger log)
        {
            // Proba sve exchange config fajlove dok ne nađe connection string
            // Koristimo LoadCryptoConfig jer tražimo SAMO u crypto fajlovima
            var exchanges = new[] { "kraken", "bitfinex", "deribit", "bybit" };
            foreach (var ex in exchanges)
            {
                var cfg = LoadCryptoConfig($"appsettings.crypto.{ex}.json");
                var connStr = cfg["Postgres:ConnectionString"];
                if (!string.IsNullOrWhiteSpace(connStr))
                {
                    log.Information("[CRYPTO-DB] Found Postgres connection string in appsettings.crypto.{Exchange}.json", ex);
                    return connStr;
                }
            }

            // Fallback na generic appsettings.json (samo za connection string, ne za trading config)
            var fallbackCfg = LoadConfig("appsettings.json");
            return fallbackCfg["Postgres:ConnectionString"];
        }

        // =========================================================
        //  Shared helpers
        // =========================================================

        /// <summary>
        /// Učitava SAMO crypto specifični config fajl (appsettings.crypto.*.json) bez fallback-a na appsettings.json.
        /// Crypto projekat ne sme da koristi IBKR config iz appsettings.json.
        /// </summary>
        private static IConfigurationRoot LoadCryptoConfig(string fileName)
        {
            var basePath = AppContext.BaseDirectory;
            var fullPath = Path.Combine(basePath, fileName);
            var exists = File.Exists(fullPath);

            // Log za debugging
            if (!exists)
            {
                AppLog.ForContext(typeof(CryptoTestProgram)).Warning(
                    "[CRYPTO-CONFIG] Config file not found: {File} (BaseDirectory: {BaseDir})",
                    fileName, basePath);
            }

            return new ConfigurationBuilder()
                .SetBasePath(basePath)
                .AddJsonFile(fileName, optional: true, reloadOnChange: false)
                .Build();
        }

        /// <summary>
        /// Učitava config sa fallback-om na appsettings.json (koristi se samo za connection string).
        /// </summary>
        private static IConfigurationRoot LoadConfig(string fileName)
        {
            return new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile(fileName, optional: true, reloadOnChange: false)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false) // Fallback samo za connection string
                .Build();
        }

        private static List<CryptoExchangeSettings> ReadExchanges(IConfiguration cfg)
        {
            return cfg.GetSection("Crypto:Exchanges")
                       .Get<List<CryptoExchangeSettings>>()
                   ?? new List<CryptoExchangeSettings>();
        }

        private static CryptoExchangeSettings GetExchangeOrThrow(
            ILogger log,
            List<CryptoExchangeSettings> exchanges,
            CryptoExchangeId id,
            string configFile)
        {
            var enabled = exchanges.Where(e => e.Enabled).ToList();
            if (enabled.Count == 0)
                throw new InvalidOperationException($"Nema enabled exchanges u {configFile}");

            var ex = enabled.FirstOrDefault(e => e.ExchangeId == id);
            if (ex is null)
                throw new InvalidOperationException($"{id} nije pronađen u Crypto:Exchanges u {configFile}");

            log.Information("Učitane {Count} berze iz {File}, koristim {Name} ({Id})",
                enabled.Count, configFile, ex.Name, ex.ExchangeId);

            return ex;
        }

        private static IEnumerable<CryptoSymbol> ReadEnabledSymbols(CryptoExchangeSettings ex)
        {
            if (ex.Symbols is null)
                yield break;

            foreach (var s in ex.Symbols.Where(x => x.Enabled))
            {
                yield return new CryptoSymbol(
                    ExchangeId: ex.ExchangeId,
                    BaseAsset: s.BaseAsset,
                    QuoteAsset: s.QuoteAsset,
                    NativeSymbol: s.NativeSymbol
                );
            }
        }

        private static IReadOnlyDictionary<string, decimal> BuildPerSymbolBudgetMap(
            CryptoExchangeSettings? exchangeSettings,
            decimal defaultPerSymbolBudgetUsd)
        {
            var map = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

            if (exchangeSettings?.Symbols is null)
                return map;

            foreach (var s in exchangeSettings.Symbols.Where(x => x.Enabled))
            {
                var symbol = new CryptoSymbol(
                    ExchangeId: exchangeSettings.ExchangeId,
                    BaseAsset: s.BaseAsset,
                    QuoteAsset: s.QuoteAsset,
                    NativeSymbol: s.NativeSymbol
                );

                var budget = s.PerSymbolBudgetUsd.GetValueOrDefault(defaultPerSymbolBudgetUsd);
                if (budget <= 0m)
                    budget = defaultPerSymbolBudgetUsd;

                if (budget > 0m)
                    map[symbol.PublicSymbol] = budget;
            }

            return map;
        }

        // =========================================================
        //  KRAKEN
        // =========================================================

        private static async Task DeribitWsPrivateSanityAsync(CryptoExchangeSettings ex, DeribitWebSocketFeed ws, ILogger log, CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(ex.ApiKey) || string.IsNullOrWhiteSpace(ex.ApiSecret))
                {
                    log.Warning("[DERIBIT] Preskačem private sanity: nema ApiKey/ApiSecret u Crypto:Exchanges");
                    return;
                }

                var api = new DeribitTradingApi(ws, log);

                log.Information("[DERIBIT] AUTH sanity start...");
                var ok = await api.AuthenticateAsync(ex.ApiKey, ex.ApiSecret, ct).ConfigureAwait(false);

                if (!ok)
                {
                    log.Warning("[DERIBIT] AUTH nije uspeo (nastavljam market data)");
                    return;
                }

                log.Information("[DERIBIT] AUTH OK");

                var balance = await api.GetBalanceAsync("BTC", ct).ConfigureAwait(false);
                log.Information("[DERIBIT] Balance OK (len={Len})", balance?.Length ?? 0);

                var positions = await api.GetPositionsAsync("BTC", ct).ConfigureAwait(false);
                log.Information("[DERIBIT] Positions OK (len={Len})", positions?.Length ?? 0);
            }
            catch (Exception ex2)
            {
                log.Error(ex2, "[DERIBIT] Private sanity failed (nastavljam market data).");
            }
        }

        // =========================================================
        //  CRYPTO TRADING ORCHESTRATOR
        // =========================================================

        /// <summary>
        /// Wrapper za RunCryptoTradingAsync koji hvata exception-e i loguje ih.
        /// </summary>
        private static async Task RunCryptoTradingAsyncSafe(
            string exchangeArg,
            bool useRest,
            ILogger parentLog,
            CryptoOrderBookService? orderBookService,
            BoundedTickQueue? tickQueue,
            CryptoSnapshotRepository? snapshotRepo,
            CancellationToken ct)
        {
            parentLog.Information("[CRYPTO-TRADING-SAFE] Starting RunCryptoTradingAsyncSafe for exchange: {Exchange}", exchangeArg);
            try
            {
                await RunCryptoTradingAsync(exchangeArg, useRest, parentLog, orderBookService, tickQueue, snapshotRepo, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                parentLog.Error(ex, "[CRYPTO-TRADING-SAFE] Fatal error u RunCryptoTradingAsync za exchange: {Exchange}", exchangeArg);
            }
        }

        private static async Task RunCryptoTradingAsync(
            string exchangeArg,
            bool useRest,
            ILogger parentLog,
            CryptoOrderBookService? orderBookService,
            BoundedTickQueue? tickQueue,
            CryptoSnapshotRepository? snapshotRepo,
            CancellationToken ct)
        {
            var log = parentLog;
            log.Information("[CRYPTO-TRADING] Starting RunCryptoTradingAsync for exchange: {Exchange}", exchangeArg);

            // Učitaj config - SAMO crypto specifični fajl, bez fallback-a na appsettings.json
            var configFileName = $"appsettings.crypto.{exchangeArg}.json";
            log.Information("[CRYPTO-TRADING] Loading config file: {File}", configFileName);
            var cfg = LoadCryptoConfig(configFileName);
            var pgConnStr = cfg["Postgres:ConnectionString"];

            if (string.IsNullOrWhiteSpace(pgConnStr))
            {
                log.Error("[CRYPTO-TRADING] Postgres:ConnectionString nije konfigurisan u {File} - trading mode zahteva bazu!", configFileName);
                return;
            }

            log.Information("[CRYPTO-TRADING] Config loaded successfully for {Exchange}, Postgres connection string found", exchangeArg);

            var dbFactory = new PgConnectionFactory(pgConnStr, log);

            // Discord notifier
            var discordWebhookUrl = cfg["Discord:WebhookUrl"];
            DiscordNotifier? discordNotifier = null;
            if (!string.IsNullOrWhiteSpace(discordWebhookUrl))
            {
                discordNotifier = new DiscordNotifier(discordWebhookUrl, log);
                log.Information("[DISCORD] Discord notifier initialized for {Exchange} with webhook URL", exchangeArg);
            }
            else
            {
                log.Warning("[DISCORD] Discord:WebhookUrl nije konfigurisan u appsettings.crypto.{Exchange}.json - Discord notifikacije neće raditi!", exchangeArg);
            }

            // DEBUG: Log discordNotifier status
            log.Information("[DISCORD-DEBUG] discordNotifier is {Status} for {Exchange}",
                discordNotifier != null ? "INITIALIZED" : "NULL", exchangeArg);

            // Repositories
            var journalRepo = new TradeJournalRepository(dbFactory);
            var fillRepo = new TradeFillRepository(dbFactory, log);
            var signalRepo = new TradeSignalRepository(dbFactory, log);
            var orderRepo = new BrokerOrderRepository(dbFactory, log);
            var pnlRepo = new CryptoDailyPnlRepository(dbFactory, log, discordNotifier, System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(exchangeArg.ToLowerInvariant()));
            var swingRepo = new SwingPositionRepository(dbFactory, log);
            var slayerRepo = new SignalSlayerDecisionRepository(dbFactory, log);

            // Market data feed (zavisi od exchange-a)
            IMarketDataFeed? mdFeed = null;
            IOrderService? orderService = null;
            CryptoExchangeSettings? exchangeSettings = null;
            KrakenMarketDataFeed? krakenMd = null;
            BitfinexMarketDataFeed? bitfinexMd = null;
            DeribitMarketDataFeed? deribitMd = null;
            BybitMarketDataFeed? bybitMd = null;
            ICryptoWebSocketFeed? ws = null; // WebSocket feed za snapshot events
            Bitfinex.BitfinexOrderManager? bitfinexOrderManager = null; // Order manager za Bitfinex
            BitfinexFundingManager? bitfinexFundingManager = null;
            IActivityTicksProvider? activityTicksProvider = null; // Trade-based activity za strategiju (npr. Bitfinex)
            long tickCount = 0, orderBookCount = 0, tradeCount = 0, tickerCount = 0; // Za CRYPTO-MD-STATS (moraju biti pre switcha)

            log.Information("[CRYPTO-MD] Initializing market data feed for exchange: {Exchange}", exchangeArg);

            switch (exchangeArg.ToLowerInvariant())
            {
                case "kraken":
                    {
                        var exchanges = ReadExchanges(cfg);
                        exchangeSettings = GetExchangeOrThrow(log, exchanges, CryptoExchangeId.Kraken, $"appsettings.crypto.{exchangeArg}.json");

                        // DEBUG: Proveri RiskProfile nakon učitavanja
                        if (exchangeSettings?.RiskProfile != null)
                        {
                            log.Information("[CRYPTO-CONFIG] Kraken RiskProfile loaded: MaxTradesPerSymbolPerDay={PerSym}, MaxTradesPerDay={Total}",
                                exchangeSettings.RiskProfile.MaxTradesPerSymbolPerDay,
                                exchangeSettings.RiskProfile.MaxTradesPerDay);
                        }
                        else
                        {
                            log.Warning("[CRYPTO-CONFIG] Kraken RiskProfile is NULL after loading!");
                        }

                        var krakenWs = new KrakenWebSocketFeed(exchangeSettings.WebSocketUrl, log);
                        ws = krakenWs;
                        krakenMd = new KrakenMarketDataFeed(krakenWs, log);
                        mdFeed = krakenMd;

                        foreach (var meta in ReadEnabledSymbols(exchangeSettings))
                        {
                            krakenMd.AddSymbol(meta);
                        }

                        if (orderBookService != null)
                        {
                            orderBookService.Subscribe(krakenWs);
                        }

                        log.Information("[KRAKEN] Connecting WS...");
                        await krakenWs.ConnectAsync(ct).ConfigureAwait(false);
                        log.Information("[KRAKEN] WS connected.");

                        // Subscribe na orderbooks i ticker (za market data feed)
                        foreach (var meta in ReadEnabledSymbols(exchangeSettings))
                        {
                            await krakenWs.SubscribeOrderBookAsync(meta, ct).ConfigureAwait(false);

                            // Subscribe na ticker za market quotes
                            var engineSymbol = new Symbol(
                                Ticker: meta.PublicSymbol,
                                Currency: meta.QuoteAsset,
                                Exchange: exchangeArg.ToUpperInvariant());
                            mdFeed.SubscribeQuotes(engineSymbol);
                        }

                        // Order service (za sada null - paper mode)
                        // TODO: Implementirati KrakenOrderService kada bude potrebno
                        break;
                    }

                case "bitfinex":
                    {
                        var exchanges = ReadExchanges(cfg);
                        exchangeSettings = GetExchangeOrThrow(log, exchanges, CryptoExchangeId.Bitfinex, $"appsettings.crypto.bitfinex.json");

                        //test only
                        // await BitfinexStartupSmokeTest.RunAsync(exchangeSettings, log, ct).ConfigureAwait(false);

                        var bitfinexWs = new BitfinexWebSocketFeed(exchangeSettings.WebSocketUrl, log);
                        ws = bitfinexWs;
                        bitfinexMd = new BitfinexMarketDataFeed(bitfinexWs, log);
                        mdFeed = bitfinexMd;

                        foreach (var meta in ReadEnabledSymbols(exchangeSettings))
                        {
                            bitfinexMd.AddSymbol(meta);
                        }

                        if (orderBookService != null)
                        {
                            orderBookService.Subscribe(bitfinexWs);
                        }

                        log.Information("[BITFINEX] Connecting WS...");
                        await bitfinexWs.ConnectAsync(ct).ConfigureAwait(false);
                        log.Information("[BITFINEX] WS connected.");

                        // Bitfinex često vrati "subscribe: limit" ako pošaljemo burst subscribe zahteve.
                        // Uvedi mali throttle između ticker/book/trades subscribe poruka.
                        const int bitfinexWsSubscribeDelayMs = 100;

                        // Subscribe na orderbooks
                        foreach (var meta in ReadEnabledSymbols(exchangeSettings))
                        {
                            var engineSymbol = new Symbol(
                                Ticker: meta.PublicSymbol,
                                Currency: meta.QuoteAsset,
                                Exchange: meta.ExchangeId.ToString());
                            mdFeed.SubscribeQuotes(engineSymbol);
                            await Task.Delay(bitfinexWsSubscribeDelayMs, ct).ConfigureAwait(false);
                            await bitfinexWs.SubscribeOrderBookAsync(meta, ct).ConfigureAwait(false);
                            await Task.Delay(bitfinexWsSubscribeDelayMs, ct).ConfigureAwait(false);
                            await bitfinexWs.SubscribeTradesAsync(meta, ct).ConfigureAwait(false);
                            await Task.Delay(bitfinexWsSubscribeDelayMs, ct).ConfigureAwait(false);
                        }

                        // Activity ticks iz trade-ova (in-memory + batch DB) i provider za strategiju
                        var cryptoTradesRepo = new CryptoTradesRepository(dbFactory, log);
                        var tradeTicksBatchWriter = new CryptoTradeTicksBatchWriter(cryptoTradesRepo, "Bitfinex", log);
                        bitfinexWs.TradeReceived += tick =>
                        {
                            tradeTicksBatchWriter.Add(tick);
                            Interlocked.Increment(ref tradeCount);
                        };
                        tradeTicksBatchWriter.StartFlushLoop(ct);
                        activityTicksProvider = tradeTicksBatchWriter;
                        log.Information("[BITFINEX] Trade ticks batch writer and activity provider enabled");

                        // Order service (za sada null - paper mode)
                        // TODO: Implementirati BitfinexOrderService kada bude potrebno
                        break;
                    }

                case "deribit":
                    {
                        var exchanges = ReadExchanges(cfg);
                        exchangeSettings = GetExchangeOrThrow(log, exchanges, CryptoExchangeId.Deribit, $"appsettings.crypto.{exchangeArg}.json");

                        var deribitWs = new DeribitWebSocketFeed(exchangeSettings.WebSocketUrl, log);
                        ws = deribitWs;

                        log.Information("[DERIBIT] Connecting WS...");
                        await deribitWs.ConnectAsync(ct).ConfigureAwait(false);
                        log.Information("[DERIBIT] WS connected.");

                        if (useRest && !string.IsNullOrWhiteSpace(exchangeSettings.ApiKey))
                        {
                            await DeribitWsPrivateSanityAsync(exchangeSettings, deribitWs, log, ct).ConfigureAwait(false);
                        }

                        deribitMd = new DeribitMarketDataFeed(deribitWs, log);
                        mdFeed = deribitMd;

                        foreach (var meta in ReadEnabledSymbols(exchangeSettings))
                        {
                            deribitMd.AddSymbol(meta);
                        }

                        if (orderBookService != null)
                        {
                            orderBookService.Subscribe(deribitWs);
                        }

                        // Subscribe na orderbooks
                        foreach (var meta in ReadEnabledSymbols(exchangeSettings))
                        {
                            var engineSymbol = new Symbol(
                                Ticker: meta.PublicSymbol,
                                Currency: meta.QuoteAsset,
                                Exchange: exchangeArg.ToUpperInvariant());
                            mdFeed.SubscribeQuotes(engineSymbol);
                            await deribitWs.SubscribeOrderBookAsync(meta, ct).ConfigureAwait(false);
                        }

                        // Order service (za sada null - paper mode)
                        // TODO: Implementirati DeribitOrderService kada bude potrebno
                        break;
                    }

                case "bybit":
                    {
                        log.Information("[BYBIT] Starting Bybit initialization...");
                        var exchanges = ReadExchanges(cfg);
                        log.Information("[BYBIT] Found {Count} exchanges in config", exchanges.Count);
                        exchangeSettings = GetExchangeOrThrow(log, exchanges, CryptoExchangeId.Bybit, $"appsettings.crypto.{exchangeArg}.json");
                        log.Information("[BYBIT] Exchange settings loaded: Name={Name} Enabled={Enabled}", exchangeSettings.Name, exchangeSettings.Enabled);

                        var bybitWs = new BybitWebSocketFeed(exchangeSettings.WebSocketUrl, log);
                        ws = bybitWs;
                        bybitMd = new BybitMarketDataFeed(bybitWs, log);
                        mdFeed = bybitMd;

                        foreach (var meta in ReadEnabledSymbols(exchangeSettings))
                        {
                            bybitMd.AddSymbol(meta);
                        }

                        if (orderBookService != null)
                        {
                            orderBookService.Subscribe(bybitWs);
                        }

                        log.Information("[BYBIT] Connecting WS...");
                        await bybitWs.ConnectAsync(ct).ConfigureAwait(false);
                        log.Information("[BYBIT] WS connected.");

                        // Subscribe na orderbooks i ticker (za market data feed)
                        foreach (var meta in ReadEnabledSymbols(exchangeSettings))
                        {
                            await bybitWs.SubscribeOrderBookAsync(meta, ct).ConfigureAwait(false);
                            await bybitWs.SubscribeTickerAsync(meta, ct).ConfigureAwait(false);
                            await bybitWs.SubscribeTradesAsync(meta, ct).ConfigureAwait(false);

                            // Subscribe na ticker za market quotes
                            var engineSymbol = new Symbol(
                                Ticker: meta.PublicSymbol,
                                Currency: meta.QuoteAsset,
                                Exchange: meta.ExchangeId.ToString());
                            mdFeed.SubscribeQuotes(engineSymbol);
                        }

                        // Order service (za sada null - paper mode)
                        // TODO: Implementirati BybitOrderService kada bude potrebno
                        break;
                    }

                default:
                    log.Error("[CRYPTO-TRADING] Nepoznat exchange '{Exchange}' za trading mode", exchangeArg);
                    return;
            }

            if (mdFeed == null)
            {
                log.Error("[CRYPTO-TRADING] Nije moguće kreirati market data feed za {Exchange}", exchangeArg);
                return;
            }

            if (ws != null && exchangeSettings != null)
            {
                var tickerStallWatchdog = new CryptoTickerStallWatchdog(
                    exchange: exchangeArg,
                    log: log,
                    discordNotifier: discordNotifier,
                    staleAfter: TimeSpan.FromMinutes(5),
                    checkInterval: TimeSpan.FromSeconds(30),
                    alertCooldown: TimeSpan.FromMinutes(15));

                foreach (var meta in ReadEnabledSymbols(exchangeSettings))
                {
                    tickerStallWatchdog.RegisterExpectedSymbol(meta.PublicSymbol);
                }

                ws.TickerUpdated += tickerStallWatchdog.ObserveTicker;
                _ = tickerStallWatchdog.RunAsync(ct);
                log.Information("[MD-WATCHDOG] Ticker stall watchdog enabled for {Exchange}", exchangeArg.ToUpperInvariant());
            }

            // =========================================================
            //  TRADING MODE (read early, before order service creation)
            // =========================================================
            var tradingSection = cfg.GetSection("Trading");
            var modeStr = tradingSection.GetValue<string>("Mode", "Paper");
            var isRealMode = string.Equals(modeStr, "Real", StringComparison.OrdinalIgnoreCase);

            // =========================================================
            //  ORDER SERVICE (REAL MODE ONLY)
            // =========================================================
            ICryptoTradingApi? tradingApi = null;
            Abstractions.ICryptoSymbolMetadataProvider? symbolMetadataProvider = null;

            if (isRealMode && exchangeSettings != null)
            {
                try
                {

                    switch (exchangeArg.ToLowerInvariant())
                    {
                        case "kraken":
                            {
                                var krakenApiSection = cfg.GetSection("KrakenApi");
                                var krakenApiSettings = new Kraken.Config.KrakenApiSettings
                                {
                                    BaseUrl = krakenApiSection.GetValue<string>("BaseUrl") ?? "https://api.kraken.com",
                                    ApiKey = krakenApiSection.GetValue<string>("ApiKey") ?? string.Empty,
                                    ApiSecret = krakenApiSection.GetValue<string>("ApiSecret") ?? string.Empty
                                };

                                if (string.IsNullOrWhiteSpace(krakenApiSettings.ApiKey) || string.IsNullOrWhiteSpace(krakenApiSettings.ApiSecret))
                                {
                                    log.Warning("[CRYPTO-ORD] Kraken ApiKey/ApiSecret nisu konfigurisani - ne mogu kreirati order service. Nastavljam sa Paper mode.");
                                    isRealMode = false;
                                }
                                else
                                {
                                    tradingApi = new Kraken.KrakenTradingApi(krakenApiSettings, log);
                                    log.Information("[CRYPTO-ORD] KrakenTradingApi kreiran za REAL mode");
                                }
                                break;
                            }

                        case "bitfinex":
                            {
                                var bitfinexNonceProvider = new Bitfinex.BitfinexAuthNonceProvider();

                                if (string.IsNullOrWhiteSpace(exchangeSettings.ApiKey) || string.IsNullOrWhiteSpace(exchangeSettings.ApiSecret))
                                {
                                    log.Warning("[CRYPTO-ORD] Bitfinex ApiKey/ApiSecret nisu konfigurisani - ne mogu kreirati order service. Nastavljam sa Paper mode.");
                                    isRealMode = false;
                                }
                                else
                                {
                                    var bitfinexTradingApi = new Bitfinex.BitfinexTradingApi(
                                        exchangeSettings.RestBaseUrl,
                                        exchangeSettings.ApiKey,
                                        exchangeSettings.ApiSecret,
                                        log,
                                        bitfinexNonceProvider);
                                    tradingApi = bitfinexTradingApi; // Assign to interface for orderService
                                    log.Information("[CRYPTO-ORD] BitfinexTradingApi kreiran za REAL mode");

                                    // Kreiraj BitfinexOrderManager za real-time order updates + reconciliation
                                    bitfinexOrderManager = new Bitfinex.BitfinexOrderManager(
                                        orderRepo: orderRepo,
                                        tradingApi: bitfinexTradingApi,
                                        exchangeSettings: exchangeSettings,
                                        log: log,
                                        nonceProvider: bitfinexNonceProvider);
                                    bitfinexOrderManager.Start(ct);
                                    log.Information("[CRYPTO-ORD] BitfinexOrderManager pokrenut (Private WS + Reconciliation)");
                                }

                                if (exchangeSettings.Funding?.Enabled == true)
                                {
                                    var fundingApiKey = string.IsNullOrWhiteSpace(exchangeSettings.Funding.ApiKeyOverride)
                                        ? exchangeSettings.ApiKey
                                        : exchangeSettings.Funding.ApiKeyOverride;
                                    var fundingApiSecret = string.IsNullOrWhiteSpace(exchangeSettings.Funding.ApiSecretOverride)
                                        ? exchangeSettings.ApiSecret
                                        : exchangeSettings.Funding.ApiSecretOverride;
                                    var fundingWsApiKey = string.IsNullOrWhiteSpace(exchangeSettings.Funding.WsApiKeyOverride)
                                        ? fundingApiKey
                                        : exchangeSettings.Funding.WsApiKeyOverride;
                                    var fundingWsApiSecret = string.IsNullOrWhiteSpace(exchangeSettings.Funding.WsApiSecretOverride)
                                        ? fundingApiSecret
                                        : exchangeSettings.Funding.WsApiSecretOverride;
                                    var fundingUsesDedicatedCredentials =
                                        !string.IsNullOrWhiteSpace(exchangeSettings.Funding.ApiKeyOverride) ||
                                        !string.IsNullOrWhiteSpace(exchangeSettings.Funding.ApiSecretOverride);
                                    var fundingUsesDedicatedWsCredentials =
                                        !string.IsNullOrWhiteSpace(exchangeSettings.Funding.WsApiKeyOverride) ||
                                        !string.IsNullOrWhiteSpace(exchangeSettings.Funding.WsApiSecretOverride);
                                    var fundingRestNonceProvider = fundingUsesDedicatedCredentials
                                        ? new Bitfinex.BitfinexAuthNonceProvider()
                                        : bitfinexNonceProvider;
                                    var fundingWsNonceProvider = fundingUsesDedicatedWsCredentials
                                        ? new Bitfinex.BitfinexAuthNonceProvider()
                                        : fundingRestNonceProvider;

                                    if (exchangeSettings.Funding.UsePrivateWebSocket &&
                                        string.Equals(fundingApiKey, fundingWsApiKey, StringComparison.Ordinal) &&
                                        string.Equals(fundingApiSecret, fundingWsApiSecret, StringComparison.Ordinal))
                                    {
                                        log.Warning("[BFX-FUND] Funding REST and funding private WS are sharing the same API key. Bitfinex docs recommend separate API keys per authenticated client to avoid nonce errors.");
                                    }

                                    BitfinexFundingPrivateWebSocketFeed? fundingWsFeed = null;
                                    if (exchangeSettings.Funding.UsePrivateWebSocket)
                                    {
                                        fundingWsFeed = new BitfinexFundingPrivateWebSocketFeed(
                                            wsUrl: "wss://api.bitfinex.com/ws/2",
                                            apiKey: fundingWsApiKey,
                                            apiSecret: fundingWsApiSecret,
                                            fundingSymbols: exchangeSettings.Funding.PreferredSymbols,
                                            log: log,
                                            nonceProvider: fundingWsNonceProvider);
                                    }

                                    var fundingApi = new BitfinexFundingApi(
                                        exchangeSettings.RestBaseUrl,
                                        fundingApiKey,
                                        fundingApiSecret,
                                        log,
                                        fundingRestNonceProvider);
                                    var fundingRepo = new BitfinexFundingRepository(dbFactory, log);
                                    bitfinexFundingManager = new BitfinexFundingManager(
                                        exchangeSettings.Funding,
                                        fundingApi,
                                        fundingWsFeed,
                                        fundingRepo,
                                        snapshotRepo,
                                        log);
                                    bitfinexFundingManager.Start(ct);
                                    log.Information("[BFX-FUND] Funding manager startup hook registered");
                                }
                                break;
                            }

                        case "deribit":
                            {
                                if (ws is Deribit.DeribitWebSocketFeed deribitWs &&
                                    !string.IsNullOrWhiteSpace(exchangeSettings.ApiKey) &&
                                    !string.IsNullOrWhiteSpace(exchangeSettings.ApiSecret))
                                {
                                    tradingApi = new Deribit.DeribitTradingApi(deribitWs, log);
                                    log.Information("[CRYPTO-ORD] DeribitTradingApi kreiran za REAL mode");
                                }
                                else
                                {
                                    log.Warning("[CRYPTO-ORD] Deribit ApiKey/ApiSecret nisu konfigurisani ili WebSocket nije spreman - ne mogu kreirati order service. Nastavljam sa Paper mode");
                                    isRealMode = false;
                                }
                                break;
                            }

                        case "bybit":
                            {
                                var bybitApiSection = cfg.GetSection("BybitApi");
                                var bybitApiSettings = new BybitApiSettings
                                {
                                    BaseUrl = bybitApiSection.GetValue<string>("BaseUrl") ?? "https://api.bybit.com",
                                    ApiKey = bybitApiSection.GetValue<string>("ApiKey") ?? string.Empty,
                                    ApiSecret = bybitApiSection.GetValue<string>("ApiSecret") ?? string.Empty,
                                    RecvWindowMs = bybitApiSection.GetValue<int>("RecvWindowMs", 5000),
                                    DefaultCategory = bybitApiSection.GetValue<string>("DefaultCategory") ?? "spot"
                                };

                                if (string.IsNullOrWhiteSpace(bybitApiSettings.ApiKey) || string.IsNullOrWhiteSpace(bybitApiSettings.ApiSecret))
                                {
                                    log.Warning("[CRYPTO-ORD] Bybit ApiKey/ApiSecret nisu konfigurisani - ne mogu kreirati order service. Nastavljam sa Paper mode");
                                    isRealMode = false;
                                }
                                else
                                {
                                    tradingApi = new Bybit.BybitTradingApi(bybitApiSettings, log);
                                    log.Information("[CRYPTO-ORD] BybitTradingApi kreiran za REAL mode");
                                }
                                break;
                            }
                    }

                    if (tradingApi != null)
                    {
                        var exchangesList = ReadExchanges(cfg);
                        symbolMetadataProvider = new Common.CryptoSymbolMetadataProvider(exchangesList);

                        var apis = new Dictionary<Core.Crypto.CryptoExchangeId, Abstractions.ICryptoTradingApi>
                        {
                            { tradingApi.ExchangeId, tradingApi }
                        };

                        orderService = new Adapters.CryptoOrderService(apis, symbolMetadataProvider, log);
                        log.Information("[CRYPTO-ORD] CryptoOrderService kreiran za REAL mode - {Exchange}", exchangeArg.ToUpperInvariant());
                    }
                }
                catch (Exception ex)
                {
                    log.Error(ex, "[CRYPTO-ORD] Greška pri kreiranju order service za {Exchange} - nastavljam sa Paper mode", exchangeArg);
                    isRealMode = false;
                    orderService = null;
                }
            }

            // Market data snimanje i statistike (tickCount, orderBookCount, tradeCount, tickerCount već deklarisani gore)
            var statsInterval = TimeSpan.FromSeconds(30);

            // Market ticks queue
            if (tickQueue != null)
            {
                mdFeed.MarketQuoteUpdated += q =>
                {
                    Interlocked.Increment(ref tickCount);
                    _ = tickQueue.TryEnqueue(q);
                };
            }

            // Periodični task za statistike (loguje svakih 30 sekundi)
            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(statsInterval, ct).ConfigureAwait(false);

                        var ticks = Interlocked.Read(ref tickCount);
                        var ob = Interlocked.Read(ref orderBookCount);
                        var trades = Interlocked.Read(ref tradeCount);
                        var tickers = Interlocked.Read(ref tickerCount);

                        log.Debug("[CRYPTO-MD-STATS] {Exchange} Ticks={Ticks} OrderBooks={OB} Trades={Trades} Tickers={Tickers} (last {Interval}s)",
                            exchangeArg.ToUpperInvariant(), ticks, ob, trades, tickers, statsInterval.TotalSeconds);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        log.Warning(ex, "[CRYPTO-MD-STATS] Error logging stats for {Exchange}", exchangeArg);
                    }
                }
            }, ct);

            // Orderbook statistike (ako postoji orderBookService)
            if (orderBookService != null)
            {
                orderBookService.OrderBookReceived += _ =>
                {
                    Interlocked.Increment(ref orderBookCount);
                };
            }

            // Snapshot snimanje (trades, tickers) - ako postoji snapshotRepo
            if (snapshotRepo != null && ws != null)
            {
                // Kraken: TradeReceived event
                if (exchangeArg == "kraken")
                {
                    ws.TradeReceived += async tick =>
                    {
                        Interlocked.Increment(ref tradeCount);
                        try
                        {
                            await snapshotRepo.InsertAsync(
                                utc: DateTime.UtcNow,
                                exchange: exchangeArg,
                                symbol: tick.Symbol.PublicSymbol,
                                snapshotType: "trade",
                                data: tick,
                                metadata: null,
                                ct: CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            log.Warning(ex, "[CRYPTO-SNAPSHOT] Failed to save trade snapshot");
                        }
                    };
                }

                // Bitfinex: TickerUpdated event
                if (exchangeArg == "bitfinex")
                {
                    ws.TickerUpdated += async ticker =>
                    {
                        Interlocked.Increment(ref tickerCount);
                        try
                        {
                            await snapshotRepo.InsertAsync(
                                utc: DateTime.UtcNow,
                                exchange: exchangeArg,
                                symbol: ticker.Symbol.PublicSymbol,
                                snapshotType: "ticker",
                                data: ticker,
                                metadata: null,
                                ct: CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            log.Warning(ex, "[CRYPTO-SNAPSHOT] Failed to save ticker snapshot");
                        }
                    };
                }
            }

            // Strategy - Adaptive selector (automatski bira između scalp i pullback)
            var pullbackConfig = PullbackConfigProvider.GetConfig();

            // SignalSlayer config - čita iz appsettings.crypto.{exchange}.json
            var slayerSection = cfg.GetSection("SignalSlayer");
            var slayerConfig = new SignalSlayerConfig
            {
                EnableMicroFilter = slayerSection.GetValue<bool>("EnableMicroFilter", true),
                MinAtrFractionOfPrice = slayerSection.GetValue<decimal?>("MinAtrFractionOfPrice"),
                MaxAtrFractionOfPrice = slayerSection.GetValue<decimal?>("MaxAtrFractionOfPrice"),
                MaxSpreadBps = slayerSection.GetValue<decimal?>("MaxSpreadBps"),
                MinActivityTicks = slayerSection.GetValue<int?>("MinActivityTicks"),
                MaxSignalsPerSymbolPerDay = slayerSection.GetValue<int?>("MaxSignalsPerSymbolPerDay")
            };

            log.Information("[CRYPTO-SLAYER] Config loaded: EnableMicroFilter={Micro}, MinAtrFrac={MinAtr}, MaxSpreadBps={Spread}, MinActivityTicks={Ticks}",
                slayerConfig.EnableMicroFilter,
                slayerConfig.MinAtrFractionOfPrice?.ToString("E6") ?? "default",
                slayerConfig.MaxSpreadBps?.ToString("F1") ?? "default",
                slayerConfig.MinActivityTicks?.ToString() ?? "default");

            // Trading config (tradingSection je već deklarisan gore)

            var pullbackStrategy = new PullbackInUptrendStrategy(
                pullbackConfig,
                slayerConfig,
                slayerRepo: slayerRepo,
                runEnv: "Crypto",
                activityTicksProvider: activityTicksProvider);

            // Scalp strategija - opciono, kontrolisano preko UseScalp config parametra
            ScalpStrategy? scalpStrategy = null;
            MicroPullbackReversionStrategy? microPullbackStrategy = null;
            var useScalp = tradingSection.GetValue<bool>("UseScalp", false);
            var scalpDryRun = tradingSection.GetValue<bool>("ScalpDryRun", false);
            var useMicroPullbackReversion = tradingSection.GetValue<bool>("UseMicroPullbackReversion", false);
            var microPullbackDryRun = tradingSection.GetValue<bool>("MicroPullbackReversionDryRun", false);
            var scalpSymbols = tradingSection.GetSection("ScalpSymbols").Get<string[]>();
            var microPullbackSymbols = tradingSection.GetSection("MicroPullbackSymbols").Get<string[]>();
            var adaptiveScalpSection = tradingSection.GetSection("AdaptiveScalpSelection");
            var scalpStrategySection = tradingSection.GetSection("ScalpStrategy");
            var adaptiveMicroPullbackSection = tradingSection.GetSection("AdaptiveMicroPullbackSelection");
            var microPullbackStrategySection = tradingSection.GetSection("MicroPullbackReversionStrategy");

            if (useScalp && useMicroPullbackReversion)
            {
                log.Warning("[CRYPTO-EXPERIMENTS] UseScalp and UseMicroPullbackReversion are both enabled. Disabling MicroPullbackReversion to keep experimental flows isolated.");
                useMicroPullbackReversion = false;
            }

            var adaptiveScalpMinAtrFraction = adaptiveScalpSection.GetValue<decimal?>("MinAtrFraction")
                ?? 0.001m;
            var adaptiveScalpMinTicksPerWindow = adaptiveScalpSection.GetValue<int?>("MinTicksPerWindow")
                ?? 100;
            var adaptiveScalpMaxSpreadBps = adaptiveScalpSection.GetValue<decimal?>("MaxSpreadBps")
                ?? 10.0m;
            var adaptiveScalpMinReadySeconds = adaptiveScalpSection.GetValue<int?>("MinReadySeconds")
                ?? 20;
            var adaptiveScalpMinReadyQuotes = adaptiveScalpSection.GetValue<int?>("MinReadyQuotes")
                ?? 3;

            var scalpMaxSpreadBps = scalpStrategySection.GetValue<decimal?>("MaxSpreadBps")
                ?? 10.0m;
            var scalpMinLiquidityUsd = scalpStrategySection.GetValue<decimal?>("MinLiquidityUsd")
                ?? 1000m;
            var scalpProfitTargetPct = scalpStrategySection.GetValue<decimal?>("ProfitTargetPct")
                ?? 0.002m;
            var scalpStopLossPct = scalpStrategySection.GetValue<decimal?>("StopLossPct")
                ?? 0.001m;
            var scalpMinTicksForEntry = scalpStrategySection.GetValue<int?>("MinTicksForEntry")
                ?? 5;
            var scalpMaxHoldSeconds = scalpStrategySection.GetValue<int?>("MaxHoldSeconds")
                ?? 300;
            var scalpMinImbalanceRatio = scalpStrategySection.GetValue<decimal?>("MinImbalanceRatio")
                ?? 0.05m;
            var scalpMinMomentumBps = scalpStrategySection.GetValue<decimal?>("MinMomentumBps")
                ?? 0.5m;
            var scalpMinMicropriceEdgeBps = scalpStrategySection.GetValue<decimal?>("MinMicropriceEdgeBps")
                ?? 0.2m;
            var scalpMaxBookAgeMs = scalpStrategySection.GetValue<int?>("MaxBookAgeMs")
                ?? 1500;
            var scalpMomentumLookbackQuotes = scalpStrategySection.GetValue<int?>("MomentumLookbackQuotes")
                ?? 3;
            var scalpMomentumLookbackSeconds = scalpStrategySection.GetValue<int?>("MomentumLookbackSeconds")
                ?? 45;
            var scalpEntryConfirmationQuotes = scalpStrategySection.GetValue<int?>("EntryConfirmationQuotes")
                ?? 2;
            var scalpMinSignalPersistenceSeconds = scalpStrategySection.GetValue<int?>("MinSignalPersistenceSeconds")
                ?? 15;
            var scalpExitMaxSpreadBps = scalpStrategySection.GetValue<decimal?>("ExitMaxSpreadBps")
                ?? (scalpMaxSpreadBps + 2.0m);
            var scalpExitMinImbalanceRatio = scalpStrategySection.GetValue<decimal?>("ExitMinImbalanceRatio")
                ?? 0.10m;
            var scalpExitMinMicropriceEdgeBps = scalpStrategySection.GetValue<decimal?>("ExitMinMicropriceEdgeBps")
                ?? 0.05m;
            var scalpExitMinMomentumBps = scalpStrategySection.GetValue<decimal?>("ExitMinMomentumBps")
                ?? 0.0m;
            var scalpEdgeLossFailureThreshold = scalpStrategySection.GetValue<int?>("EdgeLossFailureThreshold")
                ?? 2;

            var adaptiveMicroPullbackMinAtrFraction = adaptiveMicroPullbackSection.GetValue<decimal?>("MinAtrFraction")
                ?? 0.00020m;
            var adaptiveMicroPullbackMinTicksPerWindow = adaptiveMicroPullbackSection.GetValue<int?>("MinTicksPerWindow")
                ?? 4;
            var adaptiveMicroPullbackMaxSpreadBps = adaptiveMicroPullbackSection.GetValue<decimal?>("MaxSpreadBps")
                ?? 9.0m;
            var adaptiveMicroPullbackMinReadySeconds = adaptiveMicroPullbackSection.GetValue<int?>("MinReadySeconds")
                ?? 15;
            var adaptiveMicroPullbackMinReadyQuotes = adaptiveMicroPullbackSection.GetValue<int?>("MinReadyQuotes")
                ?? 2;

            var microPullbackMaxSpreadBps = microPullbackStrategySection.GetValue<decimal?>("MaxSpreadBps")
                ?? 8.0m;
            var microPullbackMinLiquidityUsd = microPullbackStrategySection.GetValue<decimal?>("MinLiquidityUsd")
                ?? 2000m;
            var microPullbackMaxBookAgeMs = microPullbackStrategySection.GetValue<int?>("MaxBookAgeMs")
                ?? 1500;
            var microPullbackFairValueEmaQuotes = microPullbackStrategySection.GetValue<int?>("FairValueEmaQuotes")
                ?? 20;
            var microPullbackRecentVolatilityQuotes = microPullbackStrategySection.GetValue<int?>("RecentVolatilityQuotes")
                ?? 20;
            var microPullbackMinEffectiveVolatilityBps = microPullbackStrategySection.GetValue<decimal?>("MinEffectiveVolatilityBps")
                ?? 0.75m;
            var microPullbackMaxEffectiveVolatilityBps = microPullbackStrategySection.GetValue<decimal?>("MaxEffectiveVolatilityBps")
                ?? 12.0m;
            var microPullbackMinNormalizedDislocation = microPullbackStrategySection.GetValue<decimal?>("MinNormalizedDislocation")
                ?? 1.5m;
            var microPullbackMaxNormalizedDislocation = microPullbackStrategySection.GetValue<decimal?>("MaxNormalizedDislocation")
                ?? 4.0m;
            var microPullbackMaxContinuationMomentumBps = microPullbackStrategySection.GetValue<decimal?>("MaxContinuationMomentumBps")
                ?? 2.0m;
            var microPullbackMinMomentumDecayPct = microPullbackStrategySection.GetValue<decimal?>("MinMomentumDecayPct")
                ?? 0.30m;
            var microPullbackMinImbalanceRecovery = microPullbackStrategySection.GetValue<decimal?>("MinImbalanceRecovery")
                ?? 0.10m;
            var microPullbackMinMicropriceRecoveryBps = microPullbackStrategySection.GetValue<decimal?>("MinMicropriceRecoveryBps")
                ?? 0.20m;
            var microPullbackReclaimLookbackQuotes = microPullbackStrategySection.GetValue<int?>("ReclaimLookbackQuotes")
                ?? 3;
            var microPullbackEnableEarlyReclaim = microPullbackStrategySection.GetValue<bool?>("EnableEarlyReclaim")
                ?? true;
            var microPullbackEnableConfirmedReclaim = microPullbackStrategySection.GetValue<bool?>("EnableConfirmedReclaim")
                ?? true;
            var microPullbackMinReclaimMomentumBps = microPullbackStrategySection.GetValue<decimal?>("MinReclaimMomentumBps")
                ?? 0.25m;
            var microPullbackMicroTakeProfitBps = microPullbackStrategySection.GetValue<decimal?>("MicroTakeProfitBps")
                ?? 2.0m;
            var microPullbackProfitTargetBps = microPullbackStrategySection.GetValue<decimal?>("ProfitTargetBps")
                ?? 4.0m;
            var microPullbackStopLossBps = microPullbackStrategySection.GetValue<decimal?>("StopLossBps")
                ?? 4.0m;
            var microPullbackMaxTimeToFirstPositiveMfeSeconds = microPullbackStrategySection.GetValue<int?>("MaxTimeToFirstPositiveMfeSeconds")
                ?? 2;
            var microPullbackMfeProtectMinBps = microPullbackStrategySection.GetValue<decimal?>("MfeProtectMinBps")
                ?? 2.0m;
            var microPullbackMfeGivebackBps = microPullbackStrategySection.GetValue<decimal?>("MfeGivebackBps")
                ?? 2.0m;
            var microPullbackMaxStallSeconds = microPullbackStrategySection.GetValue<int?>("MaxStallSeconds")
                ?? 2;
            var microPullbackExpectedReversionSeconds = microPullbackStrategySection.GetValue<int?>("ExpectedReversionSeconds")
                ?? 3;
            var microPullbackMaxHoldSeconds = microPullbackStrategySection.GetValue<int?>("MaxHoldSeconds")
                ?? 6;
            var microPullbackCooldownSeconds = microPullbackStrategySection.GetValue<int?>("CooldownSeconds")
                ?? 5;
            var microPullbackOneShotPerMove = microPullbackStrategySection.GetValue<bool?>("OneShotPerMove")
                ?? true;

            if (useScalp)
            {
                scalpStrategy = new ScalpStrategy(
                    maxSpreadBps: scalpMaxSpreadBps,
                    minLiquidityUsd: scalpMinLiquidityUsd,
                    profitTargetPct: scalpProfitTargetPct,
                    stopLossPct: scalpStopLossPct,
                    minTicksForEntry: scalpMinTicksForEntry,
                    maxHoldTime: TimeSpan.FromSeconds(scalpMaxHoldSeconds),
                    minImbalanceRatio: scalpMinImbalanceRatio,
                    minMomentumBps: scalpMinMomentumBps,
                    minMicropriceEdgeBps: scalpMinMicropriceEdgeBps,
                    maxBookAge: TimeSpan.FromMilliseconds(scalpMaxBookAgeMs),
                    momentumLookbackQuotes: scalpMomentumLookbackQuotes,
                    momentumLookbackWindow: TimeSpan.FromSeconds(scalpMomentumLookbackSeconds),
                    entryConfirmationQuotes: scalpEntryConfirmationQuotes,
                    minSignalPersistence: TimeSpan.FromSeconds(scalpMinSignalPersistenceSeconds),
                    exitMaxSpreadBps: scalpExitMaxSpreadBps,
                    exitMinImbalanceRatio: scalpExitMinImbalanceRatio,
                    exitMinMicropriceEdgeBps: scalpExitMinMicropriceEdgeBps,
                    exitMinMomentumBps: scalpExitMinMomentumBps,
                    edgeLossFailureThreshold: scalpEdgeLossFailureThreshold);

                log.Information(
                    "[CRYPTO-SCALP] Enabled symbols={Symbols} momentumQuotes={MomentumQuotes} momentumWindow={MomentumWindow}s confirmQuotes={ConfirmQuotes} persistence={Persistence}s exitSpread={ExitSpread:F1}bps edgeFailures={EdgeFailures}",
                    scalpSymbols is { Length: > 0 } ? string.Join(",", scalpSymbols) : "adaptive-all",
                    scalpMomentumLookbackQuotes,
                    scalpMomentumLookbackSeconds,
                    scalpEntryConfirmationQuotes,
                    scalpMinSignalPersistenceSeconds,
                    scalpExitMaxSpreadBps,
                    scalpEdgeLossFailureThreshold);

                // Prosleđuj orderbook update-e u scalp strategiju
                if (ws is IOrderBookFeed orderBookFeed)
                {
                    orderBookFeed.OrderBookUpdated += ob =>
                    {
                        // Mapiraj OrderBookUpdate na ScalpStrategy format
                        scalpStrategy.OnOrderBook(ob);
                    };
                    var replayedBooks = 0;
                    if (exchangeSettings != null && orderBookService != null)
                    {
                        foreach (var meta in ReadEnabledSymbols(exchangeSettings))
                        {
                            if (orderBookService.TryGetLatestUsableOrderBook(meta.ExchangeId, meta.PublicSymbol, out var cachedBook))
                            {
                                scalpStrategy.OnOrderBook(cachedBook);
                                replayedBooks++;
                            }
                        }
                    }
                    log.Information("[CRYPTO-SCALP] OrderBook feed connected to ScalpStrategy (Mode={Mode})",
                        scalpDryRun ? "DryRun" : "Live");
                    log.Information("[CRYPTO-SCALP] Replayed {Count} cached usable orderbooks into ScalpStrategy",
                        replayedBooks);
                }
                else
                {
                    log.Warning("[CRYPTO-SCALP] WebSocket feed does not implement IOrderBookFeed - scalp strategija neće primati orderbook update-e");
                }
            }
            else
            {
                log.Information("[CRYPTO-SCALP] Scalp strategija je onemogućena (UseScalp=false) - koristimo samo Pullback strategiju");
            }

            if (!useScalp && scalpDryRun)
            {
                log.Information("[CRYPTO-SCALP] ScalpDryRun=true ignored because UseScalp=false");
            }

            var strategyStartUtc = DateTime.UtcNow;
            if (useMicroPullbackReversion)
            {
                microPullbackStrategy = new MicroPullbackReversionStrategy(
                    maxSpreadBps: microPullbackMaxSpreadBps,
                    minLiquidityUsd: microPullbackMinLiquidityUsd,
                    maxBookAge: TimeSpan.FromMilliseconds(microPullbackMaxBookAgeMs),
                    fairValueEmaQuotes: microPullbackFairValueEmaQuotes,
                    recentVolatilityQuotes: microPullbackRecentVolatilityQuotes,
                    minEffectiveVolatilityBps: microPullbackMinEffectiveVolatilityBps,
                    maxEffectiveVolatilityBps: microPullbackMaxEffectiveVolatilityBps,
                    minNormalizedDislocation: microPullbackMinNormalizedDislocation,
                    maxNormalizedDislocation: microPullbackMaxNormalizedDislocation,
                    maxContinuationMomentumBps: microPullbackMaxContinuationMomentumBps,
                    minMomentumDecayPct: microPullbackMinMomentumDecayPct,
                    minImbalanceRecovery: microPullbackMinImbalanceRecovery,
                    minMicropriceRecoveryBps: microPullbackMinMicropriceRecoveryBps,
                    reclaimLookbackQuotes: microPullbackReclaimLookbackQuotes,
                    enableEarlyReclaim: microPullbackEnableEarlyReclaim,
                    enableConfirmedReclaim: microPullbackEnableConfirmedReclaim,
                    minReclaimMomentumBps: microPullbackMinReclaimMomentumBps,
                    microTakeProfitBps: microPullbackMicroTakeProfitBps,
                    profitTargetBps: microPullbackProfitTargetBps,
                    stopLossBps: microPullbackStopLossBps,
                    maxTimeToFirstPositiveMfe: TimeSpan.FromSeconds(microPullbackMaxTimeToFirstPositiveMfeSeconds),
                    mfeProtectMinBps: microPullbackMfeProtectMinBps,
                    mfeGivebackBps: microPullbackMfeGivebackBps,
                    maxStall: TimeSpan.FromSeconds(microPullbackMaxStallSeconds),
                    expectedReversionTime: TimeSpan.FromSeconds(microPullbackExpectedReversionSeconds),
                    maxHold: TimeSpan.FromSeconds(microPullbackMaxHoldSeconds),
                    cooldown: TimeSpan.FromSeconds(microPullbackCooldownSeconds),
                    oneShotPerMove: microPullbackOneShotPerMove);

                log.Information(
                    "[CRYPTO-MR] Enabled symbols={Symbols} fvQuotes={FairValueQuotes} volQuotes={VolQuotes} normDislocation=[{MinNorm:F2},{MaxNorm:F2}] microTp={MicroTp:F2}bps stop={Stop:F2}bps hold={Hold}s",
                    microPullbackSymbols is { Length: > 0 } ? string.Join(",", microPullbackSymbols) : "adaptive-all",
                    microPullbackFairValueEmaQuotes,
                    microPullbackRecentVolatilityQuotes,
                    microPullbackMinNormalizedDislocation,
                    microPullbackMaxNormalizedDislocation,
                    microPullbackMicroTakeProfitBps,
                    microPullbackStopLossBps,
                    microPullbackMaxHoldSeconds);

                if (ws is IOrderBookFeed microOrderBookFeed)
                {
                    microOrderBookFeed.OrderBookUpdated += ob => microPullbackStrategy.OnOrderBook(ob);
                    var replayedBooks = 0;
                    if (exchangeSettings != null && orderBookService != null)
                    {
                        foreach (var meta in ReadEnabledSymbols(exchangeSettings))
                        {
                            if (orderBookService.TryGetLatestUsableOrderBook(meta.ExchangeId, meta.PublicSymbol, out var cachedBook))
                            {
                                microPullbackStrategy.OnOrderBook(cachedBook);
                                replayedBooks++;
                            }
                        }
                    }

                    log.Information("[CRYPTO-MR] OrderBook feed connected to MicroPullbackReversionStrategy (Mode={Mode})",
                        microPullbackDryRun ? "DryRun" : "Live");
                    log.Information("[CRYPTO-MR] Replayed {Count} cached usable orderbooks into MicroPullbackReversionStrategy",
                        replayedBooks);
                }
                else
                {
                    log.Warning("[CRYPTO-MR] WebSocket feed does not implement IOrderBookFeed - micro pullback nece primati orderbook update-e");
                }
            }
            else
            {
                log.Information("[CRYPTO-MR] Micro pullback strategija je onemogucena (UseMicroPullbackReversion=false)");
            }

            if (!useMicroPullbackReversion && microPullbackDryRun)
            {
                log.Information("[CRYPTO-MR] MicroPullbackReversionDryRun=true ignored because UseMicroPullbackReversion=false");
            }

            ITradingStrategy strategy;
            if (microPullbackStrategy != null)
            {
                strategy = new AdaptiveMicroPullbackSelector(
                    pullbackStrategy: pullbackStrategy,
                    microPullbackStrategy: microPullbackStrategy,
                    microPullbackSymbols: microPullbackSymbols,
                    microPullbackDryRun: microPullbackDryRun,
                    highVolatilityThreshold: adaptiveMicroPullbackMinAtrFraction,
                    minTicksForMicroPullback: adaptiveMicroPullbackMinTicksPerWindow,
                    maxSpreadBpsForMicroPullback: adaptiveMicroPullbackMaxSpreadBps,
                    minMicroPullbackReadyTime: TimeSpan.FromSeconds(adaptiveMicroPullbackMinReadySeconds),
                    minMicroPullbackReadyQuotes: adaptiveMicroPullbackMinReadyQuotes);
            }
            else
            {
                strategy = new AdaptiveStrategySelector(
                    pullbackStrategy: pullbackStrategy,
                    scalpStrategy: scalpStrategy,
                    scalpSymbols: scalpSymbols,
                    scalpDryRun: scalpDryRun,
                    highVolatilityThreshold: adaptiveScalpMinAtrFraction,
                    minTicksForScalp: adaptiveScalpMinTicksPerWindow,
                    maxSpreadBpsForScalp: adaptiveScalpMaxSpreadBps,
                    minScalpReadyTime: TimeSpan.FromSeconds(adaptiveScalpMinReadySeconds),
                    minScalpReadyQuotes: adaptiveScalpMinReadyQuotes);
            }

            log.Information("[CRYPTO-STRATEGY] Selector={Selector} Pullback=enabled Scalp={ScalpMode} MicroPullback={MicroMode}",
                strategy.GetType().Name,
                scalpStrategy == null ? "disabled" : (scalpDryRun ? "dry-run" : "live"),
                microPullbackStrategy == null ? "disabled" : (microPullbackDryRun ? "dry-run" : "live"));

            // Strategy event tracking
            var signalCount = 0;
            var quoteCount = 0;
            strategy.TradeSignalGenerated += signal =>
            {
                Interlocked.Increment(ref signalCount);
                log.Information("[CRYPTO-STRATEGY] Signal #{Count} generated: {Sym} {Side} @ {Px:F2} reason={Reason}",
                    signalCount, signal.Symbol.Ticker, signal.Side, signal.SuggestedLimitPrice, signal.Reason);
            };

            // Track quotes received by strategy (svakih 100 quotes)
            mdFeed.MarketQuoteUpdated += q =>
            {
                var count = Interlocked.Increment(ref quoteCount);
                if (count % 100 == 0)
                {
                    log.Information("[CRYPTO-STRATEGY] Received {Count} quotes so far. Latest: {Sym} bid={Bid} ask={Ask} last={Last}",
                        count, q.Symbol.Ticker, q.Bid, q.Ask, q.Last);
                }
            };

            // Risk
            var riskValidator = new FeeAwareRiskValidator(0.01m, 0.005m);
            var riskLimits = new RiskLimits(
                MaxRiskPerTradeFraction: 0.02m,
                MaxExposurePerSymbolFrac: 0.25m,
                DailyLossStopFraction: 0.025m,
                MaxPerTradeFrac: 0.25m,
                MinAtrFraction: 0.002m
            );

            // Fees (crypto-specifični - dinamički na osnovu exchange-a)
            // Prvo probaj da učitaš iz config-a, inače koristi default vrednosti
            var cryptoFeeSchedule = CryptoFeeProvider.GetFeeSchedule(
                exchangeId: exchangeSettings?.ExchangeId ?? CryptoExchangeId.Unknown,
                config: cfg,
                tradeType: exchangeSettings?.Name?.Contains("Futures") == true ||
                          exchangeSettings?.Name?.Contains("Perpetual") == true ||
                          exchangeSettings?.Name?.Contains("Linear") == true
                    ? "Futures" : "Spot"
            );

            // Legacy CommissionSchedule za kompatibilnost (koristi se za risk validator)
            // Izračunaj prosečan fee na osnovu tipične notional vrednosti (npr. 100 USD)
            var avgNotional = 100m;
            var avgFee = cryptoFeeSchedule.CalculateFeeUsd(avgNotional, isMaker: false); // Koristi Taker kao prosečan
            var fees = new CommissionSchedule(
                EstimatedPerOrderUsd: avgFee,
                EstimatedRoundTripUsd: cryptoFeeSchedule.CalculateRoundTripFeeUsd(avgNotional, buyIsMaker: false, sellIsMaker: false)
            );

            log.Information("[CRYPTO-FEES] Fee schedule loaded: Exchange={Exchange} Type={Type} Maker={Maker:P4}% Taker={Taker:P4}% AvgFee={AvgFee:F4} USD",
                cryptoFeeSchedule.ExchangeId, cryptoFeeSchedule.TradeType ?? "Spot",
                cryptoFeeSchedule.MakerFeePercent, cryptoFeeSchedule.TakerFeePercent, avgFee);

            // Exit/trading parametri po berzi (JSON override ili default po exchange-u)
            var exchangeIdForParams = exchangeSettings?.ExchangeId ?? CryptoExchangeId.Unknown;
            var tradingParams = exchangeSettings?.TradingParams ?? CryptoExchangeTradingParams.GetDefault(exchangeIdForParams);
            log.Information("[CRYPTO-TRADING] Params: Exchange={Exchange} TP={Tp:P2}% SL={Sl:P2}% TrailAct={TrailAct:P2}% TrailDist={TrailDist:P2}% MaxHoldMin={MaxHold}",
                exchangeIdForParams, tradingParams.TpFraction, tradingParams.SlFraction,
                tradingParams.TrailActivateFraction, tradingParams.TrailDistanceFraction,
                tradingParams.MaxHoldTimeMinutes > 0 ? tradingParams.MaxHoldTimeMinutes.ToString() : "swing");

            // Trading config (tradingSection i isRealMode su već deklarisani gore)
            var startingCashUsd = tradingSection.GetValue<decimal>("StartingCashUsd", 1000m);
            var perSymbolBudgetUsd = tradingSection.GetValue<decimal>("PerSymbolBudgetUsd", 200m);
            var perSymbolBudgetByTicker = BuildPerSymbolBudgetMap(exchangeSettings, perSymbolBudgetUsd);
            var dailyLossStopFraction = tradingSection.GetValue<decimal>("DailyLossStopFraction", 0.025m);
            var swingModeStr = tradingSection.GetValue<string>("SwingMode", "Swing");
            var maxHoldingDays = tradingSection.GetValue<int>("MaxHoldingDays", 10);
            var maxSingleTradeRiskPct = tradingSection.GetValue<decimal>("MaxSingleTradeRiskPct", 0.01m);
            var multipleOrders = tradingSection.GetValue<bool>("MultipleOrders", false);
            var configuredStartingCashUsd = startingCashUsd;
            var capitalSource = "config";

            // Day guards - koristimo RiskProfile iz exchangeSettings umesto Trading sekcije
            // RiskProfile ima prioritet jer je specifičan za crypto exchange
            int maxTradesPerSymbol;
            int maxTradesTotal;

            // DEBUG: Proveri da li postoji RiskProfile
            if (exchangeSettings == null)
            {
                log.Warning("[CRYPTO-DAY-GUARDS] exchangeSettings is NULL! Using Trading section fallback");
            }
            else if (exchangeSettings.RiskProfile == null)
            {
                log.Warning("[CRYPTO-DAY-GUARDS] exchangeSettings.RiskProfile is NULL! Using Trading section fallback");
            }
            else
            {
                log.Information("[CRYPTO-DAY-GUARDS] RiskProfile found: MaxTradesPerSymbolPerDay={PerSym}, MaxTradesPerDay={Total}, DailyLossStopFraction={LossFrac}",
                    exchangeSettings.RiskProfile.MaxTradesPerSymbolPerDay,
                    exchangeSettings.RiskProfile.MaxTradesPerDay,
                    exchangeSettings.RiskProfile.DailyLossStopFraction);
            }

            if (exchangeSettings?.RiskProfile != null)
            {
                maxTradesPerSymbol = exchangeSettings.RiskProfile.MaxTradesPerSymbolPerDay > 0
                    ? exchangeSettings.RiskProfile.MaxTradesPerSymbolPerDay
                    : tradingSection.GetValue<int>("MaxTradesPerSymbol", 5);
                maxTradesTotal = exchangeSettings.RiskProfile.MaxTradesPerDay > 0
                    ? exchangeSettings.RiskProfile.MaxTradesPerDay
                    : tradingSection.GetValue<int>("MaxTradesTotal", 20);

                // Ako postoji DailyLossStopFraction u RiskProfile, koristimo ga
                if (exchangeSettings.RiskProfile.DailyLossStopFraction > 0)
                {
                    dailyLossStopFraction = exchangeSettings.RiskProfile.DailyLossStopFraction;
                }

                log.Information("[CRYPTO-DAY-GUARDS] Using RiskProfile: MaxTradesPerSymbol={PerSym}, MaxTradesTotal={Total}, DailyLossStopFraction={LossFrac}",
                    maxTradesPerSymbol, maxTradesTotal, dailyLossStopFraction);
            }
            else
            {
                // Fallback na Trading sekciju ako nema RiskProfile
                maxTradesPerSymbol = tradingSection.GetValue<int>("MaxTradesPerSymbol", 5);
                maxTradesTotal = tradingSection.GetValue<int>("MaxTradesTotal", 20);
                log.Warning("[CRYPTO-DAY-GUARDS] Using Trading section (no RiskProfile): MaxTradesPerSymbol={PerSym}, MaxTradesTotal={Total}",
                    maxTradesPerSymbol, maxTradesTotal);
            }

            // REAL mode: koristi stanje sa menjačnice kad god je dostupno.
            if (isRealMode && tradingApi != null)
            {
                try
                {
                    var balances = await tradingApi.GetBalancesAsync(ct).ConfigureAwait(false);
                    var usdtLikeBalance = balances.FirstOrDefault(b =>
                        string.Equals(b.Asset, "USDT", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(b.Asset, "UST", StringComparison.OrdinalIgnoreCase));
                    var usdBalance = balances.FirstOrDefault(b =>
                        string.Equals(b.Asset, "USD", StringComparison.OrdinalIgnoreCase));

                    if (usdtLikeBalance is { Free: > 0m })
                    {
                        startingCashUsd = usdtLikeBalance.Free;
                        var asset = usdtLikeBalance.Asset?.ToUpperInvariant() ?? "USDT";
                        capitalSource = $"exchange-wallet:{asset}";
                        log.Information("[CRYPTO-CASH] Using REAL wallet balance: {Balance:F2} {Asset} (config value was {Config:F2} USD)",
                            startingCashUsd, asset, configuredStartingCashUsd);
                    }
                    else if (usdBalance is { Free: > 0m })
                    {
                        startingCashUsd = usdBalance.Free;
                        capitalSource = "exchange-wallet:USD";
                        log.Information("[CRYPTO-CASH] Using REAL wallet balance: {Balance:F2} USD (config value was {Config:F2} USD)",
                            startingCashUsd, configuredStartingCashUsd);
                    }
                    else
                    {
                        capitalSource = "config-fallback:no-usdt-ust-usd-balance";
                        log.Warning("[CRYPTO-CASH] No USDT/UST/USD free balance on exchange. Falling back to config: {Config:F2} USD",
                            configuredStartingCashUsd);
                    }
                }
                catch (Exception ex)
                {
                    capitalSource = "config-fallback:balance-fetch-failed";
                    log.Warning(ex, "[CRYPTO-CASH] Failed to fetch REAL wallet balance. Falling back to config: {Config:F2} USD",
                        configuredStartingCashUsd);
                }
            }
            else
            {
                capitalSource = isRealMode
                    ? "config-fallback:no-trading-api"
                    : "config:paper-mode";
                log.Information("[CRYPTO-CASH] Using config capital: {Config:F2} USD (source={Source})",
                    configuredStartingCashUsd, capitalSource);
            }

            // Cash
            var cashService = new CashManager(initialFreeUsd: startingCashUsd);
            var dailyLossStopUsd = Math.Round(startingCashUsd * dailyLossStopFraction, 2);

            // Day guards
            var dayGuards = new DayGuards(
                new DayGuardLimits(
                    MaxTradesPerSymbol: maxTradesPerSymbol,
                    MaxTradesTotal: maxTradesTotal,
                    DailyLossStopUsd: dailyLossStopUsd
                )
            );

            // Swing config
            var swingMode = Enum.TryParse<CryptoSwingMode>(swingModeStr, ignoreCase: true, out var mode)
                ? mode
                : CryptoSwingMode.Swing;

            var swingConfig = new CryptoSwingConfig
            {
                Mode = swingMode,
                MaxHoldingDays = maxHoldingDays,
                MaxSingleTradeRiskPct = maxSingleTradeRiskPct,
                MultipleOrders = multipleOrders
            };

            ITrendContextProvider? cryptoTrendProvider = null;
            if (dbFactory is not null)
            {
                var trendRepo = new TrendMarketDataRepository(dbFactory, log);
                var trendSettings = tradingSection.Get<TrendAnalysisSettings>() ?? new TrendAnalysisSettings();
                cryptoTrendProvider = new CryptoTrendContextProvider(
                    trendRepo,
                    trendSettings,
                    trendMinPointsOverrideResolver: (exchange, symbol) =>
                        pullbackConfig?.Resolve(exchange, symbol).TrendMinPoints);
            }

            // Orchestrator
            using var orchestrator = new CryptoTradingOrchestrator(
                isRealMode: isRealMode,
                feed: mdFeed,
                strategy: strategy,
                risk: riskValidator,
                fees: fees,
                limits: riskLimits,
                perSymbolBudgetUsd: perSymbolBudgetUsd,
                perSymbolBudgetByTicker: perSymbolBudgetByTicker,
                orderService: orderService,
                dayGuards: dayGuards,
                cashService: cashService,
                journalRepo: journalRepo,
                fillRepo: fillRepo,
                signalRepo: signalRepo,
                orderRepo: orderRepo,
                pnlRepo: pnlRepo,
                swingPosRepo: swingRepo,
                marketTickRepo: dbFactory is not null ? new MarketTickRepository(dbFactory, log) : null,
                cryptoTradesRepo: dbFactory is not null ? new CryptoTradesRepository(dbFactory, log) : null,
                equityFloorUsd: 0m,
                minFreeCashUsd: 0m,
                swingConfig: swingConfig,
                pullbackConfig: pullbackConfig,
                exchangeName: exchangeSettings?.ExchangeId.ToString() ?? exchangeArg.ToUpperInvariant(),
                discordNotifier: discordNotifier,
                cryptoFeeSchedule: cryptoFeeSchedule,
                tradingParams: tradingParams,
                trendContextProvider: cryptoTrendProvider,
                greenGrindSettings: exchangeSettings?.GreenGrind
            );

            // DEBUG: Log discordNotifier status after orchestrator creation
            log.Information("[DISCORD-DEBUG] CryptoTradingOrchestrator created with discordNotifier={Status} for {Exchange}",
                discordNotifier != null ? "SET" : "NULL", exchangeArg);

            await orchestrator.RecoverOnStartupAsync(ct);

            // Registruj BitfinexOrderManager da prosleđuje fill-eve u orchestrator
            if (bitfinexOrderManager != null)
            {
                orchestrator.RegisterBitfinexOrderManager(bitfinexOrderManager);
            }

            if (ws is not null)
            {
                ws.TradeReceived += orchestrator.OnTradeTick;
            }

            // Periodic tasks
            var heartbeatSec = 60;
            var sweepSec = 60;
            var ttlSec = 900;

            orchestrator.StartHeartbeat(TimeSpan.FromSeconds(heartbeatSec), ct);
            orchestrator.StartPendingExpiryWatcher(TimeSpan.FromSeconds(sweepSec), TimeSpan.FromSeconds(ttlSec), ct);

            var enabledSymbols = exchangeSettings != null ? ReadEnabledSymbols(exchangeSettings).ToList() : new List<CryptoSymbol>();
            var finalModeStr = isRealMode && orderService != null ? "REAL" : "PAPER";

            log.Information("[CRYPTO-TRADING] ==========================================");
            log.Information("[CRYPTO-TRADING] Trading mode: {Mode} (config: {ConfigMode})", finalModeStr, modeStr);
            log.Information("[CRYPTO-TRADING] Exchange: {Exchange}", exchangeArg.ToUpperInvariant());
            log.Information("[CRYPTO-TRADING] Strategy: {Strategy}", strategy.GetType().Name);
            log.Information("[CRYPTO-TRADING] Simboli: {Count} ({Symbols})",
                enabledSymbols.Count, string.Join(", ", enabledSymbols.Select(s => s.PublicSymbol)));
            log.Information("[CRYPTO-TRADING] Capital: {CashUsd:F2} USD (source={Source})", startingCashUsd, capitalSource);
            log.Information("[CRYPTO-TRADING] PerSymbolBudget: {Budget:F2} USD", perSymbolBudgetUsd);
            log.Information("[CRYPTO-TRADING] DailyLossStop: {DailyDdUsd:F2} USD ({Pct:P2})",
                dailyLossStopUsd, dailyLossStopFraction);
            log.Information("[CRYPTO-TRADING] MaxTrades: {PerSym}/{Total} per symbol/total", maxTradesPerSymbol, maxTradesTotal);
            log.Information("[CRYPTO-TRADING] SwingMode: {Mode} (MaxHoldingDays={Days}) MultipleOrders={MultipleOrders}", swingMode, maxHoldingDays, multipleOrders);
            log.Information("[CRYPTO-TRADING] Snimanje u bazu: Journal={J} Fills={F} Signals={S} Orders={O} PnL={P} Swing={Sw}",
                journalRepo != null ? "DA" : "NE",
                fillRepo != null ? "DA" : "NE",
                signalRepo != null ? "DA" : "NE",
                orderRepo != null ? "DA" : "NE",
                pnlRepo != null ? "DA" : "NE",
                swingRepo != null ? "DA" : "NE");
            log.Information("[CRYPTO-TRADING] Strategija pokrenuta: {StartUtc:o}", strategyStartUtc);
            log.Information("[CRYPTO-TRADING] ==========================================");

            // Sync pozicije sa berze NAKON bannera – redosled: app up / strategija pokrenuta → pa sync (i eventualno OCO)
            if (tradingApi is Bitfinex.BitfinexTradingApi bitfinexApi && symbolMetadataProvider != null)
            {
                try
                {
                    await orchestrator.SyncPositionsFromBalancesAsync(bitfinexApi, symbolMetadataProvider, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    log.Warning(ex, "[CRYPTO-SYNC] Failed to sync positions from Bitfinex balances");
                }
            }

            Console.WriteLine("==============================================");
            Console.WriteLine($"CRYPTO TRADING MODE: {finalModeStr} (config: {modeStr})");
            Console.WriteLine($"Exchange: {exchangeArg.ToUpperInvariant()}");
            Console.WriteLine($"Strategy: {strategy.GetType().Name}");
            Console.WriteLine($"Simboli: {string.Join(", ", enabledSymbols.Select(s => s.PublicSymbol))}");
            Console.WriteLine($"Capital: {startingCashUsd:F2} USD (source={capitalSource})");
            Console.WriteLine($"PerSymbolBudget: {perSymbolBudgetUsd:F2} USD");
            Console.WriteLine($"DailyLossStop: {dailyLossStopUsd:F2} USD");
            Console.WriteLine($"SwingMode: {swingMode} (MaxHoldingDays={maxHoldingDays})");
            Console.WriteLine($"Strategija pokrenuta: {strategyStartUtc:HH:mm:ss}");
            Console.WriteLine("CTRL+C za stop");
            Console.WriteLine("==============================================");

            // Main loop
            try
            {
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                // Cleanup WebSocket
                if (ws != null)
                {
                    try
                    {
                        await ws.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        log.Warning(ex, "[CRYPTO] Error disposing WebSocket feed");
                    }
                }

                // Cleanup BitfinexOrderManager
                if (bitfinexOrderManager != null)
                {
                    try
                    {
                        await bitfinexOrderManager.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        log.Warning(ex, "[CRYPTO] Error disposing BitfinexOrderManager");
                    }
                }

                if (bitfinexFundingManager != null)
                {
                    try
                    {
                        await bitfinexFundingManager.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        log.Warning(ex, "[CRYPTO] Error disposing BitfinexFundingManager");
                    }
                }
            }

            Console.WriteLine("Zatvaram crypto trading aplikaciju");
        }
    }
}
