#nullable enable

using System.Text.Json;
using Denis.TradingEngine.Data.Repositories;
using Denis.TradingEngine.Data.Repositories.Funding;
using Denis.TradingEngine.Exchange.Crypto.Bitfinex.Funding.Api;
using Denis.TradingEngine.Exchange.Crypto.Bitfinex.Funding;
using Denis.TradingEngine.Exchange.Crypto.Bitfinex.Funding.Config;
using Denis.TradingEngine.Exchange.Crypto.Bitfinex.Funding.Models;
using Denis.TradingEngine.Exchange.Crypto.Bitfinex.Funding.Stream;
using Serilog;

namespace Denis.TradingEngine.Exchange.Crypto.Bitfinex.Funding.Runtime;

public sealed class BitfinexFundingManager : IAsyncDisposable
{
    private readonly BitfinexFundingOptions _options;
    private readonly IBitfinexFundingApi _api;
    private readonly BitfinexFundingPrivateWebSocketFeed? _privateFeed;
    private readonly BitfinexFundingRepository? _fundingRepo;
    private readonly CryptoSnapshotRepository? _snapshotRepo;
    private readonly ILogger _log;
    private readonly object _sync = new();
    private readonly object _offersSync = new();
    private readonly object _walletsSync = new();
    private readonly object _shadowSync = new();
    private readonly object _livePlacementSync = new();
    private readonly Dictionary<string, FundingOfferInfo> _activeOffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _managedOfferIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FundingWalletBalance> _latestWalletBalances = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FundingShadowActionSession> _shadowActionSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FundingLivePlacementWaitState> _livePlacementWaitStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FundingManagedFallbackCarryForwardState> _managedFallbackCarryForwardStates = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _linkedCts;
    private Task? _loopTask;
    private Task? _feedTask;
    private DateTime _lastOfferStateSyncUtc;
    private DateTime _lastLifecycleSyncUtc;
    private DateTime _lastPerformanceReportUtc;
    private bool _hasOfferSnapshot;

    public BitfinexFundingManager(
        BitfinexFundingOptions options,
        IBitfinexFundingApi api,
        BitfinexFundingPrivateWebSocketFeed? privateFeed,
        BitfinexFundingRepository? fundingRepo,
        CryptoSnapshotRepository? snapshotRepo,
        ILogger log)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _privateFeed = privateFeed;
        _fundingRepo = fundingRepo;
        _snapshotRepo = snapshotRepo;
        _log = log ?? Log.ForContext<BitfinexFundingManager>();

        if (_privateFeed is not null)
        {
            _privateFeed.OfferSnapshot += OnOfferSnapshot;
            _privateFeed.OfferNew += OnOfferNew;
            _privateFeed.OfferUpdate += OnOfferUpdate;
            _privateFeed.OfferClose += OnOfferClose;
            _privateFeed.WalletSnapshot += OnWalletSnapshot;
            _privateFeed.WalletUpdate += OnWalletUpdate;
            _privateFeed.Notification += OnNotification;
        }
    }

    public void Start(CancellationToken appCt)
    {
        if (!_options.Enabled)
            return;

        lock (_sync)
        {
            if (_loopTask is not null)
                return;

            _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
            _loopTask = Task.Run(() => RunLoopAsync(_linkedCts.Token), CancellationToken.None);
            if (_privateFeed is not null && _options.UsePrivateWebSocket)
            {
                _feedTask = Task.Run(() => RunFeedLoopAsync(_linkedCts.Token), CancellationToken.None);
            }
        }

        _log.Information(
            "[BFX-FUND] Funding manager started enabled={Enabled} dryRun={DryRun} symbols={Symbols} intervalMin={Interval} restSyncSec={RestSyncSec} ws={UseWs} allowExternal={AllowExternal}",
            _options.Enabled,
            _options.DryRun,
            string.Join(",", GetPreferredSymbols()),
            _options.RepriceIntervalMinutes,
            _options.RestOfferSyncIntervalSeconds,
            _options.UsePrivateWebSocket,
            _options.AllowManagingExternalOffers);

        if (_options.DryRun)
        {
            _log.Information("[BFX-FUND] Dry-run mode is active. Funding writes are disabled; decisions and snapshots will still be produced.");
        }

        LogEffectiveSymbolSettings();
    }

    private async Task RunFeedLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _privateFeed!.RunAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[BFX-FUND] Private WS feed failed.");
            }

            if (ct.IsCancellationRequested)
                break;

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        if (_options.StartupDelaySeconds > 0)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.StartupDelaySeconds), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        await RecoverManagedOffersAsync(ct).ConfigureAwait(false);

        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.RepriceIntervalMinutes));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[BFX-FUND] Funding cycle failed. Module remains isolated from spot flow.");
            }

            try
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        var preferredSymbols = GetPreferredSymbols();
        if (preferredSymbols.Count == 0)
        {
            _log.Warning("[BFX-FUND] No preferred funding symbols configured. Skipping cycle.");
            return;
        }

        var walletTask = _api.GetWalletBalancesAsync(ct);
        var tickerTask = _api.GetFundingTickerSnapshotsAsync(preferredSymbols, ct);
        Task<IReadOnlyList<FundingOfferInfo>>? activeOffersTask = null;
        if (ShouldRefreshActiveOffersFromRest())
        {
            activeOffersTask = _api.GetActiveOffersAsync(preferredSymbols, ct);
        }

        if (activeOffersTask is not null)
        {
            await Task.WhenAll(walletTask, tickerTask, activeOffersTask).ConfigureAwait(false);
        }
        else
        {
            await Task.WhenAll(walletTask, tickerTask).ConfigureAwait(false);
        }

        var wallets = walletTask.Result;
        var tickers = tickerTask.Result;
        IReadOnlyList<FundingOfferInfo> activeOffers;
        if (activeOffersTask is not null)
        {
            activeOffers = activeOffersTask.Result;
            MergeOfferSnapshotFromRest(activeOffers, preferredSymbols);
        }
        else
        {
            activeOffers = GetActiveOffersSnapshot();
        }

        var decisions = new List<FundingDecision>(preferredSymbols.Count);
        var actionResults = new List<FundingOfferActionResult>(preferredSymbols.Count);

        foreach (var symbol in preferredSymbols)
        {
            var offersForSymbol = activeOffers
                .Where(o => string.Equals(o.Symbol, symbol, StringComparison.OrdinalIgnoreCase) && o.IsActive)
                .OrderByDescending(o => o.UpdatedUtc ?? o.CreatedUtc ?? DateTime.MinValue)
                .ToArray();

            var candidate = TryBuildPlacementCandidate(symbol, wallets, tickers, out var skipDecision);
            FundingDecision decision;
            if (candidate is null)
            {
                ClearLivePlacementWaitState(symbol);
                decision = skipDecision!;
            }
            else
            {
                decision = BuildExecutionDecision(candidate, offersForSymbol);
            }

            decisions.Add(decision);
            LogDecision(decision);

            var actionResult = await ExecuteDecisionAsync(decision, candidate, offersForSymbol, ct).ConfigureAwait(false);
            if (actionResult is not null)
            {
                actionResults.Add(actionResult);
                LogActionResult(actionResult);
            }
        }

        LogCycleSummary(wallets, tickers, activeOffers, decisions);

        var shadowPlans = BuildShadowPlans(preferredSymbols, wallets, tickers, activeOffers);
        LogShadowPlans(shadowPlans);
        var shadowActions = BuildShadowActions(shadowPlans, activeOffers);
        LogShadowActions(shadowActions);
        var shadowSessions = BuildShadowActionSessions(shadowActions, activeOffers);
        LogShadowActionSessions(shadowSessions);

        FundingLifecycleSyncResult lifecycleSync = FundingLifecycleSyncResult.Empty;
        if (ShouldRefreshLifecycleFromRest())
        {
            lifecycleSync = await SyncLifecycleStateAsync(preferredSymbols, ct).ConfigureAwait(false);
        }

        var runtimeHealth = BuildRuntimeHealthSnapshot(preferredSymbols, wallets, tickers, activeOffers, decisions, actionResults, shadowPlans, shadowActions, shadowSessions, lifecycleSync);
        await PersistCycleAsync(wallets, tickers, activeOffers, decisions, actionResults, shadowPlans, shadowActions, shadowSessions, lifecycleSync, runtimeHealth, ct).ConfigureAwait(false);
        await MaybeLogPerformanceReportAsync(preferredSymbols, ct).ConfigureAwait(false);
        await MaybeLogDecisionQualityReportAsync(preferredSymbols, ct).ConfigureAwait(false);
    }

    private FundingPlacementCandidate? TryBuildPlacementCandidate(
        string fundingSymbol,
        IReadOnlyList<FundingWalletBalance> wallets,
        IReadOnlyList<FundingTickerSnapshot> tickers,
        out FundingDecision? skipDecision)
    {
        var nowUtc = DateTime.UtcNow;
        var symbolSettings = ResolveSymbolSettings(fundingSymbol);
        var currency = FundingSymbolToCurrency(fundingSymbol);
        var wallet = FindWalletBalance(wallets, currency);
        var walletType = _options.UseFundingWalletOnly ? "funding" : "any";

        if (!symbolSettings.Enabled)
        {
            skipDecision = new FundingDecision(
                Action: "skip_symbol_disabled",
                IsDryRun: _options.DryRun,
                IsActionable: false,
                Symbol: fundingSymbol,
                Currency: currency,
                WalletType: walletType,
                AvailableBalance: 0m,
                LendableBalance: 0m,
                ProposedAmount: null,
                ProposedRate: null,
                ProposedPeriodDays: null,
                Reason: "Funding symbol is disabled by symbol profile.",
                TimestampUtc: nowUtc
            );

            return null;
        }

        if (wallet is null)
        {
            skipDecision = new FundingDecision(
                Action: "skip_no_wallet_balance",
                IsDryRun: _options.DryRun,
                IsActionable: false,
                Symbol: fundingSymbol,
                Currency: currency,
                WalletType: walletType,
                AvailableBalance: 0m,
                LendableBalance: 0m,
                ProposedAmount: null,
                ProposedRate: null,
                ProposedPeriodDays: null,
                Reason: "No matching wallet balance found for funding currency.",
                TimestampUtc: nowUtc
            );

            return null;
        }

        if (!_options.DryRun &&
            !IsFundingWalletType(wallet.WalletType))
        {
            skipDecision = new FundingDecision(
                Action: "skip_live_requires_funding_wallet",
                IsDryRun: _options.DryRun,
                IsActionable: false,
                Symbol: fundingSymbol,
                Currency: currency,
                WalletType: wallet.WalletType,
                AvailableBalance: wallet.Available,
                LendableBalance: 0m,
                ProposedAmount: null,
                ProposedRate: null,
                ProposedPeriodDays: null,
                Reason: "Live funding offer placement requires balance in the Bitfinex funding wallet.",
                TimestampUtc: nowUtc
            );

            return null;
        }

        var ticker = tickers.FirstOrDefault(t => string.Equals(t.Symbol, fundingSymbol, StringComparison.OrdinalIgnoreCase));
        if (ticker is null)
        {
            skipDecision = new FundingDecision(
                Action: "skip_no_market_snapshot",
                IsDryRun: _options.DryRun,
                IsActionable: false,
                Symbol: fundingSymbol,
                Currency: currency,
                WalletType: wallet.WalletType,
                AvailableBalance: wallet.Available,
                LendableBalance: 0m,
                ProposedAmount: null,
                ProposedRate: null,
                ProposedPeriodDays: null,
                Reason: "No public funding ticker snapshot available.",
                TimestampUtc: nowUtc
            );

            return null;
        }

        var lendable = Math.Max(0m, wallet.Available - symbolSettings.ReserveAmount);
        if (lendable <= 0m)
        {
            skipDecision = new FundingDecision(
                Action: "skip_reserved_balance",
                IsDryRun: _options.DryRun,
                IsActionable: false,
                Symbol: fundingSymbol,
                Currency: currency,
                WalletType: wallet.WalletType,
                AvailableBalance: wallet.Available,
                LendableBalance: lendable,
                ProposedAmount: null,
                ProposedRate: null,
                ProposedPeriodDays: null,
                Reason: "Available balance is fully consumed by reserve threshold.",
                TimestampUtc: nowUtc
            );

            return null;
        }

        var proposedAmount = Math.Min(lendable, symbolSettings.MaxOfferAmount);
        if (proposedAmount < symbolSettings.MinOfferAmount)
        {
            skipDecision = new FundingDecision(
                Action: "skip_below_min_offer",
                IsDryRun: _options.DryRun,
                IsActionable: false,
                Symbol: fundingSymbol,
                Currency: currency,
                WalletType: wallet.WalletType,
                AvailableBalance: wallet.Available,
                LendableBalance: lendable,
                ProposedAmount: proposedAmount,
                ProposedRate: null,
                ProposedPeriodDays: null,
                Reason: "Lendable amount is below MinOfferAmount.",
                TimestampUtc: nowUtc
            );

            return null;
        }

        var (proposedRate, rateSelectionSummary) = SelectLiveRate(ticker, symbolSettings);
        var minPeriodDays = Math.Max(2, _options.MinPeriodDays);
        var maxPeriodDays = Math.Max(minPeriodDays, _options.MaxPeriodDays);
        var proposedPeriod = Math.Clamp(_options.DefaultPeriodDays, minPeriodDays, maxPeriodDays);

        var request = new FundingOfferRequest(
            Symbol: fundingSymbol,
            Amount: decimal.Round(proposedAmount, 8, MidpointRounding.ToZero),
            Rate: proposedRate,
            PeriodDays: proposedPeriod,
            OfferType: string.IsNullOrWhiteSpace(_options.OfferType)
                ? "LIMIT"
                : _options.OfferType.Trim().ToUpperInvariant(),
            Flags: _options.OfferFlags
        );

        skipDecision = null;

        return new FundingPlacementCandidate(
            Symbol: fundingSymbol,
            Currency: currency,
            WalletType: wallet.WalletType,
            AvailableBalance: wallet.Available,
            LendableBalance: lendable,
            Request: request,
            RateSelectionSummary: rateSelectionSummary,
            Ticker: ticker,
            SymbolSettings: symbolSettings,
            PauseNewOffers: symbolSettings.PauseNewOffers
        );
    }

    private FundingDecision BuildExecutionDecision(
        FundingPlacementCandidate candidate,
        IReadOnlyList<FundingOfferInfo> activeOffers)
    {
        var nowUtc = DateTime.UtcNow;
        var slotPlan = BuildLiveSlotPlan(candidate.SymbolSettings, candidate.LendableBalance, activeOffers.Count);

        if (candidate.PauseNewOffers)
        {
            ClearLivePlacementWaitState(candidate.Symbol);
            return new FundingDecision(
                Action: activeOffers.Count == 0
                    ? "skip_symbol_paused"
                    : "skip_symbol_paused_active_exists",
                IsDryRun: _options.DryRun,
                IsActionable: false,
                Symbol: candidate.Symbol,
                Currency: candidate.Currency,
                WalletType: candidate.WalletType,
                AvailableBalance: candidate.AvailableBalance,
                LendableBalance: candidate.LendableBalance,
                ProposedAmount: candidate.Request.Amount,
                ProposedRate: candidate.Request.Rate,
                ProposedPeriodDays: candidate.Request.PeriodDays,
                Reason: activeOffers.Count == 0
                    ? $"Funding symbol is paused by symbol profile. {candidate.RateSelectionSummary}"
                    : $"Funding symbol is paused by symbol profile while an active offer already exists. {candidate.RateSelectionSummary}",
                TimestampUtc: nowUtc
            );
        }

        if (activeOffers.Count > 0)
        {
            var externalOffers = activeOffers
                .Where(activeOffer => !(_options.AllowManagingExternalOffers || IsManagedOffer(activeOffer.OfferId)))
                .ToArray();

            if (externalOffers.Length > 0)
            {
                ClearLivePlacementWaitState(candidate.Symbol);
                return new FundingDecision(
                    Action: "skip_external_active_offer_exists",
                    IsDryRun: _options.DryRun,
                    IsActionable: false,
                    Symbol: candidate.Symbol,
                    Currency: candidate.Currency,
                    WalletType: candidate.WalletType,
                    AvailableBalance: candidate.AvailableBalance,
                    LendableBalance: candidate.LendableBalance,
                    ProposedAmount: candidate.Request.Amount,
                    ProposedRate: candidate.Request.Rate,
                    ProposedPeriodDays: candidate.Request.PeriodDays,
                    Reason: $"External active funding offer exists (offerId={externalOffers[0].OfferId}); manager will not modify it. {candidate.RateSelectionSummary}",
                    TimestampUtc: nowUtc
                );
            }

            if (activeOffers.Count >= slotPlan.TotalSlotsNow)
            {
                ClearLivePlacementWaitState(candidate.Symbol);
                return BuildManagedCapacityDecision(candidate, activeOffers, slotPlan, nowUtc);
            }

            return BuildAdditionalSlotPlacementDecision(candidate, activeOffers, slotPlan, nowUtc);
        }

        return BuildFreshPlacementDecision(candidate, activeOffers, slotPlan, nowUtc);
    }

    private FundingDecision BuildAdditionalSlotPlacementDecision(
        FundingPlacementCandidate candidate,
        IReadOnlyList<FundingOfferInfo> activeOffers,
        FundingLiveSlotPlan slotPlan,
        DateTime nowUtc)
    {
        ClearLivePlacementWaitState(candidate.Symbol);

        var slotRole = ResolveNextLiveSlotRole(candidate, activeOffers, slotPlan) ?? "Motor";
        var policy = ResolveLivePlacementPolicy(candidate, slotRole, slotPlan);
        var slotRequest = policy.PlaceImmediately
            ? policy.TargetRequest
            : policy.FallbackRequest;
        var slotSummary = policy.PlaceImmediately
            ? policy.TargetSummary
            : policy.FallbackSummary;
        var slotIndex = Math.Min(slotPlan.TotalSlotsNow, activeOffers.Count + 1);

        return new FundingDecision(
            Action: _options.DryRun ? "would_place_parallel_offer" : "place_parallel_offer",
            IsDryRun: _options.DryRun,
            IsActionable: true,
            Symbol: candidate.Symbol,
            Currency: candidate.Currency,
            WalletType: candidate.WalletType,
            AvailableBalance: candidate.AvailableBalance,
            LendableBalance: candidate.LendableBalance,
            ProposedAmount: slotRequest.Amount,
            ProposedRate: slotRequest.Rate,
            ProposedPeriodDays: slotRequest.PeriodDays,
            Reason: _options.DryRun
                ? $"Dry-run additional managed offer slot should be submitted. slotRole={slotRole} slotIndex={slotIndex}/{slotPlan.TotalSlotsNow} parallel_mode={(policy.PlaceImmediately ? "target" : "fallback")} liveSplit={DescribeLiveSplit(slotPlan)} {slotSummary}"
                : $"Additional managed offer slot should be submitted. slotRole={slotRole} slotIndex={slotIndex}/{slotPlan.TotalSlotsNow} parallel_mode={(policy.PlaceImmediately ? "target" : "fallback")} liveSplit={DescribeLiveSplit(slotPlan)} {slotSummary}",
            TimestampUtc: nowUtc,
            SlotRole: slotRole,
            SlotIndex: slotIndex,
            SlotCount: slotPlan.TotalSlotsNow
        );
    }

    private FundingDecision BuildFreshPlacementDecision(
        FundingPlacementCandidate candidate,
        IReadOnlyList<FundingOfferInfo> activeOffers,
        FundingLiveSlotPlan slotPlan,
        DateTime nowUtc)
    {
        var slotRole = ResolveNextLiveSlotRole(candidate, activeOffers, slotPlan) ?? "Motor";
        if (string.Equals(slotRole, "Motor", StringComparison.OrdinalIgnoreCase) &&
            TryApplyManagedFallbackCarryForward(candidate, nowUtc, out var carryForwardDecision))
        {
            return carryForwardDecision;
        }

        var policy = ResolveLivePlacementPolicy(candidate, slotRole, slotPlan);
        var slotIndex = Math.Min(slotPlan.TotalSlotsNow, activeOffers.Count + 1);
        if (policy.PlaceImmediately)
        {
            ClearLivePlacementWaitState(candidate.Symbol);

            return new FundingDecision(
                Action: _options.DryRun ? "would_place" : "place",
                IsDryRun: _options.DryRun,
                IsActionable: true,
                Symbol: candidate.Symbol,
                Currency: candidate.Currency,
                WalletType: candidate.WalletType,
                AvailableBalance: candidate.AvailableBalance,
                LendableBalance: candidate.LendableBalance,
                ProposedAmount: policy.TargetRequest.Amount,
                ProposedRate: policy.TargetRequest.Rate,
                ProposedPeriodDays: policy.TargetRequest.PeriodDays,
                Reason: _options.DryRun
                    ? $"Dry-run funding recommendation generated from market snapshot and funding wallet availability. slotRole={slotRole} slotIndex={slotIndex}/{slotPlan.TotalSlotsNow} liveSplit={DescribeLiveSplit(slotPlan)} {policy.TargetSummary}"
                    : $"Funding offer should be submitted. slotRole={slotRole} slotIndex={slotIndex}/{slotPlan.TotalSlotsNow} liveSplit={DescribeLiveSplit(slotPlan)} {policy.TargetSummary}",
                TimestampUtc: nowUtc,
                SlotRole: slotRole,
                SlotIndex: slotIndex,
                SlotCount: slotPlan.TotalSlotsNow
            );
        }

        var waitState = GetOrCreateLivePlacementWaitState(candidate, policy, nowUtc, out var isNewState);
        if (nowUtc < waitState.DeadlineUtc)
        {
            var waitRemaining = waitState.DeadlineUtc - nowUtc;
            return new FundingDecision(
                Action: "wait_for_target_rate",
                IsDryRun: _options.DryRun,
                IsActionable: false,
                Symbol: candidate.Symbol,
                Currency: candidate.Currency,
                WalletType: candidate.WalletType,
                AvailableBalance: candidate.AvailableBalance,
                LendableBalance: candidate.LendableBalance,
                ProposedAmount: policy.TargetRequest.Amount,
                ProposedRate: policy.TargetRequest.Rate,
                ProposedPeriodDays: policy.TargetRequest.PeriodDays,
                Reason: isNewState
                    ? $"Starting live wait window for target funding rate until {waitState.DeadlineUtc:O}. slotRole={slotRole} slotIndex={slotIndex}/{slotPlan.TotalSlotsNow} liveSplit={DescribeLiveSplit(slotPlan)} {policy.TargetSummary} fallback={policy.FallbackSummary}"
                    : $"Waiting for target funding rate until {waitState.DeadlineUtc:O} ({Math.Ceiling(waitRemaining.TotalMinutes):F0}m remaining). slotRole={slotRole} slotIndex={slotIndex}/{slotPlan.TotalSlotsNow} liveSplit={DescribeLiveSplit(slotPlan)} {policy.TargetSummary} fallback={policy.FallbackSummary}",
                TimestampUtc: nowUtc,
                SlotRole: slotRole,
                SlotIndex: slotIndex,
                SlotCount: slotPlan.TotalSlotsNow
            );
        }

        return new FundingDecision(
            Action: _options.DryRun ? "would_place_after_wait_fallback" : "place_after_wait_fallback",
            IsDryRun: _options.DryRun,
            IsActionable: true,
            Symbol: candidate.Symbol,
            Currency: candidate.Currency,
            WalletType: candidate.WalletType,
            AvailableBalance: candidate.AvailableBalance,
            LendableBalance: candidate.LendableBalance,
            ProposedAmount: policy.FallbackRequest.Amount,
            ProposedRate: policy.FallbackRequest.Rate,
            ProposedPeriodDays: policy.FallbackRequest.PeriodDays,
            Reason: _options.DryRun
                ? $"Dry-run fallback submit after wait budget expired at {waitState.DeadlineUtc:O}. slotRole={slotRole} slotIndex={slotIndex}/{slotPlan.TotalSlotsNow} liveSplit={DescribeLiveSplit(slotPlan)} {policy.FallbackSummary}"
                : $"Wait budget expired at {waitState.DeadlineUtc:O}; funding offer should fall back now. slotRole={slotRole} slotIndex={slotIndex}/{slotPlan.TotalSlotsNow} liveSplit={DescribeLiveSplit(slotPlan)} {policy.FallbackSummary}",
            TimestampUtc: nowUtc,
            SlotRole: slotRole,
            SlotIndex: slotIndex,
            SlotCount: slotPlan.TotalSlotsNow
        );
    }

    private FundingDecision BuildManagedCapacityDecision(
        FundingPlacementCandidate candidate,
        IReadOnlyList<FundingOfferInfo> activeOffers,
        FundingLiveSlotPlan slotPlan,
        DateTime nowUtc)
    {
        var minAgeOverride = ResolveManagedCapacityFullMinAge(candidate.SymbolSettings);

        if (activeOffers.Count == 1 && slotPlan.TotalSlotsNow == 1)
            return BuildManagedOfferDecision(candidate, activeOffers[0], "Motor", 1, slotPlan, nowUtc, minAgeOverride);

        var assignments = BuildManagedActiveSlotAssignments(candidate, activeOffers, slotPlan);
        FundingDecision? fallbackDecision = null;
        FundingDecision? keepDecision = null;

        foreach (var assignment in assignments)
        {
            var decision = BuildManagedOfferDecision(
                candidate,
                assignment.Offer,
                assignment.Role,
                assignment.SlotIndex,
                slotPlan,
                nowUtc,
                minAgeOverride);

            if (decision.IsActionable)
                return decision;

            if (fallbackDecision is null &&
                string.Equals(decision.Action, "wait_active_offer_fallback", StringComparison.OrdinalIgnoreCase))
            {
                fallbackDecision = decision;
            }

            if (keepDecision is null &&
                string.Equals(decision.Action, "skip_active_offer_ok", StringComparison.OrdinalIgnoreCase))
            {
                keepDecision = decision;
            }
        }

        return fallbackDecision
            ?? keepDecision
            ?? new FundingDecision(
                Action: "skip_active_offer_capacity_reached",
                IsDryRun: _options.DryRun,
                IsActionable: false,
                Symbol: candidate.Symbol,
                Currency: candidate.Currency,
                WalletType: candidate.WalletType,
                AvailableBalance: candidate.AvailableBalance,
                LendableBalance: candidate.LendableBalance,
                ProposedAmount: candidate.Request.Amount,
                ProposedRate: candidate.Request.Rate,
                ProposedPeriodDays: candidate.Request.PeriodDays,
                Reason: $"Managed active offer capacity reached for {candidate.Symbol} ({activeOffers.Count}/{slotPlan.TotalSlotsNow}); additional parallel offers are blocked until one fills. slotRole=none slotIndex={activeOffers.Count}/{slotPlan.TotalSlotsNow} liveSplit={DescribeLiveSplit(slotPlan)} {candidate.RateSelectionSummary}",
                TimestampUtc: nowUtc,
                SlotIndex: activeOffers.Count,
                SlotCount: slotPlan.TotalSlotsNow
            );
    }

    private FundingLivePlacementPolicy ResolveLivePlacementPolicy(
        FundingPlacementCandidate candidate,
        string? slotRole = null,
        FundingLiveSlotPlan? slotPlan = null)
    {
        var normalizedMode = ResolveLivePlacementPolicyMode(candidate, slotRole);
        if (normalizedMode != "MOTORWAITFALLBACK" &&
            normalizedMode != "OPPORTUNISTICWAITFALLBACK" &&
            normalizedMode != "AGGRESSIVEWAITFALLBACK" &&
            normalizedMode != "SNIPERWAITFALLBACK")
        {
            return new FundingLivePlacementPolicy(
                Mode: normalizedMode,
                Regime: "n/a",
                MaxWaitMinutes: 0,
                TargetRequest: candidate.Request,
                FallbackRequest: candidate.Request,
                TargetSummary: $"placement_policy=Immediate {candidate.RateSelectionSummary}",
                FallbackSummary: $"placement_policy=Immediate {candidate.RateSelectionSummary}",
                PlaceImmediately: true);
        }

        var askRate = candidate.Ticker.AskRate > 0m ? candidate.Ticker.AskRate : 0m;
        var bidRate = candidate.Ticker.BidRate > 0m ? candidate.Ticker.BidRate : 0m;
        var frrRate = candidate.Ticker.Frr.GetValueOrDefault() > 0m ? candidate.Ticker.Frr.GetValueOrDefault() : 0m;
        var visibleMarketCap = ResolveVisibleMarketCap(askRate, bidRate, frrRate);
        var bookAnchor = askRate > 0m
            ? askRate
            : bidRate > 0m
                ? bidRate
                : frrRate;
        var safeAnchor = bookAnchor > 0m ? bookAnchor : candidate.SymbolSettings.MinDailyRate;
        var regimeAnchor = candidate.SymbolSettings.LiveUseFrrAsFloor && frrRate > 0m
            ? Math.Max(safeAnchor, frrRate)
            : safeAnchor;
        var regime = ClassifyMarketRegime(regimeAnchor, candidate.SymbolSettings);
        var singleSlotAdaptiveCap = ResolveSingleSlotAdaptiveMaxDailyRate(
            askRate,
            bidRate,
            frrRate,
            candidate.SymbolSettings,
            slotPlan);
        var motorAdaptiveCap = ResolveMotorAdaptiveMaxDailyRate(
            askRate,
            bidRate,
            frrRate,
            candidate.SymbolSettings,
            slotPlan);
        var opportunisticAdaptiveCap = ResolveOpportunisticAdaptiveMaxDailyRate(
            askRate,
            bidRate,
            frrRate,
            candidate.SymbolSettings,
            slotPlan);
        var aggressiveAdaptiveCap = ResolveAggressiveAdaptiveMaxDailyRate(
            askRate,
            bidRate,
            frrRate,
            candidate.SymbolSettings,
            slotPlan);
        decimal? singleSlotMaxRateOverride = singleSlotAdaptiveCap > candidate.SymbolSettings.MaxDailyRate
            ? singleSlotAdaptiveCap
            : null;
        var effectiveMotorCap = Math.Max(singleSlotAdaptiveCap, motorAdaptiveCap);
        var effectiveOpportunisticCap = Math.Max(singleSlotAdaptiveCap, opportunisticAdaptiveCap);
        var effectiveAggressiveCap = Math.Max(singleSlotAdaptiveCap, aggressiveAdaptiveCap);
        decimal? motorMaxRateOverride = effectiveMotorCap > candidate.SymbolSettings.MaxDailyRate
            ? effectiveMotorCap
            : null;
        decimal? opportunisticMaxRateOverride = effectiveOpportunisticCap > candidate.SymbolSettings.MaxDailyRate
            ? effectiveOpportunisticCap
            : null;
        decimal? aggressiveMaxRateOverride = effectiveAggressiveCap > candidate.SymbolSettings.MaxDailyRate
            ? effectiveAggressiveCap
            : null;
        var singleSlotAdaptiveSummary = slotPlan?.TotalSlotsNow == 1
            ? $" singleSlotAdaptive={candidate.SymbolSettings.EnableAdaptiveSingleSlotMaxRate} singleSlotAdaptiveCap={singleSlotAdaptiveCap:E6}"
            : string.Empty;
        var motorAdaptiveSummary = $" motorAdaptive={candidate.SymbolSettings.EnableAdaptiveMotorMaxRate} motorAdaptiveCap={effectiveMotorCap:E6}";
        var opportunisticAdaptiveSummary = $" opportunisticAdaptive={candidate.SymbolSettings.EnableAdaptiveOpportunisticMaxRate} opportunisticAdaptiveCap={effectiveOpportunisticCap:E6}";
        var aggressiveAdaptiveSummary = $" aggressiveAdaptive={candidate.SymbolSettings.EnableAdaptiveAggressiveMaxRate} aggressiveAdaptiveCap={effectiveAggressiveCap:E6}";

        if (normalizedMode == "OPPORTUNISTICWAITFALLBACK")
        {
            var opportunisticRegime = string.Equals(regime, "LOW", StringComparison.OrdinalIgnoreCase)
                ? "NORMAL"
                : regime;
            var (targetRate, targetRateTelemetry) = SelectShadowRateWithTelemetry(
                safeAnchor,
                candidate.SymbolSettings.OpportunisticRateMultiplier,
                candidate.SymbolSettings,
                opportunisticMaxRateOverride,
                visibleMarketCap);
            var targetRequest = candidate.Request with { Rate = targetRate };
            var (fallbackRate, fallbackRateTelemetry) = SelectShadowRateWithTelemetry(
                safeAnchor,
                candidate.SymbolSettings.MotorRateMultiplier,
                candidate.SymbolSettings,
                motorMaxRateOverride,
                visibleMarketCap);
            var fallbackRequest = candidate.Request with { Rate = fallbackRate };
            var maxWaitMinutes = GetOpportunisticMaxWaitMinutes(opportunisticRegime, candidate.SymbolSettings);
            var placeImmediately =
                string.Equals(opportunisticRegime, "HOT", StringComparison.OrdinalIgnoreCase) ||
                Math.Abs(targetRate - fallbackRate) < _options.ReplaceMinRateDelta;

            return new FundingLivePlacementPolicy(
                Mode: normalizedMode,
                Regime: opportunisticRegime,
                MaxWaitMinutes: maxWaitMinutes,
                TargetRequest: targetRequest,
                FallbackRequest: fallbackRequest,
                TargetSummary: $"placement_policy=OpportunisticWaitFallback regime={opportunisticRegime} wait={maxWaitMinutes}m target={targetRate:E6} oppMult={candidate.SymbolSettings.OpportunisticRateMultiplier:F3} ask={askRate:E6} bid={bidRate:E6} frr={FormatRate(frrRate)}{targetRateTelemetry}{singleSlotAdaptiveSummary}{opportunisticAdaptiveSummary}",
                FallbackSummary: $"placement_policy=OpportunisticWaitFallback regime={opportunisticRegime} wait={maxWaitMinutes}m fallback={fallbackRate:E6} fallbackBucket=Motor motorMult={candidate.SymbolSettings.MotorRateMultiplier:F3} ask={askRate:E6} bid={bidRate:E6} frr={FormatRate(frrRate)}{fallbackRateTelemetry}{singleSlotAdaptiveSummary}{motorAdaptiveSummary}",
                PlaceImmediately: placeImmediately);
        }

        if (normalizedMode == "SNIPERWAITFALLBACK")
        {
            var sniperRegime = string.Equals(regime, "LOW", StringComparison.OrdinalIgnoreCase)
                ? "NORMAL"
                : regime;
            var sniperAnchor = ResolveSniperAnchorRate(askRate, bidRate, frrRate, safeAnchor);
            var sniperCap = ResolveSniperAdaptiveMaxDailyRate(askRate, bidRate, frrRate, candidate.SymbolSettings);
            var effectiveSniperCap = Math.Max(sniperCap, singleSlotAdaptiveCap);
            var (targetRate, targetRateTelemetry) = SelectShadowRateWithTelemetry(
                sniperAnchor,
                candidate.SymbolSettings.SniperRateMultiplier,
                candidate.SymbolSettings,
                effectiveSniperCap,
                visibleMarketCap);
            var targetRequest = candidate.Request with { Rate = targetRate };
            var fallbackBucket = ResolveManagedFallbackBucketName(candidate, "Sniper");
            var fallbackMultiplier = string.Equals(fallbackBucket, "Opportunistic", StringComparison.OrdinalIgnoreCase)
                ? candidate.SymbolSettings.OpportunisticRateMultiplier
                : candidate.SymbolSettings.MotorRateMultiplier;
            var (fallbackRate, fallbackRateTelemetry) = SelectShadowRateWithTelemetry(
                safeAnchor,
                fallbackMultiplier,
                candidate.SymbolSettings,
                string.Equals(fallbackBucket, "Motor", StringComparison.OrdinalIgnoreCase)
                    ? motorMaxRateOverride
                    : opportunisticMaxRateOverride,
                visibleMarketCap);
            var fallbackRequest = candidate.Request with { Rate = fallbackRate };
            var maxWaitMinutes = GetSniperMaxWaitMinutes(sniperRegime, candidate.SymbolSettings);
            var placeImmediately =
                string.Equals(sniperRegime, "HOT", StringComparison.OrdinalIgnoreCase) ||
                Math.Abs(targetRate - fallbackRate) < _options.ReplaceMinRateDelta;

            return new FundingLivePlacementPolicy(
                Mode: normalizedMode,
                Regime: sniperRegime,
                MaxWaitMinutes: maxWaitMinutes,
                TargetRequest: targetRequest,
                FallbackRequest: fallbackRequest,
                TargetSummary: $"placement_policy=SniperWaitFallback regime={sniperRegime} wait={maxWaitMinutes}m target={targetRate:E6} sniperMult={candidate.SymbolSettings.SniperRateMultiplier:F3} adaptive={candidate.SymbolSettings.EnableAdaptiveSniperMaxRate} adaptiveCap={effectiveSniperCap:E6} anchor={sniperAnchor:E6} ask={askRate:E6} bid={bidRate:E6} frr={FormatRate(frrRate)}{targetRateTelemetry}{singleSlotAdaptiveSummary}",
                FallbackSummary: $"placement_policy=SniperWaitFallback regime={sniperRegime} wait={maxWaitMinutes}m fallback={fallbackRate:E6} fallbackBucket={fallbackBucket} fallbackMult={fallbackMultiplier:F3} ask={askRate:E6} bid={bidRate:E6} frr={FormatRate(frrRate)}{fallbackRateTelemetry}{singleSlotAdaptiveSummary}{motorAdaptiveSummary}",
                PlaceImmediately: placeImmediately);
        }

        if (normalizedMode == "AGGRESSIVEWAITFALLBACK")
        {
            var aggressiveRegime = string.Equals(regime, "LOW", StringComparison.OrdinalIgnoreCase)
                ? "NORMAL"
                : regime;
            var (targetRate, targetRateTelemetry) = SelectShadowRateWithTelemetry(
                safeAnchor,
                candidate.SymbolSettings.AggressiveRateMultiplier,
                candidate.SymbolSettings,
                aggressiveMaxRateOverride,
                visibleMarketCap);
            var targetRequest = candidate.Request with { Rate = targetRate };
            var fallbackBucket = ResolveManagedFallbackBucketName(candidate, "Aggressive");
            var fallbackMultiplier = string.Equals(fallbackBucket, "Opportunistic", StringComparison.OrdinalIgnoreCase)
                ? candidate.SymbolSettings.OpportunisticRateMultiplier
                : candidate.SymbolSettings.MotorRateMultiplier;
            var (fallbackRate, fallbackRateTelemetry) = SelectShadowRateWithTelemetry(
                safeAnchor,
                fallbackMultiplier,
                candidate.SymbolSettings,
                string.Equals(fallbackBucket, "Opportunistic", StringComparison.OrdinalIgnoreCase)
                    ? opportunisticMaxRateOverride
                    : motorMaxRateOverride,
                visibleMarketCap);
            var fallbackRequest = candidate.Request with { Rate = fallbackRate };
            var maxWaitMinutes = GetAggressiveMaxWaitMinutes(aggressiveRegime, candidate.SymbolSettings);
            var placeImmediately =
                string.Equals(aggressiveRegime, "HOT", StringComparison.OrdinalIgnoreCase) ||
                Math.Abs(targetRate - fallbackRate) < _options.ReplaceMinRateDelta;

            return new FundingLivePlacementPolicy(
                Mode: normalizedMode,
                Regime: aggressiveRegime,
                MaxWaitMinutes: maxWaitMinutes,
                TargetRequest: targetRequest,
                FallbackRequest: fallbackRequest,
                TargetSummary: $"placement_policy=AggressiveWaitFallback regime={aggressiveRegime} wait={maxWaitMinutes}m target={targetRate:E6} aggressiveMult={candidate.SymbolSettings.AggressiveRateMultiplier:F3} ask={askRate:E6} bid={bidRate:E6} frr={FormatRate(frrRate)}{targetRateTelemetry}{singleSlotAdaptiveSummary}{aggressiveAdaptiveSummary}",
                FallbackSummary: $"placement_policy=AggressiveWaitFallback regime={aggressiveRegime} wait={maxWaitMinutes}m fallback={fallbackRate:E6} fallbackBucket={fallbackBucket} fallbackMult={fallbackMultiplier:F3} ask={askRate:E6} bid={bidRate:E6} frr={FormatRate(frrRate)}{fallbackRateTelemetry}{singleSlotAdaptiveSummary}{opportunisticAdaptiveSummary}{motorAdaptiveSummary}",
                PlaceImmediately: placeImmediately);
        }

        var (motorTargetRate, motorRateSelectionSummary) = SelectLiveRate(
            candidate.Ticker,
            candidate.SymbolSettings,
            motorMaxRateOverride);
        var motorTargetRequest = candidate.Request with { Rate = motorTargetRate };
        var motorMaxWaitMinutes = GetMotorMaxWaitMinutes(regime, candidate.SymbolSettings);
        var motorFallbackRate = SelectShadowRate(
            safeAnchor,
            candidate.SymbolSettings.MotorRateMultiplier,
            candidate.SymbolSettings,
            motorMaxRateOverride);
        var motorFallbackRequest = candidate.Request with { Rate = motorFallbackRate };
        var motorPlaceImmediately =
            string.Equals(regime, "HOT", StringComparison.OrdinalIgnoreCase) ||
            Math.Abs(motorTargetRate - motorFallbackRate) < _options.ReplaceMinRateDelta;

        return new FundingLivePlacementPolicy(
            Mode: normalizedMode,
            Regime: regime,
            MaxWaitMinutes: motorMaxWaitMinutes,
            TargetRequest: motorTargetRequest,
            FallbackRequest: motorFallbackRequest,
            TargetSummary: $"placement_policy=MotorWaitFallback regime={regime} wait={motorMaxWaitMinutes}m target={motorTargetRate:E6} ask={askRate:E6} bid={bidRate:E6} frr={FormatRate(frrRate)} {motorRateSelectionSummary}{singleSlotAdaptiveSummary}{motorAdaptiveSummary}",
            FallbackSummary: $"placement_policy=MotorWaitFallback regime={regime} wait={motorMaxWaitMinutes}m fallback={motorFallbackRate:E6} motorMult={candidate.SymbolSettings.MotorRateMultiplier:F3} ask={askRate:E6} bid={bidRate:E6} frr={FormatRate(frrRate)}{singleSlotAdaptiveSummary}{motorAdaptiveSummary}",
            PlaceImmediately: motorPlaceImmediately);
    }

    private string ResolveLivePlacementPolicyMode(FundingPlacementCandidate candidate, string? slotRole)
    {
        if (string.Equals(slotRole, "Sniper", StringComparison.OrdinalIgnoreCase))
            return "SNIPERWAITFALLBACK";

        if (string.Equals(slotRole, "Opportunistic", StringComparison.OrdinalIgnoreCase))
            return "OPPORTUNISTICWAITFALLBACK";

        if (string.Equals(slotRole, "Aggressive", StringComparison.OrdinalIgnoreCase))
            return "AGGRESSIVEWAITFALLBACK";

        if (string.Equals(slotRole, "Motor", StringComparison.OrdinalIgnoreCase))
            return "MOTORWAITFALLBACK";

        return NormalizeLivePlacementPolicyMode(candidate.SymbolSettings.LivePlacementPolicyMode);
    }

    private FundingLiveSlotPlan BuildLiveSlotPlan(
        FundingSymbolRuntimeSettings symbolSettings,
        decimal lendableBalance,
        int activeOfferCount)
    {
        var maxActiveOffers = Math.Max(1, symbolSettings.MaxActiveOffersPerSymbol);
        var slotAmount = Math.Max(symbolSettings.MinOfferAmount, 0.00000001m);
        var additionalSlotsByBalance = (int)decimal.Floor(Math.Max(0m, lendableBalance) / slotAmount);
        var totalSlotsNow = Math.Min(maxActiveOffers, activeOfferCount + additionalSlotsByBalance);
        var opportunisticEnabled =
            maxActiveOffers > 1 &&
            symbolSettings.OpportunisticAllocationFraction > 0m &&
            string.Equals(
                NormalizeLivePlacementPolicyMode(symbolSettings.LivePlacementPolicyMode),
                "OPPORTUNISTICWAITFALLBACK",
                StringComparison.OrdinalIgnoreCase);
        var aggressiveEnabled =
            symbolSettings.EnableLiveAggressivePromotion &&
            maxActiveOffers > 2 &&
            totalSlotsNow > 2 &&
            symbolSettings.AggressiveAllocationFraction > 0m;
        var sniperEnabled =
            symbolSettings.EnableLiveSniperPromotion &&
            maxActiveOffers > 3 &&
            totalSlotsNow > 3 &&
            symbolSettings.SniperAllocationFraction > 0m;

        var (desiredMotorSlots, desiredOpportunisticSlots, desiredAggressiveSlots, desiredSniperSlots) =
            AllocateLiveSlotCounts(totalSlotsNow, opportunisticEnabled, aggressiveEnabled, sniperEnabled, symbolSettings);

        return new FundingLiveSlotPlan(
            SlotAmount: slotAmount,
            MaxActiveOffers: maxActiveOffers,
            AdditionalSlotsByBalance: additionalSlotsByBalance,
            TotalSlotsNow: totalSlotsNow,
            DesiredMotorSlots: desiredMotorSlots,
            DesiredOpportunisticSlots: desiredOpportunisticSlots,
            DesiredAggressiveSlots: desiredAggressiveSlots,
            DesiredSniperSlots: desiredSniperSlots,
            OpportunisticEnabled: opportunisticEnabled,
            AggressiveEnabled: aggressiveEnabled,
            SniperEnabled: sniperEnabled);
    }

    private static (int MotorSlots, int OpportunisticSlots, int AggressiveSlots, int SniperSlots) AllocateLiveSlotCounts(
        int totalSlotsNow,
        bool opportunisticEnabled,
        bool aggressiveEnabled,
        bool sniperEnabled,
        FundingSymbolRuntimeSettings symbolSettings)
    {
        if (totalSlotsNow <= 0)
            return (0, 0, 0, 0);

        if (totalSlotsNow == 1)
            return (1, 0, 0, 0);

        if (!opportunisticEnabled && !aggressiveEnabled && !sniperEnabled)
            return (totalSlotsNow, 0, 0, 0);

        var desiredSniperSlots = sniperEnabled && totalSlotsNow >= 4 ? 1 : 0;
        var remainingSlots = Math.Max(0, totalSlotsNow - desiredSniperSlots);
        if (remainingSlots <= 1)
            return (remainingSlots, 0, 0, desiredSniperSlots);

        var effectiveAggressiveEnabled = aggressiveEnabled && remainingSlots >= 3;
        var effectiveOpportunisticEnabled = opportunisticEnabled && remainingSlots >= 2;

        if (!effectiveOpportunisticEnabled && !effectiveAggressiveEnabled)
            return (remainingSlots, 0, 0, desiredSniperSlots);

        if (remainingSlots == 2)
            return (1, effectiveOpportunisticEnabled ? 1 : 0, 0, desiredSniperSlots);

        var motorFraction = Math.Max(0m, symbolSettings.MotorAllocationFraction);
        var opportunisticFraction = effectiveOpportunisticEnabled ? Math.Max(0m, symbolSettings.OpportunisticAllocationFraction) : 0m;
        var aggressiveFraction = effectiveAggressiveEnabled ? Math.Max(0m, symbolSettings.AggressiveAllocationFraction) : 0m;
        var totalFraction = motorFraction + opportunisticFraction + aggressiveFraction;

        var desiredMotorSlots = 1;
        var desiredOpportunisticSlots = effectiveOpportunisticEnabled ? 1 : 0;
        var desiredAggressiveSlots = effectiveAggressiveEnabled ? 1 : 0;
        var minimumAllocated = desiredMotorSlots + desiredOpportunisticSlots + desiredAggressiveSlots;

        if (minimumAllocated >= remainingSlots)
            return (desiredMotorSlots, desiredOpportunisticSlots, desiredAggressiveSlots, desiredSniperSlots);

        var extraSlots = remainingSlots - minimumAllocated;

        if (totalFraction <= 0m)
        {
            desiredMotorSlots += extraSlots;
            return (desiredMotorSlots, desiredOpportunisticSlots, desiredAggressiveSlots, desiredSniperSlots);
        }

        var idealMotorSlots = remainingSlots * (motorFraction / totalFraction);
        var idealOpportunisticSlots = remainingSlots * (opportunisticFraction / totalFraction);
        var idealAggressiveSlots = remainingSlots * (aggressiveFraction / totalFraction);
        var motorRemainder = idealMotorSlots - desiredMotorSlots;
        var opportunisticRemainder = idealOpportunisticSlots - desiredOpportunisticSlots;
        var aggressiveRemainder = idealAggressiveSlots - desiredAggressiveSlots;

        while (extraSlots > 0)
        {
            if (effectiveAggressiveEnabled && aggressiveRemainder >= opportunisticRemainder && aggressiveRemainder >= motorRemainder)
            {
                desiredAggressiveSlots++;
            }
            else if (effectiveOpportunisticEnabled && opportunisticRemainder >= motorRemainder)
            {
                desiredOpportunisticSlots++;
            }
            else
            {
                desiredMotorSlots++;
            }

            extraSlots--;
        }

        return (desiredMotorSlots, desiredOpportunisticSlots, desiredAggressiveSlots, desiredSniperSlots);
    }

    private string? ResolveNextLiveSlotRole(
        FundingPlacementCandidate candidate,
        IReadOnlyList<FundingOfferInfo> activeOffers,
        FundingLiveSlotPlan slotPlan)
    {
        if (slotPlan.TotalSlotsNow <= activeOffers.Count)
            return null;

        var (activeMotorSlots, activeOpportunisticSlots, activeAggressiveSlots, activeSniperSlots) = ClassifyActiveSlotRoles(candidate, activeOffers, slotPlan);
        if (activeOffers.Count == 1 &&
            slotPlan.DesiredOpportunisticSlots > 0 &&
            activeMotorSlots >= 1 &&
            activeOpportunisticSlots == 0)
        {
            return "Opportunistic";
        }

        if (activeOffers.Count == 2 &&
            slotPlan.DesiredAggressiveSlots > 0 &&
            activeMotorSlots >= 1 &&
            activeOpportunisticSlots >= 1 &&
            activeAggressiveSlots == 0)
        {
            return "Aggressive";
        }

        if (activeMotorSlots < slotPlan.DesiredMotorSlots)
            return "Motor";

        if (activeOpportunisticSlots < slotPlan.DesiredOpportunisticSlots)
            return "Opportunistic";

        if (activeAggressiveSlots < slotPlan.DesiredAggressiveSlots)
            return "Aggressive";

        if (activeSniperSlots < slotPlan.DesiredSniperSlots)
            return "Sniper";

        return "Motor";
    }

    private (int MotorSlots, int OpportunisticSlots, int AggressiveSlots, int SniperSlots) ClassifyActiveSlotRoles(
        FundingPlacementCandidate candidate,
        IReadOnlyList<FundingOfferInfo> activeOffers,
        FundingLiveSlotPlan slotPlan)
    {
        var assignments = BuildManagedActiveSlotAssignments(candidate, activeOffers, slotPlan);
        var motorOffers = assignments.Count(assignment => string.Equals(assignment.Role, "Motor", StringComparison.OrdinalIgnoreCase));
        var opportunisticOffers = assignments.Count(assignment => string.Equals(assignment.Role, "Opportunistic", StringComparison.OrdinalIgnoreCase));
        var aggressiveOffers = assignments.Count(assignment => string.Equals(assignment.Role, "Aggressive", StringComparison.OrdinalIgnoreCase));
        var sniperOffers = assignments.Count(assignment => string.Equals(assignment.Role, "Sniper", StringComparison.OrdinalIgnoreCase));
        return (motorOffers, opportunisticOffers, aggressiveOffers, sniperOffers);
    }

    private IReadOnlyList<FundingManagedActiveSlotAssignment> BuildManagedActiveSlotAssignments(
        FundingPlacementCandidate candidate,
        IReadOnlyList<FundingOfferInfo> activeOffers,
        FundingLiveSlotPlan slotPlan)
    {
        if (activeOffers.Count == 0)
            return Array.Empty<FundingManagedActiveSlotAssignment>();

        var orderedOffers = activeOffers
            .OrderBy(static offer => offer.CreatedUtc ?? DateTime.MinValue)
            .ThenBy(static offer => offer.OfferId, StringComparer.Ordinal)
            .ToArray();

        if ((slotPlan.DesiredOpportunisticSlots <= 0 || !slotPlan.OpportunisticEnabled) &&
            (slotPlan.DesiredAggressiveSlots <= 0 || !slotPlan.AggressiveEnabled) &&
            (slotPlan.DesiredSniperSlots <= 0 || !slotPlan.SniperEnabled))
        {
            return orderedOffers
                .Select((offer, index) => new FundingManagedActiveSlotAssignment(offer, "Motor", index + 1))
                .ToArray();
        }

        if (orderedOffers.Length == 1)
        {
            var offer = orderedOffers[0];
            var motorTargetRate = ResolveLivePlacementPolicy(candidate, "Motor").TargetRequest.Rate;
            var motorDistance = Math.Abs(offer.Rate - motorTargetRate);
            var bestDistance = motorDistance;
            var role = "Motor";
            var slotIndex = 1;

            if (slotPlan.DesiredOpportunisticSlots > 0 && slotPlan.OpportunisticEnabled)
            {
                var opportunisticTargetRate = ResolveLivePlacementPolicy(candidate, "Opportunistic").TargetRequest.Rate;
                var opportunisticDistance = Math.Abs(offer.Rate - opportunisticTargetRate);
                if (opportunisticDistance < bestDistance)
                {
                    bestDistance = opportunisticDistance;
                    role = "Opportunistic";
                    slotIndex = Math.Max(1, slotPlan.DesiredMotorSlots + 1);
                }
            }

            if (slotPlan.DesiredAggressiveSlots > 0 && slotPlan.AggressiveEnabled)
            {
                var aggressiveTargetRate = ResolveLivePlacementPolicy(candidate, "Aggressive").TargetRequest.Rate;
                var aggressiveDistance = Math.Abs(offer.Rate - aggressiveTargetRate);
                if (aggressiveDistance < bestDistance)
                {
                    bestDistance = aggressiveDistance;
                    role = "Aggressive";
                    slotIndex = Math.Max(1, slotPlan.DesiredMotorSlots + slotPlan.DesiredOpportunisticSlots + 1);
                }
            }

            if (slotPlan.DesiredSniperSlots > 0 && slotPlan.SniperEnabled)
            {
                var sniperTargetRate = ResolveLivePlacementPolicy(candidate, "Sniper").TargetRequest.Rate;
                var sniperDistance = Math.Abs(offer.Rate - sniperTargetRate);
                if (sniperDistance < bestDistance)
                {
                    role = "Sniper";
                    slotIndex = Math.Max(1, slotPlan.DesiredMotorSlots + slotPlan.DesiredOpportunisticSlots + slotPlan.DesiredAggressiveSlots + 1);
                }
            }

            return new[] { new FundingManagedActiveSlotAssignment(offer, role, slotIndex) };
        }

        var rankedOffers = activeOffers
            .OrderByDescending(static offer => offer.Rate)
            .ThenByDescending(static offer => offer.CreatedUtc ?? DateTime.MinValue)
            .ToArray();

        var sniperLookup = rankedOffers
            .Take(Math.Min(slotPlan.DesiredSniperSlots, rankedOffers.Length))
            .Select(static offer => offer.OfferId)
            .ToHashSet(StringComparer.Ordinal);

        var aggressiveLookup = rankedOffers
            .Where(offer => !sniperLookup.Contains(offer.OfferId))
            .Take(Math.Min(slotPlan.DesiredAggressiveSlots, rankedOffers.Length - sniperLookup.Count))
            .Select(static offer => offer.OfferId)
            .ToHashSet(StringComparer.Ordinal);

        var opportunisticLookup = rankedOffers
            .Where(offer => !sniperLookup.Contains(offer.OfferId) && !aggressiveLookup.Contains(offer.OfferId))
            .Take(Math.Min(slotPlan.DesiredOpportunisticSlots, rankedOffers.Length - sniperLookup.Count - aggressiveLookup.Count))
            .Select(static offer => offer.OfferId)
            .ToHashSet(StringComparer.Ordinal);

        var motorAssignments = orderedOffers
            .Where(offer => !opportunisticLookup.Contains(offer.OfferId) && !aggressiveLookup.Contains(offer.OfferId) && !sniperLookup.Contains(offer.OfferId))
            .Select((offer, index) => new FundingManagedActiveSlotAssignment(offer, "Motor", index + 1))
            .ToList();

        var opportunisticAssignments = orderedOffers
            .Where(offer => opportunisticLookup.Contains(offer.OfferId))
            .OrderByDescending(static offer => offer.Rate)
            .ThenBy(static offer => offer.CreatedUtc ?? DateTime.MinValue)
            .Select((offer, index) => new FundingManagedActiveSlotAssignment(offer, "Opportunistic", motorAssignments.Count + index + 1))
            .ToList();

        var aggressiveAssignments = orderedOffers
            .Where(offer => aggressiveLookup.Contains(offer.OfferId))
            .OrderByDescending(static offer => offer.Rate)
            .ThenBy(static offer => offer.CreatedUtc ?? DateTime.MinValue)
            .Select((offer, index) => new FundingManagedActiveSlotAssignment(offer, "Aggressive", motorAssignments.Count + opportunisticAssignments.Count + index + 1))
            .ToList();

        var sniperAssignments = orderedOffers
            .Where(offer => sniperLookup.Contains(offer.OfferId))
            .OrderByDescending(static offer => offer.Rate)
            .ThenBy(static offer => offer.CreatedUtc ?? DateTime.MinValue)
            .Select((offer, index) => new FundingManagedActiveSlotAssignment(offer, "Sniper", motorAssignments.Count + opportunisticAssignments.Count + aggressiveAssignments.Count + index + 1))
            .ToList();

        return motorAssignments
            .Concat(opportunisticAssignments)
            .Concat(aggressiveAssignments)
            .Concat(sniperAssignments)
            .OrderByDescending(assignment => assignment.SlotIndex)
            .ToArray();
    }

    private bool TryApplyManagedFallbackCarryForward(
        FundingPlacementCandidate candidate,
        DateTime nowUtc,
        out FundingDecision decision)
    {
        lock (_livePlacementSync)
        {
            if (!_managedFallbackCarryForwardStates.TryGetValue(candidate.Symbol, out var state))
            {
                decision = default!;
                return false;
            }

            if (nowUtc > state.ExpiresUtc)
            {
                _managedFallbackCarryForwardStates.Remove(candidate.Symbol);
                decision = default!;
                return false;
            }

            if (candidate.Request.Amount != state.Amount || candidate.Request.PeriodDays != state.PeriodDays)
            {
                _managedFallbackCarryForwardStates.Remove(candidate.Symbol);
                decision = default!;
                return false;
            }

            var carryForwardRequest = candidate.Request with { Rate = state.Rate };
            decision = new FundingDecision(
                Action: _options.DryRun ? "would_place" : "place",
                IsDryRun: _options.DryRun,
                IsActionable: true,
                Symbol: candidate.Symbol,
                Currency: candidate.Currency,
                WalletType: candidate.WalletType,
                AvailableBalance: candidate.AvailableBalance,
                LendableBalance: candidate.LendableBalance,
                ProposedAmount: carryForwardRequest.Amount,
                ProposedRate: carryForwardRequest.Rate,
                ProposedPeriodDays: carryForwardRequest.PeriodDays,
                Reason: _options.DryRun
                    ? $"Dry-run funding recommendation uses managed fallback carry-forward until {state.ExpiresUtc:O}. sourceOfferId={state.SourceOfferId} carry_rate={state.Rate:E6}"
                    : $"Funding offer should reuse managed fallback carry-forward until {state.ExpiresUtc:O}. sourceOfferId={state.SourceOfferId} carry_rate={state.Rate:E6}",
                TimestampUtc: nowUtc,
                SlotRole: "Motor",
                SlotIndex: 1,
                SlotCount: 1);
            return true;
        }
    }

    private FundingDecision BuildManagedOfferDecision(
        FundingPlacementCandidate candidate,
        FundingOfferInfo activeOffer,
        string slotRole,
        int slotIndex,
        FundingLiveSlotPlan slotPlan,
        DateTime nowUtc,
        TimeSpan? minAgeOverride = null)
    {
        var (managedTargetRequest, managedTargetSummary) = ResolveManagedOfferTarget(candidate, slotRole, slotPlan);
        var managedOfferTelemetry = DescribeManagedOfferTelemetry(activeOffer, managedTargetRequest, candidate.Ticker);
        if (ShouldReplaceOffer(activeOffer, managedTargetRequest, out var replaceReason, minAgeOverride))
        {
            return new FundingDecision(
                Action: _options.DryRun ? "would_replace_offer" : "replace_offer",
                IsDryRun: _options.DryRun,
                IsActionable: true,
                Symbol: candidate.Symbol,
                Currency: candidate.Currency,
                WalletType: candidate.WalletType,
                AvailableBalance: candidate.AvailableBalance,
                LendableBalance: candidate.LendableBalance,
                ProposedAmount: managedTargetRequest.Amount,
                ProposedRate: managedTargetRequest.Rate,
                ProposedPeriodDays: managedTargetRequest.PeriodDays,
                Reason: $"Existing managed offer should be replaced (offerId={activeOffer.OfferId}). slotRole={slotRole} slotIndex={slotIndex}/{slotPlan.TotalSlotsNow} liveSplit={DescribeLiveSplit(slotPlan)} {replaceReason} {managedTargetSummary}{managedOfferTelemetry}",
                TimestampUtc: nowUtc,
                TargetOfferId: activeOffer.OfferId,
                SlotRole: slotRole,
                SlotIndex: slotIndex,
                SlotCount: slotPlan.TotalSlotsNow
            );
        }

        var fallbackDecision = TryBuildManagedOfferFallbackDecision(candidate, activeOffer, slotRole, slotIndex, slotPlan, managedTargetSummary, nowUtc, minAgeOverride);
        if (fallbackDecision is not null)
            return fallbackDecision;

        return new FundingDecision(
            Action: "skip_active_offer_ok",
            IsDryRun: _options.DryRun,
            IsActionable: false,
            Symbol: candidate.Symbol,
            Currency: candidate.Currency,
            WalletType: candidate.WalletType,
            AvailableBalance: candidate.AvailableBalance,
            LendableBalance: candidate.LendableBalance,
            ProposedAmount: managedTargetRequest.Amount,
            ProposedRate: managedTargetRequest.Rate,
            ProposedPeriodDays: managedTargetRequest.PeriodDays,
            Reason: $"Managed active offer remains within thresholds (offerId={activeOffer.OfferId}). slotRole={slotRole} slotIndex={slotIndex}/{slotPlan.TotalSlotsNow} liveSplit={DescribeLiveSplit(slotPlan)} {replaceReason} {managedTargetSummary}{managedOfferTelemetry}",
            TimestampUtc: nowUtc,
            TargetOfferId: activeOffer.OfferId,
            SlotRole: slotRole,
            SlotIndex: slotIndex,
            SlotCount: slotPlan.TotalSlotsNow
        );
    }

    private FundingDecision? TryBuildManagedOfferFallbackDecision(
        FundingPlacementCandidate candidate,
        FundingOfferInfo activeOffer,
        string slotRole,
        int slotIndex,
        FundingLiveSlotPlan slotPlan,
        string managedTargetSummary,
        DateTime nowUtc,
        TimeSpan? minAgeOverride = null)
    {
        var normalizedMode = NormalizeManagedOfferPolicyMode(candidate.SymbolSettings.ManagedOfferPolicyMode);
        if (normalizedMode != "KEEPTHENMOTORFALLBACK")
            return null;

        var (fallbackRequest, fallbackSummary) = ResolveManagedOfferFallbackTarget(candidate, slotRole, slotPlan);
        var fallbackOfferTelemetry = DescribeManagedOfferTelemetry(activeOffer, fallbackRequest, candidate.Ticker);
        var rateDelta = activeOffer.Rate - fallbackRequest.Rate;
        if (rateDelta < _options.ReplaceMinRateDelta)
            return null;

        var visibleMarketCap = ResolveVisibleMarketCap(
            candidate.Ticker.AskRate,
            candidate.Ticker.BidRate,
            candidate.Ticker.Frr.GetValueOrDefault());
        if (ShouldHoldManagedOfferNearMarket(
                activeOffer,
                fallbackRequest,
                visibleMarketCap,
                candidate.SymbolSettings,
                out var nearMarketReason))
        {
            return new FundingDecision(
                Action: "wait_active_offer_near_market",
                IsDryRun: _options.DryRun,
                IsActionable: false,
                Symbol: candidate.Symbol,
                Currency: candidate.Currency,
                WalletType: candidate.WalletType,
                AvailableBalance: candidate.AvailableBalance,
                LendableBalance: candidate.LendableBalance,
                ProposedAmount: fallbackRequest.Amount,
                ProposedRate: fallbackRequest.Rate,
                ProposedPeriodDays: fallbackRequest.PeriodDays,
                Reason: $"Managed offer remains pinned near market cap (offerId={activeOffer.OfferId}). slotRole={slotRole} slotIndex={slotIndex}/{slotPlan.TotalSlotsNow} liveSplit={DescribeLiveSplit(slotPlan)} {nearMarketReason} {managedTargetSummary} {fallbackSummary}{fallbackOfferTelemetry}",
                TimestampUtc: nowUtc,
                TargetOfferId: activeOffer.OfferId,
                SlotRole: slotRole,
                SlotIndex: slotIndex,
                SlotCount: slotPlan.TotalSlotsNow
            );
        }

        var (age, minAge) = GetManagedOfferAgeWindow(activeOffer, minAgeOverride);
        if (age < minAge)
        {
            return new FundingDecision(
                Action: "wait_active_offer_fallback",
                IsDryRun: _options.DryRun,
                IsActionable: false,
                Symbol: candidate.Symbol,
                Currency: candidate.Currency,
                WalletType: candidate.WalletType,
                AvailableBalance: candidate.AvailableBalance,
                LendableBalance: candidate.LendableBalance,
                ProposedAmount: fallbackRequest.Amount,
                ProposedRate: fallbackRequest.Rate,
                ProposedPeriodDays: fallbackRequest.PeriodDays,
                Reason: $"Managed offer is still inside fallback wait window (offerId={activeOffer.OfferId}). slotRole={slotRole} slotIndex={slotIndex}/{slotPlan.TotalSlotsNow} liveSplit={DescribeLiveSplit(slotPlan)} age={age.TotalSeconds:F0}s threshold={minAge.TotalSeconds:F0}s {managedTargetSummary} {fallbackSummary}{fallbackOfferTelemetry}",
                TimestampUtc: nowUtc,
                TargetOfferId: activeOffer.OfferId,
                SlotRole: slotRole,
                SlotIndex: slotIndex,
                SlotCount: slotPlan.TotalSlotsNow
            );
        }

        if (!ShouldReplaceOffer(activeOffer, fallbackRequest, out var replaceReason, minAgeOverride))
            return null;

        return new FundingDecision(
            Action: _options.DryRun ? "would_replace_offer" : "replace_offer",
            IsDryRun: _options.DryRun,
            IsActionable: true,
            Symbol: candidate.Symbol,
            Currency: candidate.Currency,
            WalletType: candidate.WalletType,
            AvailableBalance: candidate.AvailableBalance,
            LendableBalance: candidate.LendableBalance,
            ProposedAmount: fallbackRequest.Amount,
            ProposedRate: fallbackRequest.Rate,
            ProposedPeriodDays: fallbackRequest.PeriodDays,
            Reason: $"Managed offer should fall back via {ResolveManagedFallbackBucketName(candidate, slotRole)} repricing (offerId={activeOffer.OfferId}). slotRole={slotRole} slotIndex={slotIndex}/{slotPlan.TotalSlotsNow} liveSplit={DescribeLiveSplit(slotPlan)} {replaceReason} {managedTargetSummary} {fallbackSummary}{fallbackOfferTelemetry}",
            TimestampUtc: nowUtc,
            TargetOfferId: activeOffer.OfferId,
            SlotRole: slotRole,
            SlotIndex: slotIndex,
            SlotCount: slotPlan.TotalSlotsNow
        );
    }

    private FundingLivePlacementWaitState GetOrCreateLivePlacementWaitState(
        FundingPlacementCandidate candidate,
        FundingLivePlacementPolicy policy,
        DateTime nowUtc,
        out bool isNewState)
    {
        lock (_livePlacementSync)
        {
            if (_livePlacementWaitStates.TryGetValue(candidate.Symbol, out var existing) &&
                !RequiresLivePlacementWaitReset(existing, policy, candidate))
            {
                var refreshed = existing with { LastSeenUtc = nowUtc };
                _livePlacementWaitStates[candidate.Symbol] = refreshed;
                isNewState = false;
                return refreshed;
            }

            var created = new FundingLivePlacementWaitState(
                Symbol: candidate.Symbol,
                Regime: policy.Regime,
                Amount: candidate.Request.Amount,
                PeriodDays: candidate.Request.PeriodDays,
                TargetRate: policy.TargetRequest.Rate,
                FallbackRate: policy.FallbackRequest.Rate,
                StartedUtc: nowUtc,
                DeadlineUtc: nowUtc.AddMinutes(Math.Max(1, policy.MaxWaitMinutes)),
                LastSeenUtc: nowUtc);

            _livePlacementWaitStates[candidate.Symbol] = created;
            isNewState = true;
            return created;
        }
    }

    private bool RequiresLivePlacementWaitReset(
        FundingLivePlacementWaitState existing,
        FundingLivePlacementPolicy policy,
        FundingPlacementCandidate candidate)
    {
        if (!string.Equals(existing.Regime, policy.Regime, StringComparison.OrdinalIgnoreCase))
            return true;

        if (existing.Amount != candidate.Request.Amount || existing.PeriodDays != candidate.Request.PeriodDays)
            return true;

        if (Math.Abs(existing.TargetRate - policy.TargetRequest.Rate) >= _options.ReplaceMinRateDelta)
            return true;

        if (Math.Abs(existing.FallbackRate - policy.FallbackRequest.Rate) >= _options.ReplaceMinRateDelta)
            return true;

        return false;
    }

    private void ClearLivePlacementWaitState(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return;

        lock (_livePlacementSync)
        {
            _livePlacementWaitStates.Remove(symbol);
        }
    }

    private void RememberManagedFallbackCarryForward(
        string symbol,
        FundingOfferRequest request,
        string? sourceOfferId,
        DateTime nowUtc,
        FundingSymbolRuntimeSettings symbolSettings)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return;

        var carryMinutes = Math.Max(1, symbolSettings.ManagedOfferFallbackCarryForwardMinutes);
        var state = new FundingManagedFallbackCarryForwardState(
            Symbol: symbol,
            Amount: request.Amount,
            PeriodDays: request.PeriodDays,
            Rate: request.Rate,
            SourceOfferId: sourceOfferId,
            CreatedUtc: nowUtc,
            ExpiresUtc: nowUtc.AddMinutes(carryMinutes));

        lock (_livePlacementSync)
        {
            _managedFallbackCarryForwardStates[symbol] = state;
        }
    }

    private void ClearManagedFallbackCarryForward(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return;

        lock (_livePlacementSync)
        {
            _managedFallbackCarryForwardStates.Remove(symbol);
        }
    }

    private FundingWalletBalance? FindWalletBalance(IReadOnlyList<FundingWalletBalance> wallets, string currency)
    {
        if (wallets.Count == 0)
            return null;

        IEnumerable<FundingWalletBalance> candidates = wallets;
        if (_options.UseFundingWalletOnly)
        {
            candidates = candidates.Where(w => IsFundingWalletType(w.WalletType));
        }

        return candidates
            .Where(w => CurrencyMatches(w.Currency, currency))
            .OrderByDescending(w => w.Available)
            .FirstOrDefault();
    }

    private bool ShouldReplaceOffer(FundingOfferInfo activeOffer, FundingOfferRequest targetRequest, out string reason, TimeSpan? minAgeOverride = null)
    {
        var (age, minAge) = GetManagedOfferAgeWindow(activeOffer, minAgeOverride);
        if (age < minAge)
        {
            reason = $"Offer age {age.TotalSeconds:F0}s is below replace threshold {minAge.TotalSeconds:F0}s.";
            return false;
        }

        if (!string.Equals(activeOffer.OfferType?.Trim(), targetRequest.OfferType.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            reason = $"Offer type changed from {activeOffer.OfferType} to {targetRequest.OfferType}.";
            return true;
        }

        if (activeOffer.PeriodDays != targetRequest.PeriodDays)
        {
            reason = $"Offer period changed from {activeOffer.PeriodDays} to {targetRequest.PeriodDays}.";
            return true;
        }

        var rateDelta = Math.Abs(activeOffer.Rate - targetRequest.Rate);
        if (rateDelta >= _options.ReplaceMinRateDelta)
        {
            reason = $"Offer rate delta {rateDelta:E6} exceeded threshold {_options.ReplaceMinRateDelta:E6}.";
            return true;
        }

        var currentAmount = Math.Abs(activeOffer.Amount);
        var amountDelta = Math.Abs(currentAmount - targetRequest.Amount);
        var amountDeltaFraction = targetRequest.Amount > 0m ? amountDelta / targetRequest.Amount : 0m;
        if (amountDeltaFraction >= _options.ReplaceMinAmountDeltaFraction)
        {
            reason = $"Offer amount delta {amountDeltaFraction:P1} exceeded threshold {_options.ReplaceMinAmountDeltaFraction:P1}.";
            return true;
        }

        reason = "Offer parameters are still aligned with target thresholds.";
        return false;
    }

    private (TimeSpan Age, TimeSpan MinAge) GetManagedOfferAgeWindow(FundingOfferInfo activeOffer, TimeSpan? minAgeOverride = null)
    {
        var referenceUtc = activeOffer.UpdatedUtc ?? activeOffer.CreatedUtc ?? DateTime.UtcNow;
        var age = DateTime.UtcNow - referenceUtc;
        var minAge = minAgeOverride ?? TimeSpan.FromSeconds(Math.Max(0, _options.MinManagedOfferAgeSecondsBeforeReplace));
        return (age, minAge);
    }

    private static TimeSpan ResolveManagedCapacityFullMinAge(FundingSymbolRuntimeSettings symbolSettings)
    {
        var standardAge = Math.Max(0, symbolSettings.MinManagedOfferAgeSecondsBeforeReplace);
        var capacityFullAge = Math.Max(0, symbolSettings.MinManagedOfferAgeSecondsBeforeReplaceWhenCapacityFull);
        return TimeSpan.FromSeconds(Math.Min(standardAge, capacityFullAge));
    }

    private async Task<FundingOfferActionResult?> ExecuteDecisionAsync(
        FundingDecision decision,
        FundingPlacementCandidate? candidate,
        IReadOnlyList<FundingOfferInfo> activeOffers,
        CancellationToken ct)
    {
        if (_options.DryRun || !decision.IsActionable)
            return null;

        switch (decision.Action)
        {
            case "place":
            case "place_parallel_offer":
            case "place_after_wait_fallback":
            {
                if (candidate is null)
                {
                    return CreateLocalActionResult(
                        action: decision.Action == "place_after_wait_fallback"
                            ? "submit_offer_after_wait_fallback"
                            : decision.Action == "place_parallel_offer"
                                ? "submit_parallel_offer"
                                : "submit_offer",
                        success: false,
                        symbol: decision.Symbol,
                        offerId: null,
                        status: "NO_CANDIDATE",
                        message: "Missing placement candidate for live funding submit.");
                }

                var request = BuildDecisionRequest(candidate, decision);
                var result = await _api.SubmitOfferAsync(request, ct).ConfigureAwait(false);
                if (result.Success && !string.IsNullOrWhiteSpace(result.OfferId))
                {
                    RememberManagedOffer(result.OfferId);
                    ClearLivePlacementWaitState(candidate.Symbol);
                    ClearManagedFallbackCarryForward(candidate.Symbol);
                }

                if (result.Success)
                {
                    await RefreshActiveOffersForSymbolAsync(candidate.Symbol, ct).ConfigureAwait(false);
                }

                return result;
            }
            case "replace_offer":
            {
                if (candidate is null)
                {
                    return CreateLocalActionResult(
                        action: "replace_offer",
                        success: false,
                        symbol: decision.Symbol,
                        offerId: null,
                        status: "NO_CANDIDATE",
                        message: "Missing placement candidate for live funding replace.");
                }

                var offer = !string.IsNullOrWhiteSpace(decision.TargetOfferId)
                    ? activeOffers.FirstOrDefault(activeOffer => string.Equals(activeOffer.OfferId, decision.TargetOfferId, StringComparison.Ordinal))
                    : activeOffers.Count == 1
                        ? activeOffers[0]
                        : null;

                if (offer is null)
                {
                    return CreateLocalActionResult(
                        action: "replace_offer",
                        success: false,
                        symbol: decision.Symbol,
                        offerId: null,
                        status: "AMBIGUOUS_STATE",
                        message: string.IsNullOrWhiteSpace(decision.TargetOfferId)
                            ? "Replace requires exactly one active offer or an explicit target offer id."
                            : $"Target active offer {decision.TargetOfferId} was not found.");
                }
                var cancelResult = await _api.CancelOfferAsync(offer.Symbol, offer.OfferId, ct).ConfigureAwait(false);
                if (!cancelResult.Success)
                {
                    return new FundingOfferActionResult(
                        Action: "replace_offer",
                        Success: false,
                        IsDryRun: false,
                        Symbol: decision.Symbol,
                        OfferId: offer.OfferId,
                        Status: cancelResult.Status,
                        Message: $"Failed to cancel active offer {offer.OfferId} for replace. {cancelResult.Message}",
                        Offer: cancelResult.Offer,
                        TimestampUtc: DateTime.UtcNow
                    );
                }

                ForgetManagedOffer(offer.OfferId);
                await RefreshActiveOffersForSymbolAsync(offer.Symbol, ct).ConfigureAwait(false);

                var targetRequest = BuildDecisionRequest(candidate, decision);
                var submitResult = await _api.SubmitOfferAsync(targetRequest, ct).ConfigureAwait(false);
                if (submitResult.Success && !string.IsNullOrWhiteSpace(submitResult.OfferId))
                {
                    RememberManagedOffer(submitResult.OfferId);
                    if (decision.Reason.Contains("managed_policy=KeepThenMotorFallback", StringComparison.OrdinalIgnoreCase))
                    {
                        RememberManagedFallbackCarryForward(
                            candidate.Symbol,
                            targetRequest,
                            offer.OfferId,
                            DateTime.UtcNow,
                            candidate.SymbolSettings);
                    }
                    else
                    {
                        ClearManagedFallbackCarryForward(candidate.Symbol);
                    }
                }

                await RefreshActiveOffersForSymbolAsync(candidate.Symbol, ct).ConfigureAwait(false);

                return new FundingOfferActionResult(
                    Action: "replace_offer",
                    Success: submitResult.Success,
                    IsDryRun: false,
                    Symbol: decision.Symbol,
                    OfferId: submitResult.Success ? submitResult.OfferId : offer.OfferId,
                    Status: submitResult.Success ? submitResult.Status : $"CANCELLED_{submitResult.Status}",
                    Message: submitResult.Success
                        ? $"Replaced active offer {offer.OfferId} with {submitResult.OfferId}. {submitResult.Message} {decision.Reason}"
                        : $"Cancelled active offer {offer.OfferId}, but replacement submit failed. {submitResult.Message} {decision.Reason}",
                    Offer: submitResult.Offer,
                    TimestampUtc: DateTime.UtcNow
                );
            }
            default:
                return null;
        }
    }

    private FundingOfferActionResult CreateLocalActionResult(
        string action,
        bool success,
        string symbol,
        string? offerId,
        string status,
        string message)
    {
        return new FundingOfferActionResult(
            Action: action,
            Success: success,
            IsDryRun: false,
            Symbol: symbol,
            OfferId: offerId,
            Status: status,
            Message: message,
            Offer: null,
            TimestampUtc: DateTime.UtcNow
        );
    }

    private static FundingOfferRequest BuildDecisionRequest(FundingPlacementCandidate candidate, FundingDecision decision)
    {
        return candidate.Request with
        {
            Amount = decision.ProposedAmount ?? candidate.Request.Amount,
            Rate = decision.ProposedRate ?? candidate.Request.Rate,
            PeriodDays = decision.ProposedPeriodDays ?? candidate.Request.PeriodDays
        };
    }

    private async Task RefreshActiveOffersForSymbolAsync(string symbol, CancellationToken ct)
    {
        try
        {
            var offers = await _api.GetActiveOffersAsync(new[] { symbol }, ct).ConfigureAwait(false);
            MergeOfferSnapshotFromRest(offers, new[] { symbol });
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[BFX-FUND] Failed to refresh active offers for symbol={Symbol} after live action.", symbol);
        }
    }

    private async Task PersistCycleAsync(
        IReadOnlyList<FundingWalletBalance> wallets,
        IReadOnlyList<FundingTickerSnapshot> tickers,
        IReadOnlyList<FundingOfferInfo> activeOffers,
        IReadOnlyList<FundingDecision> decisions,
        IReadOnlyList<FundingOfferActionResult> actionResults,
        IReadOnlyList<FundingShadowPlan> shadowPlans,
        IReadOnlyList<FundingShadowAction> shadowActions,
        IReadOnlyList<FundingShadowActionSession> shadowSessions,
        FundingLifecycleSyncResult lifecycleSync,
        object runtimeHealth,
        CancellationToken ct)
    {
        if (_fundingRepo is not null)
        {
            var batch = BuildFundingPersistenceBatch(wallets, tickers, activeOffers, decisions, actionResults, shadowPlans, shadowActions, shadowSessions, lifecycleSync, runtimeHealth);
            await _fundingRepo.PersistCycleAsync(batch, ct).ConfigureAwait(false);
        }

        if (_snapshotRepo is null)
            return;

        var records = new List<CryptoSnapshotRecord>(wallets.Count + tickers.Count + activeOffers.Count + decisions.Count + actionResults.Count + shadowPlans.Count + shadowActions.Count + shadowSessions.Count + 1);

        foreach (var wallet in wallets)
        {
            records.Add(new CryptoSnapshotRecord(
                Utc: DateTime.UtcNow,
                Exchange: "bitfinex",
                Symbol: $"{wallet.WalletType}:{wallet.Currency}",
                SnapshotType: "funding_wallet",
                Data: wallet
            ));
        }

        foreach (var ticker in tickers)
        {
            records.Add(new CryptoSnapshotRecord(
                Utc: ticker.TimestampUtc,
                Exchange: "bitfinex",
                Symbol: ticker.Symbol,
                SnapshotType: "funding_market",
                Data: ticker
            ));
        }

        foreach (var offer in activeOffers)
        {
            records.Add(new CryptoSnapshotRecord(
                Utc: offer.UpdatedUtc ?? offer.CreatedUtc ?? DateTime.UtcNow,
                Exchange: "bitfinex",
                Symbol: offer.Symbol,
                SnapshotType: "funding_offer",
                Data: offer
            ));
        }

        foreach (var decision in decisions)
        {
            records.Add(new CryptoSnapshotRecord(
                Utc: decision.TimestampUtc,
                Exchange: "bitfinex",
                Symbol: decision.Symbol,
                SnapshotType: "funding_decision",
                Data: decision
            ));
        }

        foreach (var actionResult in actionResults)
        {
            records.Add(new CryptoSnapshotRecord(
                Utc: actionResult.TimestampUtc,
                Exchange: "bitfinex",
                Symbol: actionResult.Symbol,
                SnapshotType: "funding_action_result",
                Data: actionResult
            ));
        }

        foreach (var shadowPlan in shadowPlans)
        {
            records.Add(new CryptoSnapshotRecord(
                Utc: shadowPlan.TimestampUtc,
                Exchange: "bitfinex",
                Symbol: shadowPlan.Symbol,
                SnapshotType: "funding_shadow_plan",
                Data: shadowPlan
            ));
        }

        foreach (var shadowAction in shadowActions)
        {
            records.Add(new CryptoSnapshotRecord(
                Utc: shadowAction.TimestampUtc,
                Exchange: "bitfinex",
                Symbol: shadowAction.Symbol,
                SnapshotType: "funding_shadow_action",
                Data: shadowAction
            ));
        }

        foreach (var shadowSession in shadowSessions)
        {
            records.Add(new CryptoSnapshotRecord(
                Utc: shadowSession.LastUpdatedUtc,
                Exchange: "bitfinex",
                Symbol: shadowSession.Symbol,
                SnapshotType: "funding_shadow_session",
                Data: shadowSession
            ));
        }

        records.Add(new CryptoSnapshotRecord(
            Utc: DateTime.UtcNow,
            Exchange: "bitfinex",
            Symbol: "runtime",
            SnapshotType: "funding_runtime_health",
            Data: runtimeHealth
        ));

        await _snapshotRepo.BatchInsertAsync(records, ct).ConfigureAwait(false);
    }

    private async Task MaybeLogPerformanceReportAsync(
        IReadOnlyList<string> preferredSymbols,
        CancellationToken ct)
    {
        if (_fundingRepo is null || preferredSymbols.Count == 0)
            return;

        var reportingSettings = preferredSymbols
            .Select(ResolveSymbolSettings)
            .Where(settings => settings.Enabled && settings.EnableFundingPerformanceReports)
            .ToArray();

        if (reportingSettings.Length == 0)
            return;

        var intervalMinutes = Math.Max(
            5,
            reportingSettings.Min(settings => settings.FundingPerformanceReportIntervalMinutes));

        var nowUtc = DateTime.UtcNow;
        if (_lastPerformanceReportUtc != default &&
            nowUtc - _lastPerformanceReportUtc < TimeSpan.FromMinutes(intervalMinutes))
        {
            return;
        }

        var nowLocal = DateTimeOffset.Now;
        var todayStartLocal = new DateTimeOffset(nowLocal.Year, nowLocal.Month, nowLocal.Day, 0, 0, 0, nowLocal.Offset);
        var yesterdayStartLocal = todayStartLocal.AddDays(-1);
        var rolling7dStartLocal = todayStartLocal.AddDays(-6);
        var monthStartLocal = new DateTimeOffset(nowLocal.Year, nowLocal.Month, 1, 0, 0, 0, nowLocal.Offset);

        var snapshot = await _fundingRepo.GetPerformanceSnapshotAsync(
            "bitfinex",
            preferredSymbols,
            todayStartLocal.UtcDateTime,
            yesterdayStartLocal.UtcDateTime,
            rolling7dStartLocal.UtcDateTime,
            monthStartLocal.UtcDateTime,
            ct).ConfigureAwait(false);

        if (snapshot is null)
            return;

        _lastPerformanceReportUtc = nowUtc;

        _log.Information(
            "[BFX-FUND-REPORT] symbols={Symbols} active={ActiveCycles} closed={ClosedCycles} activePrincipal={ActivePrincipal:F2} walletTotal={WalletTotal:F2} walletAvailable={WalletAvailable:F2} utilization={Utilization:P2} idle={Idle:P2} avgRedeployMin7d={AvgRedeployMin7d:F1} todayNet={TodayNet:F8} yesterdayNet={YesterdayNet:F8} rolling7dNet={Rolling7dNet:F8} monthNet={MonthNet:F8} totalNet={TotalNet:F8} avgClosedNet={AvgClosedNet:F8} rolling7dApr={Rolling7dApr:P2} lastPaymentUtc={LastPaymentUtc} lastReturnedUtc={LastReturnedUtc}",
            string.Join(",", preferredSymbols),
            snapshot.ActiveCycles,
            snapshot.ClosedCycles,
            snapshot.ActivePrincipal,
            snapshot.CurrentTotalBalance,
            snapshot.CurrentAvailableBalance,
            snapshot.UtilizationPct,
            snapshot.IdleCapitalPct,
            snapshot.AvgRedeployMinutesRolling7d,
            snapshot.TodayNetInterest,
            snapshot.YesterdayNetInterest,
            snapshot.Rolling7dNetInterest,
            snapshot.MonthToDateNetInterest,
            snapshot.TotalNetInterest,
            snapshot.AvgNetInterestClosedCycle,
            snapshot.Rolling7dSimpleApr,
            snapshot.LastPaymentUtc,
            snapshot.LastPrincipalReturnedUtc);

        foreach (var symbol in snapshot.Symbols)
        {
            _log.Information(
                "[BFX-FUND-REPORT] symbol={Symbol} active={ActiveCycles} closed={ClosedCycles} activePrincipal={ActivePrincipal:F2} walletTotal={WalletTotal:F2} walletAvailable={WalletAvailable:F2} utilization={Utilization:P2} idle={Idle:P2} avgRedeployMin7d={AvgRedeployMin7d:F1} todayNet={TodayNet:F8} yesterdayNet={YesterdayNet:F8} rolling7dNet={Rolling7dNet:F8} monthNet={MonthNet:F8} totalNet={TotalNet:F8} rolling7dApr={Rolling7dApr:P2} lastPaymentUtc={LastPaymentUtc} lastReturnedUtc={LastReturnedUtc}",
                symbol.Symbol,
                symbol.ActiveCycles,
                symbol.ClosedCycles,
                symbol.ActivePrincipal,
                symbol.CurrentTotalBalance,
                symbol.CurrentAvailableBalance,
                symbol.UtilizationPct,
                symbol.IdleCapitalPct,
                symbol.AvgRedeployMinutesRolling7d,
                symbol.TodayNetInterest,
                symbol.YesterdayNetInterest,
                symbol.Rolling7dNetInterest,
                symbol.MonthToDateNetInterest,
                symbol.TotalNetInterest,
                symbol.Rolling7dSimpleApr,
                symbol.LastPaymentUtc,
                symbol.LastPrincipalReturnedUtc);
        }
    }

    private async Task MaybeLogDecisionQualityReportAsync(
        IReadOnlyList<string> preferredSymbols,
        CancellationToken ct)
    {
        if (_fundingRepo is null || preferredSymbols.Count == 0)
            return;

        var reportingSettings = preferredSymbols
            .Select(ResolveSymbolSettings)
            .Where(settings => settings.Enabled && settings.EnableFundingPerformanceReports)
            .ToArray();

        if (reportingSettings.Length == 0)
            return;

        var intervalMinutes = Math.Max(
            5,
            reportingSettings.Min(settings => settings.FundingPerformanceReportIntervalMinutes));

        var nowUtc = DateTime.UtcNow;
        if (_lastPerformanceReportUtc == default ||
            nowUtc - _lastPerformanceReportUtc > TimeSpan.FromMinutes(1) ||
            nowUtc - _lastPerformanceReportUtc < TimeSpan.Zero ||
            nowUtc - _lastPerformanceReportUtc > TimeSpan.FromMinutes(intervalMinutes))
        {
            return;
        }

        var snapshot = await _fundingRepo.GetDecisionQualitySnapshotAsync(
            "bitfinex",
            preferredSymbols,
            ct).ConfigureAwait(false);

        if (snapshot is null)
            return;

        _log.Information(
            "[BFX-FUND-QUALITY] symbols={Symbols} tracked={Tracked} actionable={Actionable} liveSeen={LiveSeen} liveMatchesShadow={LiveMatchesShadow} openShadowSessions={OpenSessions}",
            string.Join(",", preferredSymbols),
            snapshot.SymbolCount,
            snapshot.ActionableSymbolCount,
            snapshot.SymbolsWithLiveActionCount,
            snapshot.LiveActionMatchesShadowCount,
            snapshot.OpenShadowSessionCount);

        foreach (var symbol in snapshot.Symbols)
        {
            _log.Information(
                "[BFX-FUND-QUALITY] symbol={Symbol} regime={Regime} actionable={Actionable} liveAction={LiveAction} liveMatchesShadow={LiveMatchesShadow} motorAction={MotorAction} oppAction={OppAction} motorStatus={MotorStatus} oppStatus={OppStatus} openShadowSession={OpenSession} totalNet={TotalNet:F8} summary={Summary}",
                symbol.Symbol,
                symbol.LatestRegime,
                symbol.HasActionableShadowAction,
                symbol.LastLiveAction,
                symbol.LiveActionMatchesShadow,
                symbol.MotorAction,
                symbol.OpportunisticAction,
                symbol.MotorStatus,
                symbol.OpportunisticStatus,
                symbol.HasOpenShadowSession,
                symbol.TotalNetInterest,
                symbol.LatestSummary);
        }
    }

    private FundingPersistenceBatch BuildFundingPersistenceBatch(
        IReadOnlyList<FundingWalletBalance> wallets,
        IReadOnlyList<FundingTickerSnapshot> tickers,
        IReadOnlyList<FundingOfferInfo> activeOffers,
        IReadOnlyList<FundingDecision> decisions,
        IReadOnlyList<FundingOfferActionResult> actionResults,
        IReadOnlyList<FundingShadowPlan> shadowPlans,
        IReadOnlyList<FundingShadowAction> shadowActions,
        IReadOnlyList<FundingShadowActionSession> shadowSessions,
        FundingLifecycleSyncResult lifecycleSync,
        object runtimeHealthMetadata)
    {
        var actionResultsBySymbol = actionResults
            .GroupBy(static x => x.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static g => g.Key, static g => g.Last(), StringComparer.OrdinalIgnoreCase);

        var walletRows = wallets
            .Select(wallet => CreateWalletSnapshotRecord(wallet, "rest_cycle"))
            .ToArray();

        var marketRows = tickers
            .Select(ticker => new FundingMarketSnapshotRecord(
                Utc: ticker.TimestampUtc,
                Exchange: "bitfinex",
                Symbol: ticker.Symbol,
                Frr: ticker.Frr,
                BidRate: ticker.BidRate,
                BidPeriodDays: ticker.BidPeriodDays,
                BidSize: ticker.BidSize,
                AskRate: ticker.AskRate,
                AskPeriodDays: ticker.AskPeriodDays,
                AskSize: ticker.AskSize))
            .ToArray();

        var actionRows = decisions
            .Select(decision =>
            {
                actionResultsBySymbol.TryGetValue(decision.Symbol, out var actionResult);
                return new FundingOfferActionRecord(
                    Utc: decision.TimestampUtc,
                    Exchange: "bitfinex",
                    Symbol: decision.Symbol,
                    Action: decision.Action,
                    DryRun: decision.IsDryRun,
                    IsActionable: decision.IsActionable,
                    Currency: decision.Currency,
                    WalletType: decision.WalletType,
                    AvailableBalance: decision.AvailableBalance,
                    LendableBalance: decision.LendableBalance,
                    Amount: decision.ProposedAmount,
                    Rate: decision.ProposedRate,
                    PeriodDays: decision.ProposedPeriodDays,
                    Reason: decision.Reason,
                    OfferId: ParseOfferId(actionResult?.OfferId),
                    CorrelationId: null,
                    Metadata: actionResult is null
                        ? null
                        : new
                        {
                            actionResult.Action,
                            actionResult.Success,
                            actionResult.Status,
                            actionResult.Message,
                            actionResult.TimestampUtc
                        });
            })
            .ToArray();

        var offerRowsById = new Dictionary<long, FundingOfferStateRecord>();
        foreach (var offer in activeOffers)
        {
            var row = ToOfferStateRecord(offer, new
            {
                Source = "rest_cycle",
                ManagedByEngine = IsManagedOffer(offer.OfferId)
            });

            if (row is not null)
            {
                offerRowsById[row.OfferId] = row;
            }
        }

        foreach (var actionResult in actionResults)
        {
            if (actionResult.Offer is null)
                continue;

            var row = ToOfferStateRecord(actionResult.Offer, new
            {
                Source = $"action_result:{actionResult.Action}",
                actionResult.Success,
                actionResult.Status
            });

            if (row is not null)
            {
                offerRowsById[row.OfferId] = row;
            }
        }

        var eventRows = actionResults
            .Select(ToOfferEventRecord)
            .Where(static row => row is not null)
            .Cast<FundingOfferEventRecord>()
            .ToArray();

        var shadowRows = shadowPlans
            .SelectMany(ToShadowPlanRecords)
            .ToArray();

        var shadowActionRows = shadowActions
            .Select(ToShadowActionRecord)
            .ToArray();

        var shadowSessionRows = shadowSessions
            .Select(ToShadowActionSessionRecord)
            .ToArray();

        var runtimeHealth = CreateRuntimeHealthRecord(runtimeHealthMetadata);

        return new FundingPersistenceBatch(
            WalletSnapshots: walletRows,
            MarketSnapshots: marketRows,
            OfferActions: actionRows,
            Offers: offerRowsById.Values.ToArray(),
            OfferEvents: eventRows,
            ShadowPlans: shadowRows,
            ShadowActions: shadowActionRows,
            ShadowSessions: shadowSessionRows,
            Credits: lifecycleSync.Credits,
            Loans: lifecycleSync.Loans,
            Trades: lifecycleSync.Trades,
            InterestEntries: lifecycleSync.InterestEntries,
            InterestAllocations: lifecycleSync.InterestAllocations,
            CapitalEvents: lifecycleSync.CapitalEvents,
            RuntimeHealth: runtimeHealth,
            ReconciliationLog: lifecycleSync.ReconciliationLog);
    }

    private IReadOnlyList<FundingShadowPlanRecord> ToShadowPlanRecords(FundingShadowPlan plan)
    {
        if (plan.Buckets.Count == 0)
        {
            return
            [
                new FundingShadowPlanRecord(
                    Utc: plan.TimestampUtc,
                    Exchange: "bitfinex",
                    PlanKey: CreateShadowPlanKey(plan.Symbol, plan.TimestampUtc, "NONE"),
                    Symbol: plan.Symbol,
                    Currency: plan.Currency,
                    Regime: plan.Regime,
                    Bucket: "NONE",
                    AvailableBalance: plan.AvailableBalance,
                    LendableBalance: plan.LendableBalance,
                    AllocationAmount: 0m,
                    AllocationFraction: 0m,
                    TargetRate: null,
                    TargetPeriodDays: null,
                    MaxWaitMinutes: null,
                    Role: "inactive",
                    FallbackBucket: null,
                    MarketAskRate: plan.MarketAskRate,
                    MarketBidRate: plan.MarketBidRate,
                    Summary: plan.Summary,
                    Metadata: new
                    {
                        plan.Regime,
                        BucketCount = 0
                    })
            ];
        }

        return plan.Buckets
            .Select(bucket => new FundingShadowPlanRecord(
                Utc: plan.TimestampUtc,
                Exchange: "bitfinex",
                PlanKey: CreateShadowPlanKey(plan.Symbol, plan.TimestampUtc, bucket.Bucket),
                Symbol: plan.Symbol,
                Currency: plan.Currency,
                Regime: plan.Regime,
                Bucket: bucket.Bucket,
                AvailableBalance: plan.AvailableBalance,
                LendableBalance: plan.LendableBalance,
                AllocationAmount: bucket.AllocationAmount,
                AllocationFraction: bucket.AllocationFraction,
                TargetRate: bucket.TargetRate,
                TargetPeriodDays: bucket.TargetPeriodDays,
                MaxWaitMinutes: bucket.MaxWaitMinutes,
                Role: bucket.Role,
                FallbackBucket: bucket.FallbackBucket,
                MarketAskRate: plan.MarketAskRate,
                MarketBidRate: plan.MarketBidRate,
                Summary: plan.Summary,
                Metadata: new
                {
                    plan.Regime,
                    BucketCount = plan.Buckets.Count
                }))
            .ToArray();
    }

    private FundingShadowActionRecord ToShadowActionRecord(FundingShadowAction action)
    {
        return new FundingShadowActionRecord(
            Utc: action.TimestampUtc,
            Exchange: "bitfinex",
            ActionKey: CreateShadowActionKey(action.Symbol, action.TimestampUtc, action.Bucket, action.Action),
            PlanKey: CreateShadowPlanKey(action.Symbol, action.TimestampUtc, action.Bucket),
            Symbol: action.Symbol,
            Currency: action.Currency,
            Regime: action.Regime,
            Bucket: action.Bucket,
            Action: action.Action,
            IsActionable: action.IsActionable,
            AvailableBalance: action.AvailableBalance,
            LendableBalance: action.LendableBalance,
            AllocationAmount: action.AllocationAmount,
            AllocationFraction: action.AllocationFraction,
            TargetRate: action.TargetRate,
            FallbackRate: action.FallbackRate,
            TargetPeriodDays: action.TargetPeriodDays,
            MaxWaitMinutes: action.MaxWaitMinutes,
            DecisionDeadlineUtc: action.DecisionDeadlineUtc,
            Role: action.Role,
            FallbackBucket: action.FallbackBucket,
            ActiveOfferCount: action.ActiveOfferCount,
            ActiveOfferId: action.ActiveOfferId,
            ActiveOfferRate: action.ActiveOfferRate,
            ActiveOfferAmount: action.ActiveOfferAmount,
            ActiveOfferStatus: action.ActiveOfferStatus,
            Reason: action.Reason,
            Summary: action.Summary,
            Metadata: new
            {
                action.Regime,
                action.ActiveOfferCount,
                action.FallbackBucket
            });
    }

    private FundingShadowActionSessionRecord ToShadowActionSessionRecord(FundingShadowActionSession session)
    {
        return new FundingShadowActionSessionRecord(
            Exchange: "bitfinex",
            SessionKey: session.SessionKey,
            Symbol: session.Symbol,
            Currency: session.Currency,
            Bucket: session.Bucket,
            FirstRegime: session.FirstRegime,
            CurrentRegime: session.CurrentRegime,
            FirstAction: session.FirstAction,
            CurrentAction: session.CurrentAction,
            Status: session.Status,
            IsActionable: session.IsActionable,
            AvailableBalance: session.AvailableBalance,
            LendableBalance: session.LendableBalance,
            AllocationAmount: session.AllocationAmount,
            AllocationFraction: session.AllocationFraction,
            TargetRateInitial: session.TargetRateInitial,
            TargetRateCurrent: session.TargetRateCurrent,
            FallbackRate: session.FallbackRate,
            TargetPeriodDays: session.TargetPeriodDays,
            MaxWaitMinutes: session.MaxWaitMinutes,
            OpenedUtc: session.OpenedUtc,
            LastUpdatedUtc: session.LastUpdatedUtc,
            DecisionDeadlineUtc: session.DecisionDeadlineUtc,
            ClosedUtc: session.ClosedUtc,
            ActiveOfferId: session.ActiveOfferId,
            ActiveOfferRate: session.ActiveOfferRate,
            ActiveOfferAmount: session.ActiveOfferAmount,
            ActiveOfferStatus: session.ActiveOfferStatus,
            Resolution: session.Resolution,
            UpdateCount: session.UpdateCount,
            Summary: session.Summary,
            Metadata: session.Metadata);
    }

    private static string CreateShadowPlanKey(string symbol, DateTime timestampUtc, string bucket)
    {
        return $"{symbol}:{timestampUtc:O}:{bucket}";
    }

    private static string CreateShadowActionKey(string symbol, DateTime timestampUtc, string bucket, string action)
    {
        return $"{symbol}:{timestampUtc:O}:{bucket}:{action}";
    }

    private static string CreateShadowSessionSlotKey(string symbol, string bucket)
    {
        return $"{symbol}|{bucket}";
    }

    private static string CreateShadowSessionKey(string symbol, string bucket, DateTime openedUtc)
    {
        return $"{symbol}:{bucket}:{openedUtc:O}";
    }

    private bool ShouldRefreshLifecycleFromRest()
    {
        var nowUtc = DateTime.UtcNow;
        if (_lastLifecycleSyncUtc == default)
            return true;

        var lifecycleInterval = TimeSpan.FromSeconds(Math.Max(30, _options.RestLifecycleSyncIntervalSeconds));
        return (nowUtc - _lastLifecycleSyncUtc) >= lifecycleInterval;
    }

    private async Task<FundingLifecycleSyncResult> SyncLifecycleStateAsync(
        IReadOnlyList<string> preferredSymbols,
        CancellationToken ct)
    {
        if (preferredSymbols.Count == 0)
            return FundingLifecycleSyncResult.Empty;

        var startedUtc = DateTime.UtcNow;
        var currencies = preferredSymbols
            .Select(FundingSymbolToCurrency)
            .Where(static currency => !string.IsNullOrWhiteSpace(currency))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var activeCreditsTask = _api.GetActiveCreditsAsync(preferredSymbols, ct);
        var creditHistoryTask = _api.GetCreditHistoryAsync(preferredSymbols, ct);
        var activeLoansTask = _api.GetActiveLoansAsync(preferredSymbols, ct);
        var loanHistoryTask = _api.GetLoanHistoryAsync(preferredSymbols, ct);
        var tradeHistoryTask = _api.GetFundingTradeHistoryAsync(preferredSymbols, ct);
        var ledgerEntriesTask = _api.GetLedgerEntriesAsync(currencies, ct);

        await Task.WhenAll(
            activeCreditsTask,
            creditHistoryTask,
            activeLoansTask,
            loanHistoryTask,
            tradeHistoryTask,
            ledgerEntriesTask).ConfigureAwait(false);

        var lookbackCutoffUtc = DateTime.UtcNow.AddDays(-Math.Max(1, _options.HistoryLookbackDays));
        var activeCredits = activeCreditsTask.Result;
        var creditHistory = creditHistoryTask.Result
            .Where(credit => GetLifecycleRelevantUtc(credit.CreatedUtc, credit.UpdatedUtc, credit.OpenedUtc, credit.LastPayoutUtc) >= lookbackCutoffUtc)
            .ToArray();
        var activeLoans = activeLoansTask.Result;
        var loanHistory = loanHistoryTask.Result
            .Where(loan => GetLifecycleRelevantUtc(loan.CreatedUtc, loan.UpdatedUtc, loan.OpenedUtc, loan.LastPayoutUtc) >= lookbackCutoffUtc)
            .ToArray();
        var tradeHistory = tradeHistoryTask.Result
            .Where(trade => trade.Utc >= lookbackCutoffUtc)
            .ToArray();
        var ledgerEntries = ledgerEntriesTask.Result
            .Where(entry => entry.Utc >= lookbackCutoffUtc)
            .ToArray();

        var creditRows = BuildCreditStateRecords(activeCredits, creditHistory);
        var loanRows = BuildLoanStateRecords(activeLoans, loanHistory);
        var tradeRows = BuildTradeRecords(tradeHistory, creditRows, loanRows);
        var interestRows = BuildInterestLedgerRecords(ledgerEntries, tradeRows, creditRows, loanRows);
        var interestAllocations = BuildInterestAllocations(interestRows, tradeRows, creditRows, loanRows);
        var capitalEvents = BuildCapitalEvents(creditRows, loanRows, tradeRows, interestAllocations);
        var completedUtc = DateTime.UtcNow;

        _lastLifecycleSyncUtc = completedUtc;

        return new FundingLifecycleSyncResult(
            SyncedUtc: completedUtc,
            Credits: creditRows,
            Loans: loanRows,
            Trades: tradeRows,
            InterestEntries: interestRows,
            InterestAllocations: interestAllocations,
            CapitalEvents: capitalEvents,
            ReconciliationLog: BuildLifecycleReconciliationLog(
                startedUtc,
                completedUtc,
                preferredSymbols,
                creditRows,
                loanRows,
                tradeRows,
                interestRows,
                interestAllocations,
                capitalEvents));
    }

    private IReadOnlyList<FundingCreditStateRecord> BuildCreditStateRecords(
        IReadOnlyList<FundingCreditInfo> activeCredits,
        IReadOnlyList<FundingCreditInfo> creditHistory)
    {
        var rowsById = new Dictionary<long, FundingCreditStateRecord>();

        foreach (var credit in creditHistory)
        {
            var row = ToCreditStateRecord(credit);
            rowsById[row.CreditId] = row;
        }

        foreach (var credit in activeCredits)
        {
            var row = ToCreditStateRecord(credit);
            rowsById[row.CreditId] = row;
        }

        return rowsById.Values
            .OrderByDescending(row => row.UpdatedUtc ?? row.OpenedUtc ?? row.CreatedUtc ?? DateTime.MinValue)
            .ToArray();
    }

    private IReadOnlyList<FundingLoanStateRecord> BuildLoanStateRecords(
        IReadOnlyList<FundingLoanInfo> activeLoans,
        IReadOnlyList<FundingLoanInfo> loanHistory)
    {
        var rowsById = new Dictionary<long, FundingLoanStateRecord>();

        foreach (var loan in loanHistory)
        {
            var row = ToLoanStateRecord(loan);
            rowsById[row.LoanId] = row;
        }

        foreach (var loan in activeLoans)
        {
            var row = ToLoanStateRecord(loan);
            rowsById[row.LoanId] = row;
        }

        return rowsById.Values
            .OrderByDescending(row => row.UpdatedUtc ?? row.OpenedUtc ?? row.CreatedUtc ?? DateTime.MinValue)
            .ToArray();
    }

    private IReadOnlyList<FundingTradeRecord> BuildTradeRecords(
        IReadOnlyList<FundingTradeInfo> trades,
        IReadOnlyList<FundingCreditStateRecord> credits,
        IReadOnlyList<FundingLoanStateRecord> loans)
    {
        return trades
            .OrderByDescending(static trade => trade.Utc)
            .Select(trade =>
            {
                var creditCandidates = FindMatchingCredits(trade, credits);
                var loanCandidates = FindMatchingLoans(trade, loans);
                var matchedCredit = SelectPreferredCreditMatch(trade, creditCandidates);
                var matchedLoan = SelectPreferredLoanMatch(trade, loanCandidates);

                return new FundingTradeRecord(
                    Utc: trade.Utc,
                    Exchange: "bitfinex",
                    FundingTradeId: trade.FundingTradeId,
                    Symbol: trade.Symbol,
                    OfferId: trade.OfferId,
                    CreditId: matchedCredit?.CreditId,
                    LoanId: matchedLoan?.LoanId,
                    Amount: trade.Amount,
                    Rate: trade.Rate,
                    PeriodDays: trade.PeriodDays,
                    Maker: trade.Maker,
                    Metadata: new
                    {
                        CreditMatchCount = creditCandidates.Count,
                        LoanMatchCount = loanCandidates.Count,
                        MatchedCreditId = matchedCredit?.CreditId,
                        MatchedLoanId = matchedLoan?.LoanId,
                        trade.Metadata
                    });
            })
            .ToArray();
    }

    private IReadOnlyList<FundingInterestLedgerRecord> BuildInterestLedgerRecords(
        IReadOnlyList<FundingLedgerEntry> ledgerEntries,
        IReadOnlyList<FundingTradeRecord> trades,
        IReadOnlyList<FundingCreditStateRecord> credits,
        IReadOnlyList<FundingLoanStateRecord> loans)
    {
        var interestEntries = ledgerEntries
            .Where(entry => string.Equals(entry.EntryType, "margin_funding_payment", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static entry => entry.Utc)
            .ToArray();

        return interestEntries
            .Select(entry =>
            {
                var symbol = CurrencyToFundingSymbol(entry.Currency);
                var tradeCandidates = trades
                    .Where(trade =>
                        string.Equals(trade.Symbol, symbol, StringComparison.OrdinalIgnoreCase) &&
                        trade.Utc <= entry.Utc &&
                        trade.Utc >= entry.Utc.AddDays(-3))
                    .OrderByDescending(static trade => trade.Utc)
                    .Take(5)
                    .ToArray();

                var creditCandidates = credits
                    .Where(credit =>
                        string.Equals(credit.Symbol, symbol, StringComparison.OrdinalIgnoreCase) &&
                        IsLifecycleRecordRelevantAt(credit.CreatedUtc, credit.OpenedUtc, credit.ClosedUtc, entry.Utc))
                    .Take(5)
                    .ToArray();

                var loanCandidates = loans
                    .Where(loan =>
                        string.Equals(loan.Symbol, symbol, StringComparison.OrdinalIgnoreCase) &&
                        IsLifecycleRecordRelevantAt(loan.CreatedUtc, loan.OpenedUtc, loan.ClosedUtc, entry.Utc))
                    .Take(5)
                    .ToArray();

                return new FundingInterestLedgerRecord(
                    Utc: entry.Utc,
                    Exchange: "bitfinex",
                    LedgerId: entry.LedgerId,
                    Currency: entry.Currency,
                    WalletType: entry.WalletType,
                    Symbol: symbol,
                    EntryType: entry.EntryType,
                    CreditId: creditCandidates.Length == 1 ? creditCandidates[0].CreditId : null,
                    LoanId: loanCandidates.Length == 1 ? loanCandidates[0].LoanId : null,
                    FundingTradeId: tradeCandidates.Length == 1 ? tradeCandidates[0].FundingTradeId : null,
                    RawAmount: entry.Amount,
                    BalanceAfter: entry.BalanceAfter,
                    GrossInterest: entry.Amount,
                    FeeAmount: 0m,
                    NetInterest: entry.Amount,
                    Description: entry.Description,
                    Metadata: new
                    {
                        AmountInterpretation = "ledger_credit_amount",
                        TradeMatchCount = tradeCandidates.Length,
                        CreditMatchCount = creditCandidates.Length,
                        LoanMatchCount = loanCandidates.Length,
                        entry.Metadata
                    });
            })
            .ToArray();
    }

    private IReadOnlyList<FundingInterestAllocationRecord> BuildInterestAllocations(
        IReadOnlyList<FundingInterestLedgerRecord> interestEntries,
        IReadOnlyList<FundingTradeRecord> trades,
        IReadOnlyList<FundingCreditStateRecord> credits,
        IReadOnlyList<FundingLoanStateRecord> loans)
    {
        var allocations = new List<FundingInterestAllocationRecord>(interestEntries.Count * 2);

        foreach (var entry in interestEntries)
        {
            var entryAllocations = BuildInterestAllocationsForEntry(entry, trades, credits, loans);
            if (entryAllocations.Count == 0)
                continue;

            allocations.AddRange(entryAllocations);
        }

        return allocations
            .OrderByDescending(static allocation => allocation.Utc)
            .ThenBy(static allocation => allocation.AllocationKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IReadOnlyList<FundingInterestAllocationRecord> BuildInterestAllocationsForEntry(
        FundingInterestLedgerRecord entry,
        IReadOnlyList<FundingTradeRecord> trades,
        IReadOnlyList<FundingCreditStateRecord> credits,
        IReadOnlyList<FundingLoanStateRecord> loans)
    {
        if (entry.CreditId.HasValue)
        {
            var tradeId = ResolveTradeIdForLifecycle(
                explicitTradeId: entry.FundingTradeId,
                symbol: entry.Symbol,
                eventUtc: entry.Utc,
                creditId: entry.CreditId,
                loanId: null,
                trades: trades);
            return new[]
            {
                CreateInterestAllocationRecord(
                    entry,
                    allocationKey: $"ledger:{entry.LedgerId}:credit:{entry.CreditId.Value}",
                    creditId: entry.CreditId,
                    loanId: null,
                    fundingTradeId: tradeId,
                    fraction: 1m,
                    method: "direct_credit_match",
                    confidence: "high")
            };
        }

        if (entry.LoanId.HasValue)
        {
            var tradeId = ResolveTradeIdForLifecycle(
                explicitTradeId: entry.FundingTradeId,
                symbol: entry.Symbol,
                eventUtc: entry.Utc,
                creditId: null,
                loanId: entry.LoanId,
                trades: trades);
            return new[]
            {
                CreateInterestAllocationRecord(
                    entry,
                    allocationKey: $"ledger:{entry.LedgerId}:loan:{entry.LoanId.Value}",
                    creditId: null,
                    loanId: entry.LoanId,
                    fundingTradeId: tradeId,
                    fraction: 1m,
                    method: "direct_loan_match",
                    confidence: "high")
            };
        }

        var candidates = BuildInterestAllocationCandidates(entry, trades, credits, loans);
        if (candidates.Count == 0)
        {
            if (entry.FundingTradeId.HasValue)
            {
                return new[]
                {
                    CreateInterestAllocationRecord(
                        entry,
                        allocationKey: $"ledger:{entry.LedgerId}:trade:{entry.FundingTradeId.Value}",
                        creditId: null,
                        loanId: null,
                        fundingTradeId: entry.FundingTradeId,
                        fraction: 1m,
                        method: "trade_only_match",
                        confidence: "low")
                };
            }

            return Array.Empty<FundingInterestAllocationRecord>();
        }

        if (candidates.Count == 1)
        {
            var candidate = candidates[0];
            return new[]
            {
                CreateInterestAllocationRecord(
                    entry,
                    allocationKey: candidate.Kind == "credit"
                        ? $"ledger:{entry.LedgerId}:credit:{candidate.LifecycleId}"
                        : $"ledger:{entry.LedgerId}:loan:{candidate.LifecycleId}",
                    creditId: candidate.Kind == "credit" ? candidate.LifecycleId : null,
                    loanId: candidate.Kind == "loan" ? candidate.LifecycleId : null,
                    fundingTradeId: candidate.FundingTradeId,
                    fraction: 1m,
                    method: candidate.Kind == "credit" ? "single_credit_window_match" : "single_loan_window_match",
                    confidence: "high")
            };
        }

        var totalWeight = candidates.Sum(static candidate => candidate.Weight);
        var safeTotalWeight = totalWeight <= 0m ? candidates.Count : totalWeight;
        var lastIndex = candidates.Count - 1;
        var allocatedFraction = 0m;
        var rows = new List<FundingInterestAllocationRecord>(candidates.Count);

        for (int i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var fraction = i == lastIndex
                ? 1m - allocatedFraction
                : Math.Round(candidate.Weight / safeTotalWeight, 12, MidpointRounding.AwayFromZero);

            if (fraction < 0m)
                fraction = 0m;

            allocatedFraction += fraction;
            rows.Add(CreateInterestAllocationRecord(
                entry,
                allocationKey: candidate.Kind == "credit"
                    ? $"ledger:{entry.LedgerId}:credit:{candidate.LifecycleId}"
                    : $"ledger:{entry.LedgerId}:loan:{candidate.LifecycleId}",
                creditId: candidate.Kind == "credit" ? candidate.LifecycleId : null,
                loanId: candidate.Kind == "loan" ? candidate.LifecycleId : null,
                fundingTradeId: candidate.FundingTradeId,
                fraction: fraction,
                method: "pro_rata_open_amount",
                confidence: "medium"));
        }

        return rows;
    }

    private IReadOnlyList<FundingLifecycleAllocationCandidate> BuildInterestAllocationCandidates(
        FundingInterestLedgerRecord entry,
        IReadOnlyList<FundingTradeRecord> trades,
        IReadOnlyList<FundingCreditStateRecord> credits,
        IReadOnlyList<FundingLoanStateRecord> loans)
    {
        var symbol = string.IsNullOrWhiteSpace(entry.Symbol) ? CurrencyToFundingSymbol(entry.Currency) : entry.Symbol;
        if (string.IsNullOrWhiteSpace(symbol))
            return Array.Empty<FundingLifecycleAllocationCandidate>();

        var creditCandidates = credits
            .Where(credit =>
                string.Equals(credit.Symbol, symbol, StringComparison.OrdinalIgnoreCase) &&
                IsLifecycleRecordRelevantAt(credit.CreatedUtc, credit.OpenedUtc, credit.ClosedUtc, entry.Utc))
            .Select(credit => new FundingLifecycleAllocationCandidate(
                Kind: "credit",
                LifecycleId: credit.CreditId,
                Symbol: credit.Symbol,
                Weight: Math.Abs(credit.OriginalAmount ?? credit.Amount),
                FundingTradeId: ResolveTradeIdForLifecycle(
                    explicitTradeId: entry.FundingTradeId,
                    symbol: credit.Symbol,
                    eventUtc: entry.Utc,
                    creditId: credit.CreditId,
                    loanId: null,
                    trades: trades),
                OpenedUtc: credit.OpenedUtc ?? credit.CreatedUtc,
                ClosedUtc: credit.ClosedUtc))
            .ToArray();

        var loanCandidates = loans
            .Where(loan =>
                string.Equals(loan.Symbol, symbol, StringComparison.OrdinalIgnoreCase) &&
                IsLifecycleRecordRelevantAt(loan.CreatedUtc, loan.OpenedUtc, loan.ClosedUtc, entry.Utc))
            .Select(loan => new FundingLifecycleAllocationCandidate(
                Kind: "loan",
                LifecycleId: loan.LoanId,
                Symbol: loan.Symbol,
                Weight: Math.Abs(loan.OriginalAmount ?? loan.Amount),
                FundingTradeId: ResolveTradeIdForLifecycle(
                    explicitTradeId: entry.FundingTradeId,
                    symbol: loan.Symbol,
                    eventUtc: entry.Utc,
                    creditId: null,
                    loanId: loan.LoanId,
                    trades: trades),
                OpenedUtc: loan.OpenedUtc ?? loan.CreatedUtc,
                ClosedUtc: loan.ClosedUtc))
            .ToArray();

        return creditCandidates
            .Concat(loanCandidates)
            .OrderByDescending(static candidate => candidate.OpenedUtc ?? DateTime.MinValue)
            .ThenBy(static candidate => candidate.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static candidate => candidate.LifecycleId)
            .ToArray();
    }

    private FundingInterestAllocationRecord CreateInterestAllocationRecord(
        FundingInterestLedgerRecord entry,
        string allocationKey,
        long? creditId,
        long? loanId,
        long? fundingTradeId,
        decimal fraction,
        string method,
        string confidence)
    {
        var normalizedFraction = Math.Clamp(fraction, 0m, 1m);
        var gross = Math.Round(entry.GrossInterest * normalizedFraction, 12, MidpointRounding.AwayFromZero);
        var fee = Math.Round(entry.FeeAmount * normalizedFraction, 12, MidpointRounding.AwayFromZero);
        var net = Math.Round(entry.NetInterest * normalizedFraction, 12, MidpointRounding.AwayFromZero);

        return new FundingInterestAllocationRecord(
            Utc: entry.Utc,
            Exchange: entry.Exchange,
            AllocationKey: allocationKey,
            LedgerId: entry.LedgerId,
            Currency: entry.Currency,
            Symbol: entry.Symbol,
            CreditId: creditId,
            LoanId: loanId,
            FundingTradeId: fundingTradeId,
            AllocatedGrossInterest: gross,
            AllocatedFeeAmount: fee,
            AllocatedNetInterest: net,
            AllocationFraction: normalizedFraction,
            AllocationMethod: method,
            Confidence: confidence,
            Metadata: new
            {
                entry.EntryType,
                entry.Description,
                entry.BalanceAfter,
                entry.RawAmount
            });
    }

    private IReadOnlyList<FundingCapitalEventRecord> BuildCapitalEvents(
        IReadOnlyList<FundingCreditStateRecord> credits,
        IReadOnlyList<FundingLoanStateRecord> loans,
        IReadOnlyList<FundingTradeRecord> trades,
        IReadOnlyList<FundingInterestAllocationRecord> interestAllocations)
    {
        var events = new Dictionary<string, FundingCapitalEventRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (var credit in credits)
        {
            var principalAmount = GetLifecyclePrincipalAmount(credit.OriginalAmount, credit.Amount);
            var eventUtc = credit.OpenedUtc ?? credit.CreatedUtc;
            if (eventUtc.HasValue && principalAmount > 0m)
            {
                var tradeId = ResolveTradeIdForLifecycle(
                    explicitTradeId: null,
                    symbol: credit.Symbol,
                    eventUtc: eventUtc.Value,
                    creditId: credit.CreditId,
                    loanId: null,
                    trades: trades);
                var row = new FundingCapitalEventRecord(
                    Utc: eventUtc.Value,
                    Exchange: "bitfinex",
                    EventKey: $"credit:{credit.CreditId}:principal_deployed",
                    Symbol: credit.Symbol,
                    Currency: FundingSymbolToCurrency(credit.Symbol),
                    WalletType: "funding",
                    EventType: "principal_deployed",
                    CreditId: credit.CreditId,
                    LoanId: null,
                    FundingTradeId: tradeId,
                    Amount: principalAmount,
                    SourceType: "credit",
                    Description: "Principal deployed into funding credit.",
                    Metadata: new
                    {
                        credit.Status,
                        credit.CreatedUtc,
                        credit.OpenedUtc,
                        credit.UpdatedUtc
                    });

                events[row.EventKey] = row;
            }

            if (credit.ClosedUtc.HasValue && principalAmount > 0m)
            {
                var tradeId = ResolveTradeIdForLifecycle(
                    explicitTradeId: null,
                    symbol: credit.Symbol,
                    eventUtc: credit.ClosedUtc.Value,
                    creditId: credit.CreditId,
                    loanId: null,
                    trades: trades);
                var row = new FundingCapitalEventRecord(
                    Utc: credit.ClosedUtc.Value,
                    Exchange: "bitfinex",
                    EventKey: $"credit:{credit.CreditId}:principal_returned",
                    Symbol: credit.Symbol,
                    Currency: FundingSymbolToCurrency(credit.Symbol),
                    WalletType: "funding",
                    EventType: "principal_returned",
                    CreditId: credit.CreditId,
                    LoanId: null,
                    FundingTradeId: tradeId,
                    Amount: principalAmount,
                    SourceType: "credit",
                    Description: "Principal returned from funding credit.",
                    Metadata: new
                    {
                        credit.Status,
                        credit.ClosedUtc,
                        credit.UpdatedUtc
                    });

                events[row.EventKey] = row;
            }
        }

        foreach (var loan in loans)
        {
            var principalAmount = GetLifecyclePrincipalAmount(loan.OriginalAmount, loan.Amount);
            var eventUtc = loan.OpenedUtc ?? loan.CreatedUtc;
            if (eventUtc.HasValue && principalAmount > 0m)
            {
                var tradeId = ResolveTradeIdForLifecycle(
                    explicitTradeId: null,
                    symbol: loan.Symbol,
                    eventUtc: eventUtc.Value,
                    creditId: null,
                    loanId: loan.LoanId,
                    trades: trades);
                var row = new FundingCapitalEventRecord(
                    Utc: eventUtc.Value,
                    Exchange: "bitfinex",
                    EventKey: $"loan:{loan.LoanId}:principal_deployed",
                    Symbol: loan.Symbol,
                    Currency: FundingSymbolToCurrency(loan.Symbol),
                    WalletType: "funding",
                    EventType: "principal_deployed",
                    CreditId: null,
                    LoanId: loan.LoanId,
                    FundingTradeId: tradeId,
                    Amount: principalAmount,
                    SourceType: "loan",
                    Description: "Principal deployed into funding loan.",
                    Metadata: new
                    {
                        loan.Status,
                        loan.CreatedUtc,
                        loan.OpenedUtc,
                        loan.UpdatedUtc
                    });

                events[row.EventKey] = row;
            }

            if (loan.ClosedUtc.HasValue && principalAmount > 0m)
            {
                var tradeId = ResolveTradeIdForLifecycle(
                    explicitTradeId: null,
                    symbol: loan.Symbol,
                    eventUtc: loan.ClosedUtc.Value,
                    creditId: null,
                    loanId: loan.LoanId,
                    trades: trades);
                var row = new FundingCapitalEventRecord(
                    Utc: loan.ClosedUtc.Value,
                    Exchange: "bitfinex",
                    EventKey: $"loan:{loan.LoanId}:principal_returned",
                    Symbol: loan.Symbol,
                    Currency: FundingSymbolToCurrency(loan.Symbol),
                    WalletType: "funding",
                    EventType: "principal_returned",
                    CreditId: null,
                    LoanId: loan.LoanId,
                    FundingTradeId: tradeId,
                    Amount: principalAmount,
                    SourceType: "loan",
                    Description: "Principal returned from funding loan.",
                    Metadata: new
                    {
                        loan.Status,
                        loan.ClosedUtc,
                        loan.UpdatedUtc
                    });

                events[row.EventKey] = row;
            }
        }

        foreach (var allocation in interestAllocations)
        {
            var amount = allocation.AllocatedNetInterest;
            if (amount == 0m)
                continue;

            var row = new FundingCapitalEventRecord(
                Utc: allocation.Utc,
                Exchange: allocation.Exchange,
                EventKey: $"interest:{allocation.AllocationKey}",
                Symbol: allocation.Symbol,
                Currency: allocation.Currency,
                WalletType: "funding",
                EventType: "interest_paid",
                CreditId: allocation.CreditId,
                LoanId: allocation.LoanId,
                FundingTradeId: allocation.FundingTradeId,
                Amount: amount,
                SourceType: "interest_allocation",
                Description: "Funding interest payment allocated to lifecycle.",
                Metadata: new
                {
                    allocation.LedgerId,
                    allocation.AllocationMethod,
                    allocation.Confidence,
                    allocation.AllocatedGrossInterest,
                    allocation.AllocatedFeeAmount,
                    allocation.AllocationFraction
                });

            events[row.EventKey] = row;
        }

        return events.Values
            .OrderByDescending(static row => row.Utc)
            .ThenBy(static row => row.EventKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private FundingCreditStateRecord ToCreditStateRecord(FundingCreditInfo credit)
    {
        var closedUtc = IsClosedLifecycleStatus(credit.Status)
            ? (credit.UpdatedUtc ?? credit.LastPayoutUtc ?? credit.OpenedUtc ?? credit.CreatedUtc)
            : null;

        return new FundingCreditStateRecord(
            Exchange: "bitfinex",
            CreditId: credit.CreditId,
            Symbol: credit.Symbol,
            Side: credit.Side,
            Status: credit.Status,
            Amount: credit.Amount,
            OriginalAmount: credit.OriginalAmount,
            Rate: credit.Rate,
            PeriodDays: credit.PeriodDays,
            CreatedUtc: credit.CreatedUtc,
            UpdatedUtc: credit.UpdatedUtc,
            OpenedUtc: credit.OpenedUtc,
            ClosedUtc: closedUtc,
            Metadata: new
            {
                credit.FundingType,
                credit.RateReal,
                credit.Notify,
                credit.Renew,
                credit.NoClose,
                credit.PositionPair,
                credit.LastPayoutUtc,
                credit.Metadata
            });
    }

    private FundingLoanStateRecord ToLoanStateRecord(FundingLoanInfo loan)
    {
        var closedUtc = IsClosedLifecycleStatus(loan.Status)
            ? (loan.UpdatedUtc ?? loan.LastPayoutUtc ?? loan.OpenedUtc ?? loan.CreatedUtc)
            : null;

        return new FundingLoanStateRecord(
            Exchange: "bitfinex",
            LoanId: loan.LoanId,
            Symbol: loan.Symbol,
            Side: loan.Side,
            Status: loan.Status,
            Amount: loan.Amount,
            OriginalAmount: loan.OriginalAmount,
            Rate: loan.Rate,
            PeriodDays: loan.PeriodDays,
            CreatedUtc: loan.CreatedUtc,
            UpdatedUtc: loan.UpdatedUtc,
            OpenedUtc: loan.OpenedUtc,
            ClosedUtc: closedUtc,
            Metadata: new
            {
                loan.FundingType,
                loan.RateReal,
                loan.Notify,
                loan.Renew,
                loan.NoClose,
                loan.PositionPair,
                loan.LastPayoutUtc,
                loan.Metadata
            });
    }

    private FundingReconciliationLogRecord BuildLifecycleReconciliationLog(
        DateTime startedUtc,
        DateTime completedUtc,
        IReadOnlyList<string> preferredSymbols,
        IReadOnlyList<FundingCreditStateRecord> credits,
        IReadOnlyList<FundingLoanStateRecord> loans,
        IReadOnlyList<FundingTradeRecord> trades,
        IReadOnlyList<FundingInterestLedgerRecord> interestEntries,
        IReadOnlyList<FundingInterestAllocationRecord> interestAllocations,
        IReadOnlyList<FundingCapitalEventRecord> capitalEvents)
    {
        var unlinkedTrades = trades.Count(trade => !trade.CreditId.HasValue && !trade.LoanId.HasValue);
        var allocationLookup = interestAllocations
            .GroupBy(static allocation => allocation.LedgerId)
            .ToDictionary(static g => g.Key, static g => g.ToArray());

        var unresolvedInterest = interestEntries.Count(entry =>
        {
            if (!allocationLookup.TryGetValue(entry.LedgerId, out var allocations) || allocations.Length == 0)
                return true;

            return allocations.All(allocation =>
                !allocation.CreditId.HasValue &&
                !allocation.LoanId.HasValue &&
                !allocation.FundingTradeId.HasValue);
        });

        return new FundingReconciliationLogRecord(
            StartedUtc: startedUtc,
            CompletedUtc: completedUtc,
            Exchange: "bitfinex",
            Symbol: null,
            MismatchCount: unlinkedTrades + unresolvedInterest,
            CorrectedCount: 0,
            Severity: (unlinkedTrades + unresolvedInterest) == 0 ? "info" : "warning",
            Summary: $"symbols={string.Join(",", preferredSymbols)} credits={credits.Count} loans={loans.Count} trades={trades.Count} interest={interestEntries.Count} allocations={interestAllocations.Count} capitalEvents={capitalEvents.Count} unlinkedTrades={unlinkedTrades} unresolvedInterest={unresolvedInterest}",
            Metadata: new
            {
                ActiveCredits = credits.Count(credit => !IsClosedLifecycleStatus(credit.Status)),
                ClosedCredits = credits.Count(credit => IsClosedLifecycleStatus(credit.Status)),
                ActiveLoans = loans.Count(loan => !IsClosedLifecycleStatus(loan.Status)),
                ClosedLoans = loans.Count(loan => IsClosedLifecycleStatus(loan.Status)),
                InterestLedgersWithAllocations = allocationLookup.Count
            });
    }

    private static long? ResolveTradeIdForLifecycle(
        long? explicitTradeId,
        string? symbol,
        DateTime eventUtc,
        long? creditId,
        long? loanId,
        IReadOnlyList<FundingTradeRecord> trades)
    {
        if (explicitTradeId.HasValue)
            return explicitTradeId;

        return trades
            .Where(trade =>
                (!creditId.HasValue || trade.CreditId == creditId) &&
                (!loanId.HasValue || trade.LoanId == loanId) &&
                (string.IsNullOrWhiteSpace(symbol) || string.Equals(trade.Symbol, symbol, StringComparison.OrdinalIgnoreCase)) &&
                trade.Utc <= eventUtc &&
                trade.Utc >= eventUtc.AddDays(-7))
            .OrderByDescending(static trade => trade.Utc)
            .Select(static trade => (long?)trade.FundingTradeId)
            .FirstOrDefault();
    }

    private static decimal GetLifecyclePrincipalAmount(decimal? originalAmount, decimal amount)
    {
        if (originalAmount.HasValue && Math.Abs(originalAmount.Value) > 0.00000001m)
            return Math.Abs(originalAmount.Value);

        return Math.Abs(amount);
    }

    private FundingWalletSnapshotRecord CreateWalletSnapshotRecord(FundingWalletBalance wallet, string source)
    {
        var nowUtc = DateTime.UtcNow;
        var metadata = BuildWalletSnapshotMetadata(wallet, source, nowUtc);

        return new FundingWalletSnapshotRecord(
            Utc: nowUtc,
            Exchange: "bitfinex",
            WalletType: wallet.WalletType,
            Currency: wallet.Currency,
            Total: wallet.Total,
            Available: wallet.Available,
            Reserved: wallet.Reserved,
            Source: source,
            Metadata: metadata);
    }

    private object BuildWalletSnapshotMetadata(FundingWalletBalance wallet, string source, DateTime observedUtc)
    {
        lock (_walletsSync)
        {
            var key = $"{wallet.WalletType.Trim().ToLowerInvariant()}:{NormalizeCurrency(wallet.Currency)}";
            _latestWalletBalances.TryGetValue(key, out var previous);

            var deltaTotal = previous is null ? 0m : wallet.Total - previous.Total;
            var deltaAvailable = previous is null ? 0m : wallet.Available - previous.Available;
            var deltaReserved = previous is null ? 0m : wallet.Reserved - previous.Reserved;
            var movementType = ClassifyWalletMovement(previous, wallet, deltaTotal, deltaAvailable, deltaReserved);

            _latestWalletBalances[key] = wallet;

            return new
            {
                source,
                observedUtc,
                PreviousTotal = previous?.Total,
                PreviousAvailable = previous?.Available,
                PreviousReserved = previous?.Reserved,
                deltaTotal,
                deltaAvailable,
                deltaReserved,
                movementType
            };
        }
    }

    private static IReadOnlyList<FundingCreditStateRecord> FindMatchingCredits(
        FundingTradeInfo trade,
        IReadOnlyList<FundingCreditStateRecord> credits)
    {
        return credits
            .Where(credit =>
                string.Equals(credit.Symbol, trade.Symbol, StringComparison.OrdinalIgnoreCase) &&
                AreClose(Math.Abs(credit.Amount), Math.Abs(trade.Amount), 0.00000001m) &&
                (!trade.PeriodDays.HasValue || !credit.PeriodDays.HasValue || trade.PeriodDays.Value == credit.PeriodDays.Value) &&
                IsTradeLifecycleRecordRelevantAt(credit.CreatedUtc, credit.OpenedUtc, trade.Utc))
            .OrderBy(credit => GetTradeLifecycleLinkScore(trade.Utc, trade.Rate, credit.CreatedUtc, credit.OpenedUtc, credit.Rate))
            .ThenByDescending(credit => credit.OpenedUtc ?? credit.CreatedUtc ?? DateTime.MinValue)
            .ThenByDescending(credit => credit.CreditId)
            .ToArray();
    }

    private static IReadOnlyList<FundingLoanStateRecord> FindMatchingLoans(
        FundingTradeInfo trade,
        IReadOnlyList<FundingLoanStateRecord> loans)
    {
        return loans
            .Where(loan =>
                string.Equals(loan.Symbol, trade.Symbol, StringComparison.OrdinalIgnoreCase) &&
                AreClose(Math.Abs(loan.Amount), Math.Abs(trade.Amount), 0.00000001m) &&
                (!trade.PeriodDays.HasValue || !loan.PeriodDays.HasValue || trade.PeriodDays.Value == loan.PeriodDays.Value) &&
                IsTradeLifecycleRecordRelevantAt(loan.CreatedUtc, loan.OpenedUtc, trade.Utc))
            .OrderBy(loan => GetTradeLifecycleLinkScore(trade.Utc, trade.Rate, loan.CreatedUtc, loan.OpenedUtc, loan.Rate))
            .ThenByDescending(loan => loan.OpenedUtc ?? loan.CreatedUtc ?? DateTime.MinValue)
            .ThenByDescending(loan => loan.LoanId)
            .ToArray();
    }

    private static FundingCreditStateRecord? SelectPreferredCreditMatch(
        FundingTradeInfo trade,
        IReadOnlyList<FundingCreditStateRecord> candidates)
    {
        if (candidates.Count == 0)
            return null;

        return candidates
            .OrderBy(candidate => GetTradeLifecycleLinkScore(trade.Utc, trade.Rate, candidate.CreatedUtc, candidate.OpenedUtc, candidate.Rate))
            .ThenByDescending(candidate => candidate.OpenedUtc ?? candidate.CreatedUtc ?? DateTime.MinValue)
            .ThenByDescending(candidate => candidate.CreditId)
            .First();
    }

    private static FundingLoanStateRecord? SelectPreferredLoanMatch(
        FundingTradeInfo trade,
        IReadOnlyList<FundingLoanStateRecord> candidates)
    {
        if (candidates.Count == 0)
            return null;

        return candidates
            .OrderBy(candidate => GetTradeLifecycleLinkScore(trade.Utc, trade.Rate, candidate.CreatedUtc, candidate.OpenedUtc, candidate.Rate))
            .ThenByDescending(candidate => candidate.OpenedUtc ?? candidate.CreatedUtc ?? DateTime.MinValue)
            .ThenByDescending(candidate => candidate.LoanId)
            .First();
    }

    private static bool IsClosedLifecycleStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return false;

        return status.IndexOf("closed", StringComparison.OrdinalIgnoreCase) >= 0 ||
               status.IndexOf("executed", StringComparison.OrdinalIgnoreCase) >= 0 ||
               status.IndexOf("canceled", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static DateTime GetLifecycleRelevantUtc(
        DateTime? createdUtc,
        DateTime? updatedUtc,
        DateTime? openedUtc,
        DateTime? payoutUtc)
    {
        return payoutUtc ?? updatedUtc ?? openedUtc ?? createdUtc ?? DateTime.MinValue;
    }

    private static bool IsLifecycleRecordRelevantAt(
        DateTime? createdUtc,
        DateTime? openedUtc,
        DateTime? closedUtc,
        DateTime targetUtc)
    {
        var opened = openedUtc ?? createdUtc ?? DateTime.MinValue;
        var closed = closedUtc ?? targetUtc.AddDays(1);

        return targetUtc >= opened.AddMinutes(-5) && targetUtc <= closed.AddDays(1);
    }

    private static bool IsTradeLifecycleRecordRelevantAt(
        DateTime? createdUtc,
        DateTime? openedUtc,
        DateTime targetUtc)
    {
        var anchor = openedUtc ?? createdUtc;
        if (!anchor.HasValue)
            return false;

        // Funding trades should map to the lifecycle that opened nearest the trade time,
        // not to any lifecycle that merely remained "relevant" for hours afterward.
        return targetUtc >= anchor.Value.AddMinutes(-5) &&
               targetUtc <= anchor.Value.AddMinutes(20);
    }

    private static decimal GetTradeLifecycleLinkScore(
        DateTime tradeUtc,
        decimal? tradeRate,
        DateTime? createdUtc,
        DateTime? openedUtc,
        decimal? lifecycleRate)
    {
        var anchorUtc = openedUtc ?? createdUtc ?? tradeUtc;
        var timeDistanceSeconds = Math.Abs((decimal)(tradeUtc - anchorUtc).TotalSeconds);
        var rateDistance = tradeRate.HasValue && lifecycleRate.HasValue
            ? Math.Abs(tradeRate.Value - lifecycleRate.Value) * 1_000_000_000m
            : 0m;

        return timeDistanceSeconds + rateDistance;
    }

    private static bool AreClose(decimal left, decimal right, decimal tolerance)
    {
        return Math.Abs(left - right) <= tolerance;
    }

    private static string CurrencyToFundingSymbol(string currency)
    {
        var normalized = NormalizeCurrency(currency);
        return string.IsNullOrWhiteSpace(normalized) ? string.Empty : $"f{normalized}";
    }

    private static string ClassifyWalletMovement(
        FundingWalletBalance? previous,
        FundingWalletBalance current,
        decimal deltaTotal,
        decimal deltaAvailable,
        decimal deltaReserved)
    {
        if (previous is null)
            return "initial_observation";

        if (AreClose(deltaTotal, 0m, 0.00000001m) &&
            AreClose(deltaAvailable, 0m, 0.00000001m) &&
            AreClose(deltaReserved, 0m, 0.00000001m))
        {
            return "unchanged";
        }

        if (deltaTotal > 0m)
            return "wallet_total_increase";

        if (deltaTotal < 0m)
            return "wallet_total_decrease";

        if (deltaAvailable < 0m && deltaReserved > 0m)
            return "reserve_lock";

        if (deltaAvailable > 0m && deltaReserved < 0m)
            return "reserve_release";

        return "wallet_rebalance";
    }

    private DateTime? GetLastRestSyncUtc()
    {
        var lastOfferSync = _lastOfferStateSyncUtc == default ? (DateTime?)null : _lastOfferStateSyncUtc;
        var lastLifecycleSync = _lastLifecycleSyncUtc == default ? (DateTime?)null : _lastLifecycleSyncUtc;

        if (!lastOfferSync.HasValue)
            return lastLifecycleSync;

        if (!lastLifecycleSync.HasValue)
            return lastOfferSync;

        return lastOfferSync.Value >= lastLifecycleSync.Value ? lastOfferSync : lastLifecycleSync;
    }

    private FundingRuntimeHealthRecord CreateRuntimeHealthRecord(object runtimeHealthMetadata)
    {
        var nowUtc = DateTime.UtcNow;
        var wsLastMessageUtc = _privateFeed?.LastMessageUtc;
        var wsConnected = _options.UsePrivateWebSocket &&
                          _privateFeed is not null &&
                          wsLastMessageUtc.HasValue &&
                          wsLastMessageUtc.Value != default &&
                          (nowUtc - wsLastMessageUtc.Value) <= TimeSpan.FromSeconds(Math.Max(30, _options.RestOfferSyncIntervalSeconds * 2));

        return new FundingRuntimeHealthRecord(
            Utc: nowUtc,
            Exchange: "bitfinex",
            WsConnected: wsConnected,
            WsLastMessageUtc: wsLastMessageUtc == default ? null : wsLastMessageUtc,
            RestLastSyncUtc: GetLastRestSyncUtc(),
            ErrorCount: 0,
            DegradedMode: _options.UsePrivateWebSocket && !wsConnected,
            SelfDisabled: false,
            Metadata: runtimeHealthMetadata);
    }

    private FundingOfferStateRecord? ToOfferStateRecord(FundingOfferInfo offer, object? metadata = null)
    {
        var offerId = ParseOfferId(offer.OfferId);
        if (!offerId.HasValue)
            return null;

        return new FundingOfferStateRecord(
            Exchange: "bitfinex",
            OfferId: offerId.Value,
            Symbol: offer.Symbol,
            Currency: FundingSymbolToCurrency(offer.Symbol),
            WalletType: "funding",
            OfferType: offer.OfferType,
            Status: offer.Status,
            Amount: offer.Amount,
            OriginalAmount: offer.OriginalAmount,
            Rate: offer.Rate,
            RateReal: offer.RateReal,
            PeriodDays: offer.PeriodDays,
            Flags: offer.Flags,
            Notify: offer.Notify,
            Hidden: offer.Hidden,
            Renew: offer.Renew,
            IsActive: offer.IsActive,
            ManagedByEngine: IsManagedOffer(offer.OfferId),
            CreatedUtc: offer.CreatedUtc,
            UpdatedUtc: offer.UpdatedUtc,
            ClosedUtc: offer.IsActive ? null : (offer.UpdatedUtc ?? offer.CreatedUtc ?? DateTime.UtcNow),
            Metadata: metadata);
    }

    private FundingOfferEventRecord? ToOfferEventRecord(FundingOfferActionResult actionResult)
    {
        var offerId = ParseOfferId(actionResult.OfferId ?? actionResult.Offer?.OfferId);
        if (!offerId.HasValue)
            return null;

        var offer = actionResult.Offer;

        return new FundingOfferEventRecord(
            Utc: actionResult.TimestampUtc,
            Exchange: "bitfinex",
            OfferId: offerId.Value,
            Symbol: actionResult.Symbol,
            EventType: $"action_result_{actionResult.Action}",
            Status: actionResult.Status,
            Amount: offer?.Amount,
            OriginalAmount: offer?.OriginalAmount,
            Rate: offer?.Rate,
            RateReal: offer?.RateReal,
            PeriodDays: offer?.PeriodDays,
            Message: actionResult.Message,
            Metadata: new
            {
                actionResult.Success,
                actionResult.IsDryRun
            });
    }

    private FundingOfferEventRecord? ToOfferEventRecord(string eventType, FundingOfferInfo offer)
    {
        var offerId = ParseOfferId(offer.OfferId);
        if (!offerId.HasValue)
            return null;

        return new FundingOfferEventRecord(
            Utc: offer.UpdatedUtc ?? offer.CreatedUtc ?? DateTime.UtcNow,
            Exchange: "bitfinex",
            OfferId: offerId.Value,
            Symbol: offer.Symbol,
            EventType: eventType,
            Status: offer.Status,
            Amount: offer.Amount,
            OriginalAmount: offer.OriginalAmount,
            Rate: offer.Rate,
            RateReal: offer.RateReal,
            PeriodDays: offer.PeriodDays,
            Message: null,
            Metadata: new
            {
                offer.Notify,
                offer.Hidden,
                offer.Renew,
                offer.Flags,
                ManagedByEngine = IsManagedOffer(offer.OfferId)
            });
    }

    private static long? ParseOfferId(string? offerId)
    {
        return long.TryParse(offerId, out var parsed) ? parsed : null;
    }

    private bool ShouldRefreshActiveOffersFromRest()
    {
        var nowUtc = DateTime.UtcNow;
        var restInterval = TimeSpan.FromSeconds(Math.Max(10, _options.RestOfferSyncIntervalSeconds));
        if (!_hasOfferSnapshot)
            return true;

        if ((nowUtc - _lastOfferStateSyncUtc) > restInterval)
            return true;

        if (_privateFeed is null || !_options.UsePrivateWebSocket)
            return true;

        if (_privateFeed.LastMessageUtc == default)
            return true;

        return (nowUtc - _privateFeed.LastMessageUtc) > TimeSpan.FromSeconds(Math.Max(30, _options.RestOfferSyncIntervalSeconds * 2));
    }

    private void MergeOfferSnapshotFromRest(
        IReadOnlyList<FundingOfferInfo> offers,
        IReadOnlyCollection<string> scopedSymbols)
    {
        lock (_offersSync)
        {
            var scope = new HashSet<string>(
                scopedSymbols
                    .Where(static s => !string.IsNullOrWhiteSpace(s))
                    .Select(static s => s.Trim().ToUpperInvariant()),
                StringComparer.OrdinalIgnoreCase);

            if (scope.Count > 0)
            {
                var existingIdsInScope = _activeOffers.Values
                    .Where(o => scope.Contains(o.Symbol))
                    .Select(o => o.OfferId)
                    .ToArray();

                foreach (var offerId in existingIdsInScope)
                {
                    _activeOffers.Remove(offerId);
                }
            }

            foreach (var offer in offers.Where(static o => o.IsActive))
            {
                _activeOffers[offer.OfferId] = offer;
            }

            _lastOfferStateSyncUtc = DateTime.UtcNow;
            _hasOfferSnapshot = true;
        }
    }

    private IReadOnlyList<FundingOfferInfo> GetActiveOffersSnapshot()
    {
        lock (_offersSync)
        {
            return _activeOffers.Values.ToArray();
        }
    }

    private bool IsManagedOffer(string offerId)
    {
        lock (_offersSync)
        {
            return _managedOfferIds.Contains(offerId);
        }
    }

    private async Task RecoverManagedOffersAsync(CancellationToken ct)
    {
        if (_fundingRepo is null)
            return;

        var symbols = GetPreferredSymbols();
        if (symbols.Count == 0)
            return;

        var managedOfferIds = await _fundingRepo.LoadManagedActiveOfferIdsAsync("bitfinex", symbols, ct).ConfigureAwait(false);
        if (managedOfferIds.Count == 0)
        {
            _log.Information("[BFX-FUND] managed-offer recovery found no persisted active offers.");
            return;
        }

        lock (_offersSync)
        {
            _managedOfferIds.Clear();
            foreach (var offerId in managedOfferIds)
            {
                _managedOfferIds.Add(offerId.ToString());
            }
        }

        _log.Information(
            "[BFX-FUND] managed-offer recovery restored count={Count} offerIds={OfferIds}",
            managedOfferIds.Count,
            string.Join(",", managedOfferIds));
    }

    private void RememberManagedOffer(string offerId)
    {
        if (string.IsNullOrWhiteSpace(offerId))
            return;

        lock (_offersSync)
        {
            _managedOfferIds.Add(offerId);
        }
    }

    private void ForgetManagedOffer(string offerId)
    {
        if (string.IsNullOrWhiteSpace(offerId))
            return;

        lock (_offersSync)
        {
            _managedOfferIds.Remove(offerId);
        }
    }

    private void OnOfferSnapshot(FundingOfferInfo[] offers)
    {
        lock (_offersSync)
        {
            _activeOffers.Clear();
            foreach (var offer in offers.Where(static o => o.IsActive))
            {
                _activeOffers[offer.OfferId] = offer;
            }

            _hasOfferSnapshot = true;
            _lastOfferStateSyncUtc = DateTime.UtcNow;
        }

        _ = PersistOfferEventsAsync("funding_offer_ws_snapshot", offers, CancellationToken.None);
    }

    private void OnOfferNew(FundingOfferInfo offer)
    {
        lock (_offersSync)
        {
            if (offer.IsActive)
            {
                _activeOffers[offer.OfferId] = offer;
            }
            else
            {
                _activeOffers.Remove(offer.OfferId);
                _managedOfferIds.Remove(offer.OfferId);
            }

            _lastOfferStateSyncUtc = DateTime.UtcNow;
        }

        _ = PersistOfferEventsAsync("funding_offer_ws_new", new[] { offer }, CancellationToken.None);
    }

    private void OnOfferUpdate(FundingOfferInfo offer)
    {
        lock (_offersSync)
        {
            if (offer.IsActive)
            {
                _activeOffers[offer.OfferId] = offer;
            }
            else
            {
                _activeOffers.Remove(offer.OfferId);
                _managedOfferIds.Remove(offer.OfferId);
            }

            _lastOfferStateSyncUtc = DateTime.UtcNow;
        }

        _ = PersistOfferEventsAsync("funding_offer_ws_update", new[] { offer }, CancellationToken.None);
    }

    private void OnOfferClose(FundingOfferInfo offer)
    {
        lock (_offersSync)
        {
            _activeOffers.Remove(offer.OfferId);
            _managedOfferIds.Remove(offer.OfferId);
            _lastOfferStateSyncUtc = DateTime.UtcNow;
        }

        _ = PersistOfferEventsAsync("funding_offer_ws_close", new[] { offer }, CancellationToken.None);
    }

    private void OnWalletSnapshot(FundingWalletBalance[] wallets)
    {
        _ = PersistWalletEventsAsync("funding_wallet_ws_snapshot", wallets, CancellationToken.None);
    }

    private void OnWalletUpdate(FundingWalletBalance wallet)
    {
        _ = PersistWalletEventsAsync("funding_wallet_ws_update", new[] { wallet }, CancellationToken.None);
    }

    private void OnNotification(string rawNotification)
    {
        _ = PersistRawSnapshotAsync("funding_notification", "account:notify", rawNotification, CancellationToken.None);
    }

    private async Task PersistOfferEventsAsync(string snapshotType, IReadOnlyList<FundingOfferInfo> offers, CancellationToken ct)
    {
        if (offers.Count == 0)
            return;

        if (_fundingRepo is not null)
        {
            var eventRows = offers
                .Select(offer => ToOfferEventRecord(snapshotType, offer))
                .Where(static row => row is not null)
                .Cast<FundingOfferEventRecord>()
                .ToArray();

            var offerStateRows = offers
                .Select(offer => ToOfferStateRecord(offer, new
                {
                    Source = snapshotType,
                    ManagedByEngine = IsManagedOffer(offer.OfferId)
                }))
                .Where(static row => row is not null)
                .Cast<FundingOfferStateRecord>()
                .ToArray();

            await _fundingRepo.PersistOfferEventsAsync(eventRows, offerStateRows, ct).ConfigureAwait(false);
        }

        if (_snapshotRepo is null)
            return;

        var records = offers.Select(offer => new CryptoSnapshotRecord(
            Utc: offer.UpdatedUtc ?? offer.CreatedUtc ?? DateTime.UtcNow,
            Exchange: "bitfinex",
            Symbol: offer.Symbol,
            SnapshotType: snapshotType,
            Data: offer)).ToArray();

        await _snapshotRepo.BatchInsertAsync(records, ct).ConfigureAwait(false);
    }

    private async Task PersistWalletEventsAsync(string snapshotType, IReadOnlyList<FundingWalletBalance> wallets, CancellationToken ct)
    {
        if (wallets.Count == 0)
            return;

        if (_fundingRepo is not null)
        {
            var walletRows = wallets
                .Select(wallet => CreateWalletSnapshotRecord(wallet, snapshotType))
                .ToArray();

            await _fundingRepo.InsertWalletSnapshotsAsync(walletRows, ct).ConfigureAwait(false);
        }

        if (_snapshotRepo is null)
            return;

        var records = wallets.Select(wallet => new CryptoSnapshotRecord(
            Utc: DateTime.UtcNow,
            Exchange: "bitfinex",
            Symbol: $"{wallet.WalletType}:{wallet.Currency}",
            SnapshotType: snapshotType,
            Data: wallet)).ToArray();

        await _snapshotRepo.BatchInsertAsync(records, ct).ConfigureAwait(false);
    }

    private async Task PersistRawSnapshotAsync(string snapshotType, string symbol, object data, CancellationToken ct)
    {
        if (_snapshotRepo is null)
            return;

        await _snapshotRepo.InsertAsync(
            utc: DateTime.UtcNow,
            exchange: "bitfinex",
            symbol: symbol,
            snapshotType: snapshotType,
            data: data,
            metadata: null,
            ct: ct).ConfigureAwait(false);
    }

    private object BuildRuntimeHealthSnapshot(
        IReadOnlyList<string> preferredSymbols,
        IReadOnlyList<FundingWalletBalance> wallets,
        IReadOnlyList<FundingTickerSnapshot> tickers,
        IReadOnlyList<FundingOfferInfo> activeOffers,
        IReadOnlyList<FundingDecision> decisions,
        IReadOnlyList<FundingOfferActionResult> actionResults,
        IReadOnlyList<FundingShadowPlan> shadowPlans,
        IReadOnlyList<FundingShadowAction> shadowActions,
        IReadOnlyList<FundingShadowActionSession> shadowSessions,
        FundingLifecycleSyncResult lifecycleSync)
    {
        return new
        {
            Utc = DateTime.UtcNow,
            Enabled = _options.Enabled,
            DryRun = _options.DryRun,
            PreferredSymbols = preferredSymbols,
            WalletCount = wallets.Count,
            TickerCount = tickers.Count,
            ActiveOfferCount = activeOffers.Count,
            ManagedOfferCount = GetManagedOfferCount(),
            DecisionCount = decisions.Count,
            ActionResultCount = actionResults.Count,
            ShadowPlanCount = shadowPlans.Count,
            ShadowActionCount = shadowActions.Count,
            ShadowSessionCount = shadowSessions.Count,
            UsePrivateWebSocket = _options.UsePrivateWebSocket,
            PrivateWsLastMessageUtc = _privateFeed?.LastMessageUtc,
            LastRestOfferSyncUtc = _lastOfferStateSyncUtc == default ? (DateTime?)null : _lastOfferStateSyncUtc,
            LastRestLifecycleSyncUtc = _lastLifecycleSyncUtc == default ? (DateTime?)null : _lastLifecycleSyncUtc,
            HasOfferSnapshot = _hasOfferSnapshot,
            LifecycleSyncPerformed = lifecycleSync.SyncedUtc,
            CreditCount = lifecycleSync.Credits.Count,
            LoanCount = lifecycleSync.Loans.Count,
            TradeCount = lifecycleSync.Trades.Count,
            InterestEntryCount = lifecycleSync.InterestEntries.Count,
            LayeredShadowEnabled = _options.LayeredShadowEnabled
        };
    }

    private int GetManagedOfferCount()
    {
        lock (_offersSync)
        {
            return _managedOfferIds.Count;
        }
    }

    private void LogDecision(FundingDecision decision)
    {
        if (decision.IsActionable)
        {
            _log.Information(
                "[BFX-FUND] {Action} symbol={Symbol} wallet={Wallet} avail={Avail:F2} lendable={Lendable:F2} amount={Amount:F2} rate={Rate:E6} period={Period} dryRun={DryRun} reason={Reason}",
                decision.Action,
                decision.Symbol,
                decision.WalletType,
                decision.AvailableBalance,
                decision.LendableBalance,
                decision.ProposedAmount ?? 0m,
                decision.ProposedRate ?? 0m,
                decision.ProposedPeriodDays ?? 0,
                decision.IsDryRun,
                decision.Reason);
        }
        else
        {
            _log.Information(
                "[BFX-FUND] {Action} symbol={Symbol} wallet={Wallet} avail={Avail:F2} reason={Reason}",
                decision.Action,
                decision.Symbol,
                decision.WalletType,
                decision.AvailableBalance,
                decision.Reason);
        }
    }

    private void LogActionResult(FundingOfferActionResult actionResult)
    {
        if (actionResult.Success)
        {
            _log.Information(
                "[BFX-FUND] {Action} success symbol={Symbol} offerId={OfferId} status={Status} msg={Message}",
                actionResult.Action,
                actionResult.Symbol,
                actionResult.OfferId ?? string.Empty,
                actionResult.Status,
                actionResult.Message);
        }
        else
        {
            _log.Warning(
                "[BFX-FUND] {Action} failed symbol={Symbol} offerId={OfferId} status={Status} msg={Message}",
                actionResult.Action,
                actionResult.Symbol,
                actionResult.OfferId ?? string.Empty,
                actionResult.Status,
                actionResult.Message);
        }
    }

    private void LogCycleSummary(
        IReadOnlyList<FundingWalletBalance> wallets,
        IReadOnlyList<FundingTickerSnapshot> tickers,
        IReadOnlyList<FundingOfferInfo> activeOffers,
        IReadOnlyList<FundingDecision> decisions)
    {
        var walletSummary = wallets.Count == 0
            ? "none"
            : string.Join(", ", wallets
                .OrderBy(w => w.WalletType, StringComparer.OrdinalIgnoreCase)
                .ThenBy(w => w.Currency, StringComparer.OrdinalIgnoreCase)
                .Select(w => $"{w.WalletType}:{w.Currency}=tot:{w.Total:F2}/avail:{w.Available:F2}"));

        var tickerSummary = tickers.Count == 0
            ? "none"
            : string.Join(", ", tickers
                .OrderBy(t => t.Symbol, StringComparer.OrdinalIgnoreCase)
                .Select(t => $"{t.Symbol}=ask:{t.AskRate:E6}/bid:{t.BidRate:E6}"));

        var offerSummary = activeOffers.Count == 0
            ? "none"
            : string.Join(", ", activeOffers
                .OrderBy(o => o.Symbol, StringComparer.OrdinalIgnoreCase)
                .ThenBy(o => o.OfferId, StringComparer.OrdinalIgnoreCase)
                .Select(o => $"{o.Symbol}#{o.OfferId}:{o.Status} amt:{Math.Abs(o.Amount):F2} rate:{o.Rate:E6}"));

        var decisionSummary = decisions.Count == 0
            ? "none"
            : string.Join(", ", decisions
                .OrderBy(d => d.Symbol, StringComparer.OrdinalIgnoreCase)
                .Select(d => $"{d.Symbol}:{d.Action}"));

        _log.Information(
            "[BFX-FUND] cycle-summary dryRun={DryRun} wallets=[{Wallets}] tickers=[{Tickers}] offers=[{Offers}] decisions=[{Decisions}] wsLast={WsLast}",
            _options.DryRun,
            walletSummary,
            tickerSummary,
            offerSummary,
            decisionSummary,
            _privateFeed?.LastMessageUtc);
    }

    private IReadOnlyList<FundingShadowPlan> BuildShadowPlans(
        IReadOnlyList<string> preferredSymbols,
        IReadOnlyList<FundingWalletBalance> wallets,
        IReadOnlyList<FundingTickerSnapshot> tickers,
        IReadOnlyList<FundingOfferInfo> activeOffers)
    {
        if (!_options.LayeredShadowEnabled || preferredSymbols.Count == 0)
            return Array.Empty<FundingShadowPlan>();

        var plans = new List<FundingShadowPlan>(preferredSymbols.Count);

        foreach (var symbol in preferredSymbols)
        {
            var symbolSettings = ResolveSymbolSettings(symbol);
            if (!symbolSettings.Enabled)
                continue;

            var currency = FundingSymbolToCurrency(symbol);
            var wallet = FindWalletBalance(wallets, currency);
            var ticker = tickers.FirstOrDefault(t => string.Equals(t.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
            if (wallet is null || ticker is null)
                continue;

            var lendable = Math.Max(0m, wallet.Available - symbolSettings.ReserveAmount);
            var marketRate = ticker.AskRate > 0m ? ticker.AskRate : ticker.BidRate;
            var regime = ClassifyMarketRegime(marketRate, symbolSettings);
            var timestampUtc = DateTime.UtcNow;
            var buckets = new List<FundingShadowBucket>(4);
            var offersForSymbol = activeOffers
                .Where(offer => string.Equals(offer.Symbol, symbol, StringComparison.OrdinalIgnoreCase) && offer.IsActive)
                .ToArray();
            var liveSlotPlan = BuildLiveSlotPlan(symbolSettings, lendable, offersForSymbol.Length);
            var motorFraction = ClampFraction(symbolSettings.MotorAllocationFraction);
            var opportunisticFraction = ClampFraction(symbolSettings.OpportunisticAllocationFraction);
            var aggressiveFraction = ClampFraction(symbolSettings.AggressiveAllocationFraction);
            var sniperFraction = ClampFraction(symbolSettings.SniperAllocationFraction);
            var totalFraction = motorFraction + opportunisticFraction + aggressiveFraction + sniperFraction;
            var liveTotalFraction = motorFraction + opportunisticFraction + aggressiveFraction;
            var normalizedMotorFraction = liveTotalFraction <= 0m ? 0m : motorFraction / liveTotalFraction;
            var normalizedOppFraction = liveTotalFraction <= 0m ? 0m : opportunisticFraction / liveTotalFraction;
            var normalizedAggressiveFraction = liveTotalFraction <= 0m ? 0m : aggressiveFraction / liveTotalFraction;
            var normalizedSniperFraction = totalFraction <= 0m ? 0m : sniperFraction / totalFraction;

            if (liveSlotPlan.TotalSlotsNow > 0 && lendable >= symbolSettings.MinOfferAmount)
            {
                var motorTargetAmount = liveSlotPlan.DesiredMotorSlots > 0
                    ? decimal.Round(Math.Min(symbolSettings.MinOfferAmount, symbolSettings.MaxOfferAmount), 8, MidpointRounding.ToZero)
                    : 0m;
                var opportunisticTargetAmount = liveSlotPlan.DesiredOpportunisticSlots > 0
                    ? decimal.Round(Math.Min(symbolSettings.MinOfferAmount, symbolSettings.MaxOfferAmount), 8, MidpointRounding.ToZero)
                    : 0m;
                var aggressiveTargetAmount = liveSlotPlan.DesiredAggressiveSlots > 0
                    ? decimal.Round(Math.Min(symbolSettings.MinOfferAmount, symbolSettings.MaxOfferAmount), 8, MidpointRounding.ToZero)
                    : 0m;
                var sniperTargetAmount = decimal.Round(
                    Math.Min(lendable * normalizedSniperFraction, symbolSettings.MaxOfferAmount),
                    8,
                    MidpointRounding.ToZero);

                if (sniperTargetAmount < symbolSettings.MinOfferAmount)
                {
                    sniperTargetAmount = 0m;
                }

                if (motorTargetAmount > 0m)
                {
                    buckets.Add(new FundingShadowBucket(
                        Bucket: "Motor",
                        AllocationAmount: motorTargetAmount,
                        AllocationFraction: normalizedMotorFraction,
                        TargetRate: SelectShadowRate(
                            marketRate,
                            symbolSettings.MotorRateMultiplier,
                            symbolSettings,
                            ResolveMotorAdaptiveMaxDailyRate(
                                ticker.AskRate > 0m ? ticker.AskRate : 0m,
                                ticker.BidRate > 0m ? ticker.BidRate : 0m,
                                ticker.Frr.GetValueOrDefault() > 0m ? ticker.Frr.GetValueOrDefault() : 0m,
                                symbolSettings,
                                liveSlotPlan)),
                        TargetPeriodDays: 2,
                        MaxWaitMinutes: GetMotorMaxWaitMinutes(regime, symbolSettings),
                        Role: "baseline_utilization",
                        FallbackBucket: null));
                }

                if (opportunisticTargetAmount > 0m)
                {
                    var opportunisticAdaptiveCap = ResolveOpportunisticAdaptiveMaxDailyRate(
                        ticker.AskRate > 0m ? ticker.AskRate : 0m,
                        ticker.BidRate > 0m ? ticker.BidRate : 0m,
                        ticker.Frr.GetValueOrDefault() > 0m ? ticker.Frr.GetValueOrDefault() : 0m,
                        symbolSettings,
                        liveSlotPlan);
                    buckets.Add(new FundingShadowBucket(
                        Bucket: "Opportunistic",
                        AllocationAmount: opportunisticTargetAmount,
                        AllocationFraction: normalizedOppFraction,
                        TargetRate: SelectShadowRate(
                            marketRate,
                            symbolSettings.OpportunisticRateMultiplier,
                            symbolSettings,
                            opportunisticAdaptiveCap),
                        TargetPeriodDays: 2,
                        MaxWaitMinutes: GetOpportunisticMaxWaitMinutes(regime, symbolSettings),
                        Role: "yield_enhancement",
                        FallbackBucket: "Motor"));
                }

                if (aggressiveTargetAmount > 0m)
                {
                    var aggressiveAdaptiveCap = ResolveAggressiveAdaptiveMaxDailyRate(
                        ticker.AskRate > 0m ? ticker.AskRate : 0m,
                        ticker.BidRate > 0m ? ticker.BidRate : 0m,
                        ticker.Frr.GetValueOrDefault() > 0m ? ticker.Frr.GetValueOrDefault() : 0m,
                        symbolSettings,
                        liveSlotPlan);
                    buckets.Add(new FundingShadowBucket(
                        Bucket: "Aggressive",
                        AllocationAmount: aggressiveTargetAmount,
                        AllocationFraction: normalizedAggressiveFraction,
                        TargetRate: SelectShadowRate(
                            marketRate,
                            symbolSettings.AggressiveRateMultiplier,
                            symbolSettings,
                            aggressiveAdaptiveCap),
                        TargetPeriodDays: 2,
                        MaxWaitMinutes: GetAggressiveMaxWaitMinutes(regime, symbolSettings),
                        Role: "strong_yield_enhancement",
                        FallbackBucket: opportunisticTargetAmount > 0m ? "Opportunistic" : "Motor"));
                }

                if (sniperTargetAmount > 0m)
                {
                    var sniperAdaptiveCap = ResolveSniperAdaptiveMaxDailyRate(
                        ticker.AskRate > 0m ? ticker.AskRate : 0m,
                        ticker.BidRate > 0m ? ticker.BidRate : 0m,
                        ticker.Frr.GetValueOrDefault() > 0m ? ticker.Frr.GetValueOrDefault() : 0m,
                        symbolSettings);
                    var sniperAdaptiveAnchor = ResolveSniperAnchorRate(
                        ticker.AskRate > 0m ? ticker.AskRate : 0m,
                        ticker.BidRate > 0m ? ticker.BidRate : 0m,
                        ticker.Frr.GetValueOrDefault() > 0m ? ticker.Frr.GetValueOrDefault() : 0m,
                        marketRate);
                    buckets.Add(new FundingShadowBucket(
                        Bucket: "Sniper",
                        AllocationAmount: sniperTargetAmount,
                        AllocationFraction: normalizedSniperFraction,
                        TargetRate: SelectShadowRate(sniperAdaptiveAnchor, symbolSettings.SniperRateMultiplier, symbolSettings, sniperAdaptiveCap),
                        TargetPeriodDays: 2,
                        MaxWaitMinutes: GetSniperMaxWaitMinutes(regime, symbolSettings),
                        Role: "spike_capture",
                        FallbackBucket: aggressiveTargetAmount > 0m
                            ? "Aggressive"
                            : opportunisticTargetAmount > 0m
                                ? "Opportunistic"
                                : "Motor"));
                }
            }

            var summary = buckets.Count == 0
                ? totalFraction <= 0m
                    ? $"No shadow bucket actionable. zero allocation profile configured for symbol. reserve={symbolSettings.ReserveAmount:F2} regime={regime}"
                    : $"No shadow bucket actionable. lendable={lendable:F2} reserve={symbolSettings.ReserveAmount:F2} minOffer={symbolSettings.MinOfferAmount:F2} regime={regime}"
                : string.Join("; ", buckets.Select(bucket =>
                    $"{bucket.Bucket} amt={bucket.AllocationAmount:F2} rate={bucket.TargetRate:E6} wait={bucket.MaxWaitMinutes}m")) +
                  $" liveSlots={liveSlotPlan.TotalSlotsNow} split={DescribeShadowSplit(liveSlotPlan)}";

            plans.Add(new FundingShadowPlan(
                Symbol: symbol,
                Currency: currency,
                Regime: regime,
                AvailableBalance: wallet.Available,
                LendableBalance: lendable,
                MarketAskRate: ticker.AskRate,
                MarketBidRate: ticker.BidRate,
                Buckets: buckets,
                Summary: summary,
                TimestampUtc: timestampUtc));
        }

        return plans;
    }

    private void LogShadowPlans(IReadOnlyList<FundingShadowPlan> shadowPlans)
    {
        if (!_options.LayeredShadowEnabled || shadowPlans.Count == 0)
            return;

        foreach (var plan in shadowPlans)
        {
            _log.Information(
                "[BFX-FUND-SHADOW] symbol={Symbol} regime={Regime} avail={Avail:F2} lendable={Lendable:F2} summary={Summary}",
                plan.Symbol,
                plan.Regime,
                plan.AvailableBalance,
                plan.LendableBalance,
                plan.Summary);
        }
    }

    private IReadOnlyList<FundingShadowAction> BuildShadowActions(
        IReadOnlyList<FundingShadowPlan> shadowPlans,
        IReadOnlyList<FundingOfferInfo> activeOffers)
    {
        if (!_options.LayeredShadowEnabled || shadowPlans.Count == 0)
            return Array.Empty<FundingShadowAction>();

        var actions = new List<FundingShadowAction>(shadowPlans.Sum(static plan => Math.Max(1, plan.Buckets.Count)));

        foreach (var plan in shadowPlans)
        {
            var offersForSymbol = activeOffers
                .Where(offer => string.Equals(offer.Symbol, plan.Symbol, StringComparison.OrdinalIgnoreCase) && offer.IsActive)
                .OrderByDescending(offer => offer.UpdatedUtc ?? offer.CreatedUtc ?? DateTime.MinValue)
                .ToArray();

            if (plan.Buckets.Count == 0)
            {
                actions.Add(new FundingShadowAction(
                    Symbol: plan.Symbol,
                    Currency: plan.Currency,
                    Regime: plan.Regime,
                    Bucket: "NONE",
                    Action: "would_hold_no_actionable_bucket",
                    IsActionable: false,
                    AvailableBalance: plan.AvailableBalance,
                    LendableBalance: plan.LendableBalance,
                    AllocationAmount: 0m,
                    AllocationFraction: 0m,
                    TargetRate: null,
                    FallbackRate: null,
                    TargetPeriodDays: null,
                    MaxWaitMinutes: null,
                    DecisionDeadlineUtc: null,
                    Role: "inactive",
                    FallbackBucket: null,
                    ActiveOfferCount: offersForSymbol.Length,
                    ActiveOfferId: offersForSymbol.Length == 1 ? TryParseLongId(offersForSymbol[0].OfferId) : null,
                    ActiveOfferRate: offersForSymbol.Length == 1 ? offersForSymbol[0].Rate : null,
                    ActiveOfferAmount: offersForSymbol.Length == 1 ? Math.Abs(offersForSymbol[0].Amount) : null,
                    ActiveOfferStatus: offersForSymbol.Length == 1 ? offersForSymbol[0].Status : null,
                    Reason: plan.Summary,
                    Summary: plan.Summary,
                    TimestampUtc: plan.TimestampUtc));
                continue;
            }

            foreach (var bucket in plan.Buckets)
            {
                actions.Add(BuildShadowAction(plan, bucket, offersForSymbol));
            }
        }

        return actions;
    }

    private FundingShadowAction BuildShadowAction(
        FundingShadowPlan plan,
        FundingShadowBucket bucket,
        IReadOnlyList<FundingOfferInfo> activeOffers)
    {
        var symbolSettings = ResolveSymbolSettings(plan.Symbol);
        var fallbackRate = ResolveFallbackRate(plan, bucket);
        var slotPlan = BuildLiveSlotPlan(symbolSettings, plan.LendableBalance, activeOffers.Count);
        var activeOffer = activeOffers.Count == 1 ? activeOffers[0] : null;
        var deadlineUtc = bucket.MaxWaitMinutes > 0
            ? plan.TimestampUtc.AddMinutes(bucket.MaxWaitMinutes)
            : (DateTime?)null;

        if (activeOffers.Count >= slotPlan.TotalSlotsNow && !(activeOffers.Count == 1 && slotPlan.TotalSlotsNow == 1))
        {
            return CreateShadowAction(
                plan,
                bucket,
                action: "would_keep_active_offer_capacity_full",
                isActionable: false,
                fallbackRate: fallbackRate,
                decisionDeadlineUtc: deadlineUtc,
                activeOffers: activeOffers,
                reason: $"Managed active offer capacity is full for {plan.Symbol} ({activeOffers.Count}/{slotPlan.TotalSlotsNow}); shadow layer would keep current offers and wait for a slot to free up.",
                summary: $"{bucket.Bucket} would keep active offers because capacity {activeOffers.Count}/{slotPlan.TotalSlotsNow} is full.");
        }

        if (activeOffers.Count > 0)
        {
            var slotIndex = Math.Min(slotPlan.TotalSlotsNow, activeOffers.Count + 1);
            if (string.Equals(bucket.Bucket, "Motor", StringComparison.OrdinalIgnoreCase))
            {
                return CreateShadowAction(
                    plan,
                    bucket,
                    action: "would_place_parallel_offer",
                    isActionable: true,
                    fallbackRate: fallbackRate,
                    decisionDeadlineUtc: null,
                    activeOffers: activeOffers,
                    reason: $"Motor bucket sees an open managed slot for {plan.Symbol} ({slotIndex}/{slotPlan.TotalSlotsNow}) and would deploy another parallel offer immediately.",
                    summary: $"{bucket.Bucket} would place parallel slot {slotIndex}/{slotPlan.TotalSlotsNow} now at {bucket.TargetRate:E6}.");
            }

            if (string.Equals(plan.Regime, "HOT", StringComparison.OrdinalIgnoreCase))
            {
                return CreateShadowAction(
                    plan,
                    bucket,
                    action: "would_place_parallel_offer",
                    isActionable: true,
                    fallbackRate: fallbackRate,
                    decisionDeadlineUtc: null,
                    activeOffers: activeOffers,
                    reason: $"{bucket.Bucket} bucket sees an open managed slot for {plan.Symbol} ({slotIndex}/{slotPlan.TotalSlotsNow}) and a HOT regime, so it would place another parallel offer immediately.",
                    summary: $"{bucket.Bucket} would place parallel slot {slotIndex}/{slotPlan.TotalSlotsNow} because the regime is HOT.");
            }

            return CreateShadowAction(
                plan,
                bucket,
                action: "would_wait_parallel_then_fallback",
                isActionable: false,
                fallbackRate: fallbackRate,
                decisionDeadlineUtc: deadlineUtc,
                activeOffers: activeOffers,
                reason: $"{bucket.Bucket} bucket sees an open managed slot for {plan.Symbol} ({slotIndex}/{slotPlan.TotalSlotsNow}) but would still wait for a better rate before using fallback {bucket.FallbackBucket ?? "Motor"}.",
                summary: $"{bucket.Bucket} would wait up to {bucket.MaxWaitMinutes}m before using parallel fallback slot {slotIndex}/{slotPlan.TotalSlotsNow}.");
        }

        var targetRequest = BuildShadowTargetRequest(plan.Symbol, bucket, symbolSettings);
        if (activeOffer is not null)
        {
            var shouldReplace = ShouldReplaceOffer(activeOffer, targetRequest, out var replaceReason);

            if ((string.Equals(bucket.Bucket, "Opportunistic", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(bucket.Bucket, "Aggressive", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(bucket.Bucket, "Sniper", StringComparison.OrdinalIgnoreCase)) &&
                !string.Equals(plan.Regime, "HOT", StringComparison.OrdinalIgnoreCase))
            {
                return CreateShadowAction(
                    plan,
                    bucket,
                    action: shouldReplace ? "would_wait_then_fallback" : "would_keep_active_offer",
                    isActionable: false,
                    fallbackRate: fallbackRate,
                    decisionDeadlineUtc: deadlineUtc,
                    activeOffers: activeOffers,
                    reason: shouldReplace
                        ? $"{bucket.Bucket} bucket would wait for stronger conditions and then fall back to {bucket.FallbackBucket ?? "Motor"}. {replaceReason}"
                        : $"Existing active offer is acceptable for the shadow {bucket.Bucket} bucket. {replaceReason}",
                    summary: shouldReplace
                        ? $"{bucket.Bucket} would wait up to {bucket.MaxWaitMinutes}m before falling back to {bucket.FallbackBucket ?? "Motor"}."
                        : $"{bucket.Bucket} would keep the current active offer.");
            }

            return CreateShadowAction(
                plan,
                bucket,
                action: shouldReplace ? "would_reprice_active_offer" : "would_keep_active_offer",
                isActionable: shouldReplace,
                fallbackRate: fallbackRate,
                decisionDeadlineUtc: shouldReplace ? deadlineUtc : null,
                activeOffers: activeOffers,
                reason: shouldReplace
                    ? $"Shadow target diverged from the active offer. {replaceReason}"
                    : $"Existing active offer remains aligned with the shadow target. {replaceReason}",
                summary: shouldReplace
                    ? $"{bucket.Bucket} would reprice the active offer toward {targetRequest.Rate:E6}."
                    : $"{bucket.Bucket} would keep the current active offer.");
        }

        if (string.Equals(bucket.Bucket, "Motor", StringComparison.OrdinalIgnoreCase))
        {
            return CreateShadowAction(
                plan,
                bucket,
                action: "would_place_now",
                isActionable: true,
                fallbackRate: fallbackRate,
                decisionDeadlineUtc: null,
                activeOffers: activeOffers,
                reason: "Motor bucket prioritizes baseline utilization and would place immediately.",
                summary: $"{bucket.Bucket} would place now at {bucket.TargetRate:E6}.");
        }

        if (string.Equals(plan.Regime, "HOT", StringComparison.OrdinalIgnoreCase))
        {
            return CreateShadowAction(
                plan,
                bucket,
                action: "would_place_now",
                isActionable: true,
                fallbackRate: fallbackRate,
                decisionDeadlineUtc: null,
                activeOffers: activeOffers,
                reason: $"{bucket.Bucket} bucket sees a HOT regime and would place immediately.",
                summary: $"{bucket.Bucket} would place now because the regime is HOT.");
        }

        return CreateShadowAction(
            plan,
            bucket,
            action: "would_wait_for_better_rate",
            isActionable: false,
            fallbackRate: fallbackRate,
            decisionDeadlineUtc: deadlineUtc,
            activeOffers: activeOffers,
            reason: $"{bucket.Bucket} bucket would wait for a better rate for up to {bucket.MaxWaitMinutes} minutes before falling back to {bucket.FallbackBucket ?? "Motor"}.",
            summary: $"{bucket.Bucket} would wait up to {bucket.MaxWaitMinutes}m before fallback.");
    }

    private FundingShadowAction CreateShadowAction(
        FundingShadowPlan plan,
        FundingShadowBucket bucket,
        string action,
        bool isActionable,
        decimal? fallbackRate,
        DateTime? decisionDeadlineUtc,
        IReadOnlyList<FundingOfferInfo> activeOffers,
        string reason,
        string summary)
    {
        var activeOffer = activeOffers.Count == 1 ? activeOffers[0] : null;

        return new FundingShadowAction(
            Symbol: plan.Symbol,
            Currency: plan.Currency,
            Regime: plan.Regime,
            Bucket: bucket.Bucket,
            Action: action,
            IsActionable: isActionable,
            AvailableBalance: plan.AvailableBalance,
            LendableBalance: plan.LendableBalance,
            AllocationAmount: bucket.AllocationAmount,
            AllocationFraction: bucket.AllocationFraction,
            TargetRate: bucket.TargetRate,
            FallbackRate: fallbackRate,
            TargetPeriodDays: bucket.TargetPeriodDays,
            MaxWaitMinutes: bucket.MaxWaitMinutes,
            DecisionDeadlineUtc: decisionDeadlineUtc,
            Role: bucket.Role,
            FallbackBucket: bucket.FallbackBucket,
            ActiveOfferCount: activeOffers.Count,
            ActiveOfferId: activeOffer is null ? null : TryParseLongId(activeOffer.OfferId),
            ActiveOfferRate: activeOffer?.Rate,
            ActiveOfferAmount: activeOffer is null ? null : Math.Abs(activeOffer.Amount),
            ActiveOfferStatus: activeOffer?.Status,
            Reason: reason,
            Summary: summary,
            TimestampUtc: plan.TimestampUtc);
    }

    private FundingOfferRequest BuildShadowTargetRequest(string symbol, FundingShadowBucket bucket, FundingSymbolRuntimeSettings symbolSettings)
    {
        var minPeriodDays = Math.Max(2, _options.MinPeriodDays);
        var maxPeriodDays = Math.Max(minPeriodDays, _options.MaxPeriodDays);

        return new FundingOfferRequest(
            Symbol: symbol,
            Amount: decimal.Round(Math.Min(bucket.AllocationAmount, symbolSettings.MaxOfferAmount), 8, MidpointRounding.ToZero),
            Rate: Math.Clamp(bucket.TargetRate, symbolSettings.MinDailyRate, symbolSettings.MaxDailyRate),
            PeriodDays: Math.Clamp(bucket.TargetPeriodDays, minPeriodDays, maxPeriodDays),
            OfferType: string.IsNullOrWhiteSpace(_options.OfferType)
                ? "LIMIT"
                : _options.OfferType.Trim().ToUpperInvariant(),
            Flags: _options.OfferFlags);
    }

    private static decimal? ResolveFallbackRate(FundingShadowPlan plan, FundingShadowBucket bucket)
    {
        if (string.IsNullOrWhiteSpace(bucket.FallbackBucket))
            return null;

        return plan.Buckets
            .FirstOrDefault(candidate => string.Equals(candidate.Bucket, bucket.FallbackBucket, StringComparison.OrdinalIgnoreCase))
            ?.TargetRate;
    }

    private (FundingOfferRequest Request, string Summary) ResolveManagedOfferTarget(
        FundingPlacementCandidate candidate,
        string slotRole,
        FundingLiveSlotPlan slotPlan)
    {
        var normalizedMode = NormalizeManagedOfferTargetMode(candidate.SymbolSettings.ManagedOfferTargetMode);
        if (normalizedMode == "LIVE")
        {
            var policy = ResolveLivePlacementPolicy(candidate, slotRole, slotPlan);
            return (
                policy.TargetRequest,
                $"managed_target=RoleAwareLive slotRole={slotRole} {policy.TargetSummary}");
        }

        var askRate = candidate.Ticker.AskRate > 0m ? candidate.Ticker.AskRate : 0m;
        var bidRate = candidate.Ticker.BidRate > 0m ? candidate.Ticker.BidRate : 0m;
        var frrRate = candidate.Ticker.Frr.GetValueOrDefault() > 0m ? candidate.Ticker.Frr.GetValueOrDefault() : 0m;
        var marketRate = askRate > 0m
            ? askRate
                : bidRate > 0m
                ? bidRate
                : candidate.SymbolSettings.MinDailyRate;
        var visibleMarketCap = ResolveVisibleMarketCap(askRate, bidRate, frrRate);
        var motorAdaptiveCap = ResolveMotorAdaptiveMaxDailyRate(
            askRate,
            bidRate,
            frrRate,
            candidate.SymbolSettings,
            slotPlan: null);
        var opportunisticAdaptiveCap = ResolveOpportunisticAdaptiveMaxDailyRate(
            askRate,
            bidRate,
            frrRate,
            candidate.SymbolSettings,
            slotPlan: null);
        decimal? motorMaxRateOverride = motorAdaptiveCap > candidate.SymbolSettings.MaxDailyRate
            ? motorAdaptiveCap
            : null;
        decimal? opportunisticMaxRateOverride = opportunisticAdaptiveCap > candidate.SymbolSettings.MaxDailyRate
            ? opportunisticAdaptiveCap
            : null;

        decimal targetRate;
        string summary;

        switch (normalizedMode)
        {
            case "SHADOWMOTOR":
                var (shadowMotorRate, shadowMotorTelemetry) = SelectShadowRateWithTelemetry(
                    marketRate,
                    candidate.SymbolSettings.MotorRateMultiplier,
                    candidate.SymbolSettings,
                    motorMaxRateOverride,
                    marketCapOverride: visibleMarketCap);
                targetRate = shadowMotorRate;
                summary = $"managed_target=ShadowMotor slotRole={slotRole} anchor={marketRate:E6} mult={candidate.SymbolSettings.MotorRateMultiplier:F3} ask={askRate:E6} bid={bidRate:E6} frr={FormatRate(frrRate)}{shadowMotorTelemetry} motorAdaptive={candidate.SymbolSettings.EnableAdaptiveMotorMaxRate} motorAdaptiveCap={motorAdaptiveCap:E6}";
                break;

            case "SHADOWOPPORTUNISTIC":
                var (shadowOpportunisticRate, shadowOpportunisticTelemetry) = SelectShadowRateWithTelemetry(
                    marketRate,
                    candidate.SymbolSettings.OpportunisticRateMultiplier,
                    candidate.SymbolSettings,
                    opportunisticMaxRateOverride,
                    marketCapOverride: visibleMarketCap);
                targetRate = shadowOpportunisticRate;
                summary = $"managed_target=ShadowOpportunistic slotRole={slotRole} anchor={marketRate:E6} mult={candidate.SymbolSettings.OpportunisticRateMultiplier:F3} ask={askRate:E6} bid={bidRate:E6} frr={FormatRate(frrRate)}{shadowOpportunisticTelemetry} opportunisticAdaptive={candidate.SymbolSettings.EnableAdaptiveOpportunisticMaxRate} opportunisticAdaptiveCap={opportunisticAdaptiveCap:E6}";
                break;

            default:
                return (candidate.Request, $"managed_target=LivePlacement slotRole={slotRole} {candidate.RateSelectionSummary}");
        }

        return (
            candidate.Request with { Rate = targetRate },
            $"{summary} amount={candidate.Request.Amount:F2} period={candidate.Request.PeriodDays}");
    }

    private (FundingOfferRequest Request, string Summary) ResolveManagedOfferFallbackTarget(
        FundingPlacementCandidate candidate,
        string slotRole,
        FundingLiveSlotPlan slotPlan)
    {
        var askRate = candidate.Ticker.AskRate > 0m ? candidate.Ticker.AskRate : 0m;
        var bidRate = candidate.Ticker.BidRate > 0m ? candidate.Ticker.BidRate : 0m;
        var frrRate = candidate.Ticker.Frr.GetValueOrDefault() > 0m ? candidate.Ticker.Frr.GetValueOrDefault() : 0m;
        var marketRate = askRate > 0m
            ? askRate
            : bidRate > 0m
                ? bidRate
                : candidate.SymbolSettings.MinDailyRate;

        var fallbackBucket = ResolveManagedFallbackBucketName(candidate, slotRole);
        var fallbackMultiplier = string.Equals(fallbackBucket, "Opportunistic", StringComparison.OrdinalIgnoreCase)
            ? candidate.SymbolSettings.OpportunisticRateMultiplier
            : candidate.SymbolSettings.MotorRateMultiplier;
        var singleSlotAdaptiveCap = ResolveSingleSlotAdaptiveMaxDailyRate(
            askRate,
            bidRate,
            frrRate,
            candidate.SymbolSettings,
            slotPlan);
        var motorAdaptiveCap = ResolveMotorAdaptiveMaxDailyRate(
            askRate,
            bidRate,
            frrRate,
            candidate.SymbolSettings,
            slotPlan);
        var opportunisticAdaptiveCap = ResolveOpportunisticAdaptiveMaxDailyRate(
            askRate,
            bidRate,
            frrRate,
            candidate.SymbolSettings,
            slotPlan);
        decimal? singleSlotMaxRateOverride = singleSlotAdaptiveCap > candidate.SymbolSettings.MaxDailyRate
            ? singleSlotAdaptiveCap
            : null;
        var effectiveMotorCap = Math.Max(singleSlotAdaptiveCap, motorAdaptiveCap);
        var effectiveOpportunisticCap = Math.Max(singleSlotAdaptiveCap, opportunisticAdaptiveCap);
        decimal? motorMaxRateOverride = effectiveMotorCap > candidate.SymbolSettings.MaxDailyRate
            ? effectiveMotorCap
            : null;
        decimal? opportunisticMaxRateOverride = effectiveOpportunisticCap > candidate.SymbolSettings.MaxDailyRate
            ? effectiveOpportunisticCap
            : null;
        var fallbackRate = SelectShadowRate(
            marketRate,
            fallbackMultiplier,
            candidate.SymbolSettings,
            string.Equals(fallbackBucket, "Motor", StringComparison.OrdinalIgnoreCase)
                ? motorMaxRateOverride
                : opportunisticMaxRateOverride);
        var fallbackTelemetry = DescribeRateClipTelemetry(
            marketRate * fallbackMultiplier,
            fallbackRate,
            Math.Max(
                candidate.SymbolSettings.MinDailyRate,
                string.Equals(fallbackBucket, "Motor", StringComparison.OrdinalIgnoreCase)
                    ? motorMaxRateOverride ?? candidate.SymbolSettings.MaxDailyRate
                    : opportunisticMaxRateOverride ?? candidate.SymbolSettings.MaxDailyRate),
            ResolveVisibleMarketCap(askRate, bidRate, frrRate));
        return (
            candidate.Request with { Rate = fallbackRate },
            $"managed_policy=KeepThenMotorFallback fallback={fallbackRate:E6} fallbackBucket={fallbackBucket} anchor={marketRate:E6} fallbackMult={fallbackMultiplier:F3} ask={askRate:E6} bid={bidRate:E6} frr={FormatRate(frrRate)}{fallbackTelemetry} motorAdaptive={candidate.SymbolSettings.EnableAdaptiveMotorMaxRate} motorAdaptiveCap={effectiveMotorCap:E6} opportunisticAdaptive={candidate.SymbolSettings.EnableAdaptiveOpportunisticMaxRate} opportunisticAdaptiveCap={effectiveOpportunisticCap:E6}");
    }

    private void LogShadowActions(IReadOnlyList<FundingShadowAction> shadowActions)
    {
        if (!_options.LayeredShadowEnabled || shadowActions.Count == 0)
            return;

        foreach (var action in shadowActions)
        {
            _log.Information(
                "[BFX-FUND-SHADOW-ACT] symbol={Symbol} bucket={Bucket} action={Action} actionable={Actionable} summary={Summary}",
                action.Symbol,
                action.Bucket,
                action.Action,
                action.IsActionable,
                action.Summary);
        }
    }

    private IReadOnlyList<FundingShadowActionSession> BuildShadowActionSessions(
        IReadOnlyList<FundingShadowAction> shadowActions,
        IReadOnlyList<FundingOfferInfo> activeOffers)
    {
        if (!_options.LayeredShadowEnabled)
            return Array.Empty<FundingShadowActionSession>();

        var touched = new List<FundingShadowActionSession>();
        var seenSlotKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var latestUtc = shadowActions.Count == 0
            ? DateTime.UtcNow
            : shadowActions.Max(static action => action.TimestampUtc);

        lock (_shadowSync)
        {
            foreach (var action in shadowActions.Where(static action => !string.Equals(action.Bucket, "NONE", StringComparison.OrdinalIgnoreCase)))
            {
                var slotKey = CreateShadowSessionSlotKey(action.Symbol, action.Bucket);
                seenSlotKeys.Add(slotKey);

                if (_shadowActionSessions.TryGetValue(slotKey, out var existing))
                {
                    var updated = existing with
                    {
                        CurrentRegime = action.Regime,
                        CurrentAction = action.Action,
                        Status = MapShadowActionToSessionStatus(action.Action),
                        IsActionable = action.IsActionable,
                        AvailableBalance = action.AvailableBalance,
                        LendableBalance = action.LendableBalance,
                        AllocationAmount = action.AllocationAmount,
                        AllocationFraction = action.AllocationFraction,
                        TargetRateCurrent = action.TargetRate,
                        FallbackRate = action.FallbackRate,
                        TargetPeriodDays = action.TargetPeriodDays,
                        MaxWaitMinutes = action.MaxWaitMinutes,
                        LastUpdatedUtc = action.TimestampUtc,
                        DecisionDeadlineUtc = action.DecisionDeadlineUtc,
                        ActiveOfferId = action.ActiveOfferId,
                        ActiveOfferRate = action.ActiveOfferRate,
                        ActiveOfferAmount = action.ActiveOfferAmount,
                        ActiveOfferStatus = action.ActiveOfferStatus,
                        Summary = action.Summary,
                        Metadata = SerializeShadowSessionMetadata(action, existing.UpdateCount + 1),
                        UpdateCount = existing.UpdateCount + 1
                    };

                    _shadowActionSessions[slotKey] = updated;
                    touched.Add(updated);
                    continue;
                }

                var opened = new FundingShadowActionSession(
                    SessionKey: CreateShadowSessionKey(action.Symbol, action.Bucket, action.TimestampUtc),
                    Symbol: action.Symbol,
                    Currency: action.Currency,
                    Bucket: action.Bucket,
                    FirstRegime: action.Regime,
                    CurrentRegime: action.Regime,
                    FirstAction: action.Action,
                    CurrentAction: action.Action,
                    Status: MapShadowActionToSessionStatus(action.Action),
                    IsActionable: action.IsActionable,
                    AvailableBalance: action.AvailableBalance,
                    LendableBalance: action.LendableBalance,
                    AllocationAmount: action.AllocationAmount,
                    AllocationFraction: action.AllocationFraction,
                    TargetRateInitial: action.TargetRate,
                    TargetRateCurrent: action.TargetRate,
                    FallbackRate: action.FallbackRate,
                    TargetPeriodDays: action.TargetPeriodDays,
                    MaxWaitMinutes: action.MaxWaitMinutes,
                    OpenedUtc: action.TimestampUtc,
                    LastUpdatedUtc: action.TimestampUtc,
                    DecisionDeadlineUtc: action.DecisionDeadlineUtc,
                    ClosedUtc: null,
                    ActiveOfferId: action.ActiveOfferId,
                    ActiveOfferRate: action.ActiveOfferRate,
                    ActiveOfferAmount: action.ActiveOfferAmount,
                    ActiveOfferStatus: action.ActiveOfferStatus,
                    Resolution: null,
                    UpdateCount: 1,
                    Summary: action.Summary,
                    Metadata: SerializeShadowSessionMetadata(action, 1));

                _shadowActionSessions[slotKey] = opened;
                touched.Add(opened);
            }

            var staleSlotKeys = _shadowActionSessions.Keys
                .Where(slotKey => !seenSlotKeys.Contains(slotKey))
                .ToArray();

            foreach (var slotKey in staleSlotKeys)
            {
                var existing = _shadowActionSessions[slotKey];
                var liveOffer = activeOffers
                    .Where(offer => string.Equals(offer.Symbol, existing.Symbol, StringComparison.OrdinalIgnoreCase) && offer.IsActive)
                    .OrderByDescending(offer => offer.UpdatedUtc ?? offer.CreatedUtc ?? DateTime.MinValue)
                    .FirstOrDefault();

                var resolution = liveOffer is not null
                    ? "live_offer_became_active"
                    : "shadow_no_longer_actionable";
                var closedStatus = liveOffer is not null
                    ? "closed_live_offer_active"
                    : "closed_no_actionable_bucket";

                var closed = existing with
                {
                    Status = closedStatus,
                    IsActionable = false,
                    LastUpdatedUtc = latestUtc,
                    ClosedUtc = latestUtc,
                    ActiveOfferId = liveOffer is null ? existing.ActiveOfferId : TryParseLongId(liveOffer.OfferId),
                    ActiveOfferRate = liveOffer?.Rate ?? existing.ActiveOfferRate,
                    ActiveOfferAmount = liveOffer is null ? existing.ActiveOfferAmount : Math.Abs(liveOffer.Amount),
                    ActiveOfferStatus = liveOffer?.Status ?? existing.ActiveOfferStatus,
                    Resolution = resolution,
                    Summary = liveOffer is not null
                        ? $"{existing.Bucket} shadow session closed because a live offer became active."
                        : $"{existing.Bucket} shadow session closed because the idea is no longer actionable.",
                    Metadata = SerializeShadowSessionCloseMetadata(existing, liveOffer, resolution),
                    UpdateCount = existing.UpdateCount + 1
                };

                _shadowActionSessions.Remove(slotKey);
                touched.Add(closed);
            }
        }

        return touched;
    }

    private void LogShadowActionSessions(IReadOnlyList<FundingShadowActionSession> shadowSessions)
    {
        if (!_options.LayeredShadowEnabled || shadowSessions.Count == 0)
            return;

        foreach (var session in shadowSessions)
        {
            _log.Information(
                "[BFX-FUND-SHADOW-SESSION] symbol={Symbol} bucket={Bucket} status={Status} action={Action} resolution={Resolution}",
                session.Symbol,
                session.Bucket,
                session.Status,
                session.CurrentAction,
                session.Resolution);
        }
    }

    private static string MapShadowActionToSessionStatus(string action)
    {
        return action switch
        {
            "would_place_now" => "ready_now",
            "would_place_parallel_offer" => "ready_parallel",
            "would_wait_for_better_rate" => "waiting",
            "would_wait_then_fallback" => "waiting_fallback",
            "would_wait_parallel_then_fallback" => "waiting_parallel_fallback",
            "would_reprice_active_offer" => "reprice_ready",
            "would_keep_active_offer" => "aligned_with_live_offer",
            "would_keep_active_offer_capacity_full" => "capacity_full",
            _ => "observing"
        };
    }

    private static object SerializeShadowSessionMetadata(FundingShadowAction action, int updateCount)
    {
        return new
        {
            action.Regime,
            action.Action,
            action.FallbackBucket,
            action.ActiveOfferCount,
            UpdateCount = updateCount
        };
    }

    private static object SerializeShadowSessionCloseMetadata(FundingShadowActionSession session, FundingOfferInfo? liveOffer, string resolution)
    {
        return new
        {
            session.FirstAction,
            session.CurrentAction,
            session.FirstRegime,
            session.CurrentRegime,
            Resolution = resolution,
            LiveOfferId = liveOffer?.OfferId,
            LiveOfferStatus = liveOffer?.Status
        };
    }

    private decimal SelectShadowRate(decimal marketRate, decimal multiplier, FundingSymbolRuntimeSettings symbolSettings, decimal? maxRateOverride = null)
    {
        var safeMarketRate = marketRate > 0m ? marketRate : symbolSettings.MinDailyRate;
        var scaled = safeMarketRate * multiplier;
        var effectiveMaxRate = Math.Max(symbolSettings.MinDailyRate, maxRateOverride ?? symbolSettings.MaxDailyRate);
        return Math.Clamp(scaled, symbolSettings.MinDailyRate, effectiveMaxRate);
    }

    private decimal ResolveSniperAdaptiveMaxDailyRate(
        decimal askRate,
        decimal bidRate,
        decimal frrRate,
        FundingSymbolRuntimeSettings symbolSettings)
    {
        var baseMaxRate = Math.Max(symbolSettings.MinDailyRate, symbolSettings.MaxDailyRate);
        if (!symbolSettings.EnableAdaptiveSniperMaxRate)
            return baseMaxRate;

        var configuredMaxRate = Math.Max(baseMaxRate, symbolSettings.SniperAdaptiveMaxDailyRate);
        var marketCap = Math.Max(askRate, Math.Max(bidRate, frrRate));
        if (marketCap <= 0m)
            return baseMaxRate;

        return Math.Max(baseMaxRate, Math.Min(configuredMaxRate, marketCap));
    }

    private decimal ResolveSingleSlotAdaptiveMaxDailyRate(
        decimal askRate,
        decimal bidRate,
        decimal frrRate,
        FundingSymbolRuntimeSettings symbolSettings,
        FundingLiveSlotPlan? slotPlan)
    {
        var baseMaxRate = Math.Max(symbolSettings.MinDailyRate, symbolSettings.MaxDailyRate);
        if (slotPlan?.TotalSlotsNow != 1 || !symbolSettings.EnableAdaptiveSingleSlotMaxRate)
            return baseMaxRate;

        var configuredMaxRate = Math.Max(baseMaxRate, symbolSettings.SingleSlotAdaptiveMaxDailyRate);
        var marketCap = Math.Max(askRate, Math.Max(bidRate, frrRate));
        if (marketCap <= 0m)
            return baseMaxRate;

        return Math.Max(baseMaxRate, Math.Min(configuredMaxRate, marketCap));
    }

    private decimal ResolveMotorAdaptiveMaxDailyRate(
        decimal askRate,
        decimal bidRate,
        decimal frrRate,
        FundingSymbolRuntimeSettings symbolSettings,
        FundingLiveSlotPlan? slotPlan)
    {
        var baseMaxRate = Math.Max(symbolSettings.MinDailyRate, symbolSettings.MaxDailyRate);
        if (!symbolSettings.EnableAdaptiveMotorMaxRate)
            return baseMaxRate;

        if (slotPlan is not null && slotPlan.TotalSlotsNow <= 0)
            return baseMaxRate;

        var configuredMaxRate = Math.Max(baseMaxRate, symbolSettings.MotorAdaptiveMaxDailyRate);
        var marketCap = Math.Max(askRate, Math.Max(bidRate, frrRate));
        if (marketCap <= 0m)
            return baseMaxRate;

        return Math.Max(baseMaxRate, Math.Min(configuredMaxRate, marketCap));
    }

    private decimal ResolveOpportunisticAdaptiveMaxDailyRate(
        decimal askRate,
        decimal bidRate,
        decimal frrRate,
        FundingSymbolRuntimeSettings symbolSettings,
        FundingLiveSlotPlan? slotPlan)
    {
        var baseMaxRate = Math.Max(symbolSettings.MinDailyRate, symbolSettings.MaxDailyRate);
        if (!symbolSettings.EnableAdaptiveOpportunisticMaxRate)
            return baseMaxRate;

        if (slotPlan is not null && slotPlan.TotalSlotsNow <= 0)
            return baseMaxRate;

        var configuredMaxRate = Math.Max(baseMaxRate, symbolSettings.OpportunisticAdaptiveMaxDailyRate);
        var marketCap = Math.Max(askRate, Math.Max(bidRate, frrRate));
        if (marketCap <= 0m)
            return baseMaxRate;

        return Math.Max(baseMaxRate, Math.Min(configuredMaxRate, marketCap));
    }

    private decimal ResolveAggressiveAdaptiveMaxDailyRate(
        decimal askRate,
        decimal bidRate,
        decimal frrRate,
        FundingSymbolRuntimeSettings symbolSettings,
        FundingLiveSlotPlan? slotPlan)
    {
        var baseMaxRate = Math.Max(symbolSettings.MinDailyRate, symbolSettings.MaxDailyRate);
        if (!symbolSettings.EnableAdaptiveAggressiveMaxRate)
            return baseMaxRate;

        if (slotPlan is not null && slotPlan.TotalSlotsNow <= 0)
            return baseMaxRate;

        var configuredMaxRate = Math.Max(baseMaxRate, symbolSettings.AggressiveAdaptiveMaxDailyRate);
        var marketCap = Math.Max(askRate, Math.Max(bidRate, frrRate));
        if (marketCap <= 0m)
            return baseMaxRate;

        return Math.Max(baseMaxRate, Math.Min(configuredMaxRate, marketCap));
    }

    private static decimal ResolveSniperAnchorRate(decimal askRate, decimal bidRate, decimal frrRate, decimal safeAnchor)
    {
        return Math.Max(safeAnchor, Math.Max(bidRate, frrRate));
    }

    private static decimal ResolveVisibleMarketCap(decimal askRate, decimal bidRate, decimal frrRate)
    {
        return Math.Max(askRate, Math.Max(bidRate, frrRate));
    }

    private (decimal Rate, string Telemetry) SelectShadowRateWithTelemetry(
        decimal marketRate,
        decimal multiplier,
        FundingSymbolRuntimeSettings symbolSettings,
        decimal? maxRateOverride = null,
        decimal? marketCapOverride = null)
    {
        var safeMarketRate = marketRate > 0m ? marketRate : symbolSettings.MinDailyRate;
        var rawRate = safeMarketRate * multiplier;
        var effectiveMaxRate = Math.Max(symbolSettings.MinDailyRate, maxRateOverride ?? symbolSettings.MaxDailyRate);
        var selectedRate = Math.Clamp(rawRate, symbolSettings.MinDailyRate, effectiveMaxRate);
        var marketCap = marketCapOverride.GetValueOrDefault() > 0m ? marketCapOverride.Value : safeMarketRate;
        return (selectedRate, DescribeRateClipTelemetry(rawRate, selectedRate, effectiveMaxRate, marketCap));
    }

    private static string DescribeRateClipTelemetry(
        decimal rawRate,
        decimal selectedRate,
        decimal effectiveMaxRate,
        decimal marketCap)
    {
        var clipped = rawRate > effectiveMaxRate;
        var upsideGap = clipped && marketCap > selectedRate
            ? marketCap - selectedRate
            : 0m;
        return $" rawTarget={rawRate:E6} cap={effectiveMaxRate:E6} marketCap={marketCap:E6} clipped={clipped} upsideGap={upsideGap:E6}";
    }

    private static string DescribeManagedOfferTelemetry(
        FundingOfferInfo activeOffer,
        FundingOfferRequest targetRequest,
        FundingTickerSnapshot ticker)
    {
        var marketCap = ResolveVisibleMarketCap(
            ticker.AskRate > 0m ? ticker.AskRate : 0m,
            ticker.BidRate > 0m ? ticker.BidRate : 0m,
            ticker.Frr.GetValueOrDefault() > 0m ? ticker.Frr.GetValueOrDefault() : 0m);
        var activeDeltaToTarget = targetRequest.Rate - activeOffer.Rate;
        var activeGapToMarket = marketCap > 0m ? marketCap - activeOffer.Rate : 0m;
        var targetGapToMarket = marketCap > 0m ? marketCap - targetRequest.Rate : 0m;
        return $" activeRate={activeOffer.Rate:E6} targetRate={targetRequest.Rate:E6} activeVsTarget={activeDeltaToTarget:E6} marketVsActive={activeGapToMarket:E6} marketVsTarget={targetGapToMarket:E6}";
    }

    private static bool ShouldHoldManagedOfferNearMarket(
        FundingOfferInfo activeOffer,
        FundingOfferRequest fallbackRequest,
        decimal visibleMarketCap,
        FundingSymbolRuntimeSettings symbolSettings,
        out string reason)
    {
        reason = string.Empty;
        if (!symbolSettings.EnableManagedFallbackNearMarketHold || visibleMarketCap <= 0m)
            return false;

        var holdDelta = Math.Max(0m, symbolSettings.ManagedFallbackNearMarketHoldDelta);
        var activeGapToMarket = visibleMarketCap - activeOffer.Rate;
        var fallbackGapFromActive = activeOffer.Rate - fallbackRequest.Rate;
        if (fallbackGapFromActive <= holdDelta)
            return false;

        if (activeOffer.Rate >= visibleMarketCap - holdDelta)
        {
            reason = $"active offer is within near-market hold band. marketCap={visibleMarketCap:E6} activeGap={activeGapToMarket:E6} holdDelta={holdDelta:E6}";
            return true;
        }

        return false;
    }

    private (decimal Rate, string Summary) SelectLiveRate(
        FundingTickerSnapshot ticker,
        FundingSymbolRuntimeSettings symbolSettings,
        decimal? maxRateOverride = null)
    {
        var askRate = ticker.AskRate > 0m ? ticker.AskRate : 0m;
        var bidRate = ticker.BidRate > 0m ? ticker.BidRate : 0m;
        var frrRate = ticker.Frr.GetValueOrDefault() > 0m ? ticker.Frr.GetValueOrDefault() : 0m;
        var bookAnchor = askRate > 0m
            ? askRate
            : bidRate > 0m
                ? bidRate
                : frrRate;

        var safeAnchor = bookAnchor > 0m ? bookAnchor : symbolSettings.MinDailyRate;
        var normalizedMode = NormalizeLiveRateMode(symbolSettings.LiveRateMode);
        var effectiveMaxRate = Math.Max(symbolSettings.MinDailyRate, maxRateOverride ?? symbolSettings.MaxDailyRate);
        var visibleMarketCap = ResolveVisibleMarketCap(askRate, bidRate, frrRate);

        decimal selectedRate;
        decimal rawRate;
        string summary;

        switch (normalizedMode)
        {
            case "BOOKASK":
                rawRate = safeAnchor;
                selectedRate = Math.Clamp(rawRate, symbolSettings.MinDailyRate, effectiveMaxRate);
                summary = $"rate_mode=BookAsk anchor={safeAnchor:E6} ask={askRate:E6} bid={bidRate:E6} frr={FormatRate(frrRate)} band={symbolSettings.MinDailyRate:E6}..{effectiveMaxRate:E6}{DescribeRateClipTelemetry(rawRate, selectedRate, effectiveMaxRate, visibleMarketCap)}";
                break;

            case "SHADOWMOTOR":
                rawRate = safeAnchor * symbolSettings.MotorRateMultiplier;
                selectedRate = SelectShadowRate(safeAnchor, symbolSettings.MotorRateMultiplier, symbolSettings, maxRateOverride);
                summary = $"rate_mode=ShadowMotor anchor={safeAnchor:E6} mult={symbolSettings.MotorRateMultiplier:F3} ask={askRate:E6} bid={bidRate:E6} frr={FormatRate(frrRate)} band={symbolSettings.MinDailyRate:E6}..{effectiveMaxRate:E6}{DescribeRateClipTelemetry(rawRate, selectedRate, effectiveMaxRate, visibleMarketCap)}";
                break;

            case "SHADOWOPPORTUNISTIC":
                rawRate = safeAnchor * symbolSettings.OpportunisticRateMultiplier;
                selectedRate = SelectShadowRate(safeAnchor, symbolSettings.OpportunisticRateMultiplier, symbolSettings, maxRateOverride);
                summary = $"rate_mode=ShadowOpportunistic anchor={safeAnchor:E6} mult={symbolSettings.OpportunisticRateMultiplier:F3} ask={askRate:E6} bid={bidRate:E6} frr={FormatRate(frrRate)} band={symbolSettings.MinDailyRate:E6}..{effectiveMaxRate:E6}{DescribeRateClipTelemetry(rawRate, selectedRate, effectiveMaxRate, visibleMarketCap)}";
                break;

            default:
                var anchor = symbolSettings.LiveUseFrrAsFloor && frrRate > 0m
                    ? Math.Max(safeAnchor, frrRate)
                    : safeAnchor;
                var regime = ClassifyMarketRegime(anchor, symbolSettings);
                var multiplier = regime switch
                {
                    "HOT" => Math.Max(1m, symbolSettings.LiveHotRegimeRateMultiplier),
                    "LOW" => Math.Max(1m, symbolSettings.LiveLowRegimeRateMultiplier),
                    _ => Math.Max(1m, symbolSettings.LiveNormalRegimeRateMultiplier)
                };

                rawRate = anchor * multiplier;
                selectedRate = Math.Clamp(rawRate, symbolSettings.MinDailyRate, effectiveMaxRate);
                summary = $"rate_mode=SmartRegime regime={regime} anchor={anchor:E6} mult={multiplier:F3} ask={askRate:E6} bid={bidRate:E6} frr={FormatRate(frrRate)} pause={symbolSettings.PauseNewOffers} reserve={symbolSettings.ReserveAmount:F2} band={symbolSettings.MinDailyRate:E6}..{effectiveMaxRate:E6}{DescribeRateClipTelemetry(rawRate, selectedRate, effectiveMaxRate, visibleMarketCap)}";
                break;
        }

        return (selectedRate, summary);
    }

    private static string NormalizeLiveRateMode(string? rateMode)
    {
        if (string.IsNullOrWhiteSpace(rateMode))
            return "SMARTREGIME";

        return rateMode
            .Trim()
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
    }

    private static string NormalizeLivePlacementPolicyMode(string? placementPolicyMode)
    {
        if (string.IsNullOrWhiteSpace(placementPolicyMode))
            return "IMMEDIATE";

        return placementPolicyMode
            .Trim()
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
    }

    private static string NormalizeManagedOfferTargetMode(string? targetMode)
    {
        if (string.IsNullOrWhiteSpace(targetMode))
            return "LIVE";

        return targetMode
            .Trim()
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
    }

    private static string DescribeLiveSplit(FundingLiveSlotPlan slotPlan)
        => $"Motor:{slotPlan.DesiredMotorSlots}/Opportunistic:{slotPlan.DesiredOpportunisticSlots}/Aggressive:{slotPlan.DesiredAggressiveSlots}/Sniper:{slotPlan.DesiredSniperSlots}";

    private static string DescribeShadowSplit(FundingLiveSlotPlan slotPlan)
        => $"{slotPlan.DesiredMotorSlots}/{slotPlan.DesiredOpportunisticSlots}/{slotPlan.DesiredAggressiveSlots}/{slotPlan.DesiredSniperSlots}";

    private static string ResolveManagedFallbackBucketName(FundingPlacementCandidate candidate, string slotRole)
    {
        var opportunisticEnabled =
            candidate.SymbolSettings.OpportunisticAllocationFraction > 0m &&
            string.Equals(
                NormalizeLivePlacementPolicyMode(candidate.SymbolSettings.LivePlacementPolicyMode),
                "OPPORTUNISTICWAITFALLBACK",
                StringComparison.OrdinalIgnoreCase);
        var aggressiveEnabled =
            candidate.SymbolSettings.EnableLiveAggressivePromotion &&
            candidate.SymbolSettings.AggressiveAllocationFraction > 0m;

        if (string.Equals(slotRole, "Sniper", StringComparison.OrdinalIgnoreCase))
        {
            if (aggressiveEnabled)
                return "Aggressive";

            if (opportunisticEnabled)
                return "Opportunistic";
        }

        if (string.Equals(slotRole, "Aggressive", StringComparison.OrdinalIgnoreCase) && opportunisticEnabled)
        {
            return "Opportunistic";
        }

        return "Motor";
    }

    private static string NormalizeManagedOfferPolicyMode(string? policyMode)
    {
        if (string.IsNullOrWhiteSpace(policyMode))
            return "IMMEDIATE";

        return policyMode
            .Trim()
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
    }

    private static string FormatRate(decimal rate)
    {
        return rate > 0m ? rate.ToString("E6") : "n/a";
    }

    private string ClassifyMarketRegime(decimal marketRate, FundingSymbolRuntimeSettings symbolSettings)
    {
        var minRate = symbolSettings.MinDailyRate;
        var maxRate = Math.Max(minRate + 0.00000001m, symbolSettings.MaxDailyRate);
        var span = maxRate - minRate;
        var normalized = span <= 0m ? 0m : (marketRate - minRate) / span;

        if (normalized <= 0.33m)
            return "LOW";

        if (normalized >= 0.66m)
            return "HOT";

        return "NORMAL";
    }

    private int GetMotorMaxWaitMinutes(string regime, FundingSymbolRuntimeSettings symbolSettings)
    {
        return regime switch
        {
            "HOT" => Math.Max(1, symbolSettings.MotorMaxWaitMinutesHotRegime),
            "LOW" => Math.Max(1, symbolSettings.MotorMaxWaitMinutesLowRegime),
            _ => Math.Max(1, symbolSettings.MotorMaxWaitMinutesNormalRegime)
        };
    }

    private int GetOpportunisticMaxWaitMinutes(string regime, FundingSymbolRuntimeSettings symbolSettings)
    {
        return regime switch
        {
            "HOT" => Math.Max(1, symbolSettings.OpportunisticMaxWaitMinutesHotRegime),
            "LOW" => Math.Max(1, symbolSettings.OpportunisticMaxWaitMinutesLowRegime),
            _ => Math.Max(1, symbolSettings.OpportunisticMaxWaitMinutesNormalRegime)
        };
    }

    private int GetAggressiveMaxWaitMinutes(string regime, FundingSymbolRuntimeSettings symbolSettings)
    {
        return regime switch
        {
            "HOT" => Math.Max(1, symbolSettings.AggressiveMaxWaitMinutesHotRegime),
            "LOW" => Math.Max(1, symbolSettings.AggressiveMaxWaitMinutesLowRegime),
            _ => Math.Max(1, symbolSettings.AggressiveMaxWaitMinutesNormalRegime)
        };
    }

    private int GetSniperMaxWaitMinutes(string regime, FundingSymbolRuntimeSettings symbolSettings)
    {
        return regime switch
        {
            "HOT" => Math.Max(1, symbolSettings.SniperMaxWaitMinutesHotRegime),
            "LOW" => Math.Max(1, symbolSettings.SniperMaxWaitMinutesLowRegime),
            _ => Math.Max(1, symbolSettings.SniperMaxWaitMinutesNormalRegime)
        };
    }

    private static decimal ClampFraction(decimal value)
    {
        return Math.Clamp(value, 0m, 1m);
    }

    private static long? TryParseLongId(string? value)
    {
        return long.TryParse(value, out var parsed) ? parsed : null;
    }

    private IReadOnlyList<string> GetPreferredSymbols()
    {
        var configuredSymbols = _options.PreferredSymbols
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Select(BitfinexFundingSymbolNormalizer.Normalize)
            .Where(symbol => ResolveSymbolSettings(symbol).Enabled)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (configuredSymbols.Length > 0)
        {
            return configuredSymbols;
        }

        return BitfinexFundingOptions.DefaultPreferredSymbols
            .Select(BitfinexFundingSymbolNormalizer.Normalize)
            .Where(symbol => ResolveSymbolSettings(symbol).Enabled)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private FundingSymbolRuntimeSettings ResolveSymbolSettings(string fundingSymbol)
    {
        var normalizedSymbol = BitfinexFundingSymbolNormalizer.Normalize(fundingSymbol);
        var profile = _options.SymbolProfiles
            .FirstOrDefault(profile => string.Equals(
                BitfinexFundingSymbolNormalizer.Normalize(profile.Symbol),
                normalizedSymbol,
                StringComparison.OrdinalIgnoreCase));

        return new FundingSymbolRuntimeSettings(
            Symbol: normalizedSymbol,
            Enabled: profile?.Enabled ?? true,
            EnableFundingPerformanceReports: profile?.EnableFundingPerformanceReports ?? _options.EnableFundingPerformanceReports,
            FundingPerformanceReportIntervalMinutes: Math.Max(5, profile?.FundingPerformanceReportIntervalMinutes ?? _options.FundingPerformanceReportIntervalMinutes),
            PauseNewOffers: profile?.PauseNewOffers ?? false,
            MinOfferAmount: profile?.MinOfferAmount ?? _options.MinOfferAmount,
            MaxOfferAmount: profile?.MaxOfferAmount ?? _options.MaxOfferAmount,
            ReserveAmount: profile?.ReserveAmount ?? _options.ReserveAmount,
            MinDailyRate: profile?.MinDailyRate ?? _options.MinDailyRate,
            MaxDailyRate: Math.Max(profile?.MinDailyRate ?? _options.MinDailyRate, profile?.MaxDailyRate ?? _options.MaxDailyRate),
            MinManagedOfferAgeSecondsBeforeReplace: profile?.MinManagedOfferAgeSecondsBeforeReplace ?? _options.MinManagedOfferAgeSecondsBeforeReplace,
            MinManagedOfferAgeSecondsBeforeReplaceWhenCapacityFull: profile?.MinManagedOfferAgeSecondsBeforeReplaceWhenCapacityFull ?? _options.MinManagedOfferAgeSecondsBeforeReplaceWhenCapacityFull,
            EnableManagedFallbackNearMarketHold: profile?.EnableManagedFallbackNearMarketHold ?? _options.EnableManagedFallbackNearMarketHold,
            ManagedFallbackNearMarketHoldDelta: profile?.ManagedFallbackNearMarketHoldDelta ?? _options.ManagedFallbackNearMarketHoldDelta,
            LiveRateMode: string.IsNullOrWhiteSpace(profile?.LiveRateMode) ? _options.LiveRateMode : profile!.LiveRateMode!.Trim(),
            LivePlacementPolicyMode: string.IsNullOrWhiteSpace(profile?.LivePlacementPolicyMode) ? _options.LivePlacementPolicyMode : profile!.LivePlacementPolicyMode!.Trim(),
            ManagedOfferTargetMode: string.IsNullOrWhiteSpace(profile?.ManagedOfferTargetMode) ? _options.ManagedOfferTargetMode : profile!.ManagedOfferTargetMode!.Trim(),
            ManagedOfferPolicyMode: string.IsNullOrWhiteSpace(profile?.ManagedOfferPolicyMode) ? _options.ManagedOfferPolicyMode : profile!.ManagedOfferPolicyMode!.Trim(),
            LiveUseFrrAsFloor: profile?.LiveUseFrrAsFloor ?? _options.LiveUseFrrAsFloor,
            LiveLowRegimeRateMultiplier: profile?.LiveLowRegimeRateMultiplier ?? _options.LiveLowRegimeRateMultiplier,
            LiveNormalRegimeRateMultiplier: profile?.LiveNormalRegimeRateMultiplier ?? _options.LiveNormalRegimeRateMultiplier,
            LiveHotRegimeRateMultiplier: profile?.LiveHotRegimeRateMultiplier ?? _options.LiveHotRegimeRateMultiplier,
            MotorAllocationFraction: profile?.MotorAllocationFraction ?? _options.MotorAllocationFraction,
            OpportunisticAllocationFraction: profile?.OpportunisticAllocationFraction ?? _options.OpportunisticAllocationFraction,
            AggressiveAllocationFraction: profile?.AggressiveAllocationFraction ?? _options.AggressiveAllocationFraction,
            SniperAllocationFraction: profile?.SniperAllocationFraction ?? _options.SniperAllocationFraction,
            EnableLiveAggressivePromotion: profile?.EnableLiveAggressivePromotion ?? _options.EnableLiveAggressivePromotion,
            EnableLiveSniperPromotion: profile?.EnableLiveSniperPromotion ?? _options.EnableLiveSniperPromotion,
            EnableAdaptiveAggressiveMaxRate: profile?.EnableAdaptiveAggressiveMaxRate ?? _options.EnableAdaptiveAggressiveMaxRate,
            AggressiveAdaptiveMaxDailyRate: profile?.AggressiveAdaptiveMaxDailyRate ?? _options.AggressiveAdaptiveMaxDailyRate,
            EnableAdaptiveSniperMaxRate: profile?.EnableAdaptiveSniperMaxRate ?? _options.EnableAdaptiveSniperMaxRate,
            SniperAdaptiveMaxDailyRate: profile?.SniperAdaptiveMaxDailyRate ?? _options.SniperAdaptiveMaxDailyRate,
            EnableAdaptiveSingleSlotMaxRate: profile?.EnableAdaptiveSingleSlotMaxRate ?? _options.EnableAdaptiveSingleSlotMaxRate,
            SingleSlotAdaptiveMaxDailyRate: profile?.SingleSlotAdaptiveMaxDailyRate ?? _options.SingleSlotAdaptiveMaxDailyRate,
            EnableAdaptiveMotorMaxRate: profile?.EnableAdaptiveMotorMaxRate ?? _options.EnableAdaptiveMotorMaxRate,
            MotorAdaptiveMaxDailyRate: profile?.MotorAdaptiveMaxDailyRate ?? _options.MotorAdaptiveMaxDailyRate,
            EnableAdaptiveOpportunisticMaxRate: profile?.EnableAdaptiveOpportunisticMaxRate ?? _options.EnableAdaptiveOpportunisticMaxRate,
            OpportunisticAdaptiveMaxDailyRate: profile?.OpportunisticAdaptiveMaxDailyRate ?? _options.OpportunisticAdaptiveMaxDailyRate,
            MotorRateMultiplier: profile?.MotorRateMultiplier ?? _options.MotorRateMultiplier,
            OpportunisticRateMultiplier: profile?.OpportunisticRateMultiplier ?? _options.OpportunisticRateMultiplier,
            AggressiveRateMultiplier: profile?.AggressiveRateMultiplier ?? _options.AggressiveRateMultiplier,
            SniperRateMultiplier: profile?.SniperRateMultiplier ?? _options.SniperRateMultiplier,
            MotorMaxWaitMinutesLowRegime: profile?.MotorMaxWaitMinutesLowRegime ?? _options.MotorMaxWaitMinutesLowRegime,
            MotorMaxWaitMinutesNormalRegime: profile?.MotorMaxWaitMinutesNormalRegime ?? _options.MotorMaxWaitMinutesNormalRegime,
            MotorMaxWaitMinutesHotRegime: profile?.MotorMaxWaitMinutesHotRegime ?? _options.MotorMaxWaitMinutesHotRegime,
            ManagedOfferFallbackCarryForwardMinutes: profile?.ManagedOfferFallbackCarryForwardMinutes ?? _options.ManagedOfferFallbackCarryForwardMinutes,
            MaxActiveOffersPerSymbol: profile?.MaxActiveOffersPerSymbol ?? _options.MaxActiveOffersPerSymbol,
            OpportunisticMaxWaitMinutesLowRegime: profile?.OpportunisticMaxWaitMinutesLowRegime ?? _options.OpportunisticMaxWaitMinutesLowRegime,
            OpportunisticMaxWaitMinutesNormalRegime: profile?.OpportunisticMaxWaitMinutesNormalRegime ?? _options.OpportunisticMaxWaitMinutesNormalRegime,
            OpportunisticMaxWaitMinutesHotRegime: profile?.OpportunisticMaxWaitMinutesHotRegime ?? _options.OpportunisticMaxWaitMinutesHotRegime,
            AggressiveMaxWaitMinutesLowRegime: profile?.AggressiveMaxWaitMinutesLowRegime ?? _options.AggressiveMaxWaitMinutesLowRegime,
            AggressiveMaxWaitMinutesNormalRegime: profile?.AggressiveMaxWaitMinutesNormalRegime ?? _options.AggressiveMaxWaitMinutesNormalRegime,
            AggressiveMaxWaitMinutesHotRegime: profile?.AggressiveMaxWaitMinutesHotRegime ?? _options.AggressiveMaxWaitMinutesHotRegime,
            SniperMaxWaitMinutesLowRegime: profile?.SniperMaxWaitMinutesLowRegime ?? _options.SniperMaxWaitMinutesLowRegime,
            SniperMaxWaitMinutesNormalRegime: profile?.SniperMaxWaitMinutesNormalRegime ?? _options.SniperMaxWaitMinutesNormalRegime,
            SniperMaxWaitMinutesHotRegime: profile?.SniperMaxWaitMinutesHotRegime ?? _options.SniperMaxWaitMinutesHotRegime
        );
    }

    private void LogEffectiveSymbolSettings()
    {
        foreach (var symbol in GetPreferredSymbols())
        {
            var settings = ResolveSymbolSettings(symbol);
            var liveSplitMode = settings.MaxActiveOffersPerSymbol <= 1
                ? "MotorOnly"
                : settings.EnableLiveSniperPromotion && settings.MaxActiveOffersPerSymbol > 3
                    ? "Motor+Opportunistic+Aggressive+SniperPromotion"
                    : settings.EnableLiveAggressivePromotion && settings.MaxActiveOffersPerSymbol > 2
                        ? "Motor+Opportunistic+Aggressive"
                    : settings.OpportunisticAllocationFraction > 0m &&
                      string.Equals(
                          NormalizeLivePlacementPolicyMode(settings.LivePlacementPolicyMode),
                          "OPPORTUNISTICWAITFALLBACK",
                          StringComparison.OrdinalIgnoreCase)
                        ? settings.MaxActiveOffersPerSymbol > 2
                            ? "MotorFirst+OpportunisticWeighted"
                            : "MotorFirst+OpportunisticSecond"
                        : "MotorFirst";
            _log.Information(
                "[BFX-FUND] symbol-profile symbol={Symbol} enabled={Enabled} reports={ReportsEnabled}/{ReportInterval}m pause={Pause} reserve={Reserve:F2} amountBand={MinAmount:F2}..{MaxAmount:F2} maxActive={MaxActive} liveSplit={LiveSplit} aggressiveLive={AggressiveLive} aggressiveAdaptive={AggressiveAdaptive} aggressiveAdaptiveCap={AggressiveAdaptiveCap:E6} sniperLive={SniperLive} sniperAdaptive={SniperAdaptive} sniperAdaptiveCap={SniperAdaptiveCap:E6} singleSlotAdaptive={SingleSlotAdaptive} singleSlotAdaptiveCap={SingleSlotAdaptiveCap:E6} motorAdaptive={MotorAdaptive} motorAdaptiveCap={MotorAdaptiveCap:E6} opportunisticAdaptive={OpportunisticAdaptive} opportunisticAdaptiveCap={OpportunisticAdaptiveCap:E6} nearMarketHold={NearMarketHold} nearMarketDelta={NearMarketDelta:E6} rateMode={RateMode} placementPolicy={PlacementPolicy} managedTarget={ManagedTarget} managedPolicy={ManagedPolicy} managedAge={ManagedAge}s/{ManagedCapacityAge}s managedCarry={ManagedCarry}m rateBand={MinRate:E6}..{MaxRate:E6} liveMult={Low:F3}/{Normal:F3}/{Hot:F3} shadowAlloc={MotorAlloc:F3}/{OppAlloc:F3}/{AggAlloc:F3}/{SniperAlloc:F3} shadowRateMult={MotorRate:F3}/{OppRate:F3}/{AggRate:F3}/{SniperRate:F3} motorWait={MotorLow}/{MotorNormal}/{MotorHot} oppWait={OppLow}/{OppNormal}/{OppHot} aggressiveWait={AggLow}/{AggNormal}/{AggHot} sniperWait={SniperLow}/{SniperNormal}/{SniperHot}",
                settings.Symbol,
                settings.Enabled,
                settings.EnableFundingPerformanceReports,
                settings.FundingPerformanceReportIntervalMinutes,
                settings.PauseNewOffers,
                settings.ReserveAmount,
                settings.MinOfferAmount,
                settings.MaxOfferAmount,
                settings.MaxActiveOffersPerSymbol,
                liveSplitMode,
                settings.EnableLiveAggressivePromotion,
                settings.EnableAdaptiveAggressiveMaxRate,
                settings.AggressiveAdaptiveMaxDailyRate,
                settings.EnableLiveSniperPromotion,
                settings.EnableAdaptiveSniperMaxRate,
                settings.SniperAdaptiveMaxDailyRate,
                settings.EnableAdaptiveSingleSlotMaxRate,
                settings.SingleSlotAdaptiveMaxDailyRate,
                settings.EnableAdaptiveMotorMaxRate,
                settings.MotorAdaptiveMaxDailyRate,
                settings.EnableAdaptiveOpportunisticMaxRate,
                settings.OpportunisticAdaptiveMaxDailyRate,
                settings.EnableManagedFallbackNearMarketHold,
                settings.ManagedFallbackNearMarketHoldDelta,
                settings.LiveRateMode,
                settings.LivePlacementPolicyMode,
                settings.ManagedOfferTargetMode,
                settings.ManagedOfferPolicyMode,
                settings.MinManagedOfferAgeSecondsBeforeReplace,
                settings.MinManagedOfferAgeSecondsBeforeReplaceWhenCapacityFull,
                settings.ManagedOfferFallbackCarryForwardMinutes,
                settings.MinDailyRate,
                settings.MaxDailyRate,
                settings.LiveLowRegimeRateMultiplier,
                settings.LiveNormalRegimeRateMultiplier,
                settings.LiveHotRegimeRateMultiplier,
                settings.MotorAllocationFraction,
                settings.OpportunisticAllocationFraction,
                settings.AggressiveAllocationFraction,
                settings.SniperAllocationFraction,
                settings.MotorRateMultiplier,
                settings.OpportunisticRateMultiplier,
                settings.AggressiveRateMultiplier,
                settings.SniperRateMultiplier,
                settings.MotorMaxWaitMinutesLowRegime,
                settings.MotorMaxWaitMinutesNormalRegime,
                settings.MotorMaxWaitMinutesHotRegime,
                settings.OpportunisticMaxWaitMinutesLowRegime,
                settings.OpportunisticMaxWaitMinutesNormalRegime,
                settings.OpportunisticMaxWaitMinutesHotRegime,
                settings.AggressiveMaxWaitMinutesLowRegime,
                settings.AggressiveMaxWaitMinutesNormalRegime,
                settings.AggressiveMaxWaitMinutesHotRegime,
                settings.SniperMaxWaitMinutesLowRegime,
                settings.SniperMaxWaitMinutesNormalRegime,
                settings.SniperMaxWaitMinutesHotRegime);
        }
    }

    private static string FundingSymbolToCurrency(string fundingSymbol)
    {
        if (string.IsNullOrWhiteSpace(fundingSymbol))
            return string.Empty;

        var symbol = BitfinexFundingSymbolNormalizer.Normalize(fundingSymbol);
        if (symbol.Length >= 2 && char.ToUpperInvariant(symbol[0]) == 'F')
            return symbol.Substring(1);

        return symbol.ToUpperInvariant();
    }

    private static bool IsFundingWalletType(string? walletType)
    {
        if (string.IsNullOrWhiteSpace(walletType))
            return false;

        return string.Equals(walletType, "funding", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(walletType, "deposit", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CurrencyMatches(string walletCurrency, string fundingCurrency)
    {
        return string.Equals(
            NormalizeCurrency(walletCurrency),
            NormalizeCurrency(fundingCurrency),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeCurrency(string currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
            return string.Empty;

        var normalized = currency.Trim().ToUpperInvariant();
        return normalized switch
        {
            "USDT" => "UST",
            _ => normalized
        };
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? linkedCts;
        Task? loopTask;
        Task? feedTask;

        lock (_sync)
        {
            linkedCts = _linkedCts;
            loopTask = _loopTask;
            feedTask = _feedTask;
            _linkedCts = null;
            _loopTask = null;
            _feedTask = null;
        }

        if (linkedCts is not null)
        {
            try
            {
                linkedCts.Cancel();
            }
            catch
            {
            }
        }

        if (loopTask is not null)
        {
            try
            {
                await loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (feedTask is not null)
        {
            try
            {
                await feedTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        linkedCts?.Dispose();
        if (_privateFeed is not null)
        {
            await _privateFeed.DisposeAsync().ConfigureAwait(false);
        }
        await _api.DisposeAsync().ConfigureAwait(false);
    }

    private sealed record FundingPlacementCandidate(
        string Symbol,
        string Currency,
        string WalletType,
        decimal AvailableBalance,
        decimal LendableBalance,
        FundingOfferRequest Request,
        string RateSelectionSummary,
        FundingTickerSnapshot Ticker,
        FundingSymbolRuntimeSettings SymbolSettings,
        bool PauseNewOffers);

    private sealed record FundingSymbolRuntimeSettings(
        string Symbol,
        bool Enabled,
        bool EnableFundingPerformanceReports,
        int FundingPerformanceReportIntervalMinutes,
        bool PauseNewOffers,
        decimal MinOfferAmount,
        decimal MaxOfferAmount,
        decimal ReserveAmount,
        decimal MinDailyRate,
        decimal MaxDailyRate,
        int MinManagedOfferAgeSecondsBeforeReplace,
        int MinManagedOfferAgeSecondsBeforeReplaceWhenCapacityFull,
        bool EnableManagedFallbackNearMarketHold,
        decimal ManagedFallbackNearMarketHoldDelta,
        string LiveRateMode,
        string LivePlacementPolicyMode,
        string ManagedOfferTargetMode,
        string ManagedOfferPolicyMode,
        bool LiveUseFrrAsFloor,
        decimal LiveLowRegimeRateMultiplier,
        decimal LiveNormalRegimeRateMultiplier,
        decimal LiveHotRegimeRateMultiplier,
        decimal MotorAllocationFraction,
        decimal OpportunisticAllocationFraction,
        decimal AggressiveAllocationFraction,
        decimal SniperAllocationFraction,
        bool EnableLiveAggressivePromotion,
        bool EnableAdaptiveAggressiveMaxRate,
        decimal AggressiveAdaptiveMaxDailyRate,
        bool EnableLiveSniperPromotion,
        bool EnableAdaptiveSniperMaxRate,
        decimal SniperAdaptiveMaxDailyRate,
        bool EnableAdaptiveSingleSlotMaxRate,
        decimal SingleSlotAdaptiveMaxDailyRate,
        bool EnableAdaptiveMotorMaxRate,
        decimal MotorAdaptiveMaxDailyRate,
        bool EnableAdaptiveOpportunisticMaxRate,
        decimal OpportunisticAdaptiveMaxDailyRate,
        decimal MotorRateMultiplier,
        decimal OpportunisticRateMultiplier,
        decimal AggressiveRateMultiplier,
        decimal SniperRateMultiplier,
        int MotorMaxWaitMinutesLowRegime,
        int MotorMaxWaitMinutesNormalRegime,
        int MotorMaxWaitMinutesHotRegime,
        int ManagedOfferFallbackCarryForwardMinutes,
        int MaxActiveOffersPerSymbol,
        int OpportunisticMaxWaitMinutesLowRegime,
        int OpportunisticMaxWaitMinutesNormalRegime,
        int OpportunisticMaxWaitMinutesHotRegime,
        int AggressiveMaxWaitMinutesLowRegime,
        int AggressiveMaxWaitMinutesNormalRegime,
        int AggressiveMaxWaitMinutesHotRegime,
        int SniperMaxWaitMinutesLowRegime,
        int SniperMaxWaitMinutesNormalRegime,
        int SniperMaxWaitMinutesHotRegime);

    private sealed record FundingLivePlacementPolicy(
        string Mode,
        string Regime,
        int MaxWaitMinutes,
        FundingOfferRequest TargetRequest,
        FundingOfferRequest FallbackRequest,
        string TargetSummary,
        string FallbackSummary,
        bool PlaceImmediately);

    private sealed record FundingLiveSlotPlan(
        decimal SlotAmount,
        int MaxActiveOffers,
        int AdditionalSlotsByBalance,
        int TotalSlotsNow,
        int DesiredMotorSlots,
        int DesiredOpportunisticSlots,
        int DesiredAggressiveSlots,
        int DesiredSniperSlots,
        bool OpportunisticEnabled,
        bool AggressiveEnabled,
        bool SniperEnabled);

    private sealed record FundingLivePlacementWaitState(
        string Symbol,
        string Regime,
        decimal Amount,
        int PeriodDays,
        decimal TargetRate,
        decimal FallbackRate,
        DateTime StartedUtc,
        DateTime DeadlineUtc,
        DateTime LastSeenUtc);

    private sealed record FundingManagedActiveSlotAssignment(
        FundingOfferInfo Offer,
        string Role,
        int SlotIndex);

    private sealed record FundingManagedFallbackCarryForwardState(
        string Symbol,
        decimal Amount,
        int PeriodDays,
        decimal Rate,
        string? SourceOfferId,
        DateTime CreatedUtc,
        DateTime ExpiresUtc);

    private sealed record FundingLifecycleAllocationCandidate(
        string Kind,
        long LifecycleId,
        string Symbol,
        decimal Weight,
        long? FundingTradeId,
        DateTime? OpenedUtc,
        DateTime? ClosedUtc);

    private sealed record FundingLifecycleSyncResult(
        DateTime? SyncedUtc,
        IReadOnlyList<FundingCreditStateRecord> Credits,
        IReadOnlyList<FundingLoanStateRecord> Loans,
        IReadOnlyList<FundingTradeRecord> Trades,
        IReadOnlyList<FundingInterestLedgerRecord> InterestEntries,
        IReadOnlyList<FundingInterestAllocationRecord> InterestAllocations,
        IReadOnlyList<FundingCapitalEventRecord> CapitalEvents,
        FundingReconciliationLogRecord? ReconciliationLog)
    {
        public static FundingLifecycleSyncResult Empty { get; } = new(
            SyncedUtc: null,
            Credits: Array.Empty<FundingCreditStateRecord>(),
            Loans: Array.Empty<FundingLoanStateRecord>(),
            Trades: Array.Empty<FundingTradeRecord>(),
            InterestEntries: Array.Empty<FundingInterestLedgerRecord>(),
            InterestAllocations: Array.Empty<FundingInterestAllocationRecord>(),
            CapitalEvents: Array.Empty<FundingCapitalEventRecord>(),
            ReconciliationLog: null);
    }
}
