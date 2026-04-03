#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Denis.TradingEngine.Core.Accounts;
using Denis.TradingEngine.Core.Crypto;
using Denis.TradingEngine.Core.Interfaces;
using Denis.TradingEngine.Core.Orders;
using Denis.TradingEngine.Core.Positions;
using Denis.TradingEngine.Core.Risk;
using Denis.TradingEngine.Core.Swing;
using Denis.TradingEngine.Core.Trading;
using Denis.TradingEngine.Data.Repositories;
using Denis.TradingEngine.Logging;
using Denis.TradingEngine.Logging.Discord;
using Denis.TradingEngine.MetricsServer;
using Denis.TradingEngine.Orders;
using Denis.TradingEngine.Simulation;
using Denis.TradingEngine.Strategy.Filters;
using Denis.TradingEngine.Strategy.GreenGrind;
using Denis.TradingEngine.Strategy.Pullback;
using Denis.TradingEngine.Strategy.Trend;
using Denis.TradingEngine.Exchange.Crypto.Config;
using Denis.TradingEngine.Exchange.Crypto.Abstractions;
using System.Collections.Concurrent;
using System.Linq;
using Serilog;

namespace Denis.TradingEngine.Exchange.Crypto.Trading;

/// <summary>
/// Crypto trading orchestrator - zaseban orchestrator za crypto trgovanje.
/// Koristi iste core komponente kao TradingOrchestrator, ali bez RTH provera (24/7 trgovanje).
/// </summary>
public sealed class CryptoTradingOrchestrator : IDisposable
{
    private readonly ILogger _log = AppLog.ForContext<CryptoTradingOrchestrator>();

    // ---------------- CORE STANJE ----------------
    private readonly bool _isRealMode;
    private readonly PositionBook _positionBook = new();
    private readonly IMarketDataFeed _feed;
    private readonly ITradingStrategy _strategy;
    private readonly IRiskValidator _risk;
    private readonly CommissionSchedule _fees;
    private readonly CryptoFeeSchedule? _cryptoFeeSchedule;
    private readonly RiskLimits _limits;
    private readonly decimal _perSymbolBudgetUsd;
    private readonly IReadOnlyDictionary<string, decimal> _perSymbolBudgetByTicker;
    private readonly IDayGuardService? _dayGuards;
    private IOrderService? _orderService;
    private readonly IAccountCashService _cashService;
    private readonly PaperExecutionSimulator _paperSim = new();
    private readonly Action<MarketQuote>? _paperForwarder;
    private readonly IOrderCoordinator _orders = new OrderCoordinator();
    private readonly CryptoSwingConfig _swingConfig;

    // Zaštita od duplih exit-ova
    private readonly object _sync = new();
    private readonly Dictionary<string, DateTime> _lastExitUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _exitPending = new(StringComparer.OrdinalIgnoreCase);

    // Cancel dedupe: ne šalji dupli cancel za isti brokerOrderId u kratkom vremenu
    private readonly Dictionary<string, DateTime> _cancelRequestedAt = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CancelDedupeWindow = TimeSpan.FromSeconds(5);

    // TP/SL heuristika (po berzi iz CryptoExchangeTradingParams ili default)
    private readonly decimal _tpFraction;
    private readonly decimal _slFraction;

    // Pozadinsko osvežavanje keša
    private readonly CancellationTokenSource _cashRefreshCts = new();
    private Core.Accounts.CashState _lastCash = new(0m, 0m, 0m);

    // Repositories
    private readonly TradeJournalRepository? _journalRepo;
    private readonly TradeFillRepository? _fillRepo;
    private readonly TradeSignalRepository? _signalRepo;
    private readonly BrokerOrderRepository? _orderRepo;
    private readonly CryptoDailyPnlRepository? _pnlRepo;
    private readonly SwingPositionRepository? _swingPosRepo;
    private readonly MarketTickRepository? _marketTickRepo;
    private readonly CryptoTradesRepository? _cryptoTradesRepo;

    // Cache poslednjeg kvota po simbolu
    private readonly Dictionary<string, MarketQuote> _lastQuotes = new(StringComparer.OrdinalIgnoreCase);

    // Equity floor zaštita
    private readonly decimal _equityFloorUsd;
    private readonly decimal _minFreeCashUsd;

    // ATR tracking
    private readonly Dictionary<string, AtrState> _atr = new();

    // Pullback config root (za DebugLogging po simbolu)
    private readonly PullbackConfigRoot? _pullbackConfig;

    // Exchange name (za logovanje)
    private readonly string _exchangeName;

    // Discord notifier
    private readonly DiscordNotifier? _discordNotifier;
    private readonly ITrendContextProvider? _trendContextProvider;
    private readonly GreenGrindSettings? _greenGrindSettings;
    private readonly SymbolGreenGrindRegimeService? _greenGrindRegime;
    private readonly Dictionary<string, DateTime> _greenGrindAlertSentUtc = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan GreenGrindAlertCooldown = TimeSpan.FromMinutes(15);

    // Trading API (za dohvatanje stvarnih balances u heartbeat-u)
    private ICryptoTradingApi? _tradingApi;
    
    // Symbol metadata provider (za filtriranje enabled simbola u heartbeat-u)
    private ICryptoSymbolMetadataProvider? _symbolProvider;

    // Position runtime state (za trailing stop)
    private readonly Dictionary<string, PositionRuntimeState> _posRuntime = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastExitLogUtc = new(StringComparer.OrdinalIgnoreCase);

    // Trailing stop parametri
    private readonly decimal _trailActivateFraction = 0.01m; // aktivira se posle +1%
    private readonly decimal _trailDistanceFraction = 0.005m; // stop ~0.5% ispod najboljeg

    // Max vreme držanja pozicije
    private readonly TimeSpan _maxHoldTime;

    // Cumulative filled po orderu
    private readonly Dictionary<string, decimal> _cumFilledByCorrId = new(StringComparer.Ordinal);

    // Global strategy rate-limit
    private readonly int _maxSignalsPerWindow = 5;
    private readonly TimeSpan _signalWindow = TimeSpan.FromSeconds(10);
    private readonly Queue<DateTime> _signalTimestamps = new();

    // ATR-based TP/SL
    private const decimal TpAtrMultiple = 2.5m;
    private const decimal SlAtrMultiple = 1.5m;
    private const decimal TrailActivateAtrMultiple = 1.0m;
    private const decimal TrailDistanceAtrMultiple = 0.7m;

    // Symbol cooldown posle gubitka
    private readonly TimeSpan _symbolCooldownAfterLoss = TimeSpan.FromMinutes(15);
    private readonly Dictionary<string, DateTime> _lastLossUtc = new(StringComparer.OrdinalIgnoreCase);

    // ATR sizing metadata
    private readonly Dictionary<string, (decimal RiskFraction, decimal? AtrUsed, decimal? PriceRisk)> _sizingMetadata = new(StringComparer.Ordinal);

    // Idempotentnost za real fills
    private readonly Dictionary<string, DateTime> _processedRealFills = new(StringComparer.Ordinal);
    private static readonly TimeSpan ProcessedFillTtl = TimeSpan.FromMinutes(30);

    // Broker timeout
    private readonly TimeSpan _brokerPlaceTimeout = TimeSpan.FromSeconds(5);

    // Max spread fraction (crypto može imati veći spread)
    private const decimal MaxSpreadFraction = 0.01m; // 1% za crypto (veći nego stocks)

    // Max quote age (crypto trguje 24/7, možemo biti fleksibilniji)
    // Olabavljeno za crypto - crypto market data može biti sporiji od stocks
    private static readonly TimeSpan MaxQuoteAge = TimeSpan.FromSeconds(60);

    // Terminal duplicate guard
    private readonly ConcurrentDictionary<string, DateTime> _terminalByBrokerId = new();

    // Cancel tracking
    private readonly Dictionary<string, DateTime> _cancelRequestedUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastCancelUtcByBrokerId = new(StringComparer.Ordinal);

    // Startup position sync retry (kada prvi sync preskoči jer quote još nije stigao)
    private const int MaxStartupPositionSyncRetries = 6;
    private volatile bool _startupPositionSyncPending;
    private int _startupPositionSyncRetryCount;
    private int _startupPositionSyncRetryRunning;

    public CryptoTradingOrchestrator(
        bool isRealMode,
        IMarketDataFeed feed,
        ITradingStrategy strategy,
        IRiskValidator risk,
        CommissionSchedule fees,
        RiskLimits limits,
        decimal perSymbolBudgetUsd,
        IReadOnlyDictionary<string, decimal>? perSymbolBudgetByTicker = null,
        IOrderService? orderService = null,
        IDayGuardService? dayGuards = null,
        IAccountCashService? cashService = null,
        TradeJournalRepository? journalRepo = null,
        TradeFillRepository? fillRepo = null,
        TradeSignalRepository? signalRepo = null,
        BrokerOrderRepository? orderRepo = null,
        CryptoDailyPnlRepository? pnlRepo = null,
        SwingPositionRepository? swingPosRepo = null,
        MarketTickRepository? marketTickRepo = null,
        CryptoTradesRepository? cryptoTradesRepo = null,
        decimal equityFloorUsd = 0m,
        decimal minFreeCashUsd = 0m,
        CryptoSwingConfig? swingConfig = null,
        PullbackConfigRoot? pullbackConfig = null,
        string exchangeName = "",
        DiscordNotifier? discordNotifier = null,
        CryptoFeeSchedule? cryptoFeeSchedule = null,
        CryptoExchangeTradingParams? tradingParams = null,
        ITrendContextProvider? trendContextProvider = null,
        GreenGrindSettings? greenGrindSettings = null)
    {
        _isRealMode = isRealMode;
        _equityFloorUsd = equityFloorUsd;
        _minFreeCashUsd = minFreeCashUsd;

        if (_minFreeCashUsd > 0m)
        {
            _log.Information("[CASH-FLOOR] Enabled min free cash={Floor:F2}", _minFreeCashUsd);
        }

        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        _risk = risk ?? throw new ArgumentNullException(nameof(risk));
        _fees = fees ?? throw new ArgumentNullException(nameof(fees));
        _cryptoFeeSchedule = cryptoFeeSchedule;
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _perSymbolBudgetUsd = perSymbolBudgetUsd;
        _perSymbolBudgetByTicker = perSymbolBudgetByTicker ?? new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        _dayGuards = dayGuards;
        _orderService = orderService;
        _cashService = cashService ?? throw new ArgumentNullException(nameof(cashService));
        _journalRepo = journalRepo;
        _fillRepo = fillRepo;
        _signalRepo = signalRepo;
        _orderRepo = orderRepo;
        _pnlRepo = pnlRepo;
        _swingPosRepo = swingPosRepo;
        _marketTickRepo = marketTickRepo;
        _cryptoTradesRepo = cryptoTradesRepo;
        _pullbackConfig = pullbackConfig;
        _exchangeName = exchangeName;
        _discordNotifier = discordNotifier;
        _trendContextProvider = trendContextProvider;
        _greenGrindSettings = greenGrindSettings;
        if (greenGrindSettings is not null && (greenGrindSettings.Enabled || greenGrindSettings.DryRun))
        {
            _greenGrindRegime = new SymbolGreenGrindRegimeService(greenGrindSettings, _log);
            _greenGrindRegime.StateChanged += OnGreenGrindStateChanged;
            _log.Information("[GREEN-GRIND] Initialized crypto regime gate enabled={Enabled} dryRun={DryRun} scope={Scope}",
                greenGrindSettings.Enabled, greenGrindSettings.DryRun, greenGrindSettings.Scope ?? "Symbol");
        }

        // Helper za računanje fees
        if (_cryptoFeeSchedule != null)
        {
            _log.Information("[CRYPTO-FEES] Using fee schedule: Exchange={Exchange} Type={Type} Maker={Maker:P4}% Taker={Taker:P4}%",
                _cryptoFeeSchedule.ExchangeId, _cryptoFeeSchedule.TradeType ?? "Spot",
                _cryptoFeeSchedule.MakerFeePercent, _cryptoFeeSchedule.TakerFeePercent);
        }
        else
        {
            _log.Information("[CRYPTO-FEES] Using legacy CommissionSchedule: {PerOrder:F2} USD per order",
                _fees.EstimatedPerOrderUsd);
        }

        // Event handlers (BEZ RTH provera - crypto trguje 24/7)
        _feed.MarketQuoteUpdated += _strategy.OnQuote;
        _feed.MarketQuoteUpdated += EvaluateCryptoExitsOnQuote;

        _paperForwarder = q =>
        {
            try
            {
                _paperSim.OnQuote(q.Symbol, q.Last, q.Bid, q.Ask, q);
            }
            catch
            {
                // tolerantno
            }
        };
        _feed.MarketQuoteUpdated += _paperForwarder;
        _feed.MarketQuoteUpdated += OnQuoteCached;
        _feed.MarketQuoteUpdated += UpdateAtrOnQuote;
        if (_greenGrindRegime is not null)
            _feed.MarketQuoteUpdated += OnQuoteGreenGrind;

        // Strategija -> orchestrator
        _strategy.TradeSignalGenerated += OnTradeSignal;

        // Paper simulator -> fill
        _paperSim.Filled += OnPaperFilled;

        // Pozadinski refresh keša
        _ = Task.Run(async () =>
        {
            var token = _cashRefreshCts.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var cs = await _cashService.GetCashStateAsync().ConfigureAwait(false);
                    _lastCash = cs;
                }
                catch
                {
                    // ignore
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                }
                catch
                {
                    // ignore
                }
            }
        });

        // Ako imamo real order service, slušaj ga
        if (_orderService is not null)
        {
            _orderService.OrderUpdated += OnOrderUpdated;
        }

        _swingConfig = swingConfig ?? new CryptoSwingConfig { Mode = CryptoSwingMode.Swing };

        // TP/SL i trailing po berzi (tradingParams) ili default
        _tpFraction = tradingParams?.TpFraction ?? 0.014m;
        _slFraction = tradingParams?.SlFraction ?? 0.01m;
        _trailActivateFraction = tradingParams?.TrailActivateFraction ?? 0.01m;
        _trailDistanceFraction = tradingParams?.TrailDistanceFraction ?? 0.005m;

        _maxHoldTime = tradingParams != null && tradingParams.MaxHoldTimeMinutes > 0
            ? TimeSpan.FromMinutes(tradingParams.MaxHoldTimeMinutes)
            : (_swingConfig.Mode == CryptoSwingMode.Swing && _swingConfig.MaxHoldingDays > 0
                ? TimeSpan.FromDays(_swingConfig.MaxHoldingDays)
                : TimeSpan.FromMinutes(30));

        var initialMode = _orderService is not null ? $"Real ({_orderService.GetType().Name})"
            : _isRealMode ? "Real (order service pending attach)"
            : "Paper (no order service)";

        _log.Information("[CRYPTO-ORCH] READY. InitialMode={Mode}", initialMode);
    }

    /// <summary>
    /// Izračunava fee u USD na osnovu notional vrednosti i tipa naloga.
    /// Ako je CryptoFeeSchedule dostupan, koristi ga; inače koristi legacy CommissionSchedule.
    /// </summary>
    private decimal CalculateFeeUsd(decimal notionalUsd, bool isMaker = false)
    {
        if (_cryptoFeeSchedule != null)
        {
            return _cryptoFeeSchedule.CalculateFeeUsd(notionalUsd, isMaker);
        }

        // Fallback na legacy CommissionSchedule (fiksni fee po nalogu)
        return _fees.EstimatedPerOrderUsd;
    }

    // TODO: Implementirati metode:
    // - OnTradeSignal (entry logika)
    // - EvaluateCryptoExitsOnQuote (exit logika, bez RTH provera)
    // - OnQuoteCached
    // - UpdateAtrOnQuote
    // - OnPaperFilled
    // - OnOrderUpdated
    // - ApplyFillCore (fill handling)
    // - RecoverOnStartupAsync

    private void OnQuoteCached(MarketQuote q)
    {
        lock (_sync)
        {
            _lastQuotes[q.Symbol.Ticker] = q;
        }

        TryScheduleStartupPositionSyncRetry();
    }

    private void OnQuoteGreenGrind(MarketQuote q)
    {
        try
        {
            _greenGrindRegime?.OnQuote(q);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "[GREEN-GRIND] Quote processing failed for {Sym}", q.Symbol?.Ticker);
        }
    }

    public void OnTradeTick(TradeTick tick)
    {
        if (_greenGrindRegime is null || tick.Symbol is null)
            return;

        try
        {
            _greenGrindRegime.OnTrade(
                symbol: tick.Symbol.PublicSymbol,
                utc: tick.TimestampUtc,
                quantity: tick.Quantity,
                isBuy: tick.Side == TradeSide.Buy);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "[GREEN-GRIND] Trade processing failed for {Sym}", tick.Symbol.PublicSymbol);
        }
    }

    private void OnGreenGrindStateChanged(GreenGrindStateTransition ev)
    {
        if (ev is null)
            return;

        // Log every transition (debugging/tracking), alert only when condition is met.
        _log.Information(
            "[GREEN-GRIND-STATE] {Sym} {Prev}->{Next} candidate={Cand} grindId={GrindId} net3h={NetBps} up3h={UpRatio} eff3h={Eff} spike3h={Spike} ctx_pct={CtxPct} rows3h={Rows} span3h={Span} gap3h={Gap}",
            ev.Symbol,
            ev.PreviousState,
            ev.NewState,
            ev.Snapshot.CandidateState,
            ev.Snapshot.GrindId ?? "n/a",
            ev.Snapshot.NetBps3h?.ToString("F1") ?? "n/a",
            ev.Snapshot.UpRatio3h?.ToString("F3") ?? "n/a",
            ev.Snapshot.Eff3h?.ToString("F3") ?? "n/a",
            ev.Snapshot.Spike3h?.ToString("F3") ?? "n/a",
            ev.Snapshot.CtxPct?.ToString("F4") ?? "n/a",
            ev.Snapshot.Rows3h,
            ev.Snapshot.Span3h?.ToString() ?? "n/a",
            ev.Snapshot.MaxGap3h?.ToString() ?? "n/a");

        if (_discordNotifier is null)
            return;

        var enteredActiveFamily =
            ev.PreviousState is not (GreenGrindState.Active or GreenGrindState.Strong)
            && ev.NewState is GreenGrindState.Active or GreenGrindState.Strong;

        if (!enteredActiveFamily)
            return;

        var eventTs = ev.Snapshot.AsOfUtc == default ? DateTime.UtcNow : ev.Snapshot.AsOfUtc;
        var dbSnapshot = TryGetGreenGrindDbSnapshot(ev.Symbol, eventTs, out var dbCfg);
        if (dbSnapshot is not null && !dbSnapshot.IsActive)
        {
            _log.Information(
                "[GREEN-GRIND] alert suppressed by DB parity {Sym} state={State} reason={Reason} net3h={NetBps} up3h={UpRatio} eff3h={Eff} spike3h={Spike} ctx_pct={CtxPct} rows3h={Rows} span3h={Span} gap3h={Gap}",
                ev.Symbol,
                ev.NewState,
                dbSnapshot.InactiveReason ?? "inactive",
                dbSnapshot.NetBps3h?.ToString("F1") ?? "n/a",
                dbSnapshot.UpRatio3h?.ToString("F3") ?? "n/a",
                dbSnapshot.Eff3h?.ToString("F3") ?? "n/a",
                dbSnapshot.Spike3h?.ToString("F3") ?? "n/a",
                dbSnapshot.CtxPct?.ToString("F4") ?? "n/a",
                dbSnapshot.Rows3h,
                dbSnapshot.Span3h?.ToString() ?? "n/a",
                dbSnapshot.MaxGap3h?.ToString() ?? "n/a");
            return;
        }

        var now = DateTime.UtcNow;
        var alertKey = $"{ev.Symbol}:{ev.NewState}:{ev.Snapshot.GrindId ?? "n/a"}";
        lock (_sync)
        {
            if (_greenGrindAlertSentUtc.TryGetValue(alertKey, out var sentAt) && (now - sentAt) < GreenGrindAlertCooldown)
                return;

            _greenGrindAlertSentUtc[alertKey] = now;

            var staleBefore = now - TimeSpan.FromHours(12);
            foreach (var key in _greenGrindAlertSentUtc.Where(kvp => kvp.Value < staleBefore).Select(kvp => kvp.Key).ToList())
                _greenGrindAlertSentUtc.Remove(key);
        }

        var desc = ev.NewState == GreenGrindState.Strong
            ? "Green grind STRONG active (symbol regime)"
            : "Green grind ACTIVE active (minimum duration confirmed)";
        var netBps = dbSnapshot?.NetBps3h ?? ev.Snapshot.NetBps3h;
        var upRatio = dbSnapshot?.UpRatio3h ?? ev.Snapshot.UpRatio3h;
        var rows3h = dbSnapshot?.Rows3h ?? ev.Snapshot.Rows3h;
        var maxGap3h = dbSnapshot?.MaxGap3h ?? ev.Snapshot.MaxGap3h;
        var trades3h = dbSnapshot?.Trades3h ?? ev.Snapshot.Trades3h;
        var imb3h = dbSnapshot?.Imb3h ?? ev.Snapshot.Imb3h;
        var details =
            $"symbol={ev.Symbol}\n" +
            $"state={ev.NewState} prev={ev.PreviousState}\n" +
            $"grindId={ev.Snapshot.GrindId ?? "n/a"}\n" +
            $"net3h_bps={(netBps?.ToString("F1") ?? "n/a")} up3h={(upRatio?.ToString("F3") ?? "n/a")} eff3h={(ev.Snapshot.Eff3h?.ToString("F3") ?? "n/a")} spike3h={(ev.Snapshot.Spike3h?.ToString("F3") ?? "n/a")} ctx_pct={(ev.Snapshot.CtxPct?.ToString("F4") ?? "n/a")}\n" +
            $"rows3h={rows3h} span3h={(dbSnapshot?.Span3h?.ToString() ?? ev.Snapshot.Span3h?.ToString() ?? "n/a")} maxGap3h={(maxGap3h?.ToString() ?? "n/a")} trades3h={trades3h} imb3h={(imb3h?.ToString("F3") ?? "n/a")}" +
            (dbCfg is not null ? $"\nsource=db-canonical bar={dbCfg.BarMinutes}m window={dbCfg.MinDurationMinutes}m" : "");

        _ = Task.Run(async () =>
        {
            try
            {
                await _discordNotifier.NotifyWarningAsync(
                    title: $"GREEN_GRIND {ev.NewState} {ev.Symbol}",
                    description: desc,
                    details: details,
                    ct: CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[GREEN-GRIND] Discord alert failed for {Sym}", ev.Symbol);
            }
        });

    }

    private void TryScheduleStartupPositionSyncRetry()
    {
        if (!_startupPositionSyncPending)
            return;

        if (_tradingApi is null || _symbolProvider is null)
            return;

        if (Interlocked.CompareExchange(ref _startupPositionSyncRetryRunning, 1, 0) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                var token = _cashRefreshCts.Token;

                var alreadyAttempted = Volatile.Read(ref _startupPositionSyncRetryCount);
                if (alreadyAttempted >= MaxStartupPositionSyncRetries)
                {
                    _startupPositionSyncPending = false;
                    _log.Warning(
                        "[CRYPTO-SYNC] Startup retry limit reached ({MaxRetries}). Position sync remains partial until next manual sync/restart.",
                        MaxStartupPositionSyncRetries);
                    return;
                }

                // Daj feed-u kratko vreme da popuni _lastQuotes za vise simbola.
                await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);

                var attempt = Interlocked.Increment(ref _startupPositionSyncRetryCount);
                _log.Information(
                    "[CRYPTO-SYNC] Startup retry attempt {Attempt}/{MaxRetries} triggered by market quote",
                    attempt,
                    MaxStartupPositionSyncRetries);

                await SyncPositionsFromBalancesAsync(_tradingApi, _symbolProvider, token, isStartupRetry: true)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // normalno na shutdown
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[CRYPTO-SYNC] Startup retry failed");
            }
            finally
            {
                Interlocked.Exchange(ref _startupPositionSyncRetryRunning, 0);
            }
        });
    }

    private GreenGrindDbSnapshotRow? TryGetGreenGrindDbSnapshot(
        string symbol,
        DateTime asOfUtc,
        out GreenGrindResolvedSettings? resolvedCfg)
    {
        resolvedCfg = null;

        if (string.IsNullOrWhiteSpace(symbol) || _marketTickRepo is null || _greenGrindSettings is null)
            return null;

        var cfg = _greenGrindSettings.Resolve(symbol);
        if (!cfg.Enabled && !cfg.DryRun)
            return null;

        resolvedCfg = cfg;

        if (string.IsNullOrWhiteSpace(_exchangeName))
            return null;

        var ts = asOfUtc == default ? DateTime.UtcNow : asOfUtc;
        var minWatchNetFraction = cfg.Watch.MinNetMoveBps / 10_000m;
        if (minWatchNetFraction < 0m)
            minWatchNetFraction = 0m;

        var minActiveNetFraction = cfg.ActivationThresholds.MinNetMoveBps / 10_000m;
        if (minActiveNetFraction < 0m)
            minActiveNetFraction = 0m;

        try
        {
            return _marketTickRepo
                .GetGreenGrindLatestSnapshotAsync(
                    exchange: _exchangeName,
                    symbol: symbol,
                    asOfUtc: ts,
                    barMinutes: cfg.BarMinutes,
                    rollingMinutes: cfg.MinDurationMinutes,
                    minValidBuckets: cfg.MinValidBuckets,
                    maxGapMinutes: cfg.MaxGapMinutes,
                    minWatchNetFraction: minWatchNetFraction,
                    minWatchUpRatio: cfg.Watch.MinUpRatio,
                    minActiveNetFraction: minActiveNetFraction,
                    minActiveUpRatio: cfg.ActivationThresholds.MinUpRatio,
                    minActiveEfficiency: cfg.ActivationThresholds.MinPathEfficiency,
                    maxRangeFraction: cfg.MaxRangeFraction,
                    maxPullbackFractionOfNet: cfg.MaxPullbackFractionOfNet,
                    maxSpikeConcentration: cfg.MaxSpikeConcentration,
                    minActiveContextHighPct: cfg.MinActiveContextHighPct,
                    contextLookbackMinutes: cfg.ContextLookbackMinutes,
                    spanToleranceMinutes: cfg.SpanToleranceMinutes,
                    requireFlowConfirmation: cfg.RequireFlowConfirmation,
                    minTrades3h: cfg.MinTrades3h,
                    minImbalance3h: cfg.MinImbalance3h,
                    ct: CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[GREEN-GRIND] DB snapshot failed for {Sym}", symbol);
            return null;
        }
    }

    private void UpdateAtrOnQuote(MarketQuote q)
    {
        try
        {
            if (q?.Symbol is null || q.Last is null) return;

            var key = q.Symbol.Ticker;

            if (!_atr.TryGetValue(key, out var s))
                _atr[key] = s = new AtrState();

            var px = q.Last.Value;

            if (s.Prev is null)
            {
                s.Prev = px;
                return;
            }

            var tr = Math.Abs(px - s.Prev.Value);
            s.Tr.Enqueue(tr);
            if (s.Tr.Count > 14) s.Tr.Dequeue();

            if (s.Tr.Count == 14)
                s.Atr = s.Tr.Average();

            s.Prev = px;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[ATR] update failed");
        }
    }

    private void EvaluateCryptoExitsOnQuote(MarketQuote q)
    {
        try
        {
            if (q?.Symbol is null || (!q.Bid.HasValue && !q.Last.HasValue))
                return;

            var sym = q.Symbol.Ticker;
            var pos = _positionBook.Get(sym);
            if (pos is null || pos.Quantity <= 0m)
            {
                // Nema pozicije - normalno, preskačemo (ne logujemo da ne spamujemo)
                return;
            }

            // === REAL MODE: Proveri aktivne exit naloge ===
            // Native OCO: TP i SL su na brokeru; za TIME/TRAIL exit otkazujemo sve aktivne exit naloge za simbol.
            var activeExitBrokerOrderIds = new List<string>();
            if (_orderService != null)
            {
                var exitOrdersForSymbol = _orders.Snapshot()
                    .Where(po =>
                        po.Req.Symbol.Ticker.Equals(sym, StringComparison.OrdinalIgnoreCase) &&
                        po.Req.IsExit &&
                        !string.IsNullOrWhiteSpace(po.BrokerOrderId))
                    .ToList();
                foreach (var po in exitOrdersForSymbol)
                    if (!string.IsNullOrEmpty(po.BrokerOrderId))
                        activeExitBrokerOrderIds.Add(po.BrokerOrderId);

                // Proveri da li postoje DRUGI aktivni exit nalozi (ne SL/TP iz OCO)
                var hasOtherActiveExitOrders = _orders.Snapshot()
                    .Any(po => po.Req.Symbol.Ticker.Equals(sym, StringComparison.OrdinalIgnoreCase) &&
                               po.Req.IsExit &&
                               !po.Req.CorrelationId.StartsWith("exit-sl-", StringComparison.OrdinalIgnoreCase) &&
                               !po.Req.CorrelationId.StartsWith("exit-tp-", StringComparison.OrdinalIgnoreCase) &&
                               (po.BrokerOrderId != null || (DateTime.UtcNow - po.AtUtc).TotalMinutes < 5));

                if (hasOtherActiveExitOrders)
                {
                    // Postoje drugi aktivni exit nalozi (npr. TIME-EXIT, TRAIL-EXIT) - ne šaljemo dodatne
                    return;
                }
            }

            // DEBUG: Loguj samo kada imamo poziciju (svakih 30 sekundi da ne spamujemo) - samo ako je DebugLogging uključen za ovaj simbol
            var now = DateTime.UtcNow;
            var exchange = q.Symbol.Exchange ?? string.Empty;
            var debugLogging = _pullbackConfig?.Resolve(exchange, sym).DebugLogging ?? false;

            if (debugLogging)
            {
                lock (_sync)
                {
                    if (!_lastExitLogUtc.TryGetValue(sym, out var lastLog) || (now - lastLog).TotalSeconds >= 30)
                    {
                        _log.Information("[EXIT-CHECK] {Sym} qty={Qty} entry={Entry:F2} px={Px:F2}",
                            sym, pos.Quantity, pos.AveragePrice, q.Bid ?? q.Last ?? 0m);
                        _lastExitLogUtc[sym] = now;
                    }
                }
            }

            var entry = pos.AveragePrice;
            if (entry <= 0m)
            {
                _log.Warning("[EXIT-DEBUG] {Sym} entry={Entry} <= 0", sym, entry);
                return;
            }

            // -------- Reference prices za exit provere --------
            // NO-PRICE GUARD: Ako nema validne cene, preskoči evaluaciju (ne koristi 0m - može lažno trigerovati SL)
            // TP (sell limit): Bid je relevantan - ako nema Bid, nema TP evaluacije
            // SL (stop trigger): Last je relevantan - ako nema Last, nema SL evaluacije
            var hasBid = q.Bid.HasValue && q.Bid.Value > 0m;
            var hasLast = q.Last.HasValue && q.Last.Value > 0m;
            if (!hasBid && !hasLast)
                return; // Quote glitch - nema validne cene

            var tpRefPx = hasBid ? q.Bid!.Value : (decimal?)null;  // TP samo ako ima Bid
            var slRefPx = hasLast ? q.Last!.Value : (decimal?)null; // SL samo ako ima Last
            var bestPx = Math.Max(tpRefPx ?? 0m, slRefPx ?? 0m);

            lock (_sync)
            {
                if (_exitPending.Contains(sym))
                {
                    return;
                }
            }

            // -------- runtime state --------
            PositionRuntimeState? rt;
            lock (_sync)
            {
                _posRuntime.TryGetValue(sym, out rt);
            }

            if (rt is null)
            {
                // Determine regime and symbol baseline if not set (fallback)
                string? regime = null;
                string? symbolBaseline = null;
                decimal? atrAtEntry = null;

                lock (_sync)
                {
                    if (_atr.TryGetValue(sym, out var atrState) && atrState.Atr.HasValue && entry > 0m)
                    {
                        var rawAtr = atrState.Atr.Value;
                        var minAtrFrac = _limits.MinAtrFraction;
                        var minAtrAbs = entry * minAtrFrac;
                        var flooredAtr = Math.Max(rawAtr, minAtrAbs);
                        atrAtEntry = flooredAtr;

                        var atrFrac = flooredAtr / entry;
                        if (atrFrac > 0.0005m) regime = "HIGH";
                        else if (atrFrac > 0.00015m) regime = "NORMAL";
                        else regime = "LOW";

                        if (atrFrac < 0.0002m) symbolBaseline = "slow";
                        else if (atrFrac <= 0.0005m) symbolBaseline = "normal";
                        else symbolBaseline = "fast";
                    }
                }

                rt = new PositionRuntimeState
                {
                    EntryUtc = now,
                    EntryPrice = entry,
                    BestPrice = bestPx,
                    IsExternal = false,
                    RegimeAtEntry = regime ?? "LOW",
                    SymbolBaseline = symbolBaseline ?? "normal",
                    AtrAtEntry = atrAtEntry
                };
                lock (_sync) _posRuntime[sym] = rt;

                _log.Warning(
                    "[EXIT-RUNTIME-FALLBACK] {Sym} runtime state created in EvaluateCryptoExitsOnQuote entry={Entry:F2} qty={Qty} entryUtc={Utc} (should have been created in ApplyFillCore!)",
                    sym, entry, pos.Quantity, now);
            }
            else
            {
                if (bestPx > rt.BestPrice)
                    rt.BestPrice = bestPx;
            }

            // -------- 1) TIME EXIT --------
            var holding = now - rt.EntryUtc;
            if (holding >= _maxHoldTime)
            {
                var exitPx = slRefPx ?? tpRefPx ?? 0m; // Mora biti bar jedna cena (guard iznad)
                var pnl = (exitPx - entry) * pos.Quantity;
                var pnlPct = entry > 0m ? ((exitPx - entry) / entry) * 100m : 0m;

                foreach (var brokerId in activeExitBrokerOrderIds)
                {
                    _log.Information("[EXIT-TIME-CANCEL] Canceling exit order before TIME exit: brokerId={BrokerId} sym={Sym}", brokerId, sym);
                    FireAndForgetCancel(brokerId, sym);
                }

                SendExit(
                    sym,
                    pos.Quantity,
                    exitPx,
                    $"TIME-EXIT holding={holding.TotalMinutes:F1}min",
                    corrPrefix: "exit-time");
                return;
            }

            // -------- 2) MINIMUM HOLDING TIME (prevent immediate TP/SL) --------
            // Ne dozvoljavamo TP/SL proveru dok ne prođe minimum vreme (npr. 5 sekundi)
            // Ovo sprečava da se TP aktivira odmah nakon entry-ja zbog spread-a ili malog TP nivoa
            const int minHoldingSecondsForTpSl = 5;

            // DEBUG: Loguj TP/SL nivoe svakih 10 sekundi (koristimo istu promenljivu za obe provere)
            var timeSinceLastLog = now - (rt.LastTpSlLogUtc ?? DateTime.MinValue);

            if (holding.TotalSeconds < minHoldingSecondsForTpSl)
            {
                // DEBUG: Loguj kada se preskače zbog min holding time - samo ako je DebugLogging uključen za ovaj simbol
                if (debugLogging && timeSinceLastLog.TotalSeconds >= 10)
                {
                    _log.Information(
                        "[EXIT-SKIP] {Sym} skipping TP/SL check - holding={Hold:F1}s < {Min}s entry={Entry:F2} bid={Bid} last={Last}",
                        sym, holding.TotalSeconds, minHoldingSecondsForTpSl, entry, q.Bid, q.Last);
                    rt.LastTpSlLogUtc = now;
                }
                return; // Preskačemo TP/SL proveru dok ne prođe minimum vreme
            }

            // -------- 3) TP/SL --------
            var (tpPx, slPx, atrUsed) = ComputeTpSlLevels(sym, entry);

            // DEBUG: Loguj TP/SL nivoe svakih 10 sekundi - samo ako je DebugLogging uključen za ovaj simbol
            if (debugLogging && timeSinceLastLog.TotalSeconds >= 10)
            {
                _log.Information(
                    "[EXIT-DEBUG] {Sym} entry={Entry:F2} bid={Bid} last={Last} tp={TP:F2} sl={SL:F2} holding={Hold:F1}s atr={Atr} qty={Qty}",
                    sym, entry, q.Bid, q.Last, tpPx, slPx, holding.TotalSeconds, atrUsed ?? 0m, pos.Quantity);
                rt.LastTpSlLogUtc = now;
            }

            // TP/SL: na brokeru (native OCO); fill dolazi preko OnOrderUpdated. Ne šaljemo soft TP/SL iz quote-a.

            // -------- 4) TRAILING STOP --------
            // Trailing stop je stop exit - koristi Last (kao i SL)
            if (rt.BestPrice > entry)
            {
                var profitFromEntry = rt.BestPrice - entry;
                var profitFrac = entry > 0m ? profitFromEntry / entry : 0m;

                if (profitFrac >= _trailActivateFraction)
                {
                    var trailStop = rt.BestPrice * (1m - _trailDistanceFraction);

                    // TRAIL: samo ako ima Last (kao SL)
                    if (slRefPx.HasValue && slRefPx.Value <= trailStop)
                    {
                        _log.Information(
                            "[EXIT-TRAIL] {Sym} TRAIL hit! entry={Entry:F2} bid={Bid} last={Last} trailStop={Trail:F2} best={Best:F2}",
                            sym, entry, q.Bid, q.Last, trailStop, rt.BestPrice);

                        foreach (var brokerId in activeExitBrokerOrderIds)
                        {
                            _log.Information("[EXIT-TRAIL-CANCEL] Canceling exit order before TRAIL exit: brokerId={BrokerId} sym={Sym}", brokerId, sym);
                            FireAndForgetCancel(brokerId, sym);
                        }

                        SendExit(
                            sym,
                            pos.Quantity,
                            slRefPx!.Value,
                            $"TRAIL-EXIT trailStop={trailStop:F2} best={rt.BestPrice:F2} last={slRefPx:F2}",
                            corrPrefix: "exit-trail");
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[CRYPTO-EXIT] EvaluateCryptoExitsOnQuote failed");
        }
    }

    private void OnTradeSignal(TradeSignal signal)
    {
        var now = DateTime.UtcNow;
        var signalUtc = signal.TimestampUtc == default ? now : signal.TimestampUtc;
        var strategyName = _strategy.GetType().Name;

        void MarkBlocked(string shortReason)
        {
            try
            {
                StrategyMetrics.Instance.SignalBlocked(strategyName, signal.Symbol.Ticker, shortReason);
                StrategyMetrics.Instance.SignalBlockedByPhase(strategyName, signal.Symbol.Ticker, shortReason, signalUtc);
            }
            catch { }
        }

        var runEnv = _orderService is null ? "Paper" : "Real";
        var rthWindow = "n/a"; // Crypto trguje 24/7

        async Task LogSignalAsync(
            bool accepted,
            string? rejectReason,
            decimal? plannedQty = null,
            decimal? plannedNotional = null,
            string? corrId = null)
        {
            if (_signalRepo is null) return;
            try
            {
                await _signalRepo.InsertAsync(
                    utc: signalUtc,
                    symbol: signal.Symbol.Ticker,
                    side: signal.Side.ToString(),
                    suggestedPrice: signal.SuggestedLimitPrice,
                    strategy: strategyName,
                    reason: signal.Reason,
                    accepted: accepted,
                    rejectReason: rejectReason,
                    plannedQty: plannedQty,
                    plannedNotional: plannedNotional,
                    correlationId: corrId,
                    runEnv: runEnv,
                    rthWindow: rthWindow,
                    exchange: signal.Symbol.Exchange ?? _exchangeName,
                    ct: CancellationToken.None);
            }
            catch
            {
                try { AppMetrics.Instance.IncDbException(); } catch { }
            }
        }

        if (!signal.ShouldEnter)
        {
            var symbol = signal.Symbol.Ticker;
            var exitPosition = _positionBook.Get(symbol);
            if (exitPosition is null || exitPosition.Quantity <= 0m)
            {
                _log.Information("[STRATEGY-EXIT] Ignoring exit signal for {Sym} - no open position", symbol);
                MarkBlocked("no-position");
                _ = LogSignalAsync(false, "no-position");
                return;
            }

            lock (_sync)
            {
                if (_exitPending.Contains(symbol))
                {
                    MarkBlocked("exit-pending");
                    _ = LogSignalAsync(false, "exit-pending");
                    return;
                }
            }

            var exitPx = signal.SuggestedLimitPrice ?? 0m;
            if (exitPx <= 0m)
            {
                if (!TryGetQuote(symbol, now, out var exitQuote, out var exitReason))
                {
                    MarkBlocked("exit-liquidity");
                    _ = LogSignalAsync(false, $"exit-liquidity:{exitReason}");
                    return;
                }

                exitPx = exitQuote.Bid ?? exitQuote.Last ?? 0m;
            }

            if (exitPx <= 0m)
            {
                MarkBlocked("invalid-exit-price");
                _ = LogSignalAsync(false, "invalid-exit-price");
                return;
            }

            if (_orderService != null)
            {
                var activeExitBrokerOrderIds = _orders.Snapshot()
                    .Where(po =>
                        po.Req.Symbol.Ticker.Equals(symbol, StringComparison.OrdinalIgnoreCase) &&
                        po.Req.IsExit &&
                        !string.IsNullOrWhiteSpace(po.BrokerOrderId))
                    .Select(po => po.BrokerOrderId!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var brokerId in activeExitBrokerOrderIds)
                {
                    _log.Information("[STRATEGY-EXIT-CANCEL] Canceling active exit before strategy exit: brokerId={BrokerId} sym={Sym}",
                        brokerId, symbol);
                    FireAndForgetCancel(brokerId, symbol);
                }
            }

            SendExit(symbol, exitPosition.Quantity, exitPx, signal.Reason, corrPrefix: "exit-strategy");
            _ = LogSignalAsync(true, null, exitPosition.Quantity, exitPosition.Quantity * exitPx);
            return;
        }

        // 0) GLOBAL STRATEGY RATE-LIMIT
        if (IsRateLimited(now, out var rlReason))
        {
            _log.Warning("[RATE-LIMIT] blocked signal {Sym} reason={Reason}", signal.Symbol.Ticker, rlReason);
            MarkBlocked("rate-limit");
            _ = LogSignalAsync(false, $"rate-limit:{rlReason}");
            return;
        }

        // 1) HARD STOP – Equity Floor
        if (_equityFloorUsd > 0m)
        {
            var (equity0, inPosUsd) = GetApproxEquity();
            if (equity0 <= _equityFloorUsd)
            {
                _log.Warning("[EQ-FLOOR] {Sym} equity {Eq:F2} (inPos={InPos:F2}) <= floor {Floor:F2}",
                    signal.Symbol.Ticker, equity0, inPosUsd, _equityFloorUsd);
                MarkBlocked("equity-floor");
                _ = LogSignalAsync(false, "equity-floor");
                return;
            }
        }

        // 2) Cooldown posle gubitka
        if (IsInCooldown(signal.Symbol, now, out var cooldownLeft))
        {
            MarkBlocked("cooldown");
            _ = LogSignalAsync(false, $"cooldown:{(int)cooldownLeft.TotalSeconds}");
            return;
        }

        // 3) Mora da postoji "zdrav" quote (fresh + NBBO + spread ok)
        if (!TryGetQuote(signal.Symbol.Ticker, now, out var q, out var liqReason))
        {
            MarkBlocked("liquidity");
            _ = LogSignalAsync(false, $"liquidity:{liqReason}");
            return;
        }

        // 3.5) Green grind regime gate (DB-canonical for metric parity; DryRun supported)
        if (_greenGrindSettings is not null)
        {
            try
            {
                var ggTs = q.TimestampUtc == default ? now : q.TimestampUtc;
                var dbSnapshot = TryGetGreenGrindDbSnapshot(signal.Symbol.Ticker, ggTs, out var ggCfg);
                if (ggCfg is not null)
                {
                    var hardGate = ggCfg.Enabled && !ggCfg.DryRun;
                    var dryRun = ggCfg.DryRun;

                    if (dbSnapshot is null)
                    {
                        _log.Warning(
                            "[GREEN-GRIND] DB snapshot unavailable for {Sym} asOf={Ts:O} (hardGate={HardGate} dryRun={DryRun})",
                            signal.Symbol.Ticker,
                            ggTs,
                            hardGate,
                            dryRun);

                        if (hardGate)
                        {
                            MarkBlocked("green-grind-error");
                            _ = LogSignalAsync(false, "green-grind-error:db-unavailable");
                            return;
                        }
                    }
                    else if (!dbSnapshot.IsActive)
                    {
                        var suffix = string.IsNullOrWhiteSpace(dbSnapshot.InactiveReason) ? "inactive" : dbSnapshot.InactiveReason;
                        var rejectReason = $"green-grind-inactive:{suffix}";

                        if (dryRun)
                        {
                            _log.Information(
                                "[GREEN-GRIND-DRYRUN] wouldBlock {Sym} source=db reason={Reason} net3h={NetBps} up3h={UpRatio} eff3h={Eff} spike3h={Spike} ctx_pct={CtxPct} rows3h={Rows} span3h={Span} gap3h={Gap} trades3h={Trades} imb3h={Imb}",
                                signal.Symbol.Ticker,
                                suffix,
                                dbSnapshot.NetBps3h?.ToString("F1") ?? "n/a",
                                dbSnapshot.UpRatio3h?.ToString("F3") ?? "n/a",
                                dbSnapshot.Eff3h?.ToString("F3") ?? "n/a",
                                dbSnapshot.Spike3h?.ToString("F3") ?? "n/a",
                                dbSnapshot.CtxPct?.ToString("F4") ?? "n/a",
                                dbSnapshot.Rows3h,
                                dbSnapshot.Span3h?.ToString() ?? "n/a",
                                dbSnapshot.MaxGap3h?.ToString() ?? "n/a",
                                dbSnapshot.Trades3h,
                                dbSnapshot.Imb3h?.ToString("F3") ?? "n/a");
                        }
                        else if (hardGate)
                        {
                            _log.Information(
                                "[GREEN-GRIND] blocked {Sym} source=db reason={Reason} net3h={NetBps} up3h={UpRatio} eff3h={Eff} spike3h={Spike} ctx_pct={CtxPct} rows3h={Rows} span3h={Span} gap3h={Gap} trades3h={Trades} imb3h={Imb}",
                                signal.Symbol.Ticker,
                                suffix,
                                dbSnapshot.NetBps3h?.ToString("F1") ?? "n/a",
                                dbSnapshot.UpRatio3h?.ToString("F3") ?? "n/a",
                                dbSnapshot.Eff3h?.ToString("F3") ?? "n/a",
                                dbSnapshot.Spike3h?.ToString("F3") ?? "n/a",
                                dbSnapshot.CtxPct?.ToString("F4") ?? "n/a",
                                dbSnapshot.Rows3h,
                                dbSnapshot.Span3h?.ToString() ?? "n/a",
                                dbSnapshot.MaxGap3h?.ToString() ?? "n/a",
                                dbSnapshot.Trades3h,
                                dbSnapshot.Imb3h?.ToString("F3") ?? "n/a");
                            MarkBlocked("green-grind-inactive");
                            _ = LogSignalAsync(false, rejectReason);
                            return;
                        }
                    }
                    else if (dryRun)
                    {
                        _log.Information(
                            "[GREEN-GRIND-DRYRUN] wouldPass {Sym} source=db net3h={NetBps} up3h={UpRatio} eff3h={Eff} spike3h={Spike} ctx_pct={CtxPct} rows3h={Rows} span3h={Span} gap3h={Gap} trades3h={Trades} imb3h={Imb}",
                            signal.Symbol.Ticker,
                            dbSnapshot.NetBps3h?.ToString("F1") ?? "n/a",
                            dbSnapshot.UpRatio3h?.ToString("F3") ?? "n/a",
                            dbSnapshot.Eff3h?.ToString("F3") ?? "n/a",
                            dbSnapshot.Spike3h?.ToString("F3") ?? "n/a",
                            dbSnapshot.CtxPct?.ToString("F4") ?? "n/a",
                            dbSnapshot.Rows3h,
                            dbSnapshot.Span3h?.ToString() ?? "n/a",
                            dbSnapshot.MaxGap3h?.ToString() ?? "n/a",
                            dbSnapshot.Trades3h,
                            dbSnapshot.Imb3h?.ToString("F3") ?? "n/a");
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[GREEN-GRIND] Failed to evaluate DB-canonical regime gate for {Sym}", signal.Symbol.Ticker);
                var cfg = _greenGrindSettings.Resolve(signal.Symbol.Ticker);
                var hardGate = cfg.Enabled && !cfg.DryRun;
                if (hardGate)
                {
                    MarkBlocked("green-grind-error");
                    _ = LogSignalAsync(false, "green-grind-error");
                    return;
                }
            }
        }

        try
        {
            // =========================================================
            // 4) Crypto trguje 24/7 - NEMA RTH PROVERA
            // =========================================================

            // 4.5) Macro trend gate (logs into trade_signals.reject_reason)
            if (_trendContextProvider is not null)
            {
                try
                {
                    TrendContext? trend;
                    TrendContextDiagnosticsResult? trendDiagnostics = null;
                    var quoteTs = q.TimestampUtc == default ? now : q.TimestampUtc;

                    if (_trendContextProvider is ITrendContextDiagnosticsProvider diagnosticsProvider)
                    {
                        trendDiagnostics = diagnosticsProvider
                            .GetTrendContextDiagnosticsAsync(
                                exchange: signal.Symbol.Exchange ?? _exchangeName,
                                symbol: signal.Symbol.Ticker,
                                quoteTsUtc: quoteTs,
                                ct: CancellationToken.None)
                            .GetAwaiter()
                            .GetResult();
                        trend = trendDiagnostics.Context;
                    }
                    else
                    {
                        trend = _trendContextProvider
                            .GetTrendContextAsync(
                                exchange: signal.Symbol.Exchange ?? _exchangeName,
                                symbol: signal.Symbol.Ticker,
                                quoteTsUtc: quoteTs,
                                ct: CancellationToken.None)
                            .GetAwaiter()
                            .GetResult();
                    }

                    if (trend is null)
                    {
                        var rejectReason = "macro-trend-block:unknown:no-data";
                        if (trendDiagnostics is not null && !string.IsNullOrWhiteSpace(trendDiagnostics.NoContextReason))
                        {
                            var noContextReason = trendDiagnostics.NoContextReason.Trim().ToLowerInvariant();
                            rejectReason = noContextReason == "insufficient-points"
                                ? $"macro-trend-block:unknown:insufficient-points:have={trendDiagnostics.UsablePointCount}:min={trendDiagnostics.RequiredMinPoints}"
                                : $"macro-trend-block:unknown:{noContextReason}";
                        }

                        MarkBlocked("macro-trend-block");
                        _ = LogSignalAsync(false, rejectReason);
                        return;
                    }

                    if (trend.LocalQuality is { Enabled: true, DryRun: true } lq)
                    {
                        _log.Information(
                            "[TREND-LQ-DRYRUN] {Sym} src={Src} macro={Macro:F6} mag30={Mag:F6} chop15Path={ChopPath:F6} chop15Eff={Eff:F4} ratio={Ratio} wouldPass={WouldPass} reason={Reason}",
                            signal.Symbol.Ticker,
                            lq.Source,
                            trend.Score,
                            lq.MagnitudeNetMoveFraction,
                            lq.ChopPathFraction,
                            lq.ChopEfficiency,
                            lq.ChopToMagnitudeRatio?.ToString("F2") ?? "n/a",
                            lq.WouldPass,
                            lq.DecisionReason);
                    }

                    if (trend.Direction != TrendDirection.Up)
                    {
                        var trendDirection = trend.Direction.ToString().ToLowerInvariant();
                        MarkBlocked("macro-trend-block");
                        _ = LogSignalAsync(false, $"macro-trend-block:{trendDirection}:score={trend.Score:F6}");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[TREND] Failed to evaluate macro trend for {Sym}", signal.Symbol.Ticker);
                    MarkBlocked("macro-trend-block");
                    _ = LogSignalAsync(false, "macro-trend-block:error");
                    return;
                }
            }

            // =========================================================
            // 5) Prevent spam — već ima pending order na simbolu (osim ako MultipleOrders=true)
            // =========================================================
            var hasPending = HasPendingForSymbol(signal.Symbol.Ticker);
            if (hasPending)
            {
                if (_orderService is null)
                {
                    _log.Information("[PENDING-IGNORE] PAPER mode: pending-exists for {Sym}, ali NE blokiramo signal",
                        signal.Symbol.Ticker);
                }
                else if (_swingConfig.MultipleOrders)
                {
                    _log.Information("[PENDING-OK] MultipleOrders=true: dozvoljen novi signal za {Sym} (već ima pending)",
                        signal.Symbol.Ticker);
                }
                else
                {
                    MarkBlocked("pending-exists");
                    _ = LogSignalAsync(false, "pending-exists");
                    return;
                }
            }

            // 5.1) Block add-on entry: ako vec imamo otvorenu poziciju i aktivan exit nalog na istom simbolu
            var currentPos = _positionBook.Get(signal.Symbol.Ticker);
            var hasOpenPosition = currentPos is not null && currentPos.Quantity > 0m;
            var hasActiveExitForSymbol = _orders.Snapshot().Any(po =>
                po?.Req is not null &&
                po.Req.Symbol.Ticker.Equals(signal.Symbol.Ticker, StringComparison.OrdinalIgnoreCase) &&
                po.Req.IsExit &&
                (po.BrokerOrderId != null || (now - po.AtUtc).TotalMinutes < 10));

            if (hasOpenPosition && hasActiveExitForSymbol)
            {
                _log.Information("[BLOCK] {Sym} position-open-with-exit: qty={Qty:F8}", signal.Symbol.Ticker, currentPos!.Quantity);
                MarkBlocked("position-open-with-exit");
                _ = LogSignalAsync(false, "position-open-with-exit");
                return;
            }

            // =========================================================
            // 6) Risk engine (osnovni sizing)
            // =========================================================
            var cash = _lastCash;
            var budgetForSymbol = ResolvePerSymbolBudgetUsd(signal.Symbol.Ticker);
            var rc = _risk.EvaluateEntry(signal, cash, _limits, _fees, budgetForSymbol);

            if (!rc.Allowed || rc.Quantity <= 0m)
            {
                MarkBlocked("risk");
                _ = LogSignalAsync(false, $"risk:{rc.Reason}");
                return;
            }

            var px = signal.SuggestedLimitPrice ?? 0m;
            if (px <= 0m)
            {
                MarkBlocked("invalid-price");
                _ = LogSignalAsync(false, "invalid-price");
                return;
            }

            // 6.1) Day-guards: max trades / daily loss lock
            if (_dayGuards is not null)
            {
                if (!_dayGuards.CanTrade(signal.Symbol, now, out var dgReason))
                {
                    _log.Warning("[DAY-GUARD] Blocked entry for {Sym}. Reason={Reason}",
                        signal.Symbol.Ticker, dgReason ?? "n/a");
                    MarkBlocked("day-guard");
                    _ = LogSignalAsync(false, $"day-guard:{dgReason}");
                    return;
                }
            }

            // =========================================================
            // 6.2) ATR-based risk sizing – adaptivno ograničavanje qty
            // =========================================================
            var correlationId = $"sig-{signal.Symbol.Ticker}-{Guid.NewGuid():N}";

            decimal? atrOpt = null;
            lock (_sync)
            {
                if (_atr.TryGetValue(signal.Symbol.Ticker, out var s) && s.Atr.HasValue && s.Atr.Value > 0m)
                    atrOpt = s.Atr.Value;
            }

            decimal riskFraction = _limits.MaxRiskPerTradeFraction;

            if (IsSwingMode() && _swingConfig.MaxSingleTradeRiskPct > 0m)
            {
                if (riskFraction > 0m)
                    riskFraction = Math.Min(riskFraction, _swingConfig.MaxSingleTradeRiskPct);
                else
                    riskFraction = _swingConfig.MaxSingleTradeRiskPct;
            }

            if (atrOpt.HasValue && riskFraction > 0m)
            {
                var slAtrMultiple = SlAtrMultiple;
                var atr = atrOpt.Value;
                var equityAtr = cash.Free + cash.Settling + cash.InPositions;
                var riskPerTradeUsd = equityAtr * riskFraction;
                var pctSlDistance = px * _slFraction;
                var atrSlDistance = slAtrMultiple * atr;
                var priceRisk = Math.Max(pctSlDistance, atrSlDistance);

                if (priceRisk > 0m && riskPerTradeUsd > 0m)
                {
                    var qtyByRisk = riskPerTradeUsd / priceRisk;
                    var adjQty = Math.Min(rc.Quantity, qtyByRisk);

                    if (adjQty <= 0m)
                    {
                        _log.Information(
                            "[RISK-SIZE] Blocked {Sym} qtyByRisk<=0 (eq={Eq:F2}, riskPerTrade={Risk:F2}, priceRisk={PR:F4}, frac={Frac:P2})",
                            signal.Symbol.Ticker, equityAtr, riskPerTradeUsd, priceRisk, riskFraction);
                        MarkBlocked("atr-risk");
                        _ = LogSignalAsync(false, "atr-risk-sizer-zero");
                        return;
                    }

                    _log.Information(
                        "[RISK-SIZE] {Sym} qty adj old={Old:F6} new={New:F6} atr={Atr:F4} px={Px:F2} riskPerTrade={Risk:F2} priceRisk={PR:F4} frac={Frac:P2}",
                        signal.Symbol.Ticker, rc.Quantity, adjQty, atr, px, riskPerTradeUsd, priceRisk, riskFraction);

                    lock (_sync)
                    {
                        _sizingMetadata[correlationId] = (riskFraction, atr, priceRisk);
                    }

                    rc = rc with { Quantity = adjQty };
                }
                else
                {
                    lock (_sync)
                    {
                        _sizingMetadata[correlationId] = (riskFraction, null, null);
                    }
                }
            }
            else
            {
                if (riskFraction > 0m)
                {
                    lock (_sync)
                    {
                        _sizingMetadata[correlationId] = (riskFraction, null, null);
                    }
                }
            }

            // =========================================================
            // 7) Symbol exposure guard — MaxExposurePerSymbolFrac
            // =========================================================
            var existingPos = _positionBook.Get(signal.Symbol.Ticker);
            var equity = cash.Free + cash.Settling + cash.InPositions;
            if (equity <= 0m)
            {
                MarkBlocked("no-equity");
                _ = LogSignalAsync(false, "no-equity");
                return;
            }

            var maxSymbolExposure = equity * _limits.MaxExposurePerSymbolFrac;
            var existingNotional = existingPos is null ? 0m : existingPos.Quantity * px;
            var plannedNotional = rc.Quantity * px;

            if (existingNotional >= maxSymbolExposure)
            {
                _log.Information("[BLOCK] {Sym} symbol exposure full. existing={Existing:F2} cap={Cap:F2}",
                    signal.Symbol.Ticker, existingNotional, maxSymbolExposure);
                MarkBlocked("symbol-exposure-full");
                _ = LogSignalAsync(false, "symbol-exposure-full");
                return;
            }

            if (existingNotional + plannedNotional > maxSymbolExposure)
            {
                var remaining = maxSymbolExposure - existingNotional;
                var cappedQty = remaining / px;

                if (cappedQty <= 0m)
                {
                    _log.Information("[BLOCK] {Sym} exposure cap reached. existing={Existing:F2} cap={Cap:F2}",
                        signal.Symbol.Ticker, existingNotional, maxSymbolExposure);
                    MarkBlocked("symbol-exposure-cap");
                    _ = LogSignalAsync(false, "symbol-exposure-cap");
                    return;
                }

                _log.Information("[ADJUST] {Sym} qty capped by symbol exposure. oldQty={OldQty:F6} newQty={NewQty:F6}",
                    signal.Symbol.Ticker, rc.Quantity, cappedQty);
                rc = rc with { Quantity = cappedQty };
            }

            // =========================================================
            // 8) Normalize quantity
            // =========================================================
            var rawQty = rc.Quantity;
            var qty = NormalizeQty(signal.Symbol, rawQty);

            if (qty <= 0)
            {
                if (_orderService is null)
                {
                    // PAPER fallback: minimalna količina za crypto (0.001)
                    qty = 0.001m;
                    _log.Information("[NORM-FALLBACK] PAPER mode: overriding normalized-zero for {Sym}, rawQty={Raw:F6} -> qty={Qty:F6}",
                        signal.Symbol.Ticker, rawQty, qty);
                }

                if (qty <= 0)
                {
                    MarkBlocked("normalized-zero");
                    _ = LogSignalAsync(false, "normalized-zero");
                    return;
                }
            }
            rc = rc with { Quantity = qty };

            // =========================================================
            // 9) Real cash check + min free cash floor
            // =========================================================
            var notional = qty * px;
            var fee = CalculateFeeUsd(notional, isMaker: true); // Limit order = Maker
            var totalCost = notional + fee;

            if (_orderService != null)
            {
                if (cash.Free < totalCost)
                {
                    MarkBlocked("not-enough-cash");
                    _ = LogSignalAsync(false, "not-enough-cash");
                    return;
                }

                if (_minFreeCashUsd > 0m)
                {
                    var freeAfter = cash.Free - totalCost;
                    if (freeAfter < _minFreeCashUsd)
                    {
                        _log.Warning("[CASH-FLOOR] {Sym} freeAfter={FreeAfter:F2} < floor={Floor:F2} freeBefore={Free:F2} cost={Cost:F2}",
                            signal.Symbol.Ticker, freeAfter, _minFreeCashUsd, cash.Free, totalCost);
                        MarkBlocked("cash-floor");
                        _ = LogSignalAsync(false, "cash-floor");
                        return;
                    }
                }
                SafeReserve(totalCost, now, "entry");
            }

            // =========================================================
            // 10) Create + place order
            // =========================================================
            var corr = correlationId;

            try
            {
                var sym = signal.Symbol.Ticker;
                if (signal.Side == OrderSide.Buy)
                    OrderMetrics.Instance.SignalBuy(sym);
                else if (signal.Side == OrderSide.Sell)
                    OrderMetrics.Instance.SignalSell(sym);
            }
            catch { }

            // 10.1) Upis u broker_orders (ENTRY nalog)
            if (_orderRepo is not null)
            {
                try
                {
                    decimal? bidSnap = q.Bid;
                    decimal? askSnap = q.Ask;
                    decimal? spreadSnap = (bidSnap.HasValue && askSnap.HasValue) ? askSnap - bidSnap : null;

                    _ = _orderRepo.InsertSubmittedAsync(
                       id: corr,
                       symbol: signal.Symbol.Ticker,
                       side: signal.Side.ToString(),
                       qty: qty,
                       orderType: "limit",
                       limitPrice: px > 0 ? px : null,
                       stopPrice: null,
                       createdUtc: now,
                       submitBid: bidSnap,
                       submitAsk: askSnap,
                       submitSpread: spreadSnap,
                       exchange: signal.Symbol.Exchange ?? _exchangeName,
                       ct: CancellationToken.None
                   );
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[DB-ORDERS] InsertSubmitted failed corr={Corr}", corr);
                }
            }

            var req = new OrderRequest(
                symbol: signal.Symbol,
                side: signal.Side,
                type: OrderType.Limit,
                quantity: qty,
                limitPrice: px,
                tif: TimeInForce.Day,
                correlationId: corr,
                timestampUtc: now
            );

            _dayGuards?.OnOrderPlaced(signal.Symbol, now);
            _orders.TryAdd(new PendingOrder(req, totalCost, now, BrokerOrderId: null));
            _ = LogSignalAsync(true, null, qty, qty * px, corr);

            if (_orderService == null)
            {
                _paperSim.Register(req);
            }
            else
            {
                _ = PlaceRealAsync(req);
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[CRYPTO-ERROR] OnTradeSignal failed");
            _ = LogSignalAsync(false, "exception");
            try { MarkBlocked("exception"); } catch { }
        }
    }

    private void OnPaperFilled(OrderRequest req, decimal fillPx)
    {
        var now = DateTime.UtcNow;
        // standardni put
        ApplyFillCore(req, fillPx, now, isPaper: true);

        // Ukloni order iz pending-a i ažuriraj status u bazi
        if (_orders.TryRemove(req.CorrelationId ?? string.Empty, out var removed) && removed != null)
        {
            if (removed.ReservedUsd > 0m)
                _cashService.Unreserve(removed.ReservedUsd);

            if (removed.Req.CorrelationId?.StartsWith("exit-", StringComparison.OrdinalIgnoreCase) == true)
            {
                lock (_sync) _exitPending.Remove(removed.Req.Symbol.Ticker);
            }

            _log.Information("[PAPER-FILL] Order filled corr={Corr} sym={Sym} qty={Qty} px={Px}",
                removed.CorrelationId, removed.Req.Symbol.Ticker, removed.Req.Quantity, fillPx);

            // Ažuriraj order status u bazi na "filled"
            if (_orderRepo != null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var msg = $"paper-fill qty={removed.Req.Quantity:F6} px={fillPx:F4}";
                        await _orderRepo.UpdateStatusAsync(
                            id: removed.CorrelationId,
                            status: "filled",
                            lastMsg: msg,
                            forCrypto: true,
                            ct: CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(ex, "[DB-ORDERS] UpdateStatus(filled) failed for paper order corr={Corr}", removed.CorrelationId);
                    }
                });
            }

            ClearCancelRequested(removed.CorrelationId);
        }

        // i "isti" fee kao u realu – da paper bude što sličniji
        var notional = req.Quantity * fillPx;
        var estFee = CalculateFeeUsd(notional, isMaker: true); // Limit order = Maker
        if (estFee > 0m)
        {
            _cashService.OnCommissionPaid(estFee);
            _log.Information("[PAPER-FEE] {Sym} estFee={Fee:F2}", req.Symbol.Ticker, estFee);

            // (test) upiši fee u daily_pnl i u paper modu
            if (_pnlRepo is not null)
            {
                try
                {
                    _ = _pnlRepo.AddFeeAsync(now, estFee, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[DB-PNL] add fee (paper) failed");
                }
            }
        }
    }

    private void OnOrderUpdated(OrderResult res)
    {
        if (res is null) return;

        var now = DateTime.UtcNow;

        try
        {
            var status = res.Status ?? string.Empty;
            var brokerId = res.BrokerOrderId;

            // 0) DUPLICATE TERMINAL SPAM GUARD
            var terminalTtl = TimeSpan.FromHours(24);
            if (IsTerminalDuplicate(brokerId, now, terminalTtl))
                return;

            // Jedan lookup pending-a po brokerId
            PendingOrder? poByBid = null;
            if (!string.IsNullOrWhiteSpace(brokerId))
            {
                poByBid = _orders.Snapshot().FirstOrDefault(x => x.BrokerOrderId == brokerId);
            }

            // 1) COMMISSION EVENT
            if (res.CommissionAndFees is { } fee && fee > 0m)
            {
                _cashService.OnCommissionPaid(fee);
                _dayGuards?.OnRealizedPnl(-fee, now);

                _log.Information("[COMMISSION] fee={Fee:F2} brokerId={Bid} msg={Msg}",
                    fee, brokerId, res.Message ?? "n/a");

                if (_pnlRepo != null)
                {
                    try { _ = _pnlRepo.AddFeeAsync(now, fee, CancellationToken.None); }
                    catch (Exception ex) { _log.Warning(ex, "[DB-PNL] AddFee failed"); }
                }

                try
                {
                    if (!string.IsNullOrWhiteSpace(brokerId))
                    {
                        foreach (var po in _orders.Snapshot())
                        {
                            if (po.BrokerOrderId == brokerId)
                            {
                                _orders.TrySetFee(po.CorrelationId, fee, execId: null);
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[ORD-UPD] tagging fee failed");
                }

                if (string.Equals(status, "Commission", StringComparison.OrdinalIgnoreCase))
                    return;
            }

            // 1.5) FILL-LIKE STATUS WITHOUT PENDING
            var isFillStatus =
                status.Equals("Filled", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("PartiallyFilled", StringComparison.OrdinalIgnoreCase) ||
                status.IndexOf("partial", StringComparison.OrdinalIgnoreCase) >= 0;

            if (isFillStatus && poByBid is null)
            {
                if (res.FilledQuantity > 0)
                {
                    _log.Debug("[ORD-UPD] fill-status without pending status={Status} brokerId={Bid} cum={Cum} avg={Avg}",
                        status, brokerId, res.FilledQuantity, res.AverageFillPrice);
                }
            }

            // 1.7) PARTIAL FILL
            bool isPartial =
                status.Equals("PartiallyFilled", StringComparison.OrdinalIgnoreCase) ||
                status.IndexOf("partial", StringComparison.OrdinalIgnoreCase) >= 0;

            if (isPartial)
            {
                if (poByBid == null)
                {
                    _log.Debug("[ORD-UPD] PARTIAL without pending brokerId={Bid} cum={Cum} avg={Px}",
                        brokerId, res.FilledQuantity, res.AverageFillPrice);
                    return;
                }

                const decimal QtyEps = 0.0000001m;
                if (res.FilledQuantity >= poByBid.Req.Quantity - QtyEps)
                {
                    status = "Filled"; // tretiraj kao FILLED
                }
                else
                {
                    if (_orderRepo != null)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var msg = $"cum={res.FilledQuantity:F6} avg={res.AverageFillPrice:F4}";
                                await _orderRepo.UpdateStatusAsync(
                                    id: poByBid.CorrelationId,
                                    status: "partially_filled",
                                    lastMsg: msg,
                                    forCrypto: true,
                                    ct: CancellationToken.None);
                            }
                            catch (Exception ex)
                            {
                                _log.Warning(ex, "[DB-ORDERS] update partial failed");
                            }
                        });
                    }

                    _log.Information("[ORD-UPD] PARTIAL brokerId={Bid} cum={Cum} avg={Px}",
                        brokerId, res.FilledQuantity, res.AverageFillPrice);
                    return;
                }
            }

            // 2) CANCELED
            if (status.Equals("Canceled", StringComparison.OrdinalIgnoreCase))
            {
                if (poByBid == null)
                {
                    MarkTerminal(brokerId, now);
                    ClearCancelRateLimit(brokerId);
                    _log.Debug("[ORD-UPD] CANCELED without pending brokerId={Bid}", brokerId);
                    return;
                }

                if (_orders.TryRemove(poByBid.CorrelationId, out var removed) && removed != null)
                {
                    if (removed.ReservedUsd > 0m)
                        _cashService.Unreserve(removed.ReservedUsd);

                    if (removed.Req.CorrelationId?.StartsWith("exit-", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        lock (_sync) _exitPending.Remove(removed.Req.Symbol.Ticker);
                    }

                    ClearCumFilled(removed.CorrelationId);
                    ClearCancelRequested(removed.CorrelationId);

                    _log.Information("[ORD-UPD] CANCELED brokerId={Bid} corr={Corr}",
                        brokerId, removed.CorrelationId);

                    if (_orderRepo != null)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _orderRepo.MarkCanceledAsync(
                                    id: removed.CorrelationId,
                                    canceledUtc: now,
                                    ct: CancellationToken.None);
                            }
                            catch (Exception ex)
                            {
                                _log.Warning(ex, "[DB-ORDERS] MarkCanceled failed corr={Corr}", removed.CorrelationId);
                            }
                        });
                    }
                }

                MarkTerminal(brokerId, now);
                ClearCancelRateLimit(brokerId);
                return;
            }

            // 2b) REJECTED
            if (status.Equals("Rejected", StringComparison.OrdinalIgnoreCase))
            {
                if (poByBid == null)
                {
                    MarkTerminal(brokerId, now);
                    ClearCancelRateLimit(brokerId);
                    _log.Debug("[ORD-UPD] REJECTED without pending brokerId={Bid} msg={Msg}", brokerId, res.Message ?? "n/a");
                    return;
                }

                if (_orders.TryRemove(poByBid.CorrelationId, out var removed) && removed != null)
                {
                    if (removed.ReservedUsd > 0m)
                        _cashService.Unreserve(removed.ReservedUsd);

                    if (removed.Req.CorrelationId?.StartsWith("exit-", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        lock (_sync) _exitPending.Remove(removed.Req.Symbol.Ticker);
                    }

                    ClearCumFilled(removed.CorrelationId);
                    ClearCancelRequested(removed.CorrelationId);

                    if (_orderRepo != null)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _orderRepo.UpdateStatusAsync(
                                    id: removed.CorrelationId,
                                    status: "rejected",
                                    lastMsg: res.Message ?? "rejected",
                                    forCrypto: true,
                                    ct: CancellationToken.None);
                            }
                            catch (Exception ex)
                            {
                                _log.Warning(ex, "[DB-ORDERS] UpdateStatus(rejected) failed corr={Corr}", removed.CorrelationId);
                            }
                        });
                    }

                    _log.Information("[ORD-UPD] REJECTED brokerId={Bid} corr={Corr}",
                        brokerId, removed.CorrelationId);

                    try { OrderMetrics.Instance.Reject(removed.Req.Symbol.Ticker); }
                    catch { }
                }

                MarkTerminal(brokerId, now);
                ClearCancelRateLimit(brokerId);
                return;
            }

            // 2c) FINAL FILLED
            if (status.Equals("Filled", StringComparison.OrdinalIgnoreCase))
            {
                var matched = poByBid;

                if (matched != null)
                {
                    _log.Information("[ORD-UPD] FILLED status received: brokerId={Bid} corr={Corr} filledQty={Filled} avgPx={Px}",
                        brokerId, matched.CorrelationId, res.FilledQuantity, res.AverageFillPrice);

                    // FALLBACK: ako ExecutionDetails nisu isporučili poslednji slice
                    if (res.FilledQuantity > 0)
                    {
                        var corrId = matched.CorrelationId;

                        decimal prevCum;
                        lock (_sync) { _cumFilledByCorrId.TryGetValue(corrId, out prevCum); }

                        var finalCum = (decimal)res.FilledQuantity;
                        var missingQty = finalCum - prevCum;

                        _log.Information("[ORD-UPD] Fill calculation: corr={Corr} finalCum={FinalCum} prevCum={PrevCum} missingQty={Missing}",
                            corrId, finalCum, prevCum, missingQty);

                        if (missingQty > 0m)
                        {
                            var fillPx = res.AverageFillPrice ?? matched.Req.LimitPrice ?? 0m;

                            if (fillPx > 0m)
                            {
                                var sliceReq = new OrderRequest(
                                    symbol: matched.Req.Symbol,
                                    side: matched.Req.Side,
                                    type: matched.Req.Type,
                                    quantity: missingQty,
                                    limitPrice: matched.Req.LimitPrice,
                                    tif: matched.Req.Tif,
                                    correlationId: matched.Req.CorrelationId,
                                    timestampUtc: matched.Req.TimestampUtc,
                                    ocoGroupId: matched.Req.OcoGroupId,
                                    ocoStopPrice: matched.Req.OcoStopPrice,
                                    stopPrice: matched.Req.StopPrice,
                                    isExit: matched.Req.IsExit
                                );

                                _log.Information("[ORD-UPD] Calling ApplyFillCore: corr={Corr} qty={Qty} px={Px} side={Side} isExit={Exit}",
                                    corrId, missingQty, fillPx, matched.Req.Side, matched.Req.IsExit);

                                ApplyFillCore(sliceReq, fillPx, now, isPaper: false);

                                _log.Information(
                                    "[ORD-UPD-FALLBACK] Applied missing slice corr={Corr} missingQty={Qty:F6} finalCum={FinalCum:F6} prevCum={PrevCum:F6} px={Px:F4}",
                                    corrId, missingQty, finalCum, prevCum, fillPx);
                            }
                            else
                            {
                                _log.Warning(
                                    "[ORD-UPD-FALLBACK] Cannot apply missing slice corr={Corr} missingQty={Qty:F6} finalCum={FinalCum:F6} prevCum={PrevCum:F6} (no price)",
                                    corrId, missingQty, finalCum, prevCum);
                            }
                        }
                        else
                        {
                            _log.Warning("[ORD-UPD] missingQty <= 0, skipping ApplyFillCore: corr={Corr} finalCum={FinalCum} prevCum={PrevCum}",
                                corrId, finalCum, prevCum);
                        }
                    }
                    else
                    {
                        _log.Warning("[ORD-UPD] FilledQuantity <= 0, skipping fill processing: brokerId={Bid} corr={Corr}",
                            brokerId, matched.CorrelationId);
                    }

                    if (_orders.TryRemove(matched.CorrelationId, out var filledRemoved) && filledRemoved != null)
                    {
                        if (filledRemoved.ReservedUsd > 0m)
                            _cashService.Unreserve(filledRemoved.ReservedUsd);

                        if (filledRemoved.Req.CorrelationId?.StartsWith("exit-", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            lock (_sync) _exitPending.Remove(filledRemoved.Req.Symbol.Ticker);
                        }

                        _log.Information("[ORD-UPD] FINAL FILLED brokerId={Bid} corr={Corr}",
                            brokerId, filledRemoved.CorrelationId);

                        if (_orderRepo != null)
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    var msg = $"cum={res.FilledQuantity:F6} avg={res.AverageFillPrice:F4}";
                                    await _orderRepo.UpdateStatusAsync(
                                        id: filledRemoved.CorrelationId,
                                        status: "filled",
                                        lastMsg: msg,
                                        forCrypto: true,
                                        ct: CancellationToken.None);
                                }
                                catch (Exception ex)
                                {
                                    _log.Warning(ex, "[DB-ORDERS] UpdateStatus(filled) failed corr={Corr}", filledRemoved.CorrelationId);
                                }
                            });
                        }

                        ClearCancelRequested(filledRemoved.CorrelationId);
                        ClearCumFilled(filledRemoved.CorrelationId);

                        // ========== AUTO-CANCEL OCO GROUP ==========
                        // Ako je ovaj nalog deo OCO grupe, otkaži sve druge naloge iz iste grupe
                        if (!string.IsNullOrWhiteSpace(filledRemoved.Req.OcoGroupId) && _orderService != null)
                        {
                            var ocoGroupId = filledRemoved.Req.OcoGroupId;
                            var otherOrders = _orders.Snapshot()
                                .Where(po => po.Req.OcoGroupId == ocoGroupId &&
                                            po.CorrelationId != filledRemoved.CorrelationId &&
                                            !string.IsNullOrWhiteSpace(po.BrokerOrderId))
                                .ToList();

                            foreach (var otherOrder in otherOrders)
                            {
                                try
                                {
                                    _log.Information("[OCO-CANCEL] Canceling other OCO order corr={Corr} brokerId={Bid} group={Group}",
                                        otherOrder.CorrelationId, otherOrder.BrokerOrderId, ocoGroupId);

                                    _ = _orderService.CancelAsync(otherOrder.BrokerOrderId!);

                                    // Update status u bazi
                                    if (_orderRepo != null)
                                    {
                                        _ = Task.Run(async () =>
                                        {
                                            try
                                            {
                                                await _orderRepo.UpdateStatusAsync(
                                                    id: otherOrder.CorrelationId,
                                                    status: "canceled",
                                                    lastMsg: $"OCO-canceled: other order in group {ocoGroupId} filled",
                                                    forCrypto: true,
                                                    ct: CancellationToken.None);
                                            }
                                            catch (Exception ex)
                                            {
                                                _log.Warning(ex, "[DB-ORDERS] UpdateStatus(OCO-canceled) failed corr={Corr}", otherOrder.CorrelationId);
                                            }
                                        });
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _log.Warning(ex, "[OCO-CANCEL] Failed to cancel OCO order corr={Corr} brokerId={Bid}",
                                        otherOrder.CorrelationId, otherOrder.BrokerOrderId);
                                }
                            }
                        }
                    }

                    MarkTerminal(brokerId, now);
                }
                else
                {
                    MarkTerminal(brokerId, now);
                    _log.Debug("[ORD-UPD] FILLED without pending brokerId={Bid} cum={Cum} avg={Avg}",
                        brokerId, res.FilledQuantity, res.AverageFillPrice);
                }

                ClearCancelRateLimit(brokerId);
                return;
            }

            // 3) Ostali statusi — samo log
            _log.Information("[ORD-UPD] {Status} brokerId={Bid} filled={Filled} px={Px}",
                status, brokerId, res.FilledQuantity, res.AverageFillPrice);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[ORD-UPD] failed to process broker event");
        }
    }

    // Helper metode za OnOrderUpdated
    private bool IsTerminalDuplicate(string? brokerId, DateTime nowUtc, TimeSpan ttl)
    {
        if (string.IsNullOrWhiteSpace(brokerId))
            return false;

        if (_terminalByBrokerId.TryGetValue(brokerId, out var at))
        {
            if (nowUtc - at <= ttl)
                return true;

            _terminalByBrokerId.TryRemove(brokerId, out _);
        }

        return false;
    }

    private void MarkTerminal(string? brokerId, DateTime nowUtc)
    {
        if (!string.IsNullOrWhiteSpace(brokerId))
            _terminalByBrokerId[brokerId] = nowUtc;
    }

    private void ClearCumFilled(string corrId)
    {
        if (string.IsNullOrWhiteSpace(corrId)) return;
        lock (_sync)
        {
            _cumFilledByCorrId.Remove(corrId);
        }
    }

    private void ClearCancelRequested(string corrId)
    {
        lock (_sync)
        {
            _cancelRequestedUtc.Remove(corrId);
        }
    }

    private void ClearCancelRateLimit(string brokerOrderId)
    {
        if (string.IsNullOrWhiteSpace(brokerOrderId))
            return;

        lock (_sync)
        {
            _lastCancelUtcByBrokerId.Remove(brokerOrderId);
        }
    }

    private bool HasPendingByCorrelation(string correlationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId)) return false;
        var pendings = _orders.Snapshot();
        foreach (var p in pendings)
        {
            if (p?.Req is null) continue;
            if (string.Equals(p.Req.CorrelationId, correlationId, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private void ApplyFillCore(OrderRequest req, decimal fillPx, DateTime utcNow, bool isPaper)
    {
        var isSyncFill = req.CorrelationId?.StartsWith("sync-", StringComparison.OrdinalIgnoreCase) ?? false;
        _log.Information("[APPLY-FILL] Starting ApplyFillCore: corr={Corr} sym={Sym} side={Side} qty={Qty} px={Px} isExit={Exit} isPaper={Paper} isSync={Sync}",
            req.CorrelationId, req.Symbol.Ticker, req.Side, req.Quantity, fillPx, req.IsExit, isPaper, isSyncFill);

        // stvarni promet (za ovaj slice)
        var notional = req.Quantity * fillPx;

        // pre fill-a: zapamti stanje pozicije
        var prevPos = _positionBook.Get(req.Symbol.Ticker);
        var prevQty = prevPos?.Quantity ?? 0m;

        // 1) pozicije + cash → ovde dobijamo realizedPnl
        decimal realizedPnl;

        if (req.Side == OrderSide.Buy)
        {
            // DEBUG: Proveri stanje pozicije pre BUY fill-a
            var posBeforeBuy = _positionBook.Get(req.Symbol.Ticker);
            var avgPriceBeforeBuy = posBeforeBuy?.AveragePrice ?? 0m;
            var qtyBeforeBuy = posBeforeBuy?.Quantity ?? 0m;

            realizedPnl = _positionBook.ApplyBuyFillCrypto(req.Symbol.Ticker, req.Quantity, fillPx);

            // DEBUG: Proveri stanje pozicije posle BUY fill-a
            var posAfterBuy = _positionBook.Get(req.Symbol.Ticker);
            var avgPriceAfterBuy = posAfterBuy?.AveragePrice ?? 0m;
            var qtyAfterBuy = posAfterBuy?.Quantity ?? 0m;

            _log.Information(
                "[DEBUG-PNL-BUY] {Sym} BUY qty={Qty} @ {Px} avgBefore={AvgB} qtyBefore={QtyB} avgAfter={AvgA} qtyAfter={QtyA} notional={Not}",
                req.Symbol.Ticker, req.Quantity, fillPx, avgPriceBeforeBuy, qtyBeforeBuy, avgPriceAfterBuy, qtyAfterBuy, notional);

            _cashService.OnBuyFilled(notional, utcNow);

            // Discord notification - preskoči za sync fill-ove (pozicije koje se sinhronizuju sa berze)
            if (!isSyncFill && _discordNotifier != null)
            {
                _log.Information("[DISCORD] Sending BUY notification for {Symbol} on {Exchange}", req.Symbol.Ticker, req.Symbol.Exchange ?? _exchangeName);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _discordNotifier.NotifyBuyAsync(
                            symbol: req.Symbol.Ticker,
                            quantity: req.Quantity,
                            price: fillPx,
                            notional: notional,
                            exchange: req.Symbol.Exchange ?? _exchangeName,
                            isPaper: isPaper,
                            ct: CancellationToken.None);
                        _log.Information("[DISCORD] BUY notification sent successfully for {Symbol}", req.Symbol.Ticker);
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(ex, "[DISCORD] Failed to send BUY notification for {Symbol}", req.Symbol.Ticker);
                    }
                });
            }
            else if (isSyncFill)
            {
                _log.Debug("[DISCORD] Skipping BUY notification for sync fill: {Symbol} corr={Corr}", req.Symbol.Ticker, req.CorrelationId);
            }
            else
            {
                _log.Warning("[DISCORD] Discord notifier is NULL - BUY notification not sent for {Symbol}", req.Symbol.Ticker);
            }

            // posle BUY fill-a: ako smo upravo otvorili novu poziciju (pre je bilo 0, sada > 0)
            var newPos = _positionBook.Get(req.Symbol.Ticker);
            var newQty = newPos?.Quantity ?? 0m;
            var newAvgPrice = newPos?.AveragePrice ?? 0m;

            if (prevQty <= 0m && newQty > 0m)
            {
                // Determine regime and symbol baseline at entry (frozen snapshot)
                string? regime = null;
                string? symbolBaseline = null;
                decimal? atrAtEntry = null;

                lock (_sync)
                {
                    if (_atr.TryGetValue(req.Symbol.Ticker, out var atrState) && atrState.Atr.HasValue && fillPx > 0m)
                    {
                        var rawAtr = atrState.Atr.Value;
                        var minAtrFrac = _limits.MinAtrFraction;
                        var minAtrAbs = fillPx * minAtrFrac;
                        var flooredAtr = Math.Max(rawAtr, minAtrAbs);
                        atrAtEntry = flooredAtr;

                        // Determine regime based on ATR fraction
                        var atrFrac = flooredAtr / fillPx;
                        if (atrFrac > 0.0005m) regime = "HIGH";
                        else if (atrFrac > 0.00015m) regime = "NORMAL";
                        else regime = "LOW";

                        // Determine symbol baseline (simple heuristic: based on ATR fraction)
                        if (atrFrac < 0.0002m) symbolBaseline = "slow";
                        else if (atrFrac <= 0.0005m) symbolBaseline = "normal";
                        else symbolBaseline = "fast";
                    }

                    _posRuntime[req.Symbol.Ticker] = new PositionRuntimeState
                    {
                        EntryUtc = utcNow,
                        EntryPrice = fillPx,
                        BestPrice = fillPx,
                        IsExternal = false,
                        RegimeAtEntry = regime ?? "LOW",
                        SymbolBaseline = symbolBaseline ?? "normal",
                        AtrAtEntry = atrAtEntry
                    };

                    _log.Information(
                        "[EXIT-RUNTIME] {Sym} runtime state created entry={Entry:F2} qty={Qty} regime={Regime} atr={Atr}",
                        req.Symbol.Ticker, fillPx, newQty, regime ?? "LOW", atrAtEntry ?? 0m);
                }
            }
            else if (prevQty > 0m && newQty > prevQty)
            {
                // Pozicija se povećala - ažuriraj EntryPrice na novi weighted average, ali zadrži originalni EntryUtc
                lock (_sync)
                {
                    if (_posRuntime.TryGetValue(req.Symbol.Ticker, out var existingRt))
                    {
                        // Ažuriraj samo EntryPrice, zadrži originalni EntryUtc
                        existingRt.EntryPrice = newAvgPrice;
                        if (fillPx > existingRt.BestPrice)
                            existingRt.BestPrice = fillPx;

                        _log.Information(
                            "[EXIT-RUNTIME-UPDATE] {Sym} runtime state updated entry={Entry:F2} qty={Qty} (was {PrevQty}) entryUtc={Utc}",
                            req.Symbol.Ticker, newAvgPrice, newQty, prevQty, existingRt.EntryUtc);
                    }
                    else
                    {
                        // Fallback: kreiraj novi runtime state (ne bi trebalo da se desi)
                        _log.Warning(
                            "[EXIT-RUNTIME-FALLBACK] {Sym} runtime state missing for increased position! Creating new one entry={Entry:F2} qty={Qty}",
                            req.Symbol.Ticker, newAvgPrice, newQty);

                        // Determine regime and symbol baseline
                        string? regime = null;
                        string? symbolBaseline = null;
                        decimal? atrAtEntry = null;

                        if (_atr.TryGetValue(req.Symbol.Ticker, out var atrState) && atrState.Atr.HasValue && newAvgPrice > 0m)
                        {
                            var rawAtr = atrState.Atr.Value;
                            var minAtrFrac = _limits.MinAtrFraction;
                            var minAtrAbs = newAvgPrice * minAtrFrac;
                            var flooredAtr = Math.Max(rawAtr, minAtrAbs);
                            atrAtEntry = flooredAtr;

                            var atrFrac = flooredAtr / newAvgPrice;
                            if (atrFrac > 0.0005m) regime = "HIGH";
                            else if (atrFrac > 0.00015m) regime = "NORMAL";
                            else regime = "LOW";

                            if (atrFrac < 0.0002m) symbolBaseline = "slow";
                            else if (atrFrac <= 0.0005m) symbolBaseline = "normal";
                            else symbolBaseline = "fast";
                        }

                        _posRuntime[req.Symbol.Ticker] = new PositionRuntimeState
                        {
                            EntryUtc = utcNow, // Fallback: koristimo trenutno vreme
                            EntryPrice = newAvgPrice,
                            BestPrice = fillPx,
                            IsExternal = false,
                            RegimeAtEntry = regime ?? "LOW",
                            SymbolBaseline = symbolBaseline ?? "normal",
                            AtrAtEntry = atrAtEntry
                        };
                    }
                }

                // ========== AUTO-OCO kada se pozicija POVEĆA (add to position) ==========
                // MultipleOrders true: jedan novi OCO samo za ovaj leg (layered stops). false: ne diramo stare OCO.
                if (!isPaper && !isSyncFill && _orderService != null && _swingConfig.MultipleOrders)
                {
                    try
                    {
                        var sym = req.Symbol;
                        var addedQty = newQty - prevQty;
                        var entryPx = fillPx;

                        var ocoId = $"OCO-{sym.Ticker}-{Guid.NewGuid():N}";
                        var (tpPx, slPx, atrUsed) = ComputeTpSlLevels(sym.Ticker, entryPx);
                        var exitTif = IsSwingMode() ? TimeInForce.Gtc : TimeInForce.Day;

                        var tpReq = new OrderRequest(
                            symbol: sym,
                            side: OrderSide.Sell,
                            type: OrderType.Limit,
                            quantity: addedQty,
                            limitPrice: tpPx,
                            tif: exitTif,
                            correlationId: $"exit-tp-{Guid.NewGuid():N}",
                            timestampUtc: utcNow,
                            ocoGroupId: ocoId,
                            ocoStopPrice: slPx,
                            stopPrice: null,
                            isExit: true
                        );
                        var slReq = new OrderRequest(
                            symbol: sym,
                            side: OrderSide.Sell,
                            type: OrderType.Stop,
                            quantity: addedQty,
                            limitPrice: null,
                            tif: exitTif,
                            correlationId: $"exit-sl-{Guid.NewGuid():N}",
                            timestampUtc: utcNow,
                            ocoGroupId: ocoId,
                            ocoStopPrice: null,
                            stopPrice: slPx,
                            isExit: true
                        );

                        _log.Information(
                            "[OCO] New leg (layered) group={Oco} TP={TP:F2} SL={SL:F2} sym={Sym} qty={Qty} entry={Entry:F2}",
                            ocoId, tpPx, slPx, sym.Ticker, addedQty, entryPx);

                        if (_orderRepo != null)
                        {
                            try
                            {
                                decimal? submitBid = null, submitAsk = null, submitSpread = null;
                                lock (_sync)
                                {
                                    if (_lastQuotes.TryGetValue(sym.Ticker, out var snap) && snap is not null && snap.Bid.HasValue && snap.Ask.HasValue && snap.Bid > 0m && snap.Ask > 0m)
                                    {
                                        submitBid = snap.Bid;
                                        submitAsk = snap.Ask;
                                        submitSpread = snap.Ask - snap.Bid;
                                    }
                                }
                                _ = _orderRepo.InsertSubmittedAsync(tpReq.CorrelationId!, sym.Ticker, "Sell", addedQty, "limit", tpPx, null, utcNow, submitBid, submitAsk, submitSpread, sym.Exchange, ct: CancellationToken.None);
                                _ = _orderRepo.InsertSubmittedAsync(slReq.CorrelationId!, sym.Ticker, "Sell", addedQty, "stop", null, slPx, utcNow, submitBid, submitAsk, submitSpread, sym.Exchange, ct: CancellationToken.None);
                            }
                            catch (Exception ex)
                            {
                                _log.Warning(ex, "[DB-ORDERS] InsertSubmitted layered OCO failed sym={Sym}", sym.Ticker);
                            }
                        }

                        var tpNotional = addedQty * tpPx;
                        var slNotional = addedQty * slPx;
                        var tpFee = CalculateFeeUsd(tpNotional, isMaker: true);
                        var slFee = CalculateFeeUsd(slNotional, isMaker: false);
                        _orders.TryAdd(new PendingOrder(tpReq, 0m, utcNow, LastFeeUsd: tpFee));
                        _orders.TryAdd(new PendingOrder(slReq, 0m, utcNow, LastFeeUsd: slFee));

                        _log.Information("[OCO] Placing layered TP (limit + price_oco_stop) on broker. sym={Sym} sl={SL:F2} tp={TP:F2} group={Group}",
                            sym.Ticker, slPx, tpPx, ocoId);
                        _ = PlaceRealAsync(tpReq);
                        if (!tpReq.OcoStopPrice.HasValue)
                            _ = PlaceRealAsync(slReq);
                    }
                    catch (Exception ex)
                    {
                        _log.Error(ex, "[OCO] Failed to place layered OCO for {Sym}", req.Symbol.Ticker);
                    }
                }
            }

            // --- SWING DB: upsert open pozicije ---
            // Sync fill = samo učitana pozicija sa berze; ne upisujemo swing_positions.
            if (_swingPosRepo is not null && !isSyncFill && IsSwingMode() && newQty > 0m)
            {
                try
                {
                    var openedUtc = utcNow;
                    lock (_sync)
                    {
                        if (_posRuntime.TryGetValue(req.Symbol.Ticker, out var rt))
                            openedUtc = rt.EntryUtc;
                    }

                    var snap = new SwingPositionSnapshot
                    {
                        Symbol = req.Symbol.Ticker,
                        Quantity = newQty,
                        EntryPrice = newPos?.AveragePrice ?? fillPx,
                        OpenedUtc = openedUtc,
                        Strategy = _strategy.GetType().Name,
                        CorrelationId = req.CorrelationId ?? string.Empty,
                        PlannedHoldingDays = _swingConfig.MaxHoldingDays,
                        ExitPolicy = SwingExitPolicy.PriceOrTime
                    };

                    _ = _swingPosRepo.UpsertOpenAsync(snap, exchange: req.Symbol.Exchange ?? _exchangeName, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[SWING-DB] UpsertOpen failed for {Sym}", req.Symbol.Ticker);
                }
            }

            // ========== AUTO-OCO nakon BUY otvaranja pozicije ==========
            // Sync fill = samo učitana pozicija sa berze; NE šaljemo naloge.
            if (!isPaper && !isSyncFill && prevQty <= 0m && newQty > 0m)
            {
                try
                {
                    var sym = req.Symbol;
                    var entryPx = fillPx;

                    // isti OCO/OCA group id za oba naloga (TP i SL)
                    var ocoId = $"OCO-{sym.Ticker}-{Guid.NewGuid():N}";

                    // ATR lookup (ComputeTpSlLevels već radi floor na % cene)
                    var (tpPx, slPx, atrUsed) = ComputeTpSlLevels(sym.Ticker, entryPx);

                    // Swing: GTC, intraday: DAY
                    var exitTif = IsSwingMode()
                        ? TimeInForce.Gtc
                        : TimeInForce.Day;

                    var tpReq = new OrderRequest(
                        symbol: sym,
                        side: OrderSide.Sell,
                        type: OrderType.Limit,
                        quantity: newQty,
                        limitPrice: tpPx,
                        tif: exitTif,
                        correlationId: $"exit-tp-{Guid.NewGuid():N}",
                        timestampUtc: utcNow,
                        ocoGroupId: ocoId,
                        ocoStopPrice: slPx,
                        stopPrice: null,
                        isExit: true
                    );

                    var slReq = new OrderRequest(
                        symbol: sym,
                        side: OrderSide.Sell,
                        type: OrderType.Stop,
                        quantity: newQty,
                        limitPrice: null,
                        tif: exitTif,
                        correlationId: $"exit-sl-{Guid.NewGuid():N}",
                        timestampUtc: utcNow,
                        ocoGroupId: ocoId,
                        ocoStopPrice: null,
                        stopPrice: slPx,
                        isExit: true
                    );

                    _log.Information(
                        "[OCO] Creating OCO group={Oco} TP={TP:F2} SL={SL:F2} sym={Sym} qty={Qty} exitTif={ExitTif} orderService={OrderSvc}",
                        ocoId, tpPx, slPx, sym.Ticker, newQty, exitTif, _orderService != null ? "SET" : "NULL");

                    // --- broker_orders: insert submitted za TP i SL ---
                    if (_orderRepo is not null)
                    {
                        try
                        {
                            decimal? submitBid = null, submitAsk = null, submitSpread = null;

                            lock (_sync)
                            {
                                if (_lastQuotes.TryGetValue(sym.Ticker, out var snap) &&
                                    snap is not null &&
                                    snap.Bid.HasValue && snap.Ask.HasValue &&
                                    snap.Bid > 0m && snap.Ask > 0m)
                                {
                                    submitBid = snap.Bid;
                                    submitAsk = snap.Ask;
                                    submitSpread = snap.Ask - snap.Bid;
                                }
                            }

                            _ = _orderRepo.InsertSubmittedAsync(
                              id: tpReq.CorrelationId,
                              symbol: sym.Ticker,
                              side: "Sell",
                              qty: newQty,
                              orderType: "limit",
                              limitPrice: tpPx,
                              stopPrice: null,
                              createdUtc: utcNow,
                              submitBid: submitBid,
                              submitAsk: submitAsk,
                              submitSpread: submitSpread,
                              exchange: sym.Exchange,
                              ct: CancellationToken.None
                          );

                            // SL
                            _ = _orderRepo.InsertSubmittedAsync(
                                 id: slReq.CorrelationId,
                                 symbol: sym.Ticker,
                                 side: "Sell",
                                 qty: newQty,
                                 orderType: "stop",
                                 limitPrice: null,
                                 stopPrice: slPx,
                                 createdUtc: utcNow,
                                 submitBid: submitBid,
                                 submitAsk: submitAsk,
                                 submitSpread: submitSpread,
                                 exchange: sym.Exchange,
                                 ct: CancellationToken.None
                             );

                        }
                        catch (Exception ex)
                        {
                            _log.Warning(ex, "[DB-ORDERS] InsertSubmitted OCO failed sym={Sym}", sym.Ticker);
                        }
                    }

                    // pending store - za SELL/exit naloge ReservedUsd=0 (ne rezervišemo USD za sell)
                    // Fee je samo metadata u LastFeeUsd
                    var tpNotional = newQty * tpPx;
                    var slNotional = newQty * slPx;
                    var tpFee = CalculateFeeUsd(tpNotional, isMaker: true);  // TP limit = Maker
                    var slFee = CalculateFeeUsd(slNotional, isMaker: false); // SL stop = Taker (kad se aktivira)

                    _orders.TryAdd(new PendingOrder(tpReq, 0m, utcNow, LastFeeUsd: tpFee));
                    _orders.TryAdd(new PendingOrder(slReq, 0m, utcNow, LastFeeUsd: slFee));  // ReservedUsd=0 za exit

                    // Bitfinex OCO: jedan LIMIT nalog sa price_oco_stop=SL; berza kreira oba naloga. Ne šaljemo stop posebno.
                    _log.Information("[OCO] Placing TP (limit + price_oco_stop) on broker. sym={Sym} sl={SL:F2} tp={TP:F2} group={Group}",
                        sym.Ticker, slPx, tpPx, ocoId);
                    _ = PlaceRealAsync(tpReq);
                    if (!tpReq.OcoStopPrice.HasValue)
                        _ = PlaceRealAsync(slReq);
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "[OCO] Failed to place OCO group for {Sym}", req.Symbol.Ticker);
                }
            }
        }
        else
        {
            // DEBUG: Proveri stanje pozicije pre SELL fill-a
            var posBefore = _positionBook.Get(req.Symbol.Ticker);
            var avgPriceBefore = posBefore?.AveragePrice ?? 0m;
            var qtyBefore = posBefore?.Quantity ?? 0m;

            realizedPnl = _positionBook.ApplySellFillCrypto(req.Symbol.Ticker, req.Quantity, fillPx);

            // DEBUG: Loguj detalje za dijagnostiku
            _log.Information(
                "[DEBUG-PNL] {Sym} SELL qty={Qty} @ {Px} avgBefore={Avg} qtyBefore={QtyB} realized={PnL} expected={Exp}",
                req.Symbol.Ticker, req.Quantity, fillPx, avgPriceBefore, qtyBefore, realizedPnl,
                qtyBefore > 0m ? (fillPx - avgPriceBefore) * Math.Min(req.Quantity, qtyBefore) : 0m);

            _cashService.OnSellProceeds(notional, utcNow);

            // posle SELL fill-a: proveri da li je pozicija STVARNO zatvorena
            var newPos = _positionBook.Get(req.Symbol.Ticker);
            var newQty = newPos?.Quantity ?? 0m;

            if (newQty <= 0m)
            {
                lock (_sync)
                {
                    _exitPending.Remove(req.Symbol.Ticker);
                    _lastExitUtc[req.Symbol.Ticker] = utcNow;
                    _posRuntime.Remove(req.Symbol.Ticker);
                }
            }

            // Discord notification for SELL
            string? exitReasonStr = null;
            if (newQty <= 0m && qtyBefore > 0m)
            {
                if (_swingPosRepo is not null && IsSwingMode())
                {
                    var (exitReasonOpt, _) = InferSwingExitReason(req);
                    exitReasonStr = exitReasonOpt?.ToString() ?? "Manual";
                }
                else
                {
                    exitReasonStr = "Position Closed";
                }
            }

            if (_discordNotifier != null)
            {
                _log.Information("[DISCORD] Sending SELL notification for {Symbol} on {Exchange}", req.Symbol.Ticker, req.Symbol.Exchange ?? _exchangeName);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _discordNotifier.NotifySellAsync(
                            symbol: req.Symbol.Ticker,
                            quantity: req.Quantity,
                            price: fillPx,
                            notional: notional,
                            realizedPnl: realizedPnl,
                            exchange: req.Symbol.Exchange ?? _exchangeName,
                            isPaper: isPaper,
                            exitReason: exitReasonStr,
                            ct: CancellationToken.None);
                        _log.Information("[DISCORD] SELL notification sent successfully for {Symbol}", req.Symbol.Ticker);
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(ex, "[DISCORD] Failed to send SELL notification for {Symbol}", req.Symbol.Ticker);
                    }
                });
            }
            else
            {
                _log.Warning("[DISCORD] Discord notifier is NULL - SELL notification not sent for {Symbol}", req.Symbol.Ticker);
            }

            // SWING DB: parcijalni exit -> azuriraj quantity otvorene pozicije
            if (_swingPosRepo is not null &&
                IsSwingMode() &&
                prevQty > 0m &&
                newQty > 0m &&
                newQty < prevQty)
            {
                try
                {
                    var t = _swingPosRepo.UpdateOpenQuantityAsync(
                        symbol: req.Symbol.Ticker,
                        exchange: req.Symbol.Exchange ?? _exchangeName,
                        quantity: newQty,
                        ct: CancellationToken.None);

                    _ = t.ContinueWith(
                        tt => _log.Error(tt.Exception, "[SWING-DB] UpdateOpenQuantity faulted {Sym}", req.Symbol.Ticker),
                        TaskContinuationOptions.OnlyOnFaulted);
                    _log.Information(
                        "[SWING-DB] UpdateOpenQuantity {Sym} qty={Qty}",
                        req.Symbol.Ticker, newQty);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[SWING-DB] UpdateOpenQuantity scheduling failed for {Sym}", req.Symbol.Ticker);
                }
            }

            // SWING DB: zatvori samo kad je pozicija stvarno zatvorena
            // U paper mode-u takođe upisujemo swing pozicije za praćenje
            if (_swingPosRepo is not null &&
                IsSwingMode() &&
                prevQty > 0m &&
                newQty <= 0m)
            {
                try
                {
                    var (exitReasonOpt, autoExit) = InferSwingExitReason(req);
                    var exitReason = exitReasonOpt ?? SwingExitReason.Manual;

                    var t = _swingPosRepo.MarkClosedAsync(
                        symbol: req.Symbol.Ticker,
                        exchange: req.Symbol.Exchange ?? _exchangeName,
                        closedUtc: utcNow,
                        exitReason: exitReason,
                        autoExit: autoExit,
                        ct: CancellationToken.None);

                    _ = t.ContinueWith(
                        tt => _log.Error(tt.Exception, "[SWING-DB] MarkClosed faulted {Sym}", req.Symbol.Ticker),
                        TaskContinuationOptions.OnlyOnFaulted);
                    _log.Information(
                        "[SWING-DB] MarkClosed {Sym} exitReason={Reason} auto={Auto}",
                        req.Symbol.Ticker, exitReason, autoExit);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[SWING-DB] MarkClosed scheduling failed for {Sym}", req.Symbol.Ticker);
                }
            }
        }

        // ako je real trade i gubitak -> uključi cooldown za simbol
        if (!isPaper && realizedPnl < 0m)
        {
            lock (_sync)
            {
                _lastLossUtc[req.Symbol.Ticker] = utcNow;
            }

            _log.Information(
                "[COOLDOWN-SET] {Sym} loss={Loss:F2} at {Utc:o}, cooldown={CdMin}min",
                req.Symbol.Ticker, realizedPnl, utcNow, _symbolCooldownAfterLoss.TotalMinutes);
        }

        // day guards – javi koliko smo zaradili/izgubili
        _dayGuards?.OnRealizedPnl(realizedPnl, utcNow);

        // Sync fill = učitavanje postojeće pozicije s berze, nije nova kupnja → ne upisujemo u journal/fills/pnl
        if (!isSyncFill)
        {
            // journal – sad imamo sve info
            (decimal riskFraction, decimal? atrUsed, decimal? priceRisk) sizingMeta = (0m, null, null);
            if (!string.IsNullOrWhiteSpace(req.CorrelationId))
        {
            lock (_sync)
            {
                if (_sizingMetadata.TryGetValue(req.CorrelationId, out var meta))
                {
                    sizingMeta = meta;
                    _sizingMetadata.Remove(req.CorrelationId);
                }
            }
        }

        // broker_order_id za trade_fills / trade_journal – iz pending ordera (po correlationId), kao u IBKR
        string? brokerOrderId = null;
        if (_orders.TryGet(req.CorrelationId ?? string.Empty, out var pending) && pending != null)
            brokerOrderId = pending.BrokerOrderId;

        var entry = new TradeJournalEntry(
            Utc: utcNow,
            Symbol: req.Symbol.Ticker,
            Side: req.Side.ToString(),
            Quantity: req.Quantity,
            Price: fillPx,
            Notional: notional,
            RealizedPnl: realizedPnl,
            IsPaper: isPaper,
            IsExit: (req.CorrelationId ?? string.Empty).StartsWith("exit-", StringComparison.OrdinalIgnoreCase),
            Strategy: _strategy.GetType().Name,
            CorrelationId: req.CorrelationId,
            BrokerOrderId: brokerOrderId,
            EstimatedFeeUsd: CalculateFeeUsd(notional, isMaker: true), // Limit order = Maker
            PlannedPrice: req.LimitPrice,
            RiskFraction: sizingMeta.riskFraction > 0m ? sizingMeta.riskFraction : null,
            AtrUsed: sizingMeta.atrUsed,
            PriceRisk: sizingMeta.priceRisk,
            Exchange: req.Symbol.Exchange ?? _exchangeName
        );

        // DB: trade_journal
        if (_journalRepo is not null)
        {
            try { _ = _journalRepo.InsertAsync(entry, CancellationToken.None); }
            catch (Exception ex) { _log.Warning(ex, "[DB-JOURNAL] insert scheduling failed"); }
        }

        // DB: trade_fills (realized_pnl = entry.RealizedPnl – za BUY 0, za SELL realizovani PnL)
        if (_fillRepo is not null)
        {
            try
            {
                var rp = entry.RealizedPnl;
                _ = _fillRepo.InsertAsync(
                    utc: entry.Utc,
                    symbol: entry.Symbol,
                    side: entry.Side,
                    quantity: entry.Quantity,
                    price: entry.Price,
                    notional: entry.Notional,
                    realizedPnl: rp,
                    isPaper: entry.IsPaper,
                    isExit: entry.IsExit,
                    strategy: entry.Strategy,
                    correlationId: entry.CorrelationId,
                    brokerOrderId: entry.BrokerOrderId,
                    estimatedFeeUsd: entry.EstimatedFeeUsd,
                    exchange: entry.Exchange,
                    ct: CancellationToken.None
                ).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        _log.Error(t.Exception, "[DB-FILLS] insert failed sym={Sym} side={Side} realizedPnl={Pnl}", entry.Symbol, entry.Side, rp);
                }, TaskContinuationOptions.OnlyOnFaulted);
                _log.Debug("[DB-FILLS] insert scheduled sym={Sym} side={Side} realizedPnl={Pnl}", entry.Symbol, entry.Side, rp);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[DB-FILLS] insert scheduling failed");
            }
        }

        // DB: daily_pnl (crypto)
        // Count realized trade events only (exit fills / non-zero realized PnL),
        // so BUY entry fills do not inflate daily trade_count.
        if (_pnlRepo is not null && (realizedPnl != 0m || entry.IsExit))
        {
            _log.Information(
                "[DB-CRYPTO-PNL-CALL] Calling AddTradeAsync date={Date} pnl={Pnl:F2} side={Side} symbol={Sym} exchange={Ex} isPaper={Paper} isExit={Exit}",
                utcNow.Date, realizedPnl, req.Side, req.Symbol.Ticker, req.Symbol.Exchange ?? _exchangeName, isPaper, entry.IsExit);
            try
            {
                _ = _pnlRepo.AddTradeAsync(utcNow, realizedPnl, CancellationToken.None)
                    .ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                        {
                            _log.Error(t.Exception, "[DB-CRYPTO-PNL-ERROR] AddTradeAsync failed date={Date} pnl={Pnl}", utcNow.Date, realizedPnl);
                        }
                        else
                        {
                            _log.Information("[DB-CRYPTO-PNL-SUCCESS] AddTradeAsync completed date={Date} pnl={Pnl:F2}", utcNow.Date, realizedPnl);
                        }
                    });
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[DB-CRYPTO-PNL] add trade scheduling failed date={Date} pnl={Pnl}", utcNow.Date, realizedPnl);
            }
        }
        else if (_pnlRepo is null)
        {
            _log.Warning("[DB-CRYPTO-PNL-SKIP] _pnlRepo is NULL - skipping AddTradeAsync date={Date} pnl={Pnl:F2}", utcNow.Date, realizedPnl);
        }
        else if (realizedPnl == 0m && !entry.IsExit)
        {
            _log.Debug("[DB-CRYPTO-PNL-SKIP] Skipping AddTradeAsync for BUY trade with realizedPnl=0 date={Date} symbol={Sym}", utcNow.Date, req.Symbol.Ticker);
        }

        // log
        if (isPaper)
        {
            _log.Information(
                "[CRYPTO-PAPER-FILLED] {Side} {Sym} x{Qty:F6} @ {Px:F2} notional={Notional:F2} realized={PnL:F2}",
                req.Side, req.Symbol.Ticker, req.Quantity, fillPx, notional, realizedPnl);
        }
        else
        {
            _log.Information(
                "[CRYPTO-REAL-FILLED] {Side} {Sym} x{Qty:F6} @ {Px:F2} notional={Notional:F2} realized={PnL:F2}",
                req.Side, req.Symbol.Ticker, req.Quantity, fillPx, notional, realizedPnl);
        }

        // Prometheus order metrics
        try
        {
            var symbol = req.Symbol.Ticker;
            if (req.Side == OrderSide.Buy)
                OrderMetrics.Instance.FilledBuy(symbol, (double)notional);
            else if (req.Side == OrderSide.Sell)
                OrderMetrics.Instance.FilledSell(symbol, (double)notional);

            var posNow = _positionBook.Get(symbol);
            var qtyNow = posNow?.Quantity ?? 0m;
            OrderMetrics.Instance.SetOpenPositions(symbol, (double)qtyNow);

            if (req.LimitPrice.HasValue)
            {
                var expected = req.LimitPrice.Value;
                var slip = (double)(fillPx - expected);
                OrderMetrics.Instance.SetSlippage(symbol, slip);
            }

            var latencyMs = (utcNow - req.TimestampUtc).TotalMilliseconds;
            if (latencyMs >= 0)
                OrderMetrics.Instance.SetFillLatency(symbol, latencyMs);
        }
        catch
        {
            // metrics nikad ne sme da sruši trading engine
        }
        } // !isSyncFill
    }

    // =========================
    //  HELPER METHODS
    // =========================

    private decimal ResolvePerSymbolBudgetUsd(string ticker)
    {
        if (!string.IsNullOrWhiteSpace(ticker) &&
            _perSymbolBudgetByTicker.TryGetValue(ticker, out var budgetOverride) &&
            budgetOverride > 0m)
        {
            return budgetOverride;
        }

        return _perSymbolBudgetUsd;
    }

    private bool TryGetQuote(string ticker, DateTime utcNow, out MarketQuote q, out string? reason)
    {
        q = default!;
        reason = null;

        lock (_sync)
        {
            _lastQuotes.TryGetValue(ticker, out q);
        }

        if (q is null)
        {
            reason = "no-quote";
            return false;
        }

        var age = utcNow - q.TimestampUtc;
        if (age > MaxQuoteAge)
        {
            decimal? mid = null;
            if (q.Bid.HasValue && q.Ask.HasValue && q.Bid > 0m && q.Ask > 0m)
                mid = (q.Bid.Value + q.Ask.Value) / 2m;

            var bidStr = q.Bid?.ToString("F4") ?? "n/a";
            var askStr = q.Ask?.ToString("F4") ?? "n/a";
            var midStr = mid?.ToString("F4") ?? "n/a";

            reason = $"stale-quote:{age.TotalSeconds:F1}s;bid={bidStr};ask={askStr};mid={midStr}";
            return false;
        }

        if (q.Bid is not { } bid || q.Ask is not { } ask || bid <= 0m || ask <= 0m)
        {
            reason = "no-nbbo";
            return false;
        }

        var mid2 = (bid + ask) / 2m;
        if (mid2 <= 0m)
        {
            reason = "invalid-mid";
            return false;
        }

        var spread = ask - bid;
        var spreadFrac = spread / mid2;

        if (spreadFrac > MaxSpreadFraction)
        {
            reason = $"spread-too-wide:{spreadFrac:P2}";
            return false;
        }

        return true;
    }

    private bool IsRateLimited(DateTime nowUtc, out string reason)
    {
        lock (_sync)
        {
            // izbaci sve što je starije od prozora
            while (_signalTimestamps.Count > 0 && nowUtc - _signalTimestamps.Peek() > _signalWindow)
            {
                _signalTimestamps.Dequeue();
            }
            // upiši ovaj signal
            _signalTimestamps.Enqueue(nowUtc);
            // ako smo prešli limit – blokiraj
            if (_signalTimestamps.Count > _maxSignalsPerWindow)
            {
                reason = $"{_signalTimestamps.Count}/{_maxSignalsPerWindow} in {_signalWindow.TotalSeconds:F0}s";
                return true;
            }
            reason = string.Empty;
            return false;
        }
    }

    private bool IsInCooldown(Symbol symbol, DateTime utcNow, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;

        lock (_sync)
        {
            if (_lastLossUtc.TryGetValue(symbol.Ticker, out var lossTime))
            {
                var until = lossTime + _symbolCooldownAfterLoss;
                if (utcNow < until)
                {
                    remaining = until - utcNow;
                    return true;
                }
            }
        }

        return false;
    }

    private bool HasPendingForSymbol(string ticker)
    {
        var pendings = _orders.Snapshot();
        var now = DateTime.UtcNow;

        foreach (var p in pendings)
        {
            if (p?.Req is null)
                continue;

            if (!string.Equals(p.Req.Symbol.Ticker, ticker, StringComparison.OrdinalIgnoreCase))
                continue;

            // EXIT nalozi (TP/SL, TIME EXIT) više ne blokiraju ENTRY signale
            if (p.Req.CorrelationId?.StartsWith("exit-", StringComparison.OrdinalIgnoreCase) == true)
                continue;

            return true;
        }

        return false;
    }

    private (decimal Equity, decimal InPositionsUsd) GetApproxEquity()
    {
        var cash = _lastCash;
        decimal inPosUsd = 0m;

        var positions = _positionBook.Snapshot();
        foreach (var p in positions)
        {
            if (p.Quantity == 0m)
                continue;

            MarketQuote? q;
            lock (_sync)
            {
                _lastQuotes.TryGetValue(p.Symbol, out q);
            }

            decimal px;

            if (q is not null && q.Bid.HasValue && q.Ask.HasValue && q.Bid > 0m && q.Ask > 0m)
            {
                // mid NBBO
                px = (q.Bid.Value + q.Ask.Value) / 2m;
            }
            else if (q is not null && q.Last.HasValue && q.Last > 0m)
            {
                px = q.Last.Value;
            }
            else
            {
                // fallback – average price iz pozicije
                px = p.AveragePrice;
            }

            if (px <= 0m)
                continue;

            inPosUsd += p.Quantity * px;
        }

        var equity = cash.Free + cash.Settling + inPosUsd;
        return (equity, inPosUsd);
    }

    private (decimal Tp, decimal Sl, decimal? AtrUsed) ComputeTpSlLevels(string symbol, decimal entryPx)
    {
        if (entryPx <= 0m)
            return (0m, 0m, null);

        decimal? atrOpt = null;

        lock (_sync)
        {
            if (_atr.TryGetValue(symbol, out var s) && s.Atr.HasValue && s.Atr.Value > 0m)
            {
                var rawAtr = s.Atr.Value;

                // minimalni ATR kao % cene (iz config-a)
                var minAtrFrac = _limits.MinAtrFraction;
                var minAtrAbs = entryPx * minAtrFrac;

                atrOpt = Math.Max(rawAtr, minAtrAbs);
            }
        }

        var pctTpDist = entryPx * _tpFraction;
        var pctSlDist = entryPx * _slFraction;

        var atrTpDist = atrOpt.HasValue ? TpAtrMultiple * atrOpt.Value : 0m;
        var atrSlDist = atrOpt.HasValue ? SlAtrMultiple * atrOpt.Value : 0m;

        var tpDist = Math.Max(atrTpDist, pctTpDist);
        var slDist = Math.Max(atrSlDist, pctSlDist);

        var tpPx = entryPx + tpDist;
        var slPx = entryPx - slDist;
        return (tpPx, slPx, atrOpt);
    }

    private decimal NormalizeQty(Symbol symbol, decimal rawQty)
    {
        if (rawQty <= 0m)
            return 0m;

        // Default fallback ako metadata nije dostupna.
        var step = 0.001m;
        var minQty = 0m;

        if (_symbolProvider != null)
        {
            var exchangeText = symbol.Exchange ?? _exchangeName;
            if (Enum.TryParse<CryptoExchangeId>(exchangeText, true, out var exchangeId) &&
                exchangeId != CryptoExchangeId.Unknown &&
                _symbolProvider.TryGetSymbol(exchangeId, symbol.Ticker, out var cryptoSymbol) &&
                _symbolProvider.TryGetMetadata(cryptoSymbol, out var meta))
            {
                if (meta.QuantityStep > 0m)
                    step = meta.QuantityStep;
                if (meta.MinQuantity > 0m)
                    minQty = meta.MinQuantity;
            }
        }

        var steps = Math.Floor(rawQty / step);
        var norm = steps * step;

        if (minQty > 0m && norm < minQty)
            return 0m;
        return norm <= 0m ? 0m : norm;
    }

    private void SafeReserve(decimal amount, DateTime utcNow, string reason)
    {
        try
        {
            _cashService.Reserve(amount, utcNow);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[CASH] reserve failed {Amt:F2} reason={Reason}", amount, reason);
        }
    }

    private async Task PlaceRealAsync(OrderRequest req)
    {
        _log.Information("[PLACE-REAL] Starting PlaceRealAsync: corr={Corr} sym={Sym} side={Side} qty={Qty} px={Px} isExit={Exit} orderService={OrderSvc}",
            req.CorrelationId, req.Symbol.Ticker, req.Side, req.Quantity, req.LimitPrice, req.IsExit, _orderService != null ? "SET" : "NULL");
        try
        {
            if (_orderService == null)
            {
                _log.Error("[PLACE-REAL] OrderService is NULL! Cannot place order: corr={Corr} sym={Sym}", req.CorrelationId, req.Symbol.Ticker);
                return;
            }

            // Proveri balance sa Bitfinex-a pre nego što se pokuša da se postavi order
            if (_tradingApi != null && _isRealMode && req.Side == OrderSide.Buy)
            {
                try
                {
                    var balances = await _tradingApi.GetBalancesAsync(CancellationToken.None).ConfigureAwait(false);
                    var ustBalance = FindUsdtBalance(balances);
                    
                    if (ustBalance != null)
                    {
                        _log.Debug("[PLACE-BALANCE-CHECK] Found balance: raw={Raw} normalized=USDT free={Free}", ustBalance.Asset, ustBalance.Free);
                        var requiredAmount = req.Quantity * (req.LimitPrice ?? 0m);
                        if (requiredAmount > 0m && ustBalance.Free < requiredAmount)
                        {
                            _log.Warning(
                                "[PLACE-BALANCE-CHECK] Insufficient balance on exchange. Required={Required:F2} Available={Available:F2} Symbol={Sym}",
                                requiredAmount, ustBalance.Free, req.Symbol.Ticker);
                            
                            // Sinhronizuj lokalno cash stanje sa realnim stanjem
                            var currentCash = await _cashService.GetCashStateAsync().ConfigureAwait(false);
                            var diff = ustBalance.Free - currentCash.Free;
                            if (Math.Abs(diff) > 0.01m) // Ako je razlika veća od 1 centa
                            {
                                _log.Warning(
                                    "[CASH-SYNC] Syncing cash state. Local={Local:F2} Real={Real:F2} Diff={Diff:F2}",
                                    currentCash.Free, ustBalance.Free, diff);
                                
                                // Ažuriraj lokalno stanje da odgovara realnom
                                if (diff > 0m)
                                {
                                    _cashService.MarkFree(diff);
                                }
                                else
                                {
                                    // Ako je realno stanje manje, ne možemo direktno da smanjimo Free
                                    // ali možemo da logujemo upozorenje
                                    _log.Warning(
                                        "[CASH-SYNC] Real balance is lower than local. Cannot adjust automatically. Local={Local:F2} Real={Real:F2}",
                                        currentCash.Free, ustBalance.Free);
                                }
                            }
                            
                            throw new InvalidOperationException(
                                $"Insufficient balance on exchange. Required={requiredAmount:F2} Available={ustBalance.Free:F2}");
                        }
                    }
                }
                catch (Exception ex) when (!(ex is InvalidOperationException))
                {
                    _log.Warning(ex, "[PLACE-BALANCE-CHECK] Failed to check balance from exchange, continuing anyway");
                }
            }

            using var cts = new CancellationTokenSource(_brokerPlaceTimeout);

            _log.Information("[PLACE-REAL] Calling orderService.PlaceAsync: corr={Corr} sym={Sym} side={Side} qty={Qty}",
                req.CorrelationId, req.Symbol.Ticker, req.Side, req.Quantity);

            var brokerId = await _orderService
                .PlaceAsync(req)
                .ConfigureAwait(false);

            _log.Information("[PLACE-REAL] Order placed successfully: corr={Corr} brokerId={BrokerId}", req.CorrelationId, brokerId);

            _orders.TrySetBrokerOrderId(req.CorrelationId, brokerId);

            if (_orderRepo is not null)
            {
                try
                {
                    await _orderRepo.MarkSentAsync(
                        id: req.CorrelationId,
                        brokerOrderId: brokerId,
                        sentUtc: DateTime.UtcNow,
                        ct: CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[DB-ORDERS] MarkSent failed corr={Corr}", req.CorrelationId);
                }
            }
            _log.Information("[PLACE] id={Id} {Side} {Sym} x{Qty} @ {Px}", brokerId, req.Side, req.Symbol.Ticker, req.Quantity, req.LimitPrice);
            if (_orders.TryRemove(req.CorrelationId, out var prev) && prev is not null)
            {
                var updated = new PendingOrder(
                    Req: prev.Req,
                    ReservedUsd: prev.ReservedUsd,
                    AtUtc: prev.AtUtc,
                    BrokerOrderId: brokerId,
                    LastFeeUsd: prev.LastFeeUsd,
                    LastExecId: prev.LastExecId);
                _orders.TryAdd(updated);
            }
        }
        catch (OperationCanceledException ocex)
        {
            _log.Warning(ocex, "[PLACE-TIMEOUT] broker did not accept order in {Sec}s for {Sym} corr={Corr}", _brokerPlaceTimeout.TotalSeconds, req.Symbol.Ticker, req.CorrelationId);

            if (_orderRepo is not null)
            {
                try
                {
                    await _orderRepo.UpdateStatusAsync(
                        id: req.CorrelationId,
                        status: "place-timeout",
                        lastMsg: $"timeout after {_brokerPlaceTimeout.TotalSeconds}s",
                        forCrypto: true,
                        ct: CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[DB-ORDERS] UpdateStatus(place-timeout) failed");
                }
            }

            RollbackPendingAndReserves(req);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[PLACE-ERROR] failed to place order for {Sym} corr={Corr}", req.Symbol.Ticker, req.CorrelationId);

            if (_orderRepo is not null)
            {
                try
                {
                    await _orderRepo.UpdateStatusAsync(
                        id: req.CorrelationId,
                        status: "place-error",
                        lastMsg: ex.Message,
                        forCrypto: true,
                        ct: CancellationToken.None);
                }
                catch (Exception dex)
                {
                    _log.Warning(dex, "[DB-ORDERS] UpdateStatus(place-error) failed");
                }
            }

            RollbackPendingAndReserves(req);
        }
    }

    private void RollbackPendingAndReserves(OrderRequest req)
    {
        if (_orders.TryRemove(req.CorrelationId, out var po) && po is not null)
        {
            // 1) vrati keš
            if (po.ReservedUsd > 0m)
            {
                try
                {
                    _cashService.Unreserve(po.ReservedUsd);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[ROLLBACK] unreserve failed for {Sym}", req.Symbol.Ticker);
                }
            }

            // 2) ako je EXIT
            if (req.CorrelationId?.StartsWith("exit-", StringComparison.OrdinalIgnoreCase) == true)
            {
                lock (_sync)
                {
                    _exitPending.Remove(req.Symbol.Ticker);
                }
            }

            // 3) upiši u DB da je rollback urađen
            if (_orderRepo is not null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _orderRepo.UpdateStatusAsync(
                            id: req.CorrelationId,
                            status: "place-rolled-back",
                            lastMsg: "reserve rolled back after place failure",
                            forCrypto: true,
                            ct: CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(ex, "[DB-ORDERS] UpdateStatus(place-rolled-back) failed");
                    }
                });
            }
        }
    }

    private void SendExit(string symbol, decimal qty, decimal px, string reason, string? corrPrefix = null)
    {
        var exitTif = IsSwingMode()
            ? TimeInForce.Gtc
            : TimeInForce.Day;

        var nowUtc = DateTime.UtcNow;

        // koren za correlationId
        var prefix = string.IsNullOrWhiteSpace(corrPrefix) ? "exit" : corrPrefix;
        if (!prefix.EndsWith("-", StringComparison.Ordinal))
            prefix += "-";

        var corrId = $"{prefix}{Guid.NewGuid():N}";

        // Runtime exit (time/trail/manual): upiši broker_orders submitted pre slanja,
        // da status/sent update-i ne završe sa affected=0.
        if (_orderRepo is not null)
        {
            try
            {
                decimal? submitBid = null, submitAsk = null, submitSpread = null;
                lock (_sync)
                {
                    if (_lastQuotes.TryGetValue(symbol, out var snap) &&
                        snap is not null &&
                        snap.Bid.HasValue && snap.Ask.HasValue &&
                        snap.Bid > 0m && snap.Ask > 0m)
                    {
                        submitBid = snap.Bid;
                        submitAsk = snap.Ask;
                        submitSpread = snap.Ask - snap.Bid;
                    }
                }

                _ = _orderRepo.InsertSubmittedAsync(
                    id: corrId,
                    symbol: symbol,
                    side: "Sell",
                    qty: qty,
                    orderType: "limit",
                    limitPrice: px > 0m ? px : null,
                    stopPrice: null,
                    createdUtc: nowUtc,
                    submitBid: submitBid,
                    submitAsk: submitAsk,
                    submitSpread: submitSpread,
                    exchange: _exchangeName,
                    ct: CancellationToken.None
                );
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[DB-ORDERS] InsertSubmitted failed corr={Corr}", corrId);
            }
        }

        var req = new OrderRequest(
            symbol: new Symbol(symbol, Currency: "USD", Exchange: _exchangeName),
            side: OrderSide.Sell,
            type: OrderType.Limit,
            quantity: qty,
            limitPrice: px,
            tif: exitTif,
            correlationId: corrId,
            timestampUtc: nowUtc,
            isExit: true
        );

        lock (_sync)
        {
            _exitPending.Add(symbol);
        }

        var exitNotional = qty * px;
        var exitFee = CalculateFeeUsd(exitNotional, isMaker: true); // Limit order = Maker
        // SELL/exit nalog: ReservedUsd=0 (ne rezervišemo USD za sell), fee je samo metadata
        _orders.TryAdd(new PendingOrder(req, 0m, nowUtc, LastFeeUsd: exitFee));

        _log.Information("[SEND-EXIT] {Sym} x{Qty} @ {Px} reason={Reason} corr={Corr}", symbol, qty, px, reason, corrId);

        if (_orderService == null)
        {
            _paperSim.Register(req);
        }
        else
        {
            _ = PlaceRealAsync(req);
        }
    }

    /// <summary>
    /// Registruje BitfinexOrderManager da prosleđuje fill-eve u orchestrator.
    /// </summary>
    public void RegisterBitfinexOrderManager(Bitfinex.BitfinexOrderManager manager)
    {
        if (manager == null)
            return;

        manager.OrderFilled += OnOrderUpdated;
        manager.OrderLinked += OnOcoPartnerLinked;
        _log.Information("[CRYPTO-ORCH] BitfinexOrderManager registered - fill events and OCO partner linking will be forwarded to orchestrator");
    }

    private void OnOcoPartnerLinked(string correlationId, string brokerOrderId)
    {
        if (string.IsNullOrEmpty(correlationId) || string.IsNullOrEmpty(brokerOrderId))
            return;
        _orders.TrySetBrokerOrderId(correlationId, brokerOrderId);
        _log.Information("[CRYPTO-ORCH] OCO partner linked: corr={Corr} brokerId={Bid}", correlationId, brokerOrderId);
    }

    public async Task RecoverOnStartupAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        _log.Information("[CRYPTO-RECOVERY] Starting state recovery");

        // 1) Cash
        try
        {
            var cs = await _cashService.GetCashStateAsync().ConfigureAwait(false);
            _lastCash = cs;
            _log.Information("[CRYPTO-RECOVERY] Cash Free={Free:F2} Settling={Sett:F2} InPos={InPos:F2} Reserved={Res:F2}",
                cs.Free, cs.Settling, cs.InPositions, cs.Reserved);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[CRYPTO-RECOVERY] Failed to refresh cash state");
        }

        // 2) DayGuards recovery - učitaj trade counts i PnL iz baze (za sve crypto menjacnice)
        if (_dayGuards is not null && _signalRepo is not null && _pnlRepo is not null)
        {
            try
            {
                var today = now.Date;

                // Učitaj trade counts po simbolu i ukupan broj
                // Filtrirati po exchange-u da se razdvoje po menjacnicama (Kraken, Bitfinex, Deribit, Bybit)
                var exchangeFilter = _exchangeName; // npr. "Kraken", "Bitfinex", "Deribit", "Bybit"
                var tradesPerSymbol = await _signalRepo.GetTodayTradeCountsPerSymbolAsync(today, exchangeFilter, ct).ConfigureAwait(false);
                var tradesTotal = await _signalRepo.GetTodayTradeCountTotalAsync(today, exchangeFilter, ct).ConfigureAwait(false);

                // Učitaj realizovani PnL iz daily_pnl_crypto (crypto specifična tabela)
                var realizedPnlUsd = await _pnlRepo.GetTodayRealizedPnlAsync(today, ct).ConfigureAwait(false);

                // Restoriraj stanje u DayGuards
                _dayGuards.RestoreState(tradesPerSymbol, tradesTotal, realizedPnlUsd, now);

                _log.Information(
                    "[CRYPTO-RECOVERY] DayGuards restored: Total={Total} PerSymbol={PerSym} PnL={PnL:F2}",
                    tradesTotal,
                    tradesPerSymbol.Count > 0
                        ? string.Join(", ", tradesPerSymbol.Select(kvp => $"{kvp.Key}={kvp.Value}"))
                        : "none",
                    realizedPnlUsd);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[CRYPTO-RECOVERY] DayGuards recovery failed - continuing with empty state");
            }
        }
        else
        {
            _log.Information("[CRYPTO-RECOVERY] DayGuards recovery skipped (missing dependencies)");
        }

        // 3) Pending orders restore
        await RestorePendingOrdersFromDatabaseAsync(now, ct).ConfigureAwait(false);

        // 4) GreenGrind bootstrap (market_ticks + crypto_trades -> in-memory regime state)
        await RecoverGreenGrindFromDbAsync(now, ct).ConfigureAwait(false);

        _log.Information("[CRYPTO-RECOVERY] Recovery completed");
    }

    private async Task RecoverGreenGrindFromDbAsync(DateTime nowUtc, CancellationToken ct)
    {
        if (_greenGrindRegime is null)
        {
            _log.Information("[CRYPTO-RECOVERY] GreenGrind bootstrap skipped (disabled)");
            return;
        }

        if (_marketTickRepo is null || _cryptoTradesRepo is null)
        {
            _log.Warning("[CRYPTO-RECOVERY] GreenGrind bootstrap skipped (missing MarketTickRepository/CryptoTradesRepository)");
            return;
        }

        try
        {
            var lookback = _greenGrindRegime.BootstrapLookback;
            var sinceUtc = nowUtc - lookback;
            var barMinutes = _greenGrindRegime.BootstrapBarMinutes;

            var midBuckets = await _marketTickRepo
                .GetRecentMidBucketsAsync(_exchangeName, sinceUtc, barMinutes, ct)
                .ConfigureAwait(false);

            var tradeBuckets = await _cryptoTradesRepo
                .GetRecentBucketsAsync(_exchangeName, sinceUtc, barMinutes, ct)
                .ConfigureAwait(false);

            foreach (var row in midBuckets)
            {
                _greenGrindRegime.SeedMidBucket(row.Symbol, row.BucketUtc, row.Mid);
            }

            foreach (var row in tradeBuckets)
            {
                _greenGrindRegime.SeedTradeBucket(row.Symbol, row.BucketUtc, row.TradeCount, row.BuyQty, row.SellQty);
            }

            var recomputed = _greenGrindRegime.RecomputeAll(nowUtc);

            _log.Information(
                "[CRYPTO-RECOVERY] GreenGrind bootstrap loaded exchange={Exchange} midBuckets={MidBuckets} tradeBuckets={TradeBuckets} symbols={Symbols} lookback={Lookback}",
                _exchangeName,
                midBuckets.Count,
                tradeBuckets.Count,
                recomputed,
                lookback);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[CRYPTO-RECOVERY] GreenGrind bootstrap failed - continuing with cold start");
        }
    }

    private async Task RestorePendingOrdersFromDatabaseAsync(DateTime nowUtc, CancellationToken ct)
    {
        if (_orderRepo is null)
        {
            _log.Information("[CRYPTO-RECOVERY] No BrokerOrderRepository, skipping pending restore");
            return;
        }

        try
        {
            var recoverable = await _orderRepo
                .GetRecoverableOrdersForExchangeAsync(_exchangeName, ct)
                .ConfigureAwait(false);

            if (recoverable.Count == 0)
            {
                _log.Information("[CRYPTO-RECOVERY] No recoverable broker_orders for exchange={Exchange}", _exchangeName);
                return;
            }

            var restored = 0;
            var restoredExit = 0;
            var restoredEntry = 0;
            var skippedInvalid = 0;
            var skippedDuplicate = 0;

            foreach (var row in recoverable)
            {
                if (HasPendingByCorrelation(row.Id))
                {
                    skippedDuplicate++;
                    continue;
                }

                if (!TryBuildRecoveredOrderRequest(row, out var req))
                {
                    skippedInvalid++;
                    continue;
                }

                var reservedUsd = 0m;
                if (!req.IsExit && req.Side == OrderSide.Buy)
                {
                    var reservePx = req.LimitPrice ?? req.StopPrice;
                    if (reservePx.HasValue && reservePx.Value > 0m)
                    {
                        var reserveNotional = req.Quantity * reservePx.Value;
                        var reserveFee = CalculateFeeUsd(reserveNotional, isMaker: true);
                        reservedUsd = reserveNotional + reserveFee;
                        SafeReserve(reservedUsd, nowUtc, "recovery-open-order");
                    }
                }

                var po = new PendingOrder(
                    Req: req,
                    ReservedUsd: reservedUsd,
                    AtUtc: row.CreatedUtc,
                    BrokerOrderId: row.BrokerOrderId,
                    LastFeeUsd: 0m,
                    LastExecId: null);

                if (!_orders.TryAdd(po))
                {
                    skippedDuplicate++;
                    continue;
                }

                if (req.IsExit)
                {
                    lock (_sync)
                    {
                        _exitPending.Add(req.Symbol.Ticker);
                    }
                    restoredExit++;
                }
                else
                {
                    restoredEntry++;
                }

                restored++;
            }

            _log.Information(
                "[CRYPTO-RECOVERY] Pending restore exchange={Exchange}: restored={Restored} (entry={Entry}, exit={Exit}) skippedInvalid={Invalid} skippedDuplicate={Dup} pendingNow={Pending}",
                _exchangeName,
                restored,
                restoredEntry,
                restoredExit,
                skippedInvalid,
                skippedDuplicate,
                _orders.Snapshot().Length);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[CRYPTO-RECOVERY] Pending restore failed for exchange={Exchange}", _exchangeName);
        }
    }

    private async Task ReconcileRuntimePendingWithExchangeAsync(ICryptoTradingApi tradingApi, CancellationToken ct)
    {
        var pendingSnapshot = _orders.Snapshot();
        if (pendingSnapshot.Length == 0)
            return;

        IReadOnlyList<OpenOrderInfo> exchangeOpenOrders;
        try
        {
            exchangeOpenOrders = await tradingApi.GetOpenOrdersAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[CRYPTO-RECOVERY] Startup reconcile skipped - failed to fetch exchange open orders");
            return;
        }

        var exchangeName = tradingApi.ExchangeId.ToString();
        var openByBrokerId = new Dictionary<string, OpenOrderInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var exOrder in exchangeOpenOrders)
        {
            if (string.IsNullOrWhiteSpace(exOrder.ExchangeOrderId))
                continue;

            var brokerId = $"{exchangeName}:{exOrder.ExchangeOrderId}";
            openByBrokerId[brokerId] = exOrder;
        }

        var usedExchangeOrderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var linkedSubmitted = 0;
        var unresolvedSubmitted = 0;

        foreach (var po in pendingSnapshot.Where(p => string.IsNullOrWhiteSpace(p.BrokerOrderId)))
        {
            var candidates = exchangeOpenOrders
                .Where(o =>
                    !usedExchangeOrderIds.Contains(o.ExchangeOrderId) &&
                    string.Equals(o.Symbol.PublicSymbol, po.Req.Symbol.Ticker, StringComparison.OrdinalIgnoreCase) &&
                    ((po.Req.Side == OrderSide.Buy && o.Side == CryptoOrderSide.Buy) ||
                     (po.Req.Side == OrderSide.Sell && o.Side == CryptoOrderSide.Sell)) &&
                    Math.Abs(o.Quantity - po.Req.Quantity) <= Math.Max(0.000001m, po.Req.Quantity * 0.001m))
                .ToList();

            if (candidates.Count != 1)
            {
                unresolvedSubmitted++;
                continue;
            }

            var candidate = candidates[0];
            usedExchangeOrderIds.Add(candidate.ExchangeOrderId);

            var brokerOrderId = $"{exchangeName}:{candidate.ExchangeOrderId}";
            _orders.TrySetBrokerOrderId(po.CorrelationId, brokerOrderId);

            if (_orderRepo is not null)
            {
                try
                {
                    await _orderRepo.MarkSentAsync(
                        id: po.CorrelationId,
                        brokerOrderId: brokerOrderId,
                        sentUtc: DateTime.UtcNow,
                        ct: ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[CRYPTO-RECOVERY] Failed to MarkSent during startup link corr={Corr}", po.CorrelationId);
                }
            }

            linkedSubmitted++;
            _log.Information("[CRYPTO-RECOVERY] Linked submitted pending corr={Corr} -> brokerId={Bid}",
                po.CorrelationId, brokerOrderId);
        }

        var staleRemoved = 0;
        var stillOpen = 0;

        var pendingAfterLink = _orders.Snapshot();
        foreach (var po in pendingAfterLink)
        {
            if (string.IsNullOrWhiteSpace(po.BrokerOrderId))
                continue;

            if (openByBrokerId.ContainsKey(po.BrokerOrderId))
            {
                stillOpen++;
                continue;
            }

            if (tradingApi is Bitfinex.BitfinexTradingApi bfxApi &&
                TryExtractNativeExchangeOrderId(po.BrokerOrderId, out var nativeId))
            {
                try
                {
                    var final = await bfxApi.GetOrderAsync(nativeId, ct).ConfigureAwait(false);
                    if (final is not null)
                    {
                        var finalStatus = final.Status switch
                        {
                            CryptoOrderStatus.Filled => "Filled",
                            CryptoOrderStatus.Canceled => "Canceled",
                            CryptoOrderStatus.Rejected => "Rejected",
                            CryptoOrderStatus.PartiallyFilled => "PartiallyFilled",
                            _ => null
                        };

                        if (!string.IsNullOrWhiteSpace(finalStatus))
                        {
                            OnOrderUpdated(new OrderResult(
                                BrokerOrderId: po.BrokerOrderId!,
                                Status: finalStatus!,
                                FilledQuantity: final.FilledQuantity,
                                AverageFillPrice: final.Price > 0m ? final.Price : null,
                                CommissionAndFees: null,
                                Message: "startup-reconcile-final-status",
                                TimestampUtc: DateTime.UtcNow));
                            continue;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[CRYPTO-RECOVERY] Final status lookup failed for brokerId={Bid}", po.BrokerOrderId);
                }
            }

            if (_orders.TryRemove(po.CorrelationId, out var removed) && removed is not null)
            {
                if (removed.ReservedUsd > 0m)
                    _cashService.Unreserve(removed.ReservedUsd);

                if (removed.Req.CorrelationId?.StartsWith("exit-", StringComparison.OrdinalIgnoreCase) == true)
                {
                    lock (_sync)
                    {
                        _exitPending.Remove(removed.Req.Symbol.Ticker);
                    }
                }

                ClearCumFilled(removed.CorrelationId);
                ClearCancelRequested(removed.CorrelationId);
                staleRemoved++;

                _log.Warning(
                    "[CRYPTO-RECOVERY] Removed stale runtime pending corr={Corr} sym={Sym} brokerId={Bid} (not present in exchange open orders)",
                    removed.CorrelationId,
                    removed.Req.Symbol.Ticker,
                    removed.BrokerOrderId ?? "n/a");
            }
        }

        _log.Information(
            "[CRYPTO-RECOVERY] Startup reconcile exchange={Exchange}: linkedSubmitted={Linked} unresolvedSubmitted={Unresolved} staleRemoved={StaleRemoved} stillOpen={StillOpen} pendingNow={Pending}",
            exchangeName,
            linkedSubmitted,
            unresolvedSubmitted,
            staleRemoved,
            stillOpen,
            _orders.Snapshot().Length);
    }

    private bool TryBuildRecoveredOrderRequest(OpenBrokerOrder row, out OrderRequest req)
    {
        req = default!;

        if (string.IsNullOrWhiteSpace(row.Id) || string.IsNullOrWhiteSpace(row.Symbol))
            return false;

        var side = string.Equals(row.Side, "buy", StringComparison.OrdinalIgnoreCase)
            ? OrderSide.Buy
            : string.Equals(row.Side, "sell", StringComparison.OrdinalIgnoreCase)
                ? OrderSide.Sell
                : (OrderSide?)null;
        if (!side.HasValue)
            return false;

        var ot = (row.OrderType ?? "limit").Trim().ToLowerInvariant();
        ot = ot.Replace(" ", "_").Replace("-", "_");
        if (ot == "stp") ot = "stop";
        if (ot == "mkt") ot = "market";

        OrderType type;
        decimal? limit = null;
        decimal? stop = null;

        if (ot is "limit" or "exchange_limit")
        {
            if (!row.LimitPrice.HasValue || row.LimitPrice.Value <= 0m)
                return false;
            type = OrderType.Limit;
            limit = row.LimitPrice.Value;
        }
        else if (ot is "stop" or "exchange_stop" or "exchange_stop_limit")
        {
            var stopPx = row.StopPrice ?? row.LimitPrice;
            if (!stopPx.HasValue || stopPx.Value <= 0m)
                return false;
            type = OrderType.Stop;
            stop = stopPx.Value;
            limit = row.LimitPrice;
        }
        else if (ot is "market" or "exchange_market")
        {
            type = OrderType.Market;
        }
        else
        {
            return false;
        }

        var isExit = row.Id.StartsWith("exit-", StringComparison.OrdinalIgnoreCase);
        var tif = isExit ? TimeInForce.Gtc : TimeInForce.Day;
        var exchange = !string.IsNullOrWhiteSpace(row.Exchange) ? row.Exchange! : _exchangeName;

        req = new OrderRequest(
            symbol: new Symbol(
                Ticker: row.Symbol,
                Currency: ResolveQuoteCurrencyFromTicker(row.Symbol),
                Exchange: exchange),
            side: side.Value,
            type: type,
            quantity: row.Qty,
            limitPrice: limit,
            tif: tif,
            correlationId: row.Id,
            timestampUtc: row.CreatedUtc,
            ocoGroupId: null,
            ocoStopPrice: null,
            stopPrice: stop,
            isExit: isExit);

        return true;
    }

    private static string ResolveQuoteCurrencyFromTicker(string ticker)
    {
        if (string.IsNullOrWhiteSpace(ticker))
            return "USD";

        var t = ticker.Trim().ToUpperInvariant();
        if (t.EndsWith("USDT", StringComparison.Ordinal)) return "USDT";
        if (t.EndsWith("USDC", StringComparison.Ordinal)) return "USDC";
        if (t.EndsWith("USD", StringComparison.Ordinal)) return "USD";
        if (t.EndsWith("EUR", StringComparison.Ordinal)) return "EUR";
        if (t.EndsWith("GBP", StringComparison.Ordinal)) return "GBP";
        if (t.EndsWith("JPY", StringComparison.Ordinal)) return "JPY";
        return "USD";
    }

    private static bool TryExtractNativeExchangeOrderId(string brokerOrderId, out string nativeOrderId)
    {
        nativeOrderId = string.Empty;
        if (string.IsNullOrWhiteSpace(brokerOrderId))
            return false;

        var sep = brokerOrderId.IndexOf(':');
        if (sep <= 0 || sep >= brokerOrderId.Length - 1)
            return false;

        nativeOrderId = brokerOrderId[(sep + 1)..].Trim();
        return !string.IsNullOrWhiteSpace(nativeOrderId);
    }

    /// <summary>
    /// Sync-uje pozicije sa berze na osnovu balances.
    /// Za svaki asset sa balance > 0, kreira poziciju ako ne postoji.
    /// </summary>
    public async Task SyncPositionsFromBalancesAsync(
        ICryptoTradingApi tradingApi,
        ICryptoSymbolMetadataProvider symbolProvider,
        CancellationToken ct,
        bool isStartupRetry = false)
    {
        if (tradingApi == null || symbolProvider == null)
            return;

        // Sačuvaj tradingApi i symbolProvider za heartbeat
        _tradingApi = tradingApi;
        _symbolProvider = symbolProvider;

        try
        {
            _log.Information("[CRYPTO-SYNC] Starting position sync from exchange balances");

            var balances = await tradingApi.GetBalancesAsync(ct).ConfigureAwait(false);
            if (balances.Count == 0)
            {
                _log.Information("[CRYPTO-SYNC] No balances found on exchange");
                return;
            }

            _log.Information("[CRYPTO-SYNC] Found {Count} assets with balance", balances.Count);

            // Runtime pending sanity protiv OPEN ordera sa berze (posle restarta).
            await ReconcileRuntimePendingWithExchangeAsync(tradingApi, ct).ConfigureAwait(false);

            // U real mode-u, PRVO sinhronizuj cash stanje sa Bitfinex-om
            // Ovo mora biti PRE nego što se pozicije sinhronizuju, jer ApplyFillCore smanjuje Free cash
            if (_isRealMode && _cashService != null)
            {
                try
                {
                    await SyncCashFromBalancesAsync(balances, symbolProvider, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[CRYPTO-SYNC] Failed to sync cash from balances");
                }
            }

            // Grupiši balance-e po asset-u i nađi odgovarajući simbol
            var skippedForMissingQuote = 0;

            foreach (var balance in balances)
            {
                var totalQty = balance.Total;
                if (totalQty <= 0m)
                    continue;

                // Pokušaj da nađeš simbol koji koristi ovaj asset kao base
                // Npr. ETH -> ETHUSDT, SOL -> SOLUSDT
                var matchingSymbols = symbolProvider.GetAllSymbols()
                    .Where(s => s.ExchangeId == tradingApi.ExchangeId &&
                               s.BaseAsset.Equals(balance.Asset, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matchingSymbols.Count == 0)
                {
                    _log.Debug("[CRYPTO-SYNC] No matching symbol found for asset {Asset}", balance.Asset);
                    continue;
                }

                // Koristi prvi enabled simbol (obično je samo jedan)
                var cryptoSymbol = matchingSymbols.First();
                var ticker = cryptoSymbol.PublicSymbol; // npr. "ETHUSDT"

                // Proveri da li pozicija već postoji u PositionBook
                var existingPos = _positionBook.Get(ticker);
                if (existingPos != null && existingPos.Quantity > 0m)
                {
                    _log.Debug("[CRYPTO-SYNC] Position already exists for {Ticker}: qty={Qty}", ticker, existingPos.Quantity);
                    continue;
                }

                // Proveri da li postoji swing position u bazi sa entry price
                decimal? entryPrice = null;
                if (_swingPosRepo != null)
                {
                    try
                    {
                        var swingPos = await _swingPosRepo.GetBySymbolAsync(ticker, _exchangeName, ct).ConfigureAwait(false);
                        if (swingPos != null && swingPos.IsOpen)
                        {
                            entryPrice = swingPos.EntryPrice;
                            _log.Information("[CRYPTO-SYNC] Found swing position in DB for {Ticker}: entry={Entry}",
                                ticker, entryPrice);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(ex, "[CRYPTO-SYNC] Failed to check swing position for {Ticker}", ticker);
                    }
                }

                // Ako nema entry price iz baze, koristi trenutnu cenu iz last quotes
                if (!entryPrice.HasValue)
                {
                    if (_lastQuotes.TryGetValue(ticker, out var quote) && quote.Last.HasValue)
                    {
                        entryPrice = quote.Last.Value;
                        _log.Information("[CRYPTO-SYNC] Using current price as entry for {Ticker}: {Price}", ticker, entryPrice);
                    }
                    else
                    {
                        // Market data još nije stigla - preskoči za sada
                        _log.Information("[CRYPTO-SYNC] Skipping {Ticker}: no entry price in DB and market data not yet available qty={Qty}", ticker, totalQty);
                        skippedForMissingQuote++;
                        continue;
                    }
                }

                // Kreiraj synthetic fill event da se pozicija kreira u PositionBook
                var engineSymbol = new Core.Trading.Symbol(
                    Ticker: ticker,
                    Currency: cryptoSymbol.QuoteAsset,
                    Exchange: _exchangeName);

                // Kreiraj synthetic OrderRequest za BUY fill
                var syntheticReq = new Core.Orders.OrderRequest(
                    symbol: engineSymbol,
                    side: Core.Trading.OrderSide.Buy,
                    type: Core.Trading.OrderType.Limit,
                    quantity: totalQty,
                    limitPrice: entryPrice.Value,
                    tif: Core.Trading.TimeInForce.Gtc,
                    correlationId: $"sync-{ticker}-{DateTime.UtcNow:yyyyMMddHHmmss}",
                    timestampUtc: DateTime.UtcNow,
                    ocoGroupId: null,
                    stopPrice: null,
                    isExit: false);

                // Primeni fill direktno (bez order-a u pending store). Samo ažurira PositionBook + cash; nikad ne šalje naloge.
                if (_isRealMode)
                {
                    // Samo ApplyFillCore – on jednom ažurira poziciju (ApplyBuyFillCrypto unutra).
                    // Ne pozivati ApplyBuyFillCrypto ovde, inače pozicija bi bila 2× (npr. 0.13+0.13=0.26).
                    ApplyFillCore(syntheticReq, entryPrice.Value, DateTime.UtcNow, isPaper: false);
                    
                    // Vrati Free cash na realno stanje sa Bitfinex-a (USDT prioritet)
                    var ustBalance = FindUsdtBalance(balances);
                    
                    if (ustBalance != null)
                    {
                        var cashAfterFill = await _cashService.GetCashStateAsync().ConfigureAwait(false);
                        var freeDiff = ustBalance.Free - cashAfterFill.Free;
                        if (Math.Abs(freeDiff) > 0.01m)
                        {
                            if (freeDiff > 0m)
                            {
                                _cashService.MarkFree(freeDiff);
                            }
                            _log.Information(
                                "[CRYPTO-SYNC] Restored Free cash to real balance: {RealFree:F2} (was {LocalFree:F2})",
                                ustBalance.Free, cashAfterFill.Free);
                        }
                    }
                }
                else
                {
                    // U paper mode-u, koristi normalan ApplyFillCore
                    ApplyFillCore(syntheticReq, entryPrice.Value, DateTime.UtcNow, isPaper: false);
                }

                _log.Information("[CRYPTO-SYNC] Synced position for {Ticker}: qty={Qty} entry={Entry} (no orders placed – sync only)",
                    ticker, totalQty, entryPrice.Value);
            }

            if (skippedForMissingQuote > 0)
            {
                _startupPositionSyncPending = true;
                if (!isStartupRetry)
                {
                    Interlocked.Exchange(ref _startupPositionSyncRetryCount, 0);
                }

                _log.Information(
                    "[CRYPTO-SYNC] Partial sync: {Skipped} symbol(s) skipped due to missing quote. Startup auto-retry is enabled.",
                    skippedForMissingQuote);
            }
            else
            {
                if (_startupPositionSyncPending)
                {
                    _log.Information("[CRYPTO-SYNC] Startup position sync completed after retry.");
                }

                _startupPositionSyncPending = false;
                Interlocked.Exchange(ref _startupPositionSyncRetryCount, 0);
            }

            _log.Information("[CRYPTO-SYNC] Position sync completed");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[CRYPTO-SYNC] Failed to sync positions from balances");
        }
    }

    /// <summary>
    /// Sinhronizuje cash stanje sa Bitfinex-om na osnovu realnih balansa i pozicija.
    /// U real mode-u, cash stanje treba da odgovara realnom stanju na Bitfinex-u.
    /// </summary>
    private async Task SyncCashFromBalancesAsync(
        IReadOnlyList<BalanceInfo> balances,
        ICryptoSymbolMetadataProvider symbolProvider,
        CancellationToken ct)
    {
        if (!_isRealMode || _cashService == null)
            return;

        try
        {
            // Nađi USDT balance (podržava i UST i USDT raw ticker)
            var ustBalance = FindUsdtBalance(balances);

            if (ustBalance == null)
            {
                _log.Warning("[CRYPTO-CASH-SYNC] No USDT/UST balance found");
                return;
            }

            // Izračunaj vrednost pozicija u USD
            decimal positionsValue = 0m;
            var positions = _positionBook.Snapshot();
            foreach (var pos in positions.Where(p => p.Quantity > 0m))
            {
                // Pokušaj da nađeš trenutnu cenu iz quotes
                if (_lastQuotes.TryGetValue(pos.Symbol, out var quote) && quote.Last.HasValue)
                {
                    positionsValue += pos.Quantity * quote.Last.Value;
                }
                else
                {
                    // Ako nema quote, koristi entry price
                    positionsValue += pos.Quantity * pos.AveragePrice;
                }
            }

            // Realno cash stanje = USD balance (Free)
            var realFree = ustBalance.Free;
            
            // Lokalno cash stanje
            var localCash = await _cashService.GetCashStateAsync().ConfigureAwait(false);
            var localFree = localCash.Free;
            var localInPos = localCash.InPositions;

            // Ako postoji razlika, ažuriraj lokalno stanje
            var freeDiff = realFree - localFree;
            var inPosDiff = positionsValue - localInPos;

            if (Math.Abs(freeDiff) > 0.01m || Math.Abs(inPosDiff) > 0.01m)
            {
                _log.Information(
                    "[CRYPTO-CASH-SYNC] Syncing cash state. Local: Free={LocalFree:F2} InPos={LocalInPos:F2} | Real: Free={RealFree:F2} InPos={RealInPos:F2}",
                    localFree, localInPos, realFree, positionsValue);

                // U REAL modu želimo da lokalno stanje UVEK prati realno sa berze,
                // i kad je veće i kad je manje.
                if (Math.Abs(freeDiff) > 0.01m)
                {
                    _cashService.MarkFree(freeDiff); // freeDiff može biti + ili -

                    _log.Information(
                        "[CRYPTO-CASH-SYNC] Adjusted Free cash by {Diff:+0.00;-0.00} → now {NewFree:F2} (real {RealFree:F2})",
                        freeDiff,
                        localFree + freeDiff,
                        realFree);
                }

                // InPositions će se automatski ažurirati kada se pozicije sinhronizuju
                // jer ApplyFillCore poziva OnBuyFilled koja ažurira InPositions
            }
            else
            {
                _log.Debug("[CRYPTO-CASH-SYNC] Cash state is already synchronized");
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[CRYPTO-CASH-SYNC] Failed to sync cash from balances");
        }
    }

    public void Dispose()
    {
        _cashRefreshCts.Cancel();

        if (_feed != null)
        {
            _feed.MarketQuoteUpdated -= _strategy.OnQuote;
            _feed.MarketQuoteUpdated -= EvaluateCryptoExitsOnQuote;
            if (_paperForwarder != null)
                _feed.MarketQuoteUpdated -= _paperForwarder;
            _feed.MarketQuoteUpdated -= OnQuoteCached;
            _feed.MarketQuoteUpdated -= UpdateAtrOnQuote;
            if (_greenGrindRegime is not null)
            {
                _feed.MarketQuoteUpdated -= OnQuoteGreenGrind;
                _greenGrindRegime.StateChanged -= OnGreenGrindStateChanged;
            }
        }

        if (_strategy != null)
        {
            _strategy.TradeSignalGenerated -= OnTradeSignal;
        }

        if (_paperSim != null)
        {
            _paperSim.Filled -= OnPaperFilled;
        }

        if (_orderService != null)
        {
            _orderService.OrderUpdated -= OnOrderUpdated;
        }
    }

    // Helper metode za swing logiku (crypto verzija, bez RTH provera)
    private bool IsSwingMode()
    {
        return _swingConfig.Mode == CryptoSwingMode.Swing;
    }

    private (SwingExitReason? ExitReason, bool AutoExit) InferSwingExitReason(OrderRequest req)
    {
        var corr = req.CorrelationId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(corr))
            return (null, false);

        // 1) OCO TP / SL – nisu AUTO-EXIT, nego normalni SL/TP
        if (corr.StartsWith("exit-tp-", StringComparison.OrdinalIgnoreCase))
            return (SwingExitReason.TakeProfit, false);

        if (corr.StartsWith("exit-sl-", StringComparison.OrdinalIgnoreCase))
            return (SwingExitReason.StopLoss, false);

        if (corr.StartsWith("exit-time-", StringComparison.OrdinalIgnoreCase))
            return (SwingExitReason.TimeExit, false);

        if (corr.StartsWith("exit-trail-", StringComparison.OrdinalIgnoreCase))
            return (SwingExitReason.TrailExit, false);

        // 2) SWING auto-exit (REAL + AutoExitReal=true)
        if (corr.StartsWith("exit-swing-max-weekend-", StringComparison.OrdinalIgnoreCase))
            return (SwingExitReason.SwingMaxDays, true);

        if (corr.StartsWith("exit-swing-max-", StringComparison.OrdinalIgnoreCase))
            return (SwingExitReason.SwingMaxDays, true);

        if (corr.StartsWith("exit-swing-weekend-", StringComparison.OrdinalIgnoreCase))
            return (SwingExitReason.SwingWeekend, true);

        if (corr.StartsWith("exit-swing-", StringComparison.OrdinalIgnoreCase))
            return (SwingExitReason.SwingMaxDays, true);

        // 3) Svi ostali exit-* tretiramo kao manual/other
        if (corr.StartsWith("exit-", StringComparison.OrdinalIgnoreCase))
            return (SwingExitReason.Manual, false);

        // 4) ako uopšte nije exit-* (ne bi trebalo za close), nemamo posebnu semantiku
        return (null, false);
    }

    // =========================
    //  HEARTBEAT
    // =========================
    public void StartHeartbeat(TimeSpan period, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // T+1 dnevni roll pre svakog snapshot-a
                    _cashService.DailyRoll(DateTime.UtcNow);

                    var cash = _lastCash;

                    // Uzmi stvarne balances sa exchange-a i prikaži pozicije direktno iz balances
                    decimal? realFreeUsd = null;
                    decimal? realInPosUsd = 0m;
                    var positionsFromBalances = new List<(string Symbol, decimal Quantity)>();
                    
                    if (_tradingApi != null && _isRealMode && _symbolProvider != null)
                    {
                        try
                        {
                            var balances = await _tradingApi.GetBalancesAsync(ct).ConfigureAwait(false);
                            
                            // Nađi USDT balance (podržava i UST i USDT raw ticker)
                            var ustBalance = FindUsdtBalance(balances);
                            
                            if (ustBalance != null)
                            {
                                _log.Debug("[SYNC-BALANCE] Found USDT balance: raw={Raw} free={Free}", ustBalance.Asset, ustBalance.Free);
                                realFreeUsd = ustBalance.Free;
                            }
                            
                            // Uzmi pozicije direktno iz balances (samo enabled simbole)
                            var enabledSymbols = _symbolProvider.GetAllSymbols()
                                .Where(s => s.ExchangeId == _tradingApi.ExchangeId)
                                .ToList();
                            
                            foreach (var balance in balances)
                            {
                                // Preskoči UST (to je free cash)
                                if (balance.Asset.Equals("UST", StringComparison.OrdinalIgnoreCase))
                                    continue;
                                
                                var totalQty = balance.Total;
                                if (totalQty <= 0m)
                                    continue;
                                
                                // Nađi enabled simbol koji koristi ovaj asset kao base
                                var matchingSymbol = enabledSymbols.FirstOrDefault(s => 
                                    s.BaseAsset.Equals(balance.Asset, StringComparison.OrdinalIgnoreCase));
                                
                                if (matchingSymbol != null)
                                {
                                    positionsFromBalances.Add((matchingSymbol.PublicSymbol, totalQty));
                                    
                                    // Izračunaj vrednost pozicije u USD (koristi trenutnu cenu ako je dostupna)
                                    decimal positionValue = 0m;
                                    lock (_sync)
                                    {
                                        if (_lastQuotes.TryGetValue(matchingSymbol.PublicSymbol, out var quote) && 
                                            quote.Last.HasValue && quote.Last.Value > 0m)
                                        {
                                            positionValue = totalQty * quote.Last.Value;
                                        }
                                    }
                                    realInPosUsd += positionValue;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.Debug(ex, "[CRYPTO-HB] Failed to get real balances from exchange");
                        }
                    }

                    // Koristi pozicije iz balances ako su dostupne
                    var posCount = positionsFromBalances.Count;
                    var posText = posCount == 0
                        ? "[none]"
                        : string.Join(", ", positionsFromBalances.Select(p => $"{p.Symbol}:{p.Quantity:F4}"));

                    var (equityApprox, inPosApprox) = GetApproxEquity();
                    var swingMode = _swingConfig?.Mode.ToString() ?? "Unknown";

                    // Koristi stvarne balances sa exchange-a
                    var displayFree = realFreeUsd ?? cash.Free;
                    var displayInPos = realInPosUsd > 0m ? realInPosUsd : inPosApprox;
                    var displayEquity = displayFree + displayInPos;
                    
                    _log.Information(
                        "[CRYPTO-HB] {Exchange} cash Free={Free:F2} Settling={Sett:F2} InPos≈{InPos:F2} equity≈{Eq:F2} positions={Cnt} {Pos} Reserved={Reserved:F2}, swingMode={SwingMode}",
                        string.IsNullOrWhiteSpace(_exchangeName) ? "CRYPTO" : _exchangeName.ToUpperInvariant(),
                        displayFree,
                        cash.Settling,
                        displayInPos,
                        displayEquity,
                        posCount,
                        posText,
                        cash.Reserved,
                        swingMode);

                    // --- SWING monitoring (crypto verzija, bez weekend provera) ---
                    if (_swingConfig is not null && IsSwingMode())
                    {
                        var nowUtc = DateTime.UtcNow;

                        try
                        {
                            // 1) MaxHoldingDays warning
                            if (_swingConfig.MaxHoldingDays > 0)
                            {
                                var maxAge = TimeSpan.FromDays(_swingConfig.MaxHoldingDays);
                                List<(string Sym, TimeSpan Age)> tooOld = new();

                                lock (_sync)
                                {
                                    foreach (var kvp in _posRuntime)
                                    {
                                        var age = nowUtc - kvp.Value.EntryUtc;
                                        if (age >= maxAge)
                                            tooOld.Add((kvp.Key, age));
                                    }
                                }

                                foreach (var item in tooOld)
                                {
                                    _log.Warning(
                                        "[CRYPTO-SWING-AGE] {Sym} holding {Days:F1} days >= MaxHoldingDays={MaxDays}",
                                        item.Sym,
                                        item.Age.TotalDays,
                                        _swingConfig.MaxHoldingDays);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.Warning(ex, "[CRYPTO-SWING] heartbeat swing checks failed");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[CRYPTO-HB] failed");
                }

                try
                {
                    await Task.Delay(period, ct).ConfigureAwait(false);
                }
                catch
                {
                    // ignored
                }
            }
        }, ct);
    }

    // =========================
    //  PENDING EXPIRY
    // =========================
    public void StartPendingExpiryWatcher(TimeSpan sweepEvery, TimeSpan ttl, CancellationToken ct)
    {
        var exitUnsentTtl = TimeSpan.FromSeconds(30);

        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(sweepEvery, ct).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }

                try
                {
                    var now = DateTime.UtcNow;
                    var pendings = _orders.Snapshot();

                    foreach (var po in pendings)
                    {
                        var isExit =
                            po.Req.IsExit ||
                            (po.CorrelationId?.StartsWith("exit-", StringComparison.OrdinalIgnoreCase) ?? false);

                        var age = now - po.AtUtc;

                        // EXIT: ako je poslato brokeru (ima real brokerId, ne "SOFT"), NIKAD ne TTL-cancel
                        var hasRealBrokerId = !string.IsNullOrEmpty(po.BrokerOrderId) && 
                            !po.BrokerOrderId.Equals("SOFT", StringComparison.OrdinalIgnoreCase);
                        if (isExit && _orderService is not null && hasRealBrokerId)
                            continue;

                        // EXIT: ako NIJE poslato brokeru (nema brokerId), očisti brzo
                        if (isExit && !hasRealBrokerId && age >= exitUnsentTtl)
                        {
                            if (_orders.TryRemove(po.CorrelationId, out var removed) && removed is not null)
                            {
                                if (removed.ReservedUsd > 0m)
                                    _cashService.Unreserve(removed.ReservedUsd);

                                lock (_sync) _exitPending.Remove(removed.Req.Symbol.Ticker);
                                ClearCumFilled(removed.CorrelationId);

                                _log.Warning("[CRYPTO-PENDING-EXPIRE] removed EXIT local-only corr={Corr} sym={Sym} age={Age}s",
                                    removed.CorrelationId, removed.Req.Symbol.Ticker, age.TotalSeconds);

                                if (_orderRepo is not null)
                                    _ = _orderRepo.MarkExpiredAsync(removed.Req.CorrelationId, now, CancellationToken.None);
                            }
                            continue;
                        }

                        // ENTRY / ostalo: standard TTL
                        if (age < ttl)
                            continue;

                        if (_orderService is not null && !string.IsNullOrEmpty(po.BrokerOrderId))
                        {
                            FireAndForgetCancel(po.BrokerOrderId, po.Req.Symbol.Ticker);
                        }

                        // Ukloni iz pending store-a
                        if (!string.IsNullOrEmpty(po.CorrelationId) && _orders.TryRemove(po.CorrelationId, out var expired) && expired is not null)
                        {
                            if (expired.ReservedUsd > 0m)
                                _cashService.Unreserve(expired.ReservedUsd);

                            _log.Warning("[CRYPTO-PENDING-EXPIRE] removed corr={Corr} sym={Sym} age={Age}s",
                                expired.CorrelationId, expired.Req.Symbol.Ticker, age.TotalSeconds);

                            if (_orderRepo is not null)
                                _ = _orderRepo.MarkExpiredAsync(expired.Req.CorrelationId, now, CancellationToken.None);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[CRYPTO-PENDING-EXPIRE] sweep failed");
                }
            }
        }, ct);
    }

    private void FireAndForgetCancel(string brokerOrderId, string symbol)
    {
        if (_orderService is null)
            return;

        // Dedupe: ne šalji dupli cancel za isti brokerOrderId u kratkom vremenu
        var now = DateTime.UtcNow;
        lock (_sync)
        {
            if (_cancelRequestedAt.TryGetValue(brokerOrderId, out var last) && (now - last) < CancelDedupeWindow)
            {
                _log.Debug("[CRYPTO-CANCEL] Dedupe skip brokerId={Bid} sym={Sym} lastRequest={Last}s ago",
                    brokerOrderId, symbol, (now - last).TotalSeconds);
                return;
            }
            _cancelRequestedAt[brokerOrderId] = now;
        }

        _log.Information("[CRYPTO-CANCEL] Fire-and-forget cancel brokerId={Bid} sym={Sym}", brokerOrderId, symbol);

        _ = Task.Run(async () =>
        {
            try
            {
                await _orderService.CancelAsync(brokerOrderId).ConfigureAwait(false);
                _log.Information("[CRYPTO-CANCEL] OK brokerId={Bid}", brokerOrderId);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[CRYPTO-CANCEL] Failed brokerId={Bid}", brokerOrderId);
            }
            finally
            {
                // Clear dedupe nakon izvršenja (ili failure) da omogućimo retry posle
                lock (_sync)
                {
                    _cancelRequestedAt.Remove(brokerOrderId);
                }
            }
        });
    }

    /// <summary>
    /// Normalizuje currency ticker za konzistentnu pretragu.
    /// Bitfinex koristi "UST" za USDT na nekim endpoint-ima.
    /// </summary>
    private static string NormalizeCurrency(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return raw;

        var trimmed = raw.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return trimmed;

        // Bitfinex koristi "UST" za Tether (USDT) na nekim endpoint-ima
        if (trimmed.Equals("UST", StringComparison.OrdinalIgnoreCase))
            return "USDT";

        return trimmed;
    }

    /// <summary>
    /// Traži USDT balance u listi (podržava i UST i USDT raw ticker).
    /// </summary>
    private static BalanceInfo? FindUsdtBalance(IReadOnlyList<BalanceInfo> balances)
    {
        return balances.FirstOrDefault(b =>
            NormalizeCurrency(b.Asset).Equals("USDT", StringComparison.OrdinalIgnoreCase));
    }
}

// Helper classes (preuzeto iz TradingOrchestrator)
internal sealed class AtrState
{
    public decimal? Prev { get; set; }
    public Queue<decimal> Tr { get; } = new();
    public decimal? Atr { get; set; }
}

internal sealed class PositionRuntimeState
{
    public DateTime EntryUtc { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal BestPrice { get; set; }
    public bool IsExternal { get; set; }
    public string? RegimeAtEntry { get; set; }
    public string? SymbolBaseline { get; set; }
    public decimal? AtrAtEntry { get; set; }
    public DateTime? LastClosePrice { get; set; }
    public DateTime? LastCloseUtc { get; set; }
    public bool GapExitExecuted { get; set; }
    public DateTime? LastTpSlLogUtc { get; set; }
}
