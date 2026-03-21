#nullable enable
using Denis.TradingEngine.Core.Accounts;
using Denis.TradingEngine.Core.Crypto;
using Denis.TradingEngine.Core.Interfaces;
using Denis.TradingEngine.Core.Risk;
using Denis.TradingEngine.Core.Swing;
using Denis.TradingEngine.Core.Trading;
using Denis.TradingEngine.Data;
using Denis.TradingEngine.Data.Repositories;
using Denis.TradingEngine.Data.Repositories.Funding;
using Denis.TradingEngine.Data.Runtime;
using Denis.TradingEngine.Exchange.Crypto.Abstractions;
using Denis.TradingEngine.Exchange.Crypto.Bitfinex;
using Denis.TradingEngine.Exchange.Crypto.Bitfinex.Funding.Api;
using Denis.TradingEngine.Exchange.Crypto.Bitfinex.Funding.Runtime;
using Denis.TradingEngine.Exchange.Crypto.Bitfinex.Funding.Stream;
using Denis.TradingEngine.Exchange.Crypto.Bybit;
using Denis.TradingEngine.Exchange.Crypto.Config;
using Denis.TradingEngine.Exchange.Crypto.Deribit;
using Denis.TradingEngine.Exchange.Crypto.Kraken;
using Denis.TradingEngine.Exchange.Crypto.Monitoring;
using Denis.TradingEngine.Logging;
using Denis.TradingEngine.Logging.Discord;
using Denis.TradingEngine.Risk;
using Denis.TradingEngine.Strategy.Adaptive;
using Denis.TradingEngine.Strategy.Filters;
using Denis.TradingEngine.Strategy.Pullback;
using Denis.TradingEngine.Strategy.Scalp;
using Denis.TradingEngine.Strategy.Trend;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace Denis.TradingEngine.Exchange.Crypto.Trading;

/// <summary>
/// Runner klasa za pokretanje Crypto trading engine-a.
/// Može se koristiti kao standalone ili integrisano sa IBKR trading engine-om.
/// </summary>
public sealed class CryptoTradingRunner
{
    private readonly ILogger _log;

    public CryptoTradingRunner()
    {
        // Inicijalizuj odvojeni logger za Crypto (odvojeni log fajl)
        var env = Environment.GetEnvironmentVariable("APP_ENV") ?? "dev";
        AppLog.Init("Crypto", env);
        _log = AppLog.ForContext(typeof(CryptoTradingRunner));
    }

    /// <summary>
    /// Pokreće Crypto trading engine za sve konfigurisane exchange-e.
    /// </summary>
    public async Task RunAsync(IConfigurationRoot cfg, CancellationToken ct)
    {
        _log.Information("==============================================");
        _log.Information("[CRYPTO] Starting Crypto Trading Engine (parallel with IBKR)");
        _log.Information("==============================================");

        try
        {
            // Učitaj config za Crypto (proba sve exchange-e dok ne nađe connection string)
            var pgConnStr = GetPostgresConnectionString();

            if (string.IsNullOrWhiteSpace(pgConnStr))
            {
                _log.Warning("[CRYPTO] Postgres:ConnectionString nije konfigurisan - preskačem Crypto trading");
                return;
            }

            var dbFactory = new PgConnectionFactory(pgConnStr, _log);

            // Inicijalizacija baze za market data
            CryptoOrderBookService? orderBookService = null;
            BoundedTickQueue? tickQueue = null;
            CryptoSnapshotRepository? snapshotRepo = null;

            var tickRepo = new MarketTickRepository(dbFactory, _log);
            tickQueue = new BoundedTickQueue(
                capacity: 5000,
                onTickAsync: _ => Task.CompletedTask,
                tickRepo: tickRepo,
                batchSize: 100,
                maxBatchDelay: TimeSpan.FromMilliseconds(200),
                log: _log);
            // Quick fix: privremeno gasimo DB persistence za crypto orderbook-e.
            // In-memory orderbook tok ostaje aktivan preko exchange feed-ova i koristi se za scalp/runtime.
            // Kada finalizujemo novi analytics storage model, vraticemo dedicated orderbook writer.
            orderBookService = null;
            _log.Information("[CRYPTO-DB] OrderBook DB persistence is temporarily disabled; runtime/scalp cache remains active");

            // orderBookService = new CryptoOrderBookService(
            //     dbFactory,
            //     queueCapacity: 1000,
            //     batchSize: 50,
            //     maxBatchDelay: TimeSpan.FromMilliseconds(1000),
            //     log: _log);

            snapshotRepo = new CryptoSnapshotRepository(dbFactory, _log);

            _log.Information("[CRYPTO-DB] Database services initialized");

            // Pokreni sve exchange-e paralelno
            var exchanges = cfg.GetSection("Crypto:Exchanges").Get<List<string>>()
                ?? new List<string> { "kraken", "bitfinex", "deribit", "bybit" };

            var tasks = exchanges.Select(exchange =>
                RunExchangeAsync(exchange, orderBookService, tickQueue, snapshotRepo, ct)
            ).ToList();

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log.Information("[CRYPTO] Shutdown completed");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[CRYPTO] Fatal error in Crypto trading");
        }
        finally
        {
            _log.Information("[CRYPTO] Shutdown complete");
        }
    }

    private string? GetPostgresConnectionString()
    {
        var exchanges = new[] { "kraken", "bitfinex", "deribit", "bybit" };
        foreach (var ex in exchanges)
        {
            var cfg = LoadCryptoConfig($"appsettings.crypto.{ex}.json");
            var connStr = cfg["Postgres:ConnectionString"];
            if (!string.IsNullOrWhiteSpace(connStr))
            {
                _log.Information("[CRYPTO-DB] Found Postgres connection string in appsettings.crypto.{Exchange}.json", ex);
                return connStr;
            }
        }

        var fallbackCfg = LoadConfig("appsettings.json");
        return fallbackCfg["Postgres:ConnectionString"];
    }

    private static IConfigurationRoot LoadCryptoConfig(string fileName)
    {
        return new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(fileName, optional: true, reloadOnChange: false)
            .Build();
    }

    private static IConfigurationRoot LoadConfig(string fileName)
    {
        return new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(fileName, optional: true, reloadOnChange: false)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .Build();
    }

    private async Task RunExchangeAsync(
        string exchangeArg,
        CryptoOrderBookService? orderBookService,
        BoundedTickQueue? tickQueue,
        CryptoSnapshotRepository? snapshotRepo,
        CancellationToken ct)
    {
        try
        {
            // Učitaj config
            var cfg = LoadCryptoConfig($"appsettings.crypto.{exchangeArg}.json");
            var pgConnStr = cfg["Postgres:ConnectionString"];

            if (string.IsNullOrWhiteSpace(pgConnStr))
            {
                _log.Error("[CRYPTO-TRADING] Postgres:ConnectionString nije konfigurisan za {Exchange}", exchangeArg);
                return;
            }

            var dbFactory = new PgConnectionFactory(pgConnStr, _log);

            // Discord notifier
            var discordWebhookUrl = cfg["Discord:WebhookUrl"];
            DiscordNotifier? discordNotifier = null;
            if (!string.IsNullOrWhiteSpace(discordWebhookUrl))
            {
                discordNotifier = new DiscordNotifier(discordWebhookUrl, _log);
                _log.Information("[DISCORD] Discord notifier initialized for {Exchange} with webhook URL", exchangeArg);
            }
            else
            {
                _log.Warning("[DISCORD] Discord:WebhookUrl nije konfigurisan u appsettings.crypto.{Exchange}.json - Discord notifikacije neće raditi!", exchangeArg);
            }

            // Repositories
            var journalRepo = new TradeJournalRepository(dbFactory);
            var fillRepo = new TradeFillRepository(dbFactory, _log);
            var signalRepo = new TradeSignalRepository(dbFactory, _log);
            var orderRepo = new BrokerOrderRepository(dbFactory, _log);
            var pnlRepo = new CryptoDailyPnlRepository(dbFactory, _log, discordNotifier, System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(exchangeArg.ToLowerInvariant()));
            var swingRepo = new SwingPositionRepository(dbFactory, _log);
            var slayerRepo = new SignalSlayerDecisionRepository(dbFactory, _log);

            // Market data feed (zavisi od exchange-a)
            IMarketDataFeed? mdFeed = null;
            IOrderService? orderService = null;
            CryptoExchangeSettings? exchangeSettings = null;
            ICryptoWebSocketFeed? ws = null;
            Bitfinex.BitfinexOrderManager? bitfinexOrderManager = null; // Order manager za Bitfinex
            BitfinexFundingManager? bitfinexFundingManager = null;
            IActivityTicksProvider? activityTicksProvider = null; // Trade-based activity za strategiju (npr. Bitfinex)

            // Market data statistike (moraju biti deklarisane pre switch-a jer se koriste u event handlerima)
            long tickCount = 0;
            long orderBookCount = 0;
            long tradeCount = 0; // Za Kraken
            long tickerCount = 0; // Za Bitfinex/Deribit
            var statsInterval = TimeSpan.FromSeconds(30);

            var exchanges = cfg.GetSection("Crypto:Exchanges").Get<List<CryptoExchangeSettings>>() ?? new List<CryptoExchangeSettings>();
            var tradingSection = cfg.GetSection("Trading");
            var modeStr = tradingSection.GetValue<string>("Mode", "Paper");
            var isRealMode = string.Equals(modeStr, "Real", StringComparison.OrdinalIgnoreCase);
            Abstractions.ICryptoTradingApi? tradingApi = null;
            Abstractions.ICryptoSymbolMetadataProvider? symbolMetadataProvider = null;

            switch (exchangeArg.ToLowerInvariant())
            {
                case "kraken":
                    {
                        exchangeSettings = GetExchangeOrThrow(exchanges, CryptoExchangeId.Kraken, $"appsettings.crypto.{exchangeArg}.json");
                        var krakenWs = new KrakenWebSocketFeed(exchangeSettings.WebSocketUrl, _log);
                        ws = krakenWs;
                        var krakenMd = new KrakenMarketDataFeed(krakenWs, _log);
                        mdFeed = krakenMd;

                        foreach (var meta in ReadEnabledSymbols(exchangeSettings))
                        {
                            krakenMd.AddSymbol(meta);
                        }

                        if (orderBookService != null)
                        {
                            orderBookService.Subscribe(krakenWs);
                            krakenWs.OrderBookUpdated += _ => Interlocked.Increment(ref orderBookCount);
                        }

                        _log.Information("[KRAKEN] Connecting WS...");
                        await krakenWs.ConnectAsync(ct).ConfigureAwait(false);
                        _log.Information("[KRAKEN] WS connected.");

                        foreach (var meta in ReadEnabledSymbols(exchangeSettings))
                        {
                            await krakenWs.SubscribeOrderBookAsync(meta, ct).ConfigureAwait(false);
                            // Subscribe na trades za snapshot snimanje
                            await krakenWs.SubscribeTradesAsync(meta, ct).ConfigureAwait(false);
                            var engineSymbol = new Symbol(
                                Ticker: meta.PublicSymbol,
                                Currency: meta.QuoteAsset,
                                Exchange: exchangeArg.ToUpperInvariant());
                            mdFeed.SubscribeQuotes(engineSymbol);
                        }
                        break;
                    }

                case "bitfinex":
                    {
                        exchangeSettings = GetExchangeOrThrow(exchanges, CryptoExchangeId.Bitfinex, $"appsettings.crypto.{exchangeArg}.json");
                        var bitfinexWs = new BitfinexWebSocketFeed(exchangeSettings.WebSocketUrl, _log);
                        ws = bitfinexWs;
                        var bitfinexMd = new BitfinexMarketDataFeed(bitfinexWs, _log);
                        mdFeed = bitfinexMd;

                        foreach (var meta in ReadEnabledSymbols(exchangeSettings))
                        {
                            bitfinexMd.AddSymbol(meta);
                        }

                        if (orderBookService != null)
                        {
                            orderBookService.Subscribe(bitfinexWs);
                            bitfinexWs.OrderBookUpdated += _ => Interlocked.Increment(ref orderBookCount);
                        }

                        _log.Information("[BITFINEX] Connecting WS...");
                        await bitfinexWs.ConnectAsync(ct).ConfigureAwait(false);
                        _log.Information("[BITFINEX] WS connected.");

                        // Bitfinex private/public WS lako udari subscribe burst limit ako šaljemo prebrzo.
                        const int bitfinexWsSubscribeDelayMs = 100;

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
                        var cryptoTradesRepo = new CryptoTradesRepository(dbFactory, _log);
                        var tradeTicksBatchWriter = new CryptoTradeTicksBatchWriter(cryptoTradesRepo, "Bitfinex", _log);
                        bitfinexWs.TradeReceived += tick => tradeTicksBatchWriter.Add(tick);
                        tradeTicksBatchWriter.StartFlushLoop(ct);
                        activityTicksProvider = tradeTicksBatchWriter;
                        _log.Information("[BITFINEX] Trade ticks batch writer and activity provider enabled");

                        break;
                    }

                case "deribit":
                    {
                        exchangeSettings = GetExchangeOrThrow(exchanges, CryptoExchangeId.Deribit, $"appsettings.crypto.{exchangeArg}.json");
                        var deribitWs = new DeribitWebSocketFeed(exchangeSettings.WebSocketUrl, _log);
                        ws = deribitWs;

                        _log.Information("[DERIBIT] Connecting WS...");
                        await deribitWs.ConnectAsync(ct).ConfigureAwait(false);
                        _log.Information("[DERIBIT] WS connected.");

                        var deribitMd = new DeribitMarketDataFeed(deribitWs, _log);
                        mdFeed = deribitMd;

                        foreach (var meta in ReadEnabledSymbols(exchangeSettings))
                        {
                            deribitMd.AddSymbol(meta);
                        }

                        if (orderBookService != null)
                        {
                            orderBookService.Subscribe(deribitWs);
                            deribitWs.OrderBookUpdated += _ => Interlocked.Increment(ref orderBookCount);
                        }

                        foreach (var meta in ReadEnabledSymbols(exchangeSettings))
                        {
                            var engineSymbol = new Symbol(
                                Ticker: meta.PublicSymbol,
                                Currency: meta.QuoteAsset,
                                Exchange: exchangeArg.ToUpperInvariant());
                            mdFeed.SubscribeQuotes(engineSymbol);
                            await deribitWs.SubscribeOrderBookAsync(meta, ct).ConfigureAwait(false);
                        }
                        break;
                    }

                case "bybit":
                    {
                        _log.Information("[BYBIT] Starting Bybit initialization...");
                        exchangeSettings = GetExchangeOrThrow(exchanges, CryptoExchangeId.Bybit, $"appsettings.crypto.{exchangeArg}.json");
                        _log.Information("[BYBIT] Exchange settings loaded: Name={Name} Enabled={Enabled}", exchangeSettings.Name, exchangeSettings.Enabled);

                        var bybitWs = new BybitWebSocketFeed(exchangeSettings.WebSocketUrl, _log);
                        ws = bybitWs;
                        var bybitMd = new BybitMarketDataFeed(bybitWs, _log);
                        mdFeed = bybitMd;

                        foreach (var meta in ReadEnabledSymbols(exchangeSettings))
                        {
                            bybitMd.AddSymbol(meta);
                        }

                        if (orderBookService != null)
                        {
                            orderBookService.Subscribe(bybitWs);
                            bybitWs.OrderBookUpdated += _ => Interlocked.Increment(ref orderBookCount);
                        }

                        _log.Information("[BYBIT] Connecting WS...");
                        await bybitWs.ConnectAsync(ct).ConfigureAwait(false);
                        _log.Information("[BYBIT] WS connected.");

                        // Subscribe na orderbooks, ticker i trades
                        foreach (var meta in ReadEnabledSymbols(exchangeSettings))
                        {
                            await bybitWs.SubscribeOrderBookAsync(meta, ct).ConfigureAwait(false);
                            await bybitWs.SubscribeTickerAsync(meta, ct).ConfigureAwait(false);
                            await bybitWs.SubscribeTradesAsync(meta, ct).ConfigureAwait(false);

                            var engineSymbol = new Symbol(
                                Ticker: meta.PublicSymbol,
                                Currency: meta.QuoteAsset,
                                Exchange: meta.ExchangeId.ToString());
                            mdFeed.SubscribeQuotes(engineSymbol);
                        }
                        break;
                    }

                default:
                    _log.Error("[CRYPTO-TRADING] Nepoznat exchange '{Exchange}'", exchangeArg);
                    return;
            }

            if (mdFeed == null)
            {
                _log.Error("[CRYPTO-TRADING] Nije moguće kreirati market data feed za {Exchange}", exchangeArg);
                return;
            }

            if (ws != null && exchangeSettings != null)
            {
                var tickerStallWatchdog = new CryptoTickerStallWatchdog(
                    exchange: exchangeArg,
                    log: _log,
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
                _log.Information("[MD-WATCHDOG] Ticker stall watchdog enabled for {Exchange}", exchangeArg.ToUpperInvariant());
            }

            // Trading mode / order service (isti model kao standalone Program.cs)
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
                                    _log.Warning("[CRYPTO-ORD] Kraken ApiKey/ApiSecret nisu konfigurisani - nastavljam sa Paper mode.");
                                    isRealMode = false;
                                }
                                else
                                {
                                    tradingApi = new Kraken.KrakenTradingApi(krakenApiSettings, _log);
                                    _log.Information("[CRYPTO-ORD] KrakenTradingApi kreiran za REAL mode");
                                }
                                break;
                            }

                        case "bitfinex":
                            {
                                var bitfinexNonceProvider = new BitfinexAuthNonceProvider();

                                if (string.IsNullOrWhiteSpace(exchangeSettings.ApiKey) || string.IsNullOrWhiteSpace(exchangeSettings.ApiSecret))
                                {
                                    _log.Warning("[CRYPTO-ORD] Bitfinex ApiKey/ApiSecret nisu konfigurisani - nastavljam sa Paper mode.");
                                    isRealMode = false;
                                }
                                else
                                {
                                    var bitfinexTradingApi = new Bitfinex.BitfinexTradingApi(
                                        exchangeSettings.RestBaseUrl,
                                        exchangeSettings.ApiKey,
                                        exchangeSettings.ApiSecret,
                                        _log,
                                        bitfinexNonceProvider);
                                    tradingApi = bitfinexTradingApi;
                                    _log.Information("[CRYPTO-ORD] BitfinexTradingApi kreiran za REAL mode");

                                    bitfinexOrderManager = new Bitfinex.BitfinexOrderManager(
                                        orderRepo: orderRepo,
                                        tradingApi: bitfinexTradingApi,
                                        exchangeSettings: exchangeSettings,
                                        log: _log,
                                        nonceProvider: bitfinexNonceProvider);
                                    bitfinexOrderManager.Start(ct);
                                    _log.Information("[CRYPTO-ORD] BitfinexOrderManager pokrenut (Private WS + Reconciliation)");
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
                                        ? new BitfinexAuthNonceProvider()
                                        : bitfinexNonceProvider;
                                    var fundingWsNonceProvider = fundingUsesDedicatedWsCredentials
                                        ? new BitfinexAuthNonceProvider()
                                        : fundingRestNonceProvider;

                                    if (exchangeSettings.Funding.UsePrivateWebSocket &&
                                        string.Equals(fundingApiKey, fundingWsApiKey, StringComparison.Ordinal) &&
                                        string.Equals(fundingApiSecret, fundingWsApiSecret, StringComparison.Ordinal))
                                    {
                                        _log.Warning("[BFX-FUND] Funding REST and funding private WS are sharing the same API key. Bitfinex docs recommend separate API keys per authenticated client to avoid nonce errors.");
                                    }

                                    BitfinexFundingPrivateWebSocketFeed? fundingWsFeed = null;
                                    if (exchangeSettings.Funding.UsePrivateWebSocket)
                                    {
                                        fundingWsFeed = new BitfinexFundingPrivateWebSocketFeed(
                                            wsUrl: "wss://api.bitfinex.com/ws/2",
                                            apiKey: fundingWsApiKey,
                                            apiSecret: fundingWsApiSecret,
                                            fundingSymbols: exchangeSettings.Funding.PreferredSymbols,
                                            log: _log,
                                            nonceProvider: fundingWsNonceProvider);
                                    }

                                    var fundingApi = new BitfinexFundingApi(
                                        exchangeSettings.RestBaseUrl,
                                        fundingApiKey,
                                        fundingApiSecret,
                                        _log,
                                        fundingRestNonceProvider);
                                    var fundingRepo = new BitfinexFundingRepository(dbFactory, _log);
                                    bitfinexFundingManager = new BitfinexFundingManager(
                                        exchangeSettings.Funding,
                                        fundingApi,
                                        fundingWsFeed,
                                        fundingRepo,
                                        snapshotRepo,
                                        _log);
                                    bitfinexFundingManager.Start(ct);
                                    _log.Information("[BFX-FUND] Funding manager startup hook registered");
                                }
                                break;
                            }

                        case "deribit":
                            {
                                if (ws is DeribitWebSocketFeed deribitWs &&
                                    !string.IsNullOrWhiteSpace(exchangeSettings.ApiKey) &&
                                    !string.IsNullOrWhiteSpace(exchangeSettings.ApiSecret))
                                {
                                    tradingApi = new Deribit.DeribitTradingApi(deribitWs, _log);
                                    _log.Information("[CRYPTO-ORD] DeribitTradingApi kreiran za REAL mode");
                                }
                                else
                                {
                                    _log.Warning("[CRYPTO-ORD] Deribit ApiKey/ApiSecret nisu konfigurisani ili WebSocket nije spreman - nastavljam sa Paper mode");
                                    isRealMode = false;
                                }
                                break;
                            }

                        case "bybit":
                            {
                                var bybitApiSection = cfg.GetSection("BybitApi");
                                var bybitApiSettings = new Bybit.Config.BybitApiSettings
                                {
                                    BaseUrl = bybitApiSection.GetValue<string>("BaseUrl") ?? "https://api.bybit.com",
                                    ApiKey = bybitApiSection.GetValue<string>("ApiKey") ?? string.Empty,
                                    ApiSecret = bybitApiSection.GetValue<string>("ApiSecret") ?? string.Empty,
                                    RecvWindowMs = bybitApiSection.GetValue<int>("RecvWindowMs", 5000),
                                    DefaultCategory = bybitApiSection.GetValue<string>("DefaultCategory") ?? "spot"
                                };

                                if (string.IsNullOrWhiteSpace(bybitApiSettings.ApiKey) || string.IsNullOrWhiteSpace(bybitApiSettings.ApiSecret))
                                {
                                    _log.Warning("[CRYPTO-ORD] Bybit ApiKey/ApiSecret nisu konfigurisani - nastavljam sa Paper mode");
                                    isRealMode = false;
                                }
                                else
                                {
                                    tradingApi = new Bybit.BybitTradingApi(bybitApiSettings, _log);
                                    _log.Information("[CRYPTO-ORD] BybitTradingApi kreiran za REAL mode");
                                }
                                break;
                            }
                    }

                    if (tradingApi != null)
                    {
                        symbolMetadataProvider = new Common.CryptoSymbolMetadataProvider(exchanges);
                        var apis = new Dictionary<CryptoExchangeId, ICryptoTradingApi>
                        {
                            { tradingApi.ExchangeId, tradingApi }
                        };
                        orderService = new Adapters.CryptoOrderService(apis, symbolMetadataProvider, _log);
                        _log.Information("[CRYPTO-ORD] CryptoOrderService kreiran za REAL mode - {Exchange}", exchangeArg.ToUpperInvariant());
                    }
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "[CRYPTO-ORD] Greška pri kreiranju order service za {Exchange} - nastavljam sa Paper mode", exchangeArg);
                    isRealMode = false;
                    orderService = null;
                }
            }

            // Market ticks queue
            if (tickQueue != null)
            {
                mdFeed.MarketQuoteUpdated += q =>
                {
                    Interlocked.Increment(ref tickCount);
                    _ = tickQueue.TryEnqueue(q);
                };
            }

            // Snapshot counters
            if (ws != null)
            {
                if (exchangeArg == "kraken" && ws is KrakenWebSocketFeed krakenWs)
                {
                    krakenWs.TradeReceived += _ => Interlocked.Increment(ref tradeCount);
                }
                else if (exchangeArg == "bitfinex" && ws is BitfinexWebSocketFeed bitfinexWs)
                {
                    bitfinexWs.TickerUpdated += _ => Interlocked.Increment(ref tickerCount);
                }
                else if (exchangeArg == "deribit" && ws is DeribitWebSocketFeed deribitWs)
                {
                    deribitWs.TickerUpdated += _ => Interlocked.Increment(ref tickerCount);
                }
                else if (exchangeArg == "bybit" && ws is BybitWebSocketFeed bybitWs)
                {
                    bybitWs.TickerUpdated += _ => Interlocked.Increment(ref tickerCount);
                }
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

                        _log.Debug("[CRYPTO-MD-STATS] {Exchange} Ticks={Ticks} OrderBooks={OB} Trades={Trades} Tickers={Tickers} (last {Interval}s)",
                            exchangeArg.ToUpperInvariant(), ticks, ob, trades, tickers, statsInterval.TotalSeconds);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(ex, "[CRYPTO-MD-STATS] Error logging stats for {Exchange}", exchangeArg);
                    }
                }
            }, ct);

            // Snapshot snimanje (trades, tickers) - ako postoji snapshotRepo
            if (snapshotRepo != null && ws != null)
            {
                // Kraken: TradeReceived event
                if (exchangeArg == "kraken")
                {
                    ws.TradeReceived += async tick =>
                    {
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
                            _log.Warning(ex, "[CRYPTO-SNAPSHOT] Failed to save trade snapshot for {Exchange}", exchangeArg);
                        }
                    };
                    _log.Information("[CRYPTO-SNAPSHOT] TradeReceived handler registered for {Exchange}", exchangeArg);
                }

                // Bitfinex: TickerUpdated event
                if (exchangeArg == "bitfinex")
                {
                    ws.TickerUpdated += async ticker =>
                    {
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
                            _log.Warning(ex, "[CRYPTO-SNAPSHOT] Failed to save ticker snapshot for {Exchange}", exchangeArg);
                        }
                    };
                    _log.Information("[CRYPTO-SNAPSHOT] TickerUpdated handler registered for {Exchange}", exchangeArg);
                }

                // Deribit: TickerUpdated event
                if (exchangeArg == "deribit")
                {
                    ws.TickerUpdated += async ticker =>
                    {
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
                            _log.Warning(ex, "[CRYPTO-SNAPSHOT] Failed to save ticker snapshot for {Exchange}", exchangeArg);
                        }
                    };
                    _log.Information("[CRYPTO-SNAPSHOT] TickerUpdated handler registered for {Exchange}", exchangeArg);
                }

                // Bybit: TickerUpdated event
                if (exchangeArg == "bybit")
                {
                    ws.TickerUpdated += async ticker =>
                    {
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
                            _log.Warning(ex, "[CRYPTO-SNAPSHOT] Failed to save ticker snapshot for {Exchange}", exchangeArg);
                        }
                    };
                    _log.Information("[CRYPTO-SNAPSHOT] TickerUpdated handler registered for {Exchange}", exchangeArg);
                }
            }

            // Strategy
            var pullbackConfig = PullbackConfigProvider.GetConfig();
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

            var pullbackStrategy = new PullbackInUptrendStrategy(
                pullbackConfig,
                slayerConfig,
                slayerRepo: slayerRepo,
                runEnv: "Crypto",
                activityTicksProvider: activityTicksProvider);

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
                _log.Warning("[CRYPTO-EXPERIMENTS] UseScalp and UseMicroPullbackReversion are both enabled. Disabling MicroPullbackReversion to keep experimental flows isolated.");
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

                _log.Information(
                    "[CRYPTO-SCALP] Enabled symbols={Symbols} momentumQuotes={MomentumQuotes} momentumWindow={MomentumWindow}s confirmQuotes={ConfirmQuotes} persistence={Persistence}s exitSpread={ExitSpread:F1}bps edgeFailures={EdgeFailures}",
                    scalpSymbols is { Length: > 0 } ? string.Join(",", scalpSymbols) : "adaptive-all",
                    scalpMomentumLookbackQuotes,
                    scalpMomentumLookbackSeconds,
                    scalpEntryConfirmationQuotes,
                    scalpMinSignalPersistenceSeconds,
                    scalpExitMaxSpreadBps,
                    scalpEdgeLossFailureThreshold);

                if (ws is IOrderBookFeed orderBookFeed)
                {
                    orderBookFeed.OrderBookUpdated += ob => scalpStrategy.OnOrderBook(ob);
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

                    _log.Information("[CRYPTO-SCALP] OrderBook feed connected to ScalpStrategy (Mode={Mode})",
                        scalpDryRun ? "DryRun" : "Live");
                    _log.Information("[CRYPTO-SCALP] Replayed {Count} cached usable orderbooks into ScalpStrategy",
                        replayedBooks);
                }
                else
                {
                    _log.Warning("[CRYPTO-SCALP] WebSocket feed does not implement IOrderBookFeed - scalp neće primati orderbook update-e");
                }
            }
            else
            {
                _log.Information("[CRYPTO-SCALP] Scalp strategija onemogućena (UseScalp=false)");
            }

            if (!useScalp && scalpDryRun)
            {
                _log.Information("[CRYPTO-SCALP] ScalpDryRun=true ignored because UseScalp=false");
            }

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

                _log.Information(
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

                    _log.Information("[CRYPTO-MR] OrderBook feed connected to MicroPullbackReversionStrategy (Mode={Mode})",
                        microPullbackDryRun ? "DryRun" : "Live");
                    _log.Information("[CRYPTO-MR] Replayed {Count} cached usable orderbooks into MicroPullbackReversionStrategy",
                        replayedBooks);
                }
                else
                {
                    _log.Warning("[CRYPTO-MR] WebSocket feed does not implement IOrderBookFeed - micro pullback nece primati orderbook update-e");
                }
            }
            else
            {
                _log.Information("[CRYPTO-MR] Micro pullback strategija onemogucena (UseMicroPullbackReversion=false)");
            }

            if (!useMicroPullbackReversion && microPullbackDryRun)
            {
                _log.Information("[CRYPTO-MR] MicroPullbackReversionDryRun=true ignored because UseMicroPullbackReversion=false");
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

            // Risk
            var riskValidator = new FeeAwareRiskValidator(0.01m, 0.005m);
            var riskLimits = new RiskLimits(
                MaxRiskPerTradeFraction: 0.02m,
                MaxExposurePerSymbolFrac: 0.25m,
                DailyLossStopFraction: 0.025m,
                MaxPerTradeFrac: 0.25m,
                MinAtrFraction: 0.002m
            );

            // Fee po berzi (iz configa Fees:{ExchangeId} ili default za tu berzu)
            var exchangeId = exchangeSettings?.ExchangeId ?? (Enum.TryParse<CryptoExchangeId>(exchangeArg, true, out var parsedExId) ? parsedExId : CryptoExchangeId.Unknown);
            var tradeType = exchangeSettings?.Name?.Contains("Futures", StringComparison.OrdinalIgnoreCase) == true ||
                            exchangeSettings?.Name?.Contains("Perpetual", StringComparison.OrdinalIgnoreCase) == true ||
                            exchangeSettings?.Name?.Contains("Linear", StringComparison.OrdinalIgnoreCase) == true
                ? "Futures" : "Spot";
            var cryptoFeeSchedule = CryptoFeeProvider.GetFeeSchedule(exchangeId, cfg, tradeType);
            const decimal avgNotional = 100m;
            var fees = new CommissionSchedule(
                EstimatedPerOrderUsd: cryptoFeeSchedule.CalculateFeeUsd(avgNotional, isMaker: false),
                EstimatedRoundTripUsd: cryptoFeeSchedule.CalculateRoundTripFeeUsd(avgNotional, buyIsMaker: false, sellIsMaker: false)
            );
            _log.Information("[CRYPTO-FEES] Fee schedule: Exchange={Exchange} Type={Type} Maker={Maker:P4}% Taker={Taker:P4}% AvgFee={AvgFee:F4} USD",
                cryptoFeeSchedule.ExchangeId, cryptoFeeSchedule.TradeType ?? "Spot",
                cryptoFeeSchedule.MakerFeePercent, cryptoFeeSchedule.TakerFeePercent,
                cryptoFeeSchedule.CalculateFeeUsd(avgNotional, isMaker: false));

            // Exit/trejding parametri po berzi (iz configa ili default – Bitfinex = agresivniji, više tradeova)
            var tradingParams = exchangeSettings?.TradingParams ?? CryptoExchangeTradingParams.GetDefault(exchangeId);
            _log.Information("[CRYPTO-TRADING] Params: Exchange={Exchange} TP={Tp:P2}% SL={Sl:P2}% TrailAct={TrailAct:P2}% TrailDist={TrailDist:P2}% MaxHoldMin={MaxHold}",
                exchangeId, tradingParams.TpFraction, tradingParams.SlFraction,
                tradingParams.TrailActivateFraction, tradingParams.TrailDistanceFraction,
                tradingParams.MaxHoldTimeMinutes > 0 ? tradingParams.MaxHoldTimeMinutes.ToString() : "swing");

            var startingCashUsd = tradingSection.GetValue<decimal>("StartingCashUsd", 1000m);
            var perSymbolBudgetUsd = tradingSection.GetValue<decimal>("PerSymbolBudgetUsd", 200m);
            var perSymbolBudgetByTicker = BuildPerSymbolBudgetMap(exchangeSettings, perSymbolBudgetUsd);
            var dailyLossStopFraction = tradingSection.GetValue<decimal>("DailyLossStopFraction", 0.025m);
            var swingModeStr = tradingSection.GetValue<string>("SwingMode", "Swing");
            var maxHoldingDays = tradingSection.GetValue<int>("MaxHoldingDays", 10);
            var maxSingleTradeRiskPct = tradingSection.GetValue<decimal>("MaxSingleTradeRiskPct", 0.01m);
            var multipleOrders = tradingSection.GetValue<bool>("MultipleOrders", false);

            // Day guards - koristimo RiskProfile iz exchangeSettings umesto Trading sekcije
            // RiskProfile ima prioritet jer je specifičan za crypto exchange
            int maxTradesPerSymbol;
            int maxTradesTotal;

            // DEBUG: Proveri da li postoji RiskProfile
            if (exchangeSettings == null)
            {
                _log.Warning("[CRYPTO-DAY-GUARDS] exchangeSettings is NULL! Using Trading section fallback.");
            }
            else if (exchangeSettings.RiskProfile == null)
            {
                _log.Warning("[CRYPTO-DAY-GUARDS] exchangeSettings.RiskProfile is NULL! Using Trading section fallback.");
            }
            else
            {
                _log.Information("[CRYPTO-DAY-GUARDS] RiskProfile found: MaxTradesPerSymbolPerDay={PerSym}, MaxTradesPerDay={Total}, DailyLossStopFraction={LossFrac}",
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

                _log.Information("[CRYPTO-DAY-GUARDS] Using RiskProfile: MaxTradesPerSymbol={PerSym}, MaxTradesTotal={Total}, DailyLossStopFraction={LossFrac}",
                    maxTradesPerSymbol, maxTradesTotal, dailyLossStopFraction);
            }
            else
            {
                // Fallback na Trading sekciju ako nema RiskProfile
                maxTradesPerSymbol = tradingSection.GetValue<int>("MaxTradesPerSymbol", 5);
                maxTradesTotal = tradingSection.GetValue<int>("MaxTradesTotal", 20);
                _log.Warning("[CRYPTO-DAY-GUARDS] Using Trading section (no RiskProfile): MaxTradesPerSymbol={PerSym}, MaxTradesTotal={Total}",
                    maxTradesPerSymbol, maxTradesTotal);
            }

            // Ako imamo tradingApi, uzmi stvarni balance sa berze umesto iz JSON-a
            if (isRealMode && tradingApi != null)
            {
                try
                {
                    var balances = await tradingApi.GetBalancesAsync(ct).ConfigureAwait(false);

                    // Traži USDT ili USD balance
                    var usdtLikeBalance = balances.FirstOrDefault(b =>
                        string.Equals(b.Asset, "USDT", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(b.Asset, "UST", StringComparison.OrdinalIgnoreCase));
                    var usdBalance = balances.FirstOrDefault(b =>
                        string.Equals(b.Asset, "USD", StringComparison.OrdinalIgnoreCase));

                    var realBalance = usdtLikeBalance?.Free ?? usdBalance?.Free;

                    if (realBalance.HasValue && realBalance.Value > 0m)
                    {
                        startingCashUsd = realBalance.Value;
                        var asset = usdtLikeBalance != null
                            ? (usdtLikeBalance.Asset?.ToUpperInvariant() ?? "USDT")
                            : "USD";
                        _log.Information("[CRYPTO-CASH] Using REAL balance from exchange: {Balance:F2} {Asset} (instead of {Config:F2} from config)",
                            realBalance.Value, asset,
                            tradingSection.GetValue<decimal>("StartingCashUsd", 1000m));
                    }
                    else
                    {
                        _log.Warning("[CRYPTO-CASH] No USDT/UST/USD balance found on exchange. Using config value: {Config:F2} USD",
                            startingCashUsd);
                    }
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[CRYPTO-CASH] Failed to get balances from exchange. Using config value: {Config:F2} USD",
                        startingCashUsd);
                }
            }
            else
            {
                _log.Information("[CRYPTO-CASH] Using config value (paper mode or no API): {Config:F2} USD", startingCashUsd);
            }

            var cashService = new CashManager(initialFreeUsd: startingCashUsd);
            var dailyLossStopUsd = Math.Round(startingCashUsd * dailyLossStopFraction, 2);

            var dayGuards = new DayGuards(
                new DayGuardLimits(
                    MaxTradesPerSymbol: maxTradesPerSymbol,
                    MaxTradesTotal: maxTradesTotal,
                    DailyLossStopUsd: dailyLossStopUsd
                )
            );

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
                var trendRepo = new TrendMarketDataRepository(dbFactory, _log);
                var trendSettings = tradingSection.Get<TrendAnalysisSettings>() ?? new TrendAnalysisSettings();
                cryptoTrendProvider = new CryptoTrendContextProvider(
                    trendRepo,
                    trendSettings,
                    trendMinPointsOverrideResolver: (exchange, symbol) =>
                        pullbackConfig?.Resolve(exchange, symbol).TrendMinPoints);
            }

            // Orchestrator (cryptoFeeSchedule = fee po berzi za CalculateFeeUsd)
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
                marketTickRepo: dbFactory is not null ? new MarketTickRepository(dbFactory, _log) : null,
                cryptoTradesRepo: dbFactory is not null ? new CryptoTradesRepository(dbFactory, _log) : null,
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

            await orchestrator.RecoverOnStartupAsync(ct);

            if (ws is not null)
            {
                ws.TradeReceived += orchestrator.OnTradeTick;
            }

            // Registruj BitfinexOrderManager da prosleđuje fill-eve u orchestrator
            if (bitfinexOrderManager != null)
            {
                orchestrator.RegisterBitfinexOrderManager(bitfinexOrderManager);
            }

            var heartbeatSec = cfg.GetSection("Runtime").GetValue<int>("HeartbeatPeriodSec", 60);
            var sweepSec = cfg.GetSection("Runtime").GetValue<int>("PendingSweepSec", 60);
            var ttlSec = cfg.GetSection("Runtime").GetValue<int>("PendingTtlSec", 900);
            orchestrator.StartHeartbeat(TimeSpan.FromSeconds(heartbeatSec), ct);
            orchestrator.StartPendingExpiryWatcher(TimeSpan.FromSeconds(sweepSec), TimeSpan.FromSeconds(ttlSec), ct);

            var finalModeStr = isRealMode && orderService != null ? "REAL" : "PAPER";
            _log.Information("[CRYPTO-TRADING] {Exchange} trading started mode={Mode} (config={ConfigMode})", exchangeArg.ToUpperInvariant(), finalModeStr, modeStr);

            // Sync pozicije sa berze NAKON što je "trading started" ispisan – redosled: app up → pa sync (i eventualno OCO)
            if (isRealMode && tradingApi is Bitfinex.BitfinexTradingApi bitfinexApi && symbolMetadataProvider != null)
            {
                try
                {
                    await orchestrator.SyncPositionsFromBalancesAsync(bitfinexApi, symbolMetadataProvider, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[CRYPTO-SYNC] Failed to sync positions from Bitfinex balances");
                }
            }

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
                if (ws != null)
                {
                    try
                    {
                        await ws.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(ex, "[CRYPTO] Error disposing WebSocket feed");
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
                        _log.Warning(ex, "[CRYPTO] Error disposing BitfinexOrderManager");
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
                        _log.Warning(ex, "[CRYPTO] Error disposing BitfinexFundingManager");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            _log.Information("[CRYPTO] {Exchange} shutdown completed", exchangeArg);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[CRYPTO] Fatal error in {Exchange} trading", exchangeArg);
        }
    }

    private CryptoExchangeSettings GetExchangeOrThrow(
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

        _log.Information("Učitane {Count} berze iz {File}, koristim {Name} ({Id})",
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
}
