#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Denis.TradingEngine.Core.Trading;
using Serilog;

namespace Denis.TradingEngine.App.Trading.EodSkim
{
    /// <summary>
    /// IBKR EOD skim coordinator (V1 skeleton).
    /// Trenutno je no-op skeleton: shape + method boundaries bez live execution logike.
    /// </summary>
    public sealed class IbkrEodSkimCoordinator
    {
        private static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromSeconds(20);

        private readonly IbkrEodSkimOptions _options;
        private readonly ILogger _log;
        private readonly object _sync = new();
        private readonly HashSet<string> _excludedSymbols;
        private readonly Dictionary<string, SkimSymbolState> _stateBySymbol =
            new(StringComparer.OrdinalIgnoreCase);

        public IbkrEodSkimCoordinator(IbkrEodSkimOptions options, ILogger? log = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _log = log ?? Log.ForContext<IbkrEodSkimCoordinator>();
            _excludedSymbols = new HashSet<string>(
                (_options.ExcludeSymbols ?? new List<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim()),
                StringComparer.OrdinalIgnoreCase);
        }

        public Task EvaluateAsync(IbkrEodSkimContext context, CancellationToken ct = default)
        {
            if (context is null) throw new ArgumentNullException(nameof(context));

            if (!_options.Enabled)
                return Task.CompletedTask;

            return EvaluateEnabledAsync(context, ct);
        }

        public void OnOrderUpdated(IbkrEodSkimOrderUpdate update)
        {
            if (update is null)
                return;

            if (string.IsNullOrWhiteSpace(update.Status))
                return;

            // V1 skeleton: cleanup hook will be implemented when coordinator is wired.
        }

        public void OnSessionBoundary(DateTime utcNow)
        {
            lock (_sync)
            {
                _stateBySymbol.Clear();
            }

            _log.Debug("[EOD-SKIM] session-boundary reset utc={Utc}", utcNow);
        }

        public void ResetSymbol(string symbol, string reason, DateTime utcNow)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return;

            lock (_sync)
            {
                _stateBySymbol.Remove(symbol);
            }

            _log.Debug("[EOD-SKIM] reset sym={Sym} reason={Reason} utc={Utc}", symbol, reason, utcNow);
        }

        private Task EvaluateEnabledAsync(IbkrEodSkimContext context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            if (!context.IsRealMode || !context.IsIbkrMode)
            {
                _log.Debug(
                    "[eod-skim-enter] skip mode-check isReal={IsReal} isIbkr={IsIbkr}",
                    context.IsRealMode,
                    context.IsIbkrMode);
                return Task.CompletedTask;
            }

            var minsToClose = (context.MarketCloseUtc - context.UtcNow).TotalMinutes;
            if (!IsWithinSkimWindow(context.UtcNow, context.MarketCloseUtc))
            {
                _log.Debug(
                    "[eod-skim-enter] skip outside-window utc={Utc} closeUtc={CloseUtc} minsToClose={Mins:F1} startMin={StartMin}",
                    context.UtcNow,
                    context.MarketCloseUtc,
                    minsToClose,
                    _options.StartMinutesBeforeClose);
                return Task.CompletedTask;
            }

            // Skeleton only: wiring/flow boundaries are defined, behavior comes later.
            CleanupClosedOrTerminalSymbols(context, context.UtcNow);

            var candidates = GetEligiblePositionCandidates(context).ToArray();

            _log.Information(
                "[eod-skim-enter] enabled={Enabled} dryRun={DryRun} utc={Utc} closeUtc={CloseUtc} minsToClose={Mins:F1} openPos={OpenPos} candidates={Candidates} stateCount={StateCount}",
                _options.Enabled,
                _options.DryRun,
                context.UtcNow,
                context.MarketCloseUtc,
                minsToClose,
                context.OpenPositions.Count,
                candidates.Length,
                _stateBySymbol.Count);

            if (_excludedSymbols.Count > 0)
            {
                _log.Information(
                    "[eod-skim-config] excludedSymbolsCount={Count} excluded={Excluded}",
                    _excludedSymbols.Count,
                    string.Join(", ", _excludedSymbols.OrderBy(x => x)));
            }

            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();

                if (!TryBuildDecision(context, candidate, context.UtcNow, out var decision, out var skipReason))
                {
                    LogSkip(candidate.Symbol, skipReason ?? "not-eligible", context.UtcNow);
                    continue;
                }

                _ = ExecuteDecisionAsync(context, decision, ct);
            }

            return Task.CompletedTask;
        }

        private bool IsWithinSkimWindow(DateTime utcNow, DateTime marketCloseUtc)
        {
            if (_options.StartMinutesBeforeClose <= 0)
                return false;

            if (marketCloseUtc <= utcNow)
                return false;

            var remaining = marketCloseUtc - utcNow;
            return remaining <= TimeSpan.FromMinutes(_options.StartMinutesBeforeClose);
        }

        private IEnumerable<IbkrEodSkimPositionCandidate> GetEligiblePositionCandidates(IbkrEodSkimContext context)
        {
            return context.OpenPositions
                .Where(p => p.Quantity > 0m)
                .Where(p => p.IsExternalIbkrPosition);
        }

        private bool TryBuildDecision(
            IbkrEodSkimContext context,
            IbkrEodSkimPositionCandidate candidate,
            DateTime utcNow,
            out IbkrEodSkimDecision decision,
            out string? skipReason)
        {
            decision = default!;
            skipReason = null;

            _log.Information(
                "[eod-skim-symbol] sym={Sym} qty={Qty:F4} avg={Avg:F4} external={External}",
                candidate.Symbol,
                candidate.Quantity,
                candidate.AveragePrice,
                candidate.IsExternalIbkrPosition);

            if (IsExcludedByConfig(candidate.Symbol))
            {
                skipReason = "excluded-by-config";
                _log.Information(
                    "[eod-skim-filter] sym={Sym} filter=exclude-symbols matched=true",
                    candidate.Symbol);
                return false;
            }

            if (context.TryGetQuote is null)
            {
                skipReason = "quote-adapter-missing";
                return false;
            }

            var quoteRes = context.TryGetQuote(candidate.Symbol, utcNow);
            if (!quoteRes.Ok || quoteRes.Quote is null)
            {
                skipReason = quoteRes.Reason ?? "quote-unavailable";
                return false;
            }

            var retryCount = 0;
            SkimSymbolState? state = null;
            lock (_sync)
            {
                if (_stateBySymbol.TryGetValue(candidate.Symbol, out var st))
                {
                    state = st;
                    retryCount = st.RetryCount;
                }
            }

            _log.Information(
                "[eod-skim-state] sym={Sym} retryCount={Retry} hasActive={HasActive} activeCorr={Corr} activeBrokerId={Bid} cancelRequested={CancelReq} retryDue={RetryDue}",
                candidate.Symbol,
                retryCount,
                state?.ActiveCorrelationId is not null,
                state?.ActiveCorrelationId ?? "n/a",
                state?.ActiveBrokerOrderId ?? "n/a",
                state?.CancelRequested ?? false,
                state is not null ? IsRetryDue(state, utcNow) : true);

            var q = quoteRes.Quote;
            var quoteAgeSec = (utcNow - q.TimestampUtc).TotalSeconds;
            decimal? spread = null;
            decimal? spreadBps = null;
            if (q.Bid.HasValue && q.Ask.HasValue && q.Bid.Value > 0m && q.Ask.Value > 0m)
            {
                spread = q.Ask.Value - q.Bid.Value;
                var mid = (q.Ask.Value + q.Bid.Value) / 2m;
                if (mid > 0m)
                    spreadBps = (spread.Value / mid) * 10000m;
            }

            _log.Information(
                "[eod-skim-quote] sym={Sym} bid={Bid} ask={Ask} last={Last} ageSec={Age:F2} spread={Spread} spreadBps={SpreadBps}",
                candidate.Symbol,
                q.Bid,
                q.Ask,
                q.Last,
                quoteAgeSec,
                spread,
                spreadBps);

            var candidateSellPx = ComputeCandidateSellLimit(context, candidate.Symbol, q, retryCount);
            if (candidateSellPx <= 0m)
            {
                skipReason = "invalid-candidate-price";
                return false;
            }

            var gross = (candidateSellPx - candidate.AveragePrice) * candidate.Quantity;
            var feeBuffer = context.EstimatedPerOrderFeeUsd;
            var slippageBuffer = context.EstimatedSlippageBufferUsd;
            var net = EstimateNetProfitUsd(context, candidate, candidateSellPx);

            _log.Information(
                "[eod-skim-price] sym={Sym} candidateLimit={Lmt:F4} retry={Retry} rule={Rule}",
                candidate.Symbol,
                candidateSellPx,
                retryCount,
                retryCount > 0 ? "bid-1tick" : "bid/last/ask");

            _log.Information(
                "[eod-skim-profit] sym={Sym} gross={Gross:F4} feeBuf={FeeBuf:F4} slipBuf={SlipBuf:F4} net={Net:F4} minNet={MinNet:F4}",
                candidate.Symbol,
                gross,
                feeBuffer,
                slippageBuffer,
                net,
                _options.MinNetProfitUsd);

            if (net < _options.MinNetProfitUsd)
            {
                skipReason = $"net-profit-below-min:{net:F2}";
                return false;
            }

            decision = new IbkrEodSkimDecision(
                Symbol: candidate.Symbol,
                Quantity: candidate.Quantity,
                AveragePrice: candidate.AveragePrice,
                CandidateLimitPrice: candidateSellPx,
                EstimatedNetProfitUsd: net,
                RetryCount: retryCount,
                ReasonTag: _options.ReasonTag,
                IsRetry: retryCount > 0);

            _log.Information(
                "[eod-skim-decision] sym={Sym} decision=eligible retry={Retry} dryRun={DryRun} tag={Tag}",
                candidate.Symbol,
                retryCount,
                _options.DryRun,
                _options.ReasonTag);

            return true;
        }

        private decimal ComputeCandidateSellLimit(
            IbkrEodSkimContext context,
            string symbol,
            MarketQuote quote,
            int retryCount)
        {
            _ = context;
            _ = symbol;

            // V1 skeleton placeholder:
            // prefer bid, fallback last, then ask; aggressiveness by retry comes later.
            var px = quote.Bid ?? quote.Last ?? quote.Ask ?? 0m;
            if (px <= 0m)
                return 0m;

            if (retryCount > 0 && quote.Bid.HasValue && quote.Bid.Value > 0.01m)
                return Math.Max(quote.Bid.Value - 0.01m, 0.01m);

            return px;
        }

        private decimal EstimateNetProfitUsd(
            IbkrEodSkimContext context,
            IbkrEodSkimPositionCandidate candidate,
            decimal candidateSellPx)
        {
            var gross = (candidateSellPx - candidate.AveragePrice) * candidate.Quantity;
            var buffers = context.EstimatedPerOrderFeeUsd + context.EstimatedSlippageBufferUsd;
            return gross - buffers;
        }

        private Task ExecuteDecisionAsync(IbkrEodSkimContext context, IbkrEodSkimDecision decision, CancellationToken ct)
        {
            return _options.DryRun
                ? ExecuteDryRunAsync(context, decision, ct)
                : ExecuteLiveAsync(context, decision, ct);
        }

        private Task ExecuteDryRunAsync(IbkrEodSkimContext context, IbkrEodSkimDecision decision, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var corrPreview = BuildCorrelationId(decision);
            var brokerIdPreview = $"DRYRUN-{decision.Symbol}-{decision.RetryCount}";
            var lastMsgPreview =
                $"eod-skim dryrun place retry={decision.RetryCount} tag={decision.ReasonTag} estNet={decision.EstimatedNetProfitUsd:F2}";

            _log.Information(
                "[eod-skim-dryrun] sym={Sym} action=would-cancel-exits reasonTag={Tag} retry={Retry}",
                decision.Symbol,
                decision.ReasonTag,
                decision.RetryCount);

            _log.Information(
                "[eod-skim-dryrun] sym={Sym} action=would-place-skim qty={Qty:F4} lim={Lmt:F4} estNet={Net:F4} retry={Retry}/{MaxRetry} tag={Tag}",
                decision.Symbol,
                decision.Quantity,
                decision.CandidateLimitPrice,
                decision.EstimatedNetProfitUsd,
                decision.RetryCount,
                _options.MaxRetries,
                decision.ReasonTag);

            _log.Information(
                "[eod-skim-dryrun-broker] sym={Sym} step=cancel-existing-exits call=CancelAllExitsForSymbol(reasonTag={Tag},retry={Retry}) sideEffect=false",
                decision.Symbol,
                decision.ReasonTag,
                decision.RetryCount);

            _log.Information(
                "[eod-skim-dryrun-db] table=broker_orders sym={Sym} step=update-existing-exits statusPath=cancel-requested->canceled reason=eod-skim-cancel-before-skim sideEffect=false",
                decision.Symbol);

            _log.Information(
                "[eod-skim-dryrun-db] table=broker_orders sym={Sym} step=insert-submitted id={Corr} side=sell type=limit qty={Qty:F4} lim={Lmt:F4} status=submitted lastMsg=\"{LastMsg}\" sideEffect=false",
                decision.Symbol,
                corrPreview,
                decision.Quantity,
                decision.CandidateLimitPrice,
                lastMsgPreview);

            _log.Information(
                "[eod-skim-dryrun-broker] sym={Sym} step=place-skim-order corr={Corr} simulatedBrokerOrderId={Bid} action=SELL qty={Qty:F4} type=LMT lim={Lmt:F4} sideEffect=false",
                decision.Symbol,
                corrPreview,
                brokerIdPreview,
                decision.Quantity,
                decision.CandidateLimitPrice);

            _log.Information(
                "[eod-skim-dryrun-db] table=broker_orders sym={Sym} step=mark-sent id={Corr} brokerOrderId={Bid} status=sent sideEffect=false",
                decision.Symbol,
                corrPreview,
                brokerIdPreview);

            _log.Information(
                "[eod-skim-dryrun-expected-status-path] sym={Sym} corr={Corr} path={Path}",
                decision.Symbol,
                corrPreview,
                decision.IsRetry
                    ? "submitted->sent->(partial?) -> filled OR cancel-requested->canceled (next retry)"
                    : "submitted->sent->(partial?) -> filled OR remains-open-until-reprice");

            _log.Information(
                "[eod-skim-dryrun-db] table=trade_fills sym={Sym} step=no-write-until-fill reason=dryrun expectedOnReal=fill-event sideEffect=false",
                decision.Symbol);

            _log.Information(
                "[eod-skim-dryrun-db] table=trade_journal sym={Sym} step=no-write-until-fill reason=dryrun expectedOnReal=journal-on-fill sideEffect=false",
                decision.Symbol);

            _log.Information(
                "[eod-skim-dryrun-db] table=swing_positions sym={Sym} step=no-close-write-yet reason=wait-for-fill sideEffect=false",
                decision.Symbol);

            _log.Information(
                "[eod-skim-dryrun-db] table=daily_pnl sym={Sym} step=no-pnl-write-yet reason=wait-for-fill/commission sideEffect=false",
                decision.Symbol);

            _log.Information(
                "[eod-skim-db-audit] sym={Sym} dryrun=true action=no-db-write corrPreview={CorrPreview} brokerIdPreview={Bid}",
                decision.Symbol,
                corrPreview,
                brokerIdPreview);

            return Task.CompletedTask;
        }

        private async Task ExecuteLiveAsync(IbkrEodSkimContext context, IbkrEodSkimDecision decision, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            await CancelExistingExitsAsync(context, decision, ct).ConfigureAwait(false);
            await PlaceSkimLimitAsync(context, decision, ct).ConfigureAwait(false);
        }

        private async Task CancelExistingExitsAsync(IbkrEodSkimContext context, IbkrEodSkimDecision decision, CancellationToken ct)
        {
            if (context.CancelAllExitsForSymbolAsync is null)
                return;

            MarkStateCancelRequested(decision.Symbol, context.UtcNow);

            var reason = $"{decision.ReasonTag} dry={_options.DryRun} retry={decision.RetryCount}";
            await context.CancelAllExitsForSymbolAsync(decision.Symbol, context.UtcNow, reason, ct).ConfigureAwait(false);
        }

        private async Task PlaceSkimLimitAsync(IbkrEodSkimContext context, IbkrEodSkimDecision decision, CancellationToken ct)
        {
            if (context.PlaceSkimExitAsync is null)
                return;

            var corr = BuildCorrelationId(decision);
            var req = new IbkrEodSkimPlaceRequest(
                Symbol: decision.Symbol,
                Quantity: decision.Quantity,
                LimitPrice: decision.CandidateLimitPrice,
                CorrelationId: corr,
                ReasonTag: decision.ReasonTag);

            var placeRes = await context.PlaceSkimExitAsync(req, ct).ConfigureAwait(false);
            UpsertStateAfterPlace(
                decision.Symbol,
                placeRes.CorrelationId,
                placeRes.BrokerOrderId,
                decision.CandidateLimitPrice,
                context.UtcNow);
        }

        private bool IsRetryDue(SkimSymbolState state, DateTime utcNow)
        {
            if (state.LastActionUtc is null)
                return true;

            return utcNow - state.LastActionUtc.Value >= DefaultRetryInterval;
        }

        private bool IsExcludedByConfig(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return false;

            return _excludedSymbols.Contains(symbol);
        }

        private void UpsertStateAfterPlace(string symbol, string correlationId, string? brokerOrderId, decimal limitPx, DateTime utcNow)
        {
            lock (_sync)
            {
                if (!_stateBySymbol.TryGetValue(symbol, out var st))
                {
                    st = new SkimSymbolState { Symbol = symbol };
                    _stateBySymbol[symbol] = st;
                }

                st.ActiveCorrelationId = correlationId;
                st.ActiveBrokerOrderId = brokerOrderId;
                st.LastLimitPrice = limitPx;
                st.LastActionUtc = utcNow;
                st.CancelRequested = false;
            }
        }

        private void MarkStateCancelRequested(string symbol, DateTime utcNow)
        {
            lock (_sync)
            {
                if (!_stateBySymbol.TryGetValue(symbol, out var st))
                {
                    st = new SkimSymbolState { Symbol = symbol };
                    _stateBySymbol[symbol] = st;
                }

                st.CancelRequested = true;
                st.LastCancelRequestUtc = utcNow;
                st.LastActionUtc = utcNow;
            }
        }

        private void CleanupClosedOrTerminalSymbols(IbkrEodSkimContext context, DateTime utcNow)
        {
            _ = utcNow;

            HashSet<string> openSymbols = new(
                context.OpenPositions.Where(x => x.Quantity > 0m).Select(x => x.Symbol),
                StringComparer.OrdinalIgnoreCase);

            lock (_sync)
            {
                if (_stateBySymbol.Count == 0)
                    return;

                var toRemove = _stateBySymbol.Keys
                    .Where(sym => !openSymbols.Contains(sym))
                    .ToList();

                foreach (var sym in toRemove)
                    _stateBySymbol.Remove(sym);
            }
        }

        private void LogSkip(string symbol, string reason, DateTime utcNow)
        {
            _log.Information("[eod-skim-decision] sym={Sym} decision=skip reason={Reason} utc={Utc}", symbol, reason, utcNow);
        }

        private string BuildCorrelationId(IbkrEodSkimDecision decision)
        {
            var retryPart = decision.IsRetry ? $"r{decision.RetryCount}" : "r0";
            return $"exit-eod-skim-{retryPart}-{Guid.NewGuid():N}";
        }
    }
}
