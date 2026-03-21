#nullable enable

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
    private readonly Dictionary<string, FundingOfferInfo> _activeOffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _managedOfferIds = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _linkedCts;
    private Task? _loopTask;
    private Task? _feedTask;
    private DateTime _lastOfferStateSyncUtc;
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

        var runtimeHealth = BuildRuntimeHealthSnapshot(preferredSymbols, wallets, tickers, activeOffers, decisions, actionResults);
        await PersistCycleAsync(wallets, tickers, activeOffers, decisions, actionResults, runtimeHealth, ct).ConfigureAwait(false);
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
        object runtimeHealth,
        CancellationToken ct)
    {
        if (_fundingRepo is not null)
        {
            var batch = BuildFundingPersistenceBatch(wallets, tickers, activeOffers, decisions, actionResults, runtimeHealth);
            await _fundingRepo.PersistCycleAsync(batch, ct).ConfigureAwait(false);
        }

        if (_snapshotRepo is null)
            return;

        var records = new List<CryptoSnapshotRecord>(wallets.Count + tickers.Count + activeOffers.Count + decisions.Count + actionResults.Count + 1);

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
        object runtimeHealthMetadata)
    {
        var actionResultsBySymbol = actionResults
            .GroupBy(static x => x.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static g => g.Key, static g => g.Last(), StringComparer.OrdinalIgnoreCase);

        var walletRows = wallets
            .Select(wallet => new FundingWalletSnapshotRecord(
                Utc: DateTime.UtcNow,
                Exchange: "bitfinex",
                WalletType: wallet.WalletType,
                Currency: wallet.Currency,
                Total: wallet.Total,
                Available: wallet.Available,
                Reserved: wallet.Reserved,
                Source: "rest_cycle"))
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

        var runtimeHealth = CreateRuntimeHealthRecord(runtimeHealthMetadata);

        return new FundingPersistenceBatch(
            WalletSnapshots: walletRows,
            MarketSnapshots: marketRows,
            OfferActions: actionRows,
            Offers: offerRowsById.Values.ToArray(),
            OfferEvents: eventRows,
            RuntimeHealth: runtimeHealth);
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
            RestLastSyncUtc: _lastOfferStateSyncUtc == default ? null : _lastOfferStateSyncUtc,
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
            var walletRows = wallets.Select(wallet => new FundingWalletSnapshotRecord(
                Utc: DateTime.UtcNow,
                Exchange: "bitfinex",
                WalletType: wallet.WalletType,
                Currency: wallet.Currency,
                Total: wallet.Total,
                Available: wallet.Available,
                Reserved: wallet.Reserved,
                Source: snapshotType)).ToArray();

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
        IReadOnlyList<FundingOfferActionResult> actionResults)
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
            UsePrivateWebSocket = _options.UsePrivateWebSocket,
            PrivateWsLastMessageUtc = _privateFeed?.LastMessageUtc,
            LastRestOfferSyncUtc = _lastOfferStateSyncUtc == default ? (DateTime?)null : _lastOfferStateSyncUtc,
            HasOfferSnapshot = _hasOfferSnapshot
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
}
