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
    private readonly Dictionary<string, FundingOfferInfo> _activeOffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _managedOfferIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FundingWalletBalance> _latestWalletBalances = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FundingShadowActionSession> _shadowActionSessions = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _linkedCts;
    private Task? _loopTask;
    private Task? _feedTask;
    private DateTime _lastOfferStateSyncUtc;
    private DateTime _lastLifecycleSyncUtc;
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

        var shadowPlans = BuildShadowPlans(preferredSymbols, wallets, tickers);
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
    }

    private FundingPlacementCandidate? TryBuildPlacementCandidate(
        string fundingSymbol,
        IReadOnlyList<FundingWalletBalance> wallets,
        IReadOnlyList<FundingTickerSnapshot> tickers,
        out FundingDecision? skipDecision)
    {
        var nowUtc = DateTime.UtcNow;
        var currency = FundingSymbolToCurrency(fundingSymbol);
        var wallet = FindWalletBalance(wallets, currency);
        var walletType = _options.UseFundingWalletOnly ? "funding" : "any";

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

        var lendable = Math.Max(0m, wallet.Available - _options.ReserveAmount);
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

        var proposedAmount = Math.Min(lendable, _options.MaxOfferAmount);
        if (proposedAmount < _options.MinOfferAmount)
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

        var marketRate = ticker.AskRate > 0m ? ticker.AskRate : ticker.BidRate;
        var proposedRate = Math.Clamp(marketRate, _options.MinDailyRate, _options.MaxDailyRate);
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
            Request: request
        );
    }

    private FundingDecision BuildExecutionDecision(
        FundingPlacementCandidate candidate,
        IReadOnlyList<FundingOfferInfo> activeOffers)
    {
        var nowUtc = DateTime.UtcNow;

        if (activeOffers.Count > 1)
        {
            return new FundingDecision(
                Action: "skip_multiple_active_offers",
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
                Reason: $"Multiple active offers exist for {candidate.Symbol}; manager will not mutate ambiguous state.",
                TimestampUtc: nowUtc
            );
        }

        if (activeOffers.Count == 1)
        {
            var activeOffer = activeOffers[0];
            var canManageExisting = _options.AllowManagingExternalOffers || IsManagedOffer(activeOffer.OfferId);
            if (!canManageExisting)
            {
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
                    Reason: $"External active funding offer exists (offerId={activeOffer.OfferId}); manager will not modify it.",
                    TimestampUtc: nowUtc
                );
            }

            if (!ShouldReplaceOffer(activeOffer, candidate.Request, out var replaceReason))
            {
                return new FundingDecision(
                    Action: "skip_active_offer_ok",
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
                    Reason: $"Managed active offer remains within thresholds (offerId={activeOffer.OfferId}). {replaceReason}",
                    TimestampUtc: nowUtc
                );
            }

            return new FundingDecision(
                Action: _options.DryRun ? "would_cancel_for_replace" : "cancel_for_replace",
                IsDryRun: _options.DryRun,
                IsActionable: true,
                Symbol: candidate.Symbol,
                Currency: candidate.Currency,
                WalletType: candidate.WalletType,
                AvailableBalance: candidate.AvailableBalance,
                LendableBalance: candidate.LendableBalance,
                ProposedAmount: candidate.Request.Amount,
                ProposedRate: candidate.Request.Rate,
                ProposedPeriodDays: candidate.Request.PeriodDays,
                Reason: $"Existing managed offer should be replaced (offerId={activeOffer.OfferId}). {replaceReason}",
                TimestampUtc: nowUtc
            );
        }

        return new FundingDecision(
            Action: _options.DryRun ? "would_place" : "place",
            IsDryRun: _options.DryRun,
            IsActionable: true,
            Symbol: candidate.Symbol,
            Currency: candidate.Currency,
            WalletType: candidate.WalletType,
            AvailableBalance: candidate.AvailableBalance,
            LendableBalance: candidate.LendableBalance,
            ProposedAmount: candidate.Request.Amount,
            ProposedRate: candidate.Request.Rate,
            ProposedPeriodDays: candidate.Request.PeriodDays,
            Reason: _options.DryRun
                ? "Dry-run funding recommendation generated from market snapshot and funding wallet availability."
                : "Funding offer should be submitted.",
            TimestampUtc: nowUtc
        );
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

    private bool ShouldReplaceOffer(FundingOfferInfo activeOffer, FundingOfferRequest targetRequest, out string reason)
    {
        var referenceUtc = activeOffer.UpdatedUtc ?? activeOffer.CreatedUtc ?? DateTime.UtcNow;
        var age = DateTime.UtcNow - referenceUtc;
        var minAge = TimeSpan.FromSeconds(Math.Max(0, _options.MinManagedOfferAgeSecondsBeforeReplace));
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
            {
                if (candidate is null)
                {
                    return CreateLocalActionResult(
                        action: "submit_offer",
                        success: false,
                        symbol: decision.Symbol,
                        offerId: null,
                        status: "NO_CANDIDATE",
                        message: "Missing placement candidate for live funding submit.");
                }

                var result = await _api.SubmitOfferAsync(candidate.Request, ct).ConfigureAwait(false);
                if (result.Success && !string.IsNullOrWhiteSpace(result.OfferId))
                {
                    RememberManagedOffer(result.OfferId);
                }

                if (result.Success)
                {
                    await RefreshActiveOffersForSymbolAsync(candidate.Symbol, ct).ConfigureAwait(false);
                }

                return result;
            }
            case "cancel_for_replace":
            {
                if (activeOffers.Count != 1)
                {
                    return CreateLocalActionResult(
                        action: "cancel_offer",
                        success: false,
                        symbol: decision.Symbol,
                        offerId: null,
                        status: "AMBIGUOUS_STATE",
                        message: "Cancel/replace requires exactly one active offer.");
                }

                var offer = activeOffers[0];
                var result = await _api.CancelOfferAsync(offer.Symbol, offer.OfferId, ct).ConfigureAwait(false);
                if (result.Success)
                {
                    ForgetManagedOffer(offer.OfferId);
                    await RefreshActiveOffersForSymbolAsync(offer.Symbol, ct).ConfigureAwait(false);
                }

                return result;
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

                return new FundingTradeRecord(
                    Utc: trade.Utc,
                    Exchange: "bitfinex",
                    FundingTradeId: trade.FundingTradeId,
                    Symbol: trade.Symbol,
                    OfferId: trade.OfferId,
                    CreditId: creditCandidates.Count == 1 ? creditCandidates[0].CreditId : null,
                    LoanId: loanCandidates.Count == 1 ? loanCandidates[0].LoanId : null,
                    Amount: trade.Amount,
                    Rate: trade.Rate,
                    PeriodDays: trade.PeriodDays,
                    Maker: trade.Maker,
                    Metadata: new
                    {
                        CreditMatchCount = creditCandidates.Count,
                        LoanMatchCount = loanCandidates.Count,
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
                (!trade.Rate.HasValue || !credit.Rate.HasValue || AreClose(credit.Rate.Value, trade.Rate.Value, 0.00000050m)) &&
                (!trade.PeriodDays.HasValue || !credit.PeriodDays.HasValue || trade.PeriodDays.Value == credit.PeriodDays.Value) &&
                IsLifecycleRecordRelevantAt(credit.CreatedUtc, credit.OpenedUtc, credit.ClosedUtc, trade.Utc))
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
                (!trade.Rate.HasValue || !loan.Rate.HasValue || AreClose(loan.Rate.Value, trade.Rate.Value, 0.00000050m)) &&
                (!trade.PeriodDays.HasValue || !loan.PeriodDays.HasValue || trade.PeriodDays.Value == loan.PeriodDays.Value) &&
                IsLifecycleRecordRelevantAt(loan.CreatedUtc, loan.OpenedUtc, loan.ClosedUtc, trade.Utc))
            .ToArray();
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
        IReadOnlyList<FundingTickerSnapshot> tickers)
    {
        if (!_options.LayeredShadowEnabled || preferredSymbols.Count == 0)
            return Array.Empty<FundingShadowPlan>();

        var plans = new List<FundingShadowPlan>(preferredSymbols.Count);
        var motorFraction = ClampFraction(_options.MotorAllocationFraction);
        var opportunisticFraction = ClampFraction(_options.OpportunisticAllocationFraction);
        var totalFraction = motorFraction + opportunisticFraction;

        if (totalFraction <= 0m)
            return Array.Empty<FundingShadowPlan>();

        var normalizedMotorFraction = motorFraction / totalFraction;
        var normalizedOppFraction = opportunisticFraction / totalFraction;

        foreach (var symbol in preferredSymbols)
        {
            var currency = FundingSymbolToCurrency(symbol);
            var wallet = FindWalletBalance(wallets, currency);
            var ticker = tickers.FirstOrDefault(t => string.Equals(t.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
            if (wallet is null || ticker is null)
                continue;

            var lendable = Math.Max(0m, wallet.Available - _options.ReserveAmount);
            var marketRate = ticker.AskRate > 0m ? ticker.AskRate : ticker.BidRate;
            var regime = ClassifyMarketRegime(marketRate);
            var timestampUtc = DateTime.UtcNow;
            var buckets = new List<FundingShadowBucket>(2);

            if (lendable >= _options.MinOfferAmount)
            {
                var motorTargetAmount = decimal.Round(lendable * normalizedMotorFraction, 8, MidpointRounding.ToZero);
                var opportunisticTargetAmount = decimal.Round(lendable * normalizedOppFraction, 8, MidpointRounding.ToZero);

                if (motorTargetAmount < _options.MinOfferAmount)
                {
                    motorTargetAmount = 0m;
                }

                if (opportunisticTargetAmount < _options.MinOfferAmount)
                {
                    opportunisticTargetAmount = 0m;
                }

                if (motorTargetAmount == 0m && opportunisticTargetAmount == 0m)
                {
                    motorTargetAmount = decimal.Round(Math.Min(lendable, _options.MaxOfferAmount), 8, MidpointRounding.ToZero);
                }

                if (motorTargetAmount > 0m)
                {
                    buckets.Add(new FundingShadowBucket(
                        Bucket: "Motor",
                        AllocationAmount: motorTargetAmount,
                        AllocationFraction: normalizedMotorFraction,
                        TargetRate: SelectShadowRate(marketRate, _options.MotorRateMultiplier),
                        TargetPeriodDays: 2,
                        MaxWaitMinutes: GetMotorMaxWaitMinutes(regime),
                        Role: "baseline_utilization",
                        FallbackBucket: null));
                }

                if (opportunisticTargetAmount > 0m)
                {
                    buckets.Add(new FundingShadowBucket(
                        Bucket: "Opportunistic",
                        AllocationAmount: opportunisticTargetAmount,
                        AllocationFraction: normalizedOppFraction,
                        TargetRate: SelectShadowRate(marketRate, _options.OpportunisticRateMultiplier),
                        TargetPeriodDays: 2,
                        MaxWaitMinutes: GetOpportunisticMaxWaitMinutes(regime),
                        Role: "yield_enhancement",
                        FallbackBucket: "Motor"));
                }
            }

            var summary = buckets.Count == 0
                ? $"No shadow bucket actionable. lendable={lendable:F2} reserve={_options.ReserveAmount:F2} minOffer={_options.MinOfferAmount:F2} regime={regime}"
                : string.Join("; ", buckets.Select(bucket =>
                    $"{bucket.Bucket} amt={bucket.AllocationAmount:F2} rate={bucket.TargetRate:E6} wait={bucket.MaxWaitMinutes}m"));

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
        var fallbackRate = ResolveFallbackRate(plan, bucket);
        var activeOffer = activeOffers.Count == 1 ? activeOffers[0] : null;
        var deadlineUtc = bucket.MaxWaitMinutes > 0
            ? plan.TimestampUtc.AddMinutes(bucket.MaxWaitMinutes)
            : (DateTime?)null;

        if (activeOffers.Count > 1)
        {
            return CreateShadowAction(
                plan,
                bucket,
                action: "would_hold_ambiguous_state",
                isActionable: false,
                fallbackRate: fallbackRate,
                decisionDeadlineUtc: deadlineUtc,
                activeOffers: activeOffers,
                reason: $"Multiple active offers exist for {plan.Symbol}; shadow layer would not mutate ambiguous state.",
                summary: $"{bucket.Bucket} would hold because multiple active offers exist.");
        }

        var targetRequest = BuildShadowTargetRequest(plan.Symbol, bucket);
        if (activeOffer is not null)
        {
            var shouldReplace = ShouldReplaceOffer(activeOffer, targetRequest, out var replaceReason);

            if (string.Equals(bucket.Bucket, "Opportunistic", StringComparison.OrdinalIgnoreCase) &&
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
                        ? $"Opportunistic bucket would wait for stronger conditions and then fall back to {bucket.FallbackBucket ?? "Motor"}. {replaceReason}"
                        : $"Existing active offer is acceptable for the shadow opportunistic bucket. {replaceReason}",
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
                reason: "Opportunistic bucket sees a HOT regime and would place immediately.",
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
            reason: $"Opportunistic bucket would wait for a better rate for up to {bucket.MaxWaitMinutes} minutes before falling back to {bucket.FallbackBucket ?? "Motor"}.",
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

    private FundingOfferRequest BuildShadowTargetRequest(string symbol, FundingShadowBucket bucket)
    {
        var minPeriodDays = Math.Max(2, _options.MinPeriodDays);
        var maxPeriodDays = Math.Max(minPeriodDays, _options.MaxPeriodDays);

        return new FundingOfferRequest(
            Symbol: symbol,
            Amount: decimal.Round(Math.Min(bucket.AllocationAmount, _options.MaxOfferAmount), 8, MidpointRounding.ToZero),
            Rate: Math.Clamp(bucket.TargetRate, _options.MinDailyRate, _options.MaxDailyRate),
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
            "would_wait_for_better_rate" => "waiting",
            "would_wait_then_fallback" => "waiting_fallback",
            "would_reprice_active_offer" => "reprice_ready",
            "would_keep_active_offer" => "aligned_with_live_offer",
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

    private decimal SelectShadowRate(decimal marketRate, decimal multiplier)
    {
        var safeMarketRate = marketRate > 0m ? marketRate : _options.MinDailyRate;
        var scaled = safeMarketRate * multiplier;
        return Math.Clamp(scaled, _options.MinDailyRate, _options.MaxDailyRate);
    }

    private string ClassifyMarketRegime(decimal marketRate)
    {
        var minRate = _options.MinDailyRate;
        var maxRate = Math.Max(minRate + 0.00000001m, _options.MaxDailyRate);
        var span = maxRate - minRate;
        var normalized = span <= 0m ? 0m : (marketRate - minRate) / span;

        if (normalized <= 0.33m)
            return "LOW";

        if (normalized >= 0.66m)
            return "HOT";

        return "NORMAL";
    }

    private int GetMotorMaxWaitMinutes(string regime)
    {
        return regime switch
        {
            "HOT" => Math.Max(1, _options.MotorMaxWaitMinutesHotRegime),
            "LOW" => Math.Max(1, _options.MotorMaxWaitMinutesLowRegime),
            _ => Math.Max(1, _options.MotorMaxWaitMinutesNormalRegime)
        };
    }

    private int GetOpportunisticMaxWaitMinutes(string regime)
    {
        return regime switch
        {
            "HOT" => Math.Max(1, _options.OpportunisticMaxWaitMinutesHotRegime),
            "LOW" => Math.Max(1, _options.OpportunisticMaxWaitMinutesLowRegime),
            _ => Math.Max(1, _options.OpportunisticMaxWaitMinutesNormalRegime)
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
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (configuredSymbols.Length > 0)
        {
            return configuredSymbols;
        }

        return BitfinexFundingOptions.DefaultPreferredSymbols
            .Select(BitfinexFundingSymbolNormalizer.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
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
        FundingOfferRequest Request);

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
