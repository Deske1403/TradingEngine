#nullable enable
using Denis.TradingEngine.App.Config;
using Denis.TradingEngine.App.Trading;
using Denis.TradingEngine.App.Trading.EodSkim;
using Denis.TradingEngine.Broker.IBKR;
using Denis.TradingEngine.Broker.IBKR.IBKRWrapper;
using Denis.TradingEngine.Core.Accounts;
using Denis.TradingEngine.Core.Interfaces;
using Denis.TradingEngine.Core.Risk;
using Denis.TradingEngine.Core.Trading;
using Denis.TradingEngine.Data.Repositories;
using Denis.TradingEngine.Data.Runtime;
using Denis.TradingEngine.Logging;
using Denis.TradingEngine.Logging.Discord;
using Denis.TradingEngine.MetricsServer;
using Denis.TradingEngine.Risk;
using Denis.TradingEngine.Strategy.Filters;
using Denis.TradingEngine.Strategy.Pullback;
using Denis.TradingEngine.Strategy.Trend;
using Microsoft.Extensions.Configuration;
// Crypto trading integration
using Denis.TradingEngine.Exchange.Crypto.Trading;
using System.Globalization;


//dotnet publish -c Release -r win-x64 --self-contained true
namespace Denis.TradingEngine.App;

internal class Program
{
    static async Task Main()
    {
        // ============================
        //  BOOT + LOGGING
        // ============================
        var env = Environment.GetEnvironmentVariable("APP_ENV") ?? "dev";
        AppLog.Init("TradingEngine.Runner", env);
        var log = AppLog.ForContext(typeof(Program));
        log.Information("Program Runner booting Env={Env}", env);

        // ============================
        //  Prometheus Metrics
        // ============================
        if (MetricsManager.Instance.Start(1414))
            log.Information("[PROMETHEUS] PROMETHEUS started successfully port = 1414");
        else
            log.Error("[PROMETHEUS] PROMETHEUS ERROR");
        // ============================
        //  CONFIG
        // ============================
        var cfg = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        var mode = cfg.GetValue<string>("Mode", "Paper");
        var perSymbolBudgetUsd = cfg.GetValue<decimal>("PerSymbolBudgetUSD", 1000m);
        var ibkrHost = cfg.GetValue<string>("Ibkr:Host", "127.0.0.1") ?? "127.0.0.1";
        var ibkrPort = cfg.GetValue<int>("Ibkr:Port", 4001);
        var ibkrClientId = cfg.GetValue<int>("Ibkr:ClientId", 1);
        var ibkrConnectTimeoutSec = cfg.GetValue<int>("Ibkr:ConnectTimeoutSec", 8);
        if (ibkrConnectTimeoutSec <= 0)
            ibkrConnectTimeoutSec = 8;

        // fallback iz configa ako IBKR cash failuje u REAL modu
        var configuredStartingCashUsd = cfg.GetValue<decimal>("Risk:StartingCashUsd", perSymbolBudgetUsd);

        var equityFloorUsd = cfg.GetValue<decimal>("Risk:EquityFloorUsd", 550m);
        var minFreeCashUsd = cfg.GetValue<decimal>("Risk:MinFreeCashUsd", 550m);

        var tradingSettings = cfg.GetSection("Trading").Get<TradingSettings>() ?? new TradingSettings();
        var swinfConfig = cfg.GetSection("SwingTrading").Get<SwingTradingConfig>() ?? new SwingTradingConfig();
        var ibkrEodSkimOptions = cfg.GetSection("IbkrEodSkim").Get<IbkrEodSkimOptions>() ?? new IbkrEodSkimOptions();

        log.Information(
            "[IBKR-CONFIG] Host={Host} Port={Port} ClientId={ClientId} ConnectTimeoutSec={TimeoutSec}",
            ibkrHost,
            ibkrPort,
            ibkrClientId,
            ibkrConnectTimeoutSec);

        log.Information(
            "[EOD-SKIM-CONFIG] Enabled={Enabled} DryRun={DryRun} StartMinBeforeClose={StartMin} MaxRetries={MaxRetries} MinNetProfitUsd={MinNet:F2} ReasonTag={Tag}",
            ibkrEodSkimOptions.Enabled,
            ibkrEodSkimOptions.DryRun,
            ibkrEodSkimOptions.StartMinutesBeforeClose,
            ibkrEodSkimOptions.MaxRetries,
            ibkrEodSkimOptions.MinNetProfitUsd,
            ibkrEodSkimOptions.ReasonTag ?? "EOD-SKIM");

        if (ibkrEodSkimOptions.ExcludeSymbols is { Count: > 0 })
        {
            log.Information(
                "[EOD-SKIM-CONFIG] ExcludeSymbols={Excluded}",
                string.Join(", ", ibkrEodSkimOptions.ExcludeSymbols.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())));
        }

        // ============================
        //  POSTGRES INIT
        // ============================
        var pgConnStr = cfg["Postgres:ConnectionString"];

        Data.PgConnectionFactory? dbFactory = null;
        TradeJournalRepository? journalRepo = null;
        TradeFillRepository? fillRepo = null;
        TradeSignalRepository? signalRepo = null;
        BrokerOrderRepository? orderRepo = null;
        DailyPnlRepository? pnlRepo = null;
        MarketTickRepository? tickRepo = null;
        SwingPositionRepository? swingRepo = null;
        SignalSlayerDecisionRepository? slayerRepo = null;
        
        // Discord notifier
        var discordWebhookUrl = cfg["Discord:WebhookUrl"];
        DiscordNotifier? discordNotifier = null;
        if (!string.IsNullOrWhiteSpace(discordWebhookUrl))
        {
            discordNotifier = new DiscordNotifier(discordWebhookUrl, log);
            log.Information("[DISCORD] Discord notifier initialized");
        }

        if (!string.IsNullOrWhiteSpace(pgConnStr))
        {
            dbFactory = new Data.PgConnectionFactory(pgConnStr, log);
            var hbRepo = new ServiceHeartbeatRepository(dbFactory, log);

            using var dbCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            try
            {
                await using var conn = await dbFactory.CreateOpenConnectionAsync(dbCts.Token);
                log.Information("[DB] Connected successfully. Server={ServerVersion}", conn.PostgreSqlVersion);

                await hbRepo.InsertAsync("TradingEngine.Runner", "startup", dbCts.Token);

                journalRepo = new TradeJournalRepository(dbFactory);
                fillRepo = new TradeFillRepository(dbFactory, log);
                signalRepo = new TradeSignalRepository(dbFactory, log);
                orderRepo = new BrokerOrderRepository(dbFactory, log);
                pnlRepo = new DailyPnlRepository(dbFactory, log, discordNotifier);
                tickRepo = new MarketTickRepository(dbFactory, log);
                swingRepo = new SwingPositionRepository(dbFactory, log);
                slayerRepo = new SignalSlayerDecisionRepository(dbFactory, log);
            }
            catch (Exception ex)
            {
                log.Error(ex, "[DB] failed to connect or insert heartbeat");
            }
        }
        else
        {
            log.Warning("[DB] Postgres:ConnectionString is empty â€“ skipping DB init");
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.WriteLine("Graceful shutdown");
        };

        // ============================
        //  IBKR SESSION
        // ============================
        var wrapper = new IbkrDefaultWrapper();
        var realIbClient = new RealIbkrClient(wrapper);
        using var session = new IbkrSession(wrapper);

        Console.WriteLine($"Povezivanje na IBKR {ibkrHost}:{ibkrPort} clientId={ibkrClientId}");
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            connectCts.CancelAfter(TimeSpan.FromSeconds(ibkrConnectTimeoutSec + 35));

            await session.ConnectAsync(ibkrHost, ibkrPort, ibkrClientId, connectCts.Token);
        }
        catch (Exception ex)
        {
            log.Fatal(ex,
                "[FATAL] Ne mogu da se povezem na IBKR. Host={Host} Port={Port} ClientId={ClientId} Msg={Msg}",
                ibkrHost,
                ibkrPort,
                ibkrClientId,
                ex.Message);
            return;
        }
        Console.WriteLine($"[OK] IBKR konekcija spremna ({ibkrHost}:{ibkrPort}, clientId={ibkrClientId}).");

        // Discord alert kad IBKR konekcija padne (runtime â€“ TWS/API crash, mreÅ¾a)
        wrapper.ConnectionClosed += () =>
        {
            log.Warning("[IBKR] Connection closed â€“ sending Discord alert");
            _ = discordNotifier?.NotifyWarningAsync(
                "âš ï¸ IBKR connection lost",
                "TWS/Gateway disconnected. Engine is reconnecting...",
                details: "Check TWS/IB Gateway and network.",
                CancellationToken.None);
        };
        // Discord alert kada IB API vrati 504 (Not connected).
        // Throttle da ne spamuje kanal dok je TWS/Gateway offline.
        var ibNotConnectedAlertSync = new object();
        var ibNotConnectedLastAlertUtc = DateTime.MinValue;
        var ibNotConnectedAlertThrottle = TimeSpan.FromMinutes(5);
        wrapper.ErrorReceived += (id, errorCode, errorMsg) =>
        {
            log.Warning(
                "[IBKR][ERROR-EVENT] id={Id} code={Code} msg={Msg}",
                id,
                errorCode,
                errorMsg);

            if (errorCode != 504)
                return;

            var nowUtc = DateTime.UtcNow;
            var shouldNotify = false;

            lock (ibNotConnectedAlertSync)
            {
                if (nowUtc - ibNotConnectedLastAlertUtc >= ibNotConnectedAlertThrottle)
                {
                    ibNotConnectedLastAlertUtc = nowUtc;
                    shouldNotify = true;
                }
            }

            if (!shouldNotify)
                return;

            log.Warning("[IBKR] 504 Not connected detected - sending Discord alert");
            _ = discordNotifier?.NotifyWarningAsync(
                "IBKR not connected (504)",
                "IB API returned 'Not connected'. TWS/Gateway likely logged out or disconnected.",
                details: $"id={id}, errorCode={errorCode}, errorMsg={errorMsg}. Re-login to TWS/Gateway may be required.",
                CancellationToken.None);
        };

        // FX provider (za konverziju base->USD kad snapshot nema USD linije)
        using var fx = new IbkrFxRateProvider(wrapper);

        // ============================
        //  IBKR ACCOUNT SNAPSHOT (OBSERVABILITY ONLY)
        //  (NE DIRAMO starting cash ovde, samo log)
        // ============================
        using (var acctSnap = new IbkrAccountSnapshotProvider(wrapper))
        {
            var snap = await acctSnap.GetOnceAsync(TimeSpan.FromSeconds(5), cts.Token);
            if (snap is not null)
            {
                log.Information(
                    "[IB-ACCT] acc={Acc} base={BaseCur} " +
                    "NetLiqBase={NetLiqB:F2} EwlBase={EwlB:F2} CashBase={CashB:F2} SettledBase={SetB:F2} AvailBase={AvailB:F2} BPBase={BPB:F2} | " +
                    "NetLiqUsd={NetLiqU:F2} EwlUsd={EwlU:F2} CashUsd={CashU:F2} SettledUsd={SetU:F2} AvailUsd={AvailU:F2} BPUsd={BPU:F2}",
                    snap.Account ?? "n/a",
                    snap.BaseCurrency ?? "n/a",

                    snap.NetLiquidationBase ?? 0m,
                    snap.EquityWithLoanBase ?? 0m,
                    snap.TotalCashValueBase ?? 0m,
                    snap.SettledCashBase ?? 0m,
                    snap.AvailableFundsBase ?? 0m,
                    snap.BuyingPowerBase ?? 0m,

                    snap.NetLiquidationUsd ?? 0m,
                    snap.EquityWithLoanUsd ?? 0m,
                    snap.TotalCashValueUsd ?? 0m,
                    snap.SettledCashUsd ?? 0m,
                    snap.AvailableFundsUsd ?? 0m,
                    snap.BuyingPowerUsd ?? 0m
                );
                            }
            else
            {
                log.Warning("[IB-ACCT] snapshot not available (timeout)");
            }
        }

        // ============================
        //  REAL vs PAPER INIT (cash + ext positions)
        //  (OVDE JE JEDINO MESTO GDE SE ODREÄUJE startingCashUsd)
        // ============================
        IExternalPositionsProvider extPositions;
        decimal startingCashUsd;

        if (string.Equals(mode, "Real", StringComparison.OrdinalIgnoreCase))
        {
            var realInit = await RealTradingInitializer.InitializeAsync(
                cfg,
                wrapper,
                realIbClient,
                fx,
                perSymbolBudgetUsd,
                dbFactory,
                cts.Token);

            if (realInit.StartingCashUsd > 0m)
            {
                startingCashUsd = realInit.StartingCashUsd;
                log.Information("[CASH] REAL: using IBKR-derived cash: {CashUsd:F2} USD", startingCashUsd);
            }
            else
            {
                startingCashUsd = configuredStartingCashUsd;
                log.Warning("[CASH] REAL: IBKR cash invalid -> fallback to configured cash: {CashUsd:F2} USD", startingCashUsd);
            }

            extPositions = realInit.ExternalPositions;
            log.Information("[MODE] REAL init â€“ cash + external positions ready");
        }
        else
        {
            extPositions = new IbkrPositionsProvider(wrapper);
            startingCashUsd = perSymbolBudgetUsd;
            log.Information("[CASH] PAPER: starting cash = PerSymbolBudgetUSD ({CashUsd:F2} USD)", startingCashUsd);
            log.Information("[MODE] PAPER init â€“ using IBKR positions provider (observability)");
        }

        // ============================
        //  MARKET DATA FEED
        // ============================
        var mdFeed = new MarketDataFeedIbkr(wrapper, session);

        BoundedTickQueue? tickQueue = null;
        if (tickRepo is not null)
        {
            tickQueue = new BoundedTickQueue(
                capacity: 5000,
                onTickAsync: _ => Task.CompletedTask,
                tickRepo: tickRepo,
                batchSize: 100,
                maxBatchDelay: TimeSpan.FromMilliseconds(200),
                log: log
            );

            mdFeed.MarketQuoteUpdated += q => _ = tickQueue.TryEnqueue(q);
        }

        // ============================
        //  STRATEGY + RISK
        // ============================
        var pullbackConfig = PullbackConfigProvider.GetConfig();

        // Load SignalSlayer config from appsettings.json
        var slayerConfigSection = cfg.GetSection("SignalSlayer");
        var slayerConfig = slayerConfigSection.Get<SignalSlayerConfig>() ?? new SignalSlayerConfig
        {
            EnableMicroFilter = true,
            DistributionProtection = new DistributionProtectionConfig
            {
                Enabled = false,
                LogWhenDisabled = true,
                RejectionSpeed = new RejectionSpeedConfig
                {
                    Enabled = false,
                    DropPctThreshold = 0.0030m,
                    WindowSec = 60
                },
                TimeOfDay = new TimeOfDayConfig
                {
                    Enabled = false,
                    MaxMinutesFromOpen = 8,
                    MinMovePct = 0.007m,
                    RequireValidPullback = true
                }
            }
        };
        
        if (slayerConfig.DistributionProtection != null)
        {
            log.Information(
                "[SIGNAL-SLAYER] DistributionProtection Enabled={Enabled} LogWhenDisabled={Log} RejectionSpeed.Enabled={RejSpeed} TimeOfDay.Enabled={TimeOfDay}",
                slayerConfig.DistributionProtection.Enabled,
                slayerConfig.DistributionProtection.LogWhenDisabled,
                slayerConfig.DistributionProtection.RejectionSpeed.Enabled,
                slayerConfig.DistributionProtection.TimeOfDay.Enabled);
        }

        // Load Trading config for strategy
        var tradingSection = cfg.GetSection("Trading");
        var useMidPrice = tradingSection.GetValue<bool>("UseMidPrice", false);
        var minQuantity = tradingSection.GetValue<int>("MinQuantity", 3);
        var enableStopLimitOutsideRth = tradingSection.GetValue<bool>("EnableStopLimitOutsideRth", false);

        log.Information("[TRADING-CONFIG] UseMidPrice={UseMid} MinQuantity={MinQty} EnableStopLimitOutsideRth={EnableOrthSl}",
            useMidPrice, minQuantity, enableStopLimitOutsideRth);

        var strategy = new PullbackInUptrendStrategy(
            pullbackConfig, 
            slayerConfig,
            slayerRepo: slayerRepo,
            runEnv: mode,
            useMidPrice: useMidPrice,
            minQuantity: minQuantity);
        var riskValidator = new FeeAwareRiskValidator(0.01m, 0.005m);

        ITrendContextProvider? ibkrTrendProvider = null;
        if (dbFactory is not null)
        {
            var trendRepo = new TrendMarketDataRepository(dbFactory, log);
            var trendSettings = tradingSection.Get<TrendAnalysisSettings>() ?? new TrendAnalysisSettings();
            ibkrTrendProvider = new IbkrTrendContextProvider(trendRepo, trendSettings);
        }

        var riskLimits = new RiskLimits(
            MaxRiskPerTradeFraction: 0.02m,
            MaxExposurePerSymbolFrac: 0.25m,
            DailyLossStopFraction: 0.025m,
            MaxPerTradeFrac: 0.25m,
            MinAtrFraction: cfg.GetValue<decimal>("Risk:MinAtrFraction", 0.002m) // default 0.2%
        );

        var dailyLossStopUsd = Math.Round(startingCashUsd * 0.025m, 2);

        var fees = new CommissionSchedule(
            EstimatedPerOrderUsd: 0.35m,
            EstimatedRoundTripUsd: 0.70m
        );

        // Day guards - Äita MaxTradesPerSymbol i MaxTradesTotal iz TradingSettings
        // Ako nisu postavljeni u config-u, koristi default vrednosti (1 i 2)
        var maxTradesPerSymbol = tradingSettings.MaxTradesPerSymbol > 0
            ? tradingSettings.MaxTradesPerSymbol
            : 1;
        var maxTradesTotal = tradingSettings.MaxTradesTotal > 0
            ? tradingSettings.MaxTradesTotal
            : 2;
        
        log.Information("[DAY-GUARDS] Config loaded: MaxTradesPerSymbol={PerSym} MaxTradesTotal={Total} DailyLossStopUsd={LossStop:F2}", 
            maxTradesPerSymbol, maxTradesTotal, dailyLossStopUsd);
        
        var dayGuards = new DayGuards(
            new DayGuardLimits(
                MaxTradesPerSymbol: maxTradesPerSymbol,
                MaxTradesTotal: maxTradesTotal,
                DailyLossStopUsd: dailyLossStopUsd
            )
        );

        var cashService = new CashManager(initialFreeUsd: startingCashUsd);

        // ============================
        //  ORCHESTRATOR
        // ============================
        using var orchestrator = new TradingOrchestrator(
            isRealMode: string.Equals(mode, "Real", StringComparison.OrdinalIgnoreCase),
            feed: mdFeed,
            strategy: strategy,
            risk: riskValidator,
            fees: fees,
            limits: riskLimits,
            perSymbolBudgetUsd: perSymbolBudgetUsd,
            orderService: null,
            dayGuards: dayGuards,
            exposure: null,
            cashService: cashService,
            externalPositions: extPositions,
            settings: tradingSettings,
            journalRepo: journalRepo,
            fillRepo: fillRepo,
            signalRepo: signalRepo,
            orderRepo: orderRepo,
            pnlRepo: pnlRepo,
            swingPosRepo: swingRepo,
            equityFloorUsd: equityFloorUsd,
            minFreeCashUsd: minFreeCashUsd,
            swingConfig: swinfConfig,
            discordNotifier: discordNotifier,
            trendContextProvider: ibkrTrendProvider,
            ibkrEodSkimOptions: ibkrEodSkimOptions
        );

        await orchestrator.RecoverOnStartupAsync(cts.Token);

        // ============================
        //  RECONCILE PENDING VS IBKR (REAL MODE ONLY)
        // ============================
        var ibOpenTwsByCorrelation = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (string.Equals(mode, "Real", StringComparison.OrdinalIgnoreCase))
        {
            var openOrdersProvider = new IbkrOpenOrdersProvider(wrapper);

            var ibOpen = await openOrdersProvider.GetOpenOrdersAsync(cts.Token);

            foreach (var o in ibOpen)
            {
                if (!string.IsNullOrWhiteSpace(o.OrderRef) && o.TwsOrderId > 0)
                    ibOpenTwsByCorrelation[o.OrderRef] = o.TwsOrderId;
            }

            orchestrator.SetRecoveredIbkrOpenOrderMap(ibOpenTwsByCorrelation);

            var brokerCorrelationIds = ibOpen
                .Select(o => o.OrderRef)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            await orchestrator.ReconcilePendingWithBrokerAsync(brokerCorrelationIds, cts.Token);
        }

        // ============================
        //  REAL ORDER SERVICE (IBKR)
        // ============================
        if (string.Equals(mode, "Real", StringComparison.OrdinalIgnoreCase))
        {
            // FIX: UÄitaj max broker_order_id iz baze i koristi ga za startingOrderId
            // Osigurava da ne koristimo ID-jeve koji su veÄ‡ koriÅ¡Ä‡eni nakon restarta
            int startingOrderId = 1001; // default za praznu bazu (prvi order Ä‡e biti 1001)
            if (orderRepo is not null)
            {
                try
                {
                    var maxDbId = await orderRepo.GetMaxBrokerOrderIdAsync(cts.Token);
                    if (maxDbId > 0)
                    {
                        startingOrderId = maxDbId + 1;
                        log.Information("[INIT] Using max BrokerOrderId from DB: {MaxId}, starting IbkrOrderService at {StartId}", maxDbId, startingOrderId);
                    }
                    else
                    {
                        log.Information("[INIT] No existing broker_order_id found in DB, using default startingOrderId={StartId}", startingOrderId);
                    }
                }
                catch (Exception ex)
                {
                    log.Warning(ex, "[INIT][WARN] Failed to load max BrokerOrderId from DB for IbkrOrderService - using default {StartId}", startingOrderId);
                }
            }
            
            var ibOrderService = new IbkrOrderService(
                realIbClient,
                (req, px, utc) => orchestrator.OnRealFilled(req, px, utc),
                startingOrderId: startingOrderId
            );

            orchestrator.SetOrderService(ibOrderService);
            log.Information("[MODE] REAL trading mode â€“ IbkrOrderService attached");

            // Register recovered exit orders in IbkrOrderService
            orchestrator.RegisterRecoveredExitOrders();

            _ = orchestrator.ImportExternalPositionsNowAsync(cts.Token);

            Console.WriteLine("==============================================");
            Console.WriteLine("TRADING MODE: REAL");
        }
        else
        {
            log.Information("[MODE] PAPER trading mode â€“ no real order service (paper only)");
            Console.WriteLine("==============================================");
            Console.WriteLine("TRADING MODE: PAPER");
        }

        // ============================
        //  RUNTIME INFO
        // ============================
        log.Information("Capital={CashUsd:F2} USD | DailyDD={DailyDdUsd:F2} USD", startingCashUsd, dailyLossStopUsd);
        Console.WriteLine("CTRL+C za stop");
        Console.WriteLine("==============================================");

        // ============================
        //  PERIODIC TASKS
        // ============================
        var runtime = cfg.GetSection("Runtime");
        var heartbeatSec = runtime.GetValue<int>("HeartbeatPeriodSec", 60);
        var sweepSec = runtime.GetValue<int>("PendingSweepSec", 60);
        var ttlSec = runtime.GetValue<int>("PendingTtlSec", 900);

        orchestrator.StartHeartbeat(TimeSpan.FromSeconds(heartbeatSec), cts.Token);
        orchestrator.StartPendingExpiryWatcher(TimeSpan.FromSeconds(sweepSec), TimeSpan.FromSeconds(ttlSec), cts.Token);
        orchestrator.StartExternalPositionsSync(TimeSpan.FromSeconds(60), cts.Token);

        // ============================
        //  SUBSCRIBE SYMBOLS
        // ============================
        if (tradingSettings.Symbols is not null && tradingSettings.Symbols.Count > 0)
        {
            foreach (var sc in tradingSettings.Symbols)
            {
                if (!string.IsNullOrWhiteSpace(sc.Symbol))
                {
                    mdFeed.SubscribeQuotes(new Symbol(sc.Symbol));
                    log.Information("Subscribovan simbol: {Sym}", sc.Symbol);
                }
            }
        }
        else
        {
            var symbols = cfg.GetSection("Symbols").Get<string[]>() ?? Array.Empty<string>();
            foreach (var s in symbols.Where(s => !string.IsNullOrWhiteSpace(s)))
            {
                var trimmed = s.Trim();
                mdFeed.SubscribeQuotes(new Symbol(trimmed));
                log.Information("Subscribovan simbol: {Sym}", trimmed);
            }
        }

        // ============================
        //  CRYPTO TRADING (PARALLEL)
        // ============================
        var enableCrypto = cfg.GetValue<bool>("Crypto:Enabled", false);
        Task? cryptoTask = null;
        
        if (enableCrypto)
        {
            log.Information("[CRYPTO] Crypto trading enabled - starting in parallel with IBKR");
            var cryptoRunner = new CryptoTradingRunner();
            cryptoTask = cryptoRunner.RunAsync(cfg, cts.Token);
        }
        else
        {
            log.Information("[CRYPTO] Crypto trading disabled (set Crypto:Enabled=true to enable)");
        }

        // ============================
        //  MAIN LOOP
        // ============================
        try
        {
            if (cryptoTask != null)
            {
                // ÄŒekaj da se zavrÅ¡e oba sistema (IBKR i Crypto)
                await Task.WhenAny(
                    Task.Delay(Timeout.Infinite, cts.Token),
                    cryptoTask
                );
            }
            else
            {
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }

        Console.WriteLine("Zatvaram aplikaciju");
    }
}

