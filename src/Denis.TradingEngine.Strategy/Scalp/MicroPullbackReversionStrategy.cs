#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Denis.TradingEngine.Core.Crypto;
using Denis.TradingEngine.Core.Interfaces;
using Denis.TradingEngine.Core.Trading;
using Serilog;

namespace Denis.TradingEngine.Strategy.Scalp;

public sealed class MicroPullbackReversionStrategy : ITradingStrategy
{
    public event Action<TradeSignal>? TradeSignalGenerated;

    private readonly ILogger _log = Log.ForContext<MicroPullbackReversionStrategy>();
    private readonly decimal _maxSpreadBps = 8.0m;
    private readonly decimal _minLiquidityUsd = 2000m;
    private readonly TimeSpan _maxBookAge = TimeSpan.FromMilliseconds(1500);
    private readonly int _fairValueEmaQuotes = 20;
    private readonly int _recentVolatilityQuotes = 20;
    private readonly decimal _minEffectiveVolatilityBps = 0.75m;
    private readonly decimal _maxEffectiveVolatilityBps = 12.0m;
    private readonly decimal _minNormalizedDislocation = 1.5m;
    private readonly decimal _maxNormalizedDislocation = 4.0m;
    private readonly decimal _maxContinuationMomentumBps = 2.0m;
    private readonly decimal _minMomentumDecayPct = 0.30m;
    private readonly decimal _minImbalanceRecovery = 0.10m;
    private readonly decimal _minMicropriceRecoveryBps = 0.20m;
    private readonly int _reclaimLookbackQuotes = 3;
    private readonly bool _enableEarlyReclaim = true;
    private readonly bool _enableConfirmedReclaim = true;
    private readonly decimal _minReclaimMomentumBps = 0.25m;
    private readonly decimal _microTakeProfitBps = 2.0m;
    private readonly decimal _profitTargetBps = 4.0m;
    private readonly decimal _stopLossBps = 4.0m;
    private readonly TimeSpan _maxTimeToFirstPositiveMfe = TimeSpan.FromSeconds(2);
    private readonly decimal _mfeProtectMinBps = 2.0m;
    private readonly decimal _mfeGivebackBps = 2.0m;
    private readonly TimeSpan _maxStall = TimeSpan.FromSeconds(2);
    private readonly TimeSpan _expectedReversionTime = TimeSpan.FromSeconds(3);
    private readonly TimeSpan _maxHold = TimeSpan.FromSeconds(6);
    private readonly TimeSpan _cooldown = TimeSpan.FromSeconds(5);
    private readonly bool _oneShotPerMove = true;
    private readonly Dictionary<string, SymbolState> _state = new(StringComparer.OrdinalIgnoreCase);

    public MicroPullbackReversionStrategy(
        decimal? maxSpreadBps = null,
        decimal? minLiquidityUsd = null,
        TimeSpan? maxBookAge = null,
        int? fairValueEmaQuotes = null,
        int? recentVolatilityQuotes = null,
        decimal? minEffectiveVolatilityBps = null,
        decimal? maxEffectiveVolatilityBps = null,
        decimal? minNormalizedDislocation = null,
        decimal? maxNormalizedDislocation = null,
        decimal? maxContinuationMomentumBps = null,
        decimal? minMomentumDecayPct = null,
        decimal? minImbalanceRecovery = null,
        decimal? minMicropriceRecoveryBps = null,
        int? reclaimLookbackQuotes = null,
        bool? enableEarlyReclaim = null,
        bool? enableConfirmedReclaim = null,
        decimal? minReclaimMomentumBps = null,
        decimal? microTakeProfitBps = null,
        decimal? profitTargetBps = null,
        decimal? stopLossBps = null,
        TimeSpan? maxTimeToFirstPositiveMfe = null,
        decimal? mfeProtectMinBps = null,
        decimal? mfeGivebackBps = null,
        TimeSpan? maxStall = null,
        TimeSpan? expectedReversionTime = null,
        TimeSpan? maxHold = null,
        TimeSpan? cooldown = null,
        bool? oneShotPerMove = null)
    {
        if (maxSpreadBps.HasValue) _maxSpreadBps = maxSpreadBps.Value;
        if (minLiquidityUsd.HasValue) _minLiquidityUsd = minLiquidityUsd.Value;
        if (maxBookAge.HasValue) _maxBookAge = maxBookAge.Value;
        if (fairValueEmaQuotes.HasValue) _fairValueEmaQuotes = Math.Max(2, fairValueEmaQuotes.Value);
        if (recentVolatilityQuotes.HasValue) _recentVolatilityQuotes = Math.Max(2, recentVolatilityQuotes.Value);
        if (minEffectiveVolatilityBps.HasValue) _minEffectiveVolatilityBps = minEffectiveVolatilityBps.Value;
        if (maxEffectiveVolatilityBps.HasValue) _maxEffectiveVolatilityBps = maxEffectiveVolatilityBps.Value;
        if (minNormalizedDislocation.HasValue) _minNormalizedDislocation = minNormalizedDislocation.Value;
        if (maxNormalizedDislocation.HasValue) _maxNormalizedDislocation = maxNormalizedDislocation.Value;
        if (maxContinuationMomentumBps.HasValue) _maxContinuationMomentumBps = maxContinuationMomentumBps.Value;
        if (minMomentumDecayPct.HasValue) _minMomentumDecayPct = minMomentumDecayPct.Value;
        if (minImbalanceRecovery.HasValue) _minImbalanceRecovery = minImbalanceRecovery.Value;
        if (minMicropriceRecoveryBps.HasValue) _minMicropriceRecoveryBps = minMicropriceRecoveryBps.Value;
        if (reclaimLookbackQuotes.HasValue) _reclaimLookbackQuotes = Math.Max(2, reclaimLookbackQuotes.Value);
        if (enableEarlyReclaim.HasValue) _enableEarlyReclaim = enableEarlyReclaim.Value;
        if (enableConfirmedReclaim.HasValue) _enableConfirmedReclaim = enableConfirmedReclaim.Value;
        if (minReclaimMomentumBps.HasValue) _minReclaimMomentumBps = minReclaimMomentumBps.Value;
        if (microTakeProfitBps.HasValue) _microTakeProfitBps = microTakeProfitBps.Value;
        if (profitTargetBps.HasValue) _profitTargetBps = profitTargetBps.Value;
        if (stopLossBps.HasValue) _stopLossBps = stopLossBps.Value;
        if (maxTimeToFirstPositiveMfe.HasValue) _maxTimeToFirstPositiveMfe = maxTimeToFirstPositiveMfe.Value;
        if (mfeProtectMinBps.HasValue) _mfeProtectMinBps = mfeProtectMinBps.Value;
        if (mfeGivebackBps.HasValue) _mfeGivebackBps = mfeGivebackBps.Value;
        if (maxStall.HasValue) _maxStall = maxStall.Value;
        if (expectedReversionTime.HasValue) _expectedReversionTime = expectedReversionTime.Value;
        if (maxHold.HasValue) _maxHold = maxHold.Value;
        if (cooldown.HasValue) _cooldown = cooldown.Value;
        if (oneShotPerMove.HasValue) _oneShotPerMove = oneShotPerMove.Value;
    }

    public void OnQuote(MarketQuote q)
    {
        ProcessQuote(q, allowSignals: true);
    }

    public void ObserveQuote(MarketQuote q)
    {
        ProcessQuote(q, allowSignals: false);
    }

    private void ProcessQuote(MarketQuote q, bool allowSignals)
    {
        if (q?.Symbol is null || !q.Bid.HasValue || !q.Ask.HasValue)
        {
            return;
        }

        var symbol = q.Symbol.Ticker;
        var now = q.TimestampUtc == default ? DateTime.UtcNow : q.TimestampUtc;
        var st = GetOrCreateState(symbol);
        st.LastQuoteSymbol = q.Symbol;
        st.LastQuoteLast = q.Last;
        var mid = (q.Bid.Value + q.Ask.Value) / 2m;
        if (mid <= 0m)
        {
            return;
        }

        var spreadBps = ((q.Ask.Value - q.Bid.Value) / mid) * 10000m;
        var deltaMidBps = 0m;
        if (st.PreviousMid.HasValue && st.PreviousMid.Value > 0m)
        {
            deltaMidBps = ((mid - st.PreviousMid.Value) / st.PreviousMid.Value) * 10000m;
            st.RecentVolatilityBps = Ema(st.RecentVolatilityBps, Math.Abs(deltaMidBps), _recentVolatilityQuotes);
        }

        st.FairValueMid = Ema(st.FairValueMid, mid, _fairValueEmaQuotes);
        st.PreviousMid = mid;
        st.RecentQuotes.Add(new QuoteSample(now, mid, q.Bid.Value, q.Ask.Value, q.Last, spreadBps, deltaMidBps));
        TrimRecentQuotes(st.RecentQuotes, Math.Max(Math.Max(_fairValueEmaQuotes, _recentVolatilityQuotes), _reclaimLookbackQuotes) + 12);

        if (st.Phase == Phase.InPosition && st.EntryPrice.HasValue && st.EntryTimeUtc.HasValue)
        {
            if (allowSignals)
            {
                EvaluateExit(symbol, q, st, now, mid, spreadBps);
            }
            return;
        }

        if (allowSignals)
        {
            EvaluateEntry(symbol, q, st, now, mid, spreadBps);
        }
    }

    public void OnOrderBook(OrderBookUpdate ob)
    {
        if (ob?.Symbol is null || ob.Bids.Count == 0 || ob.Asks.Count == 0)
        {
            return;
        }

        var st = GetOrCreateState(ob.Symbol.PublicSymbol);
        st.LastOrderBook = ob;

        if (st.Phase != Phase.InPosition || !st.EntryPrice.HasValue || !st.EntryTimeUtc.HasValue)
        {
            return;
        }

        var bestBid = ob.Bids[0].Price;
        var bestAsk = ob.Asks[0].Price;
        var mid = (bestBid + bestAsk) / 2m;
        if (mid <= 0m)
        {
            return;
        }

        var spreadBps = ((bestAsk - bestBid) / mid) * 10000m;
        var symbol = st.LastQuoteSymbol ?? new Symbol(
            Ticker: ob.Symbol.PublicSymbol,
            Currency: ob.Symbol.QuoteAsset,
            Exchange: ob.Symbol.ExchangeId.ToString());
        var syntheticQuote = new MarketQuote(
            Symbol: symbol,
            Bid: bestBid,
            Ask: bestAsk,
            Last: st.LastQuoteLast ?? mid,
            BidSize: ob.Bids[0].Quantity,
            AskSize: ob.Asks[0].Quantity,
            TimestampUtc: ob.TimestampUtc);

        EvaluateExit(ob.Symbol.PublicSymbol, syntheticQuote, st, ob.TimestampUtc, mid, spreadBps);
    }

    public bool IsManagingSymbol(string symbol)
    {
        return !string.IsNullOrWhiteSpace(symbol) &&
            _state.TryGetValue(symbol, out var st) &&
            st.Phase == Phase.InPosition;
    }

    private void EvaluateEntry(string symbol, MarketQuote q, SymbolState st, DateTime now, decimal mid, decimal spreadBps)
    {
        if (st.Phase == Phase.Cooldown)
        {
            if (st.CooldownUntilUtc.HasValue && now < st.CooldownUntilUtc.Value)
            {
                LogSnapshot(symbol, st, now, "cooldown", mid, null, null, spreadBps, null, null, null, null, null, null, null, null, null, null);
                LogBlocked(symbol, st, now, $"cooldown={(st.CooldownUntilUtc.Value - now).TotalSeconds:F1}s");
                return;
            }

            st.Phase = Phase.Idle;
        }

        if (!st.FairValueMid.HasValue || !st.RecentVolatilityBps.HasValue)
        {
            LogSnapshot(symbol, st, now, "warmup", mid, null, null, spreadBps, null, null, null, null, null, null, null, null, null, null);
            LogBlocked(symbol, st, now, "warmup");
            return;
        }

        if (!TryGetBookMetrics(st, now, out var book))
        {
            LogSnapshot(symbol, st, now, "missing-book", mid, null, null, spreadBps, null, null, null, null, null, null, null, null, null, null);
            LogBlocked(symbol, st, now, "missing-orderbook");
            ResetMoveIfResolved(st, 0m, 0m);
            return;
        }

        if (book.BookAge > _maxBookAge)
        {
            LogSnapshot(symbol, st, now, "stale-book", mid, null, null, spreadBps, book.MinLiquidityUsd, book.Imbalance, book.MicropriceEdgeBps, book.BookAge.TotalMilliseconds, null, null, null, null, null, null);
            LogBlocked(symbol, st, now, $"stale-book={book.BookAge.TotalMilliseconds:F0}ms>{_maxBookAge.TotalMilliseconds:F0}ms");
            return;
        }

        if (spreadBps > _maxSpreadBps)
        {
            LogSnapshot(symbol, st, now, "spread-block", mid, null, null, spreadBps, book.MinLiquidityUsd, book.Imbalance, book.MicropriceEdgeBps, book.BookAge.TotalMilliseconds, null, null, null, null, null, null);
            LogBlocked(symbol, st, now, $"spread={spreadBps:F2}>{_maxSpreadBps:F2}bps");
            ResetMoveIfResolved(st, 0m, 0m);
            return;
        }

        if (book.MinLiquidityUsd < _minLiquidityUsd)
        {
            LogSnapshot(symbol, st, now, "liq-block", mid, null, null, spreadBps, book.MinLiquidityUsd, book.Imbalance, book.MicropriceEdgeBps, book.BookAge.TotalMilliseconds, null, null, null, null, null, null);
            LogBlocked(symbol, st, now, $"liq={book.MinLiquidityUsd:F0}<{_minLiquidityUsd:F0}USD");
            ResetMoveIfResolved(st, 0m, 0m);
            return;
        }

        var effectiveVolatilityBps = Clamp(st.RecentVolatilityBps.Value, _minEffectiveVolatilityBps, _maxEffectiveVolatilityBps);
        var dislocationBps = st.FairValueMid.Value > 0m
            ? ((st.FairValueMid.Value - mid) / st.FairValueMid.Value) * 10000m
            : 0m;
        var normalizedDislocation = effectiveVolatilityBps > 0m
            ? dislocationBps / effectiveVolatilityBps
            : 0m;

        ResetMoveIfResolved(st, dislocationBps, normalizedDislocation);

        if (dislocationBps <= 0m || normalizedDislocation < _minNormalizedDislocation || normalizedDislocation > _maxNormalizedDislocation)
        {
            LogSnapshot(symbol, st, now, "dislocation-block", mid, dislocationBps, normalizedDislocation, spreadBps, book.MinLiquidityUsd, book.Imbalance, book.MicropriceEdgeBps, book.BookAge.TotalMilliseconds, effectiveVolatilityBps, null, null, null, null, null);
            LogBlocked(symbol, st, now, $"normDislocation={normalizedDislocation:F2} outside [{_minNormalizedDislocation:F2},{_maxNormalizedDislocation:F2}]");
            return;
        }

        if (st.Phase == Phase.Idle)
        {
            StartNewMove(st, now, mid, normalizedDislocation, book.Imbalance, book.MicropriceEdgeBps);
        }

        var previousMicropriceEdgeBps = st.LastSeenMicropriceEdgeBps;
        st.LowestMidInMove = !st.LowestMidInMove.HasValue ? mid : Math.Min(st.LowestMidInMove.Value, mid);
        st.WorstImbalance = !st.WorstImbalance.HasValue ? book.Imbalance : Math.Min(st.WorstImbalance.Value, book.Imbalance);
        st.WorstMicropriceEdgeBps = !st.WorstMicropriceEdgeBps.HasValue ? book.MicropriceEdgeBps : Math.Min(st.WorstMicropriceEdgeBps.Value, book.MicropriceEdgeBps);
        st.LastSeenImbalance = book.Imbalance;
        st.LastSeenMicropriceEdgeBps = book.MicropriceEdgeBps;

        var momentumDecay = HasMomentumDecay(st, out var reclaimMomentumBps);
        var imbalanceRecovery = st.WorstImbalance.HasValue && (book.Imbalance - st.WorstImbalance.Value) >= _minImbalanceRecovery;
        var micropriceRecovery = st.WorstMicropriceEdgeBps.HasValue && (book.MicropriceEdgeBps - st.WorstMicropriceEdgeBps.Value) >= _minMicropriceRecoveryBps;
        var exhaustionCount = (momentumDecay ? 1 : 0) + (imbalanceRecovery ? 1 : 0) + (micropriceRecovery ? 1 : 0);

        if (exhaustionCount >= 2 && st.Phase == Phase.Dislocated)
        {
            st.Phase = Phase.Armed;
            st.ArmedSinceUtc = now;
            LogSetup(symbol, st, now, "ARMED", dislocationBps, normalizedDislocation, spreadBps, book.MinLiquidityUsd, book.Imbalance, book.MicropriceEdgeBps, effectiveVolatilityBps, reclaimMomentumBps);
        }
        else if (exhaustionCount < 2 && st.Phase == Phase.Armed)
        {
            st.Phase = Phase.Dislocated;
            st.ArmedSinceUtc = null;
            LogSetup(symbol, st, now, "CLEARED", dislocationBps, normalizedDislocation, spreadBps, book.MinLiquidityUsd, book.Imbalance, book.MicropriceEdgeBps, effectiveVolatilityBps, reclaimMomentumBps, $"exhaustion={exhaustionCount}/3");
        }

        var earlyReclaim = _enableEarlyReclaim && IsEarlyReclaim(st, book.MicropriceEdgeBps, previousMicropriceEdgeBps);
        var confirmedReclaim = _enableConfirmedReclaim && IsConfirmedReclaim(st, mid, book.MicropriceEdgeBps, reclaimMomentumBps);

        if (earlyReclaim)
        {
            st.LastObservedEarlyReclaimUtc = now;
        }

        if (confirmedReclaim)
        {
            st.LastObservedConfirmedReclaimUtc = now;
        }

        if (_oneShotPerMove && st.ShotTakenForCurrentMove)
        {
            LogSnapshot(symbol, st, now, "one-shot-block", mid, dislocationBps, normalizedDislocation, spreadBps, book.MinLiquidityUsd, book.Imbalance, book.MicropriceEdgeBps, book.BookAge.TotalMilliseconds, effectiveVolatilityBps, exhaustionCount, reclaimMomentumBps, earlyReclaim, confirmedReclaim, null);
            LogBlocked(symbol, st, now, "one-shot-per-move");
            return;
        }

        if (st.Phase != Phase.Armed)
        {
            LogSnapshot(symbol, st, now, "pre-armed", mid, dislocationBps, normalizedDislocation, spreadBps, book.MinLiquidityUsd, book.Imbalance, book.MicropriceEdgeBps, book.BookAge.TotalMilliseconds, effectiveVolatilityBps, exhaustionCount, reclaimMomentumBps, earlyReclaim, confirmedReclaim, null);
            LogBlocked(symbol, st, now, $"exhaustion={exhaustionCount}/3");
            return;
        }

        if (!confirmedReclaim)
        {
            LogSnapshot(symbol, st, now, "reclaim-block", mid, dislocationBps, normalizedDislocation, spreadBps, book.MinLiquidityUsd, book.Imbalance, book.MicropriceEdgeBps, book.BookAge.TotalMilliseconds, effectiveVolatilityBps, exhaustionCount, reclaimMomentumBps, earlyReclaim, false, null);
            LogBlocked(symbol, st, now, "confirmed-reclaim-missing");
            return;
        }

        var entryPrice = q.Ask!.Value;
        st.Phase = Phase.InPosition;
        st.EntryPrice = entryPrice;
        st.EntryTimeUtc = now;
        st.EntryDislocationBps = dislocationBps;
        st.EntryNormalizedDislocation = normalizedDislocation;
        st.EntryEffectiveVolatilityBps = effectiveVolatilityBps;
        st.EntryImbalance = book.Imbalance;
        st.EntryMicropriceEdgeBps = book.MicropriceEdgeBps;
        st.EntryFairValueMid = st.FairValueMid;
        st.EntryReclaimMode = "confirmed";
        st.MaxRealizedBps = ((q.Bid!.Value - entryPrice) / entryPrice) * 10000m;
        st.MinRealizedBps = st.MaxRealizedBps;
        st.BestExitPrice = q.Bid.Value;
        st.WorstExitPrice = q.Bid.Value;
        st.LastProgressUtc = now;
        st.MaxMfeGivebackBps = 0m;
        st.ShotTakenForCurrentMove = true;

        LogEntryDetail(symbol, st, now, entryPrice, mid, dislocationBps, normalizedDislocation, spreadBps, book.MinLiquidityUsd, book.Imbalance, book.MicropriceEdgeBps, effectiveVolatilityBps, exhaustionCount, reclaimMomentumBps, earlyReclaim);

        TradeSignalGenerated?.Invoke(new TradeSignal(
            Symbol: q.Symbol,
            ShouldEnter: true,
            Side: OrderSide.Buy,
            SuggestedLimitPrice: entryPrice,
            Reason: $"mr-entry: normDislocation={normalizedDislocation:F2} dislocation={dislocationBps:F2}bps reclaim=confirmed",
            TimestampUtc: now));
    }

    private void EvaluateExit(string symbol, MarketQuote q, SymbolState st, DateTime now, decimal mid, decimal spreadBps)
    {
        if (!st.EntryPrice.HasValue || !st.EntryTimeUtc.HasValue || !q.Bid.HasValue)
        {
            return;
        }

        var currentExitPrice = q.Bid.Value;
        var realizedBps = ((currentExitPrice - st.EntryPrice.Value) / st.EntryPrice.Value) * 10000m;
        var hold = now - st.EntryTimeUtc.Value;

        st.MaxRealizedBps = Math.Max(st.MaxRealizedBps, realizedBps);
        st.MinRealizedBps = Math.Min(st.MinRealizedBps, realizedBps);
        st.BestExitPrice = !st.BestExitPrice.HasValue ? currentExitPrice : Math.Max(st.BestExitPrice.Value, currentExitPrice);
        st.WorstExitPrice = !st.WorstExitPrice.HasValue ? currentExitPrice : Math.Min(st.WorstExitPrice.Value, currentExitPrice);

        if (realizedBps > 0m && !st.FirstPositiveMfeUtc.HasValue)
        {
            st.FirstPositiveMfeUtc = now;
        }

        if (realizedBps >= st.MaxRealizedBps - 0.01m)
        {
            st.LastProgressUtc = now;
        }

        var givebackBps = Math.Max(0m, st.MaxRealizedBps - realizedBps);
        st.MaxMfeGivebackBps = Math.Max(st.MaxMfeGivebackBps, givebackBps);
        var fairValueMid = st.FairValueMid ?? st.EntryFairValueMid;
        var timeToFirstPositiveMfe = st.FirstPositiveMfeUtc.HasValue
            ? st.FirstPositiveMfeUtc.Value - st.EntryTimeUtc.Value
            : (TimeSpan?)null;

        if (realizedBps <= -_stopLossBps)
        {
            ExitPosition(symbol, q, st, now, "mr-stop", realizedBps, hold, timeToFirstPositiveMfe);
            return;
        }

        if (realizedBps >= _profitTargetBps || (fairValueMid.HasValue && mid >= fairValueMid.Value && realizedBps > 0m))
        {
            ExitPosition(symbol, q, st, now, "mr-reversion-hit", realizedBps, hold, timeToFirstPositiveMfe);
            return;
        }

        if (realizedBps >= _microTakeProfitBps)
        {
            ExitPosition(symbol, q, st, now, "mr-micro-tp", realizedBps, hold, timeToFirstPositiveMfe);
            return;
        }

        if (!st.FirstPositiveMfeUtc.HasValue && hold >= _maxTimeToFirstPositiveMfe)
        {
            ExitPosition(symbol, q, st, now, "mr-time-to-first-mfe-fail", realizedBps, hold, timeToFirstPositiveMfe);
            return;
        }

        if (st.MaxRealizedBps >= _mfeProtectMinBps && givebackBps >= _mfeGivebackBps)
        {
            ExitPosition(symbol, q, st, now, "mr-mfe-protect", realizedBps, hold, timeToFirstPositiveMfe);
            return;
        }

        if (hold >= _maxHold)
        {
            ExitPosition(symbol, q, st, now, "mr-max-hold", realizedBps, hold, timeToFirstPositiveMfe);
            return;
        }

        if (hold >= _expectedReversionTime && st.MaxRealizedBps <= 0m)
        {
            ExitPosition(symbol, q, st, now, "mr-time", realizedBps, hold, timeToFirstPositiveMfe);
            return;
        }

        if (st.LastProgressUtc.HasValue && now - st.LastProgressUtc.Value >= _maxStall)
        {
            ExitPosition(symbol, q, st, now, "mr-stall", realizedBps, hold, timeToFirstPositiveMfe);
            return;
        }

        if (!TryGetBookMetrics(st, now, out var book))
        {
            return;
        }

        var reclaimMomentumBps = GetLatestDeltaMidBps(st);
        var edgeLost =
            spreadBps > (_maxSpreadBps + 1.5m) ||
            book.BookAge > _maxBookAge ||
            book.MinLiquidityUsd < _minLiquidityUsd ||
            (book.Imbalance < 0m && book.MicropriceEdgeBps < 0m && reclaimMomentumBps <= 0m);

        if (edgeLost)
        {
            ExitPosition(symbol, q, st, now, "mr-edge-lost", realizedBps, hold, timeToFirstPositiveMfe);
        }
    }

    private void ExitPosition(
        string symbol,
        MarketQuote q,
        SymbolState st,
        DateTime now,
        string reason,
        decimal realizedBps,
        TimeSpan hold,
        TimeSpan? timeToFirstPositiveMfe)
    {
        if (!q.Bid.HasValue)
        {
            return;
        }

        var exitPrice = q.Bid.Value;
        LogResult(symbol, st, exitPrice, realizedBps, hold, timeToFirstPositiveMfe, reason);

        st.Phase = Phase.Cooldown;
        st.CooldownUntilUtc = now + _cooldown;
        st.EntryPrice = null;
        st.EntryTimeUtc = null;
        st.EntryDislocationBps = null;
        st.EntryNormalizedDislocation = null;
        st.EntryEffectiveVolatilityBps = null;
        st.EntryImbalance = null;
        st.EntryMicropriceEdgeBps = null;
        st.EntryFairValueMid = null;
        st.EntryReclaimMode = null;
        st.MaxRealizedBps = 0m;
        st.MinRealizedBps = 0m;
        st.BestExitPrice = null;
        st.WorstExitPrice = null;
        st.FirstPositiveMfeUtc = null;
        st.LastProgressUtc = null;
        st.MaxMfeGivebackBps = 0m;

        TradeSignalGenerated?.Invoke(new TradeSignal(
            Symbol: q.Symbol,
            ShouldEnter: false,
            Side: OrderSide.Sell,
            SuggestedLimitPrice: exitPrice,
            Reason: reason,
            TimestampUtc: now));
    }

    private bool TryGetBookMetrics(SymbolState st, DateTime now, out BookMetrics metrics)
    {
        metrics = default;
        if (st.LastOrderBook == null || st.LastOrderBook.Bids.Count == 0 || st.LastOrderBook.Asks.Count == 0)
        {
            return false;
        }

        var ob = st.LastOrderBook;
        var bestBid = ob.Bids[0].Price;
        var bestAsk = ob.Asks[0].Price;
        var mid = (bestBid + bestAsk) / 2m;
        if (mid <= 0m)
        {
            return false;
        }

        var bidLiquidity = ob.Bids.Take(3).Sum(x => x.Price * x.Quantity);
        var askLiquidity = ob.Asks.Take(3).Sum(x => x.Price * x.Quantity);
        var topBidQty = ob.Bids.Take(3).Sum(x => x.Quantity);
        var topAskQty = ob.Asks.Take(3).Sum(x => x.Quantity);
        var totalQty = topBidQty + topAskQty;
        var imbalance = totalQty > 0m ? (topBidQty - topAskQty) / totalQty : 0m;
        var microprice = totalQty > 0m
            ? ((bestAsk * topBidQty) + (bestBid * topAskQty)) / totalQty
            : mid;
        var micropriceEdgeBps = ((microprice - mid) / mid) * 10000m;

        metrics = new BookMetrics(
            Math.Min(bidLiquidity, askLiquidity),
            imbalance,
            micropriceEdgeBps,
            now - ob.TimestampUtc);
        return true;
    }

    private bool HasMomentumDecay(SymbolState st, out decimal reclaimMomentumBps)
    {
        reclaimMomentumBps = 0m;
        if (st.RecentQuotes.Count < 5)
        {
            return false;
        }

        var count = st.RecentQuotes.Count;
        var older = new[] { st.RecentQuotes[count - 4].DeltaMidBps, st.RecentQuotes[count - 3].DeltaMidBps };
        var newer = new[] { st.RecentQuotes[count - 2].DeltaMidBps, st.RecentQuotes[count - 1].DeltaMidBps };
        var olderDown = AverageNegativeMagnitude(older);
        var newerDown = AverageNegativeMagnitude(newer);
        reclaimMomentumBps = newer.Average();

        if (olderDown <= 0m || newerDown > _maxContinuationMomentumBps)
        {
            return false;
        }

        return (olderDown - newerDown) >= (olderDown * _minMomentumDecayPct);
    }

    private bool IsEarlyReclaim(SymbolState st, decimal currentMicropriceEdgeBps, decimal? previousMicropriceEdgeBps)
    {
        if (st.RecentQuotes.Count < 2)
        {
            return false;
        }

        var current = st.RecentQuotes[^1];
        var previous = st.RecentQuotes[^2];
        return current.Mid > previous.Mid ||
            currentMicropriceEdgeBps > (previousMicropriceEdgeBps ?? currentMicropriceEdgeBps);
    }

    private bool IsConfirmedReclaim(SymbolState st, decimal currentMid, decimal currentMicropriceEdgeBps, decimal reclaimMomentumBps)
    {
        if (st.RecentQuotes.Count < (_reclaimLookbackQuotes + 1))
        {
            return false;
        }

        var startIndex = Math.Max(0, st.RecentQuotes.Count - (_reclaimLookbackQuotes + 1));
        decimal? highestPreviousMid = null;
        for (var i = startIndex; i < st.RecentQuotes.Count - 1; i++)
        {
            highestPreviousMid = !highestPreviousMid.HasValue
                ? st.RecentQuotes[i].Mid
                : Math.Max(highestPreviousMid.Value, st.RecentQuotes[i].Mid);
        }

        var midBreaksLocalHigh = highestPreviousMid.HasValue && currentMid > highestPreviousMid.Value;
        var microFlip = currentMicropriceEdgeBps >= 0m && reclaimMomentumBps >= _minReclaimMomentumBps;
        return midBreaksLocalHigh || microFlip;
    }

    private void StartNewMove(SymbolState st, DateTime now, decimal mid, decimal normalizedDislocation, decimal imbalance, decimal micropriceEdgeBps)
    {
        st.Phase = Phase.Dislocated;
        st.DislocationStartUtc = now;
        st.LowestMidInMove = mid;
        st.LowestNormalizedDislocation = normalizedDislocation;
        st.WorstImbalance = imbalance;
        st.WorstMicropriceEdgeBps = micropriceEdgeBps;
        st.ArmedSinceUtc = null;
        st.LastObservedEarlyReclaimUtc = null;
        st.LastObservedConfirmedReclaimUtc = null;
        st.ShotTakenForCurrentMove = false;
    }

    private void ResetMoveIfResolved(SymbolState st, decimal dislocationBps, decimal normalizedDislocation)
    {
        var moveResolved = dislocationBps <= 0m || normalizedDislocation < (_minNormalizedDislocation * 0.5m);
        if (!moveResolved || st.Phase == Phase.InPosition)
        {
            return;
        }

        if (st.Phase != Phase.Cooldown)
        {
            st.Phase = Phase.Idle;
        }

        st.DislocationStartUtc = null;
        st.ArmedSinceUtc = null;
        st.LowestMidInMove = null;
        st.LowestNormalizedDislocation = null;
        st.WorstImbalance = null;
        st.WorstMicropriceEdgeBps = null;
        st.LastObservedEarlyReclaimUtc = null;
        st.LastObservedConfirmedReclaimUtc = null;
        st.LastSeenImbalance = null;
        st.LastSeenMicropriceEdgeBps = null;
        st.ShotTakenForCurrentMove = false;
    }

    private void LogBlocked(string symbol, SymbolState st, DateTime now, string reason)
    {
        if (st.LastBlockedLogUtc.HasValue && (now - st.LastBlockedLogUtc.Value).TotalSeconds < 30)
        {
            return;
        }

        _log.Information("[MR-BLOCKED] {Sym} {Reason}", symbol, reason);
        st.LastBlockedLogUtc = now;
    }

    private void LogSnapshot(
        string symbol,
        SymbolState st,
        DateTime now,
        string stage,
        decimal mid,
        decimal? dislocationBps,
        decimal? normalizedDislocation,
        decimal? spreadBps,
        decimal? liquidityUsd,
        decimal? imbalance,
        decimal? micropriceEdgeBps,
        double? bookAgeMs,
        decimal? effectiveVolatilityBps,
        int? exhaustionCount,
        decimal? reclaimMomentumBps,
        bool? earlyReclaim,
        bool? confirmedReclaim,
        bool? executed)
    {
        if (st.LastSnapshotLogUtc.HasValue && (now - st.LastSnapshotLogUtc.Value).TotalSeconds < 30)
        {
            return;
        }

        _log.Information(
            "[MR-SNAPSHOT] {Sym} stage={Stage} phase={Phase} mid={Mid:F2} dislocation={Dislocation} norm={Norm} spread={Spread} liq={Liq} imbalance={Imb} microEdge={Micro} effVol={EffVol} reclaimMomentum={ReclaimMomentum} exhaustion={Exhaustion} early={Early} confirmed={Confirmed} executed={Executed} bookAgeMs={BookAge}",
            symbol,
            stage,
            st.Phase,
            mid,
            dislocationBps?.ToString("F2") ?? "n/a",
            normalizedDislocation?.ToString("F2") ?? "n/a",
            spreadBps?.ToString("F2") ?? "n/a",
            liquidityUsd?.ToString("F0") ?? "n/a",
            imbalance?.ToString("F3") ?? "n/a",
            micropriceEdgeBps?.ToString("F2") ?? "n/a",
            effectiveVolatilityBps?.ToString("F2") ?? "n/a",
            reclaimMomentumBps?.ToString("F2") ?? "n/a",
            exhaustionCount?.ToString() ?? "n/a",
            earlyReclaim.HasValue ? (earlyReclaim.Value ? "yes" : "no") : "n/a",
            confirmedReclaim.HasValue ? (confirmedReclaim.Value ? "yes" : "no") : "n/a",
            executed.HasValue ? (executed.Value ? "yes" : "no") : "n/a",
            bookAgeMs?.ToString("F0") ?? "n/a");

        st.LastSnapshotLogUtc = now;
    }

    private void LogSetup(
        string symbol,
        SymbolState st,
        DateTime now,
        string action,
        decimal dislocationBps,
        decimal normalizedDislocation,
        decimal spreadBps,
        decimal liquidityUsd,
        decimal imbalance,
        decimal micropriceEdgeBps,
        decimal effectiveVolatilityBps,
        decimal reclaimMomentumBps,
        string? reason = null)
    {
        _log.Information(
            "[MR-SETUP] {Sym} {Action} phase={Phase} dislocation={Dislocation:F2}bps norm={Norm:F2} spread={Spread:F2}bps liq={Liq:F0}USD imbalance={Imb:F3} microEdge={Micro:F2}bps effVol={EffVol:F2} reclaimMomentum={ReclaimMomentum:F2} reason={Reason}",
            symbol,
            action,
            st.Phase,
            dislocationBps,
            normalizedDislocation,
            spreadBps,
            liquidityUsd,
            imbalance,
            micropriceEdgeBps,
            effectiveVolatilityBps,
            reclaimMomentumBps,
            reason ?? "n/a");
    }

    private void LogEntryDetail(
        string symbol,
        SymbolState st,
        DateTime now,
        decimal entryPrice,
        decimal mid,
        decimal dislocationBps,
        decimal normalizedDislocation,
        decimal spreadBps,
        decimal liquidityUsd,
        decimal imbalance,
        decimal micropriceEdgeBps,
        decimal effectiveVolatilityBps,
        int exhaustionCount,
        decimal reclaimMomentumBps,
        bool earlyObservedOnly)
    {
        _log.Information(
            "[MR-ENTRY-DETAIL] {Sym} BUY @ {Px:F2} phase={Phase} mid={Mid:F2} dislocation={Dislocation:F2}bps norm={Norm:F2} effVol={EffVol:F2} spread={Spread:F2}bps liq={Liq:F0}USD imbalance={Imb:F3} microEdge={Micro:F2}bps exhaustion={Exhaustion}/3 reclaimMomentum={ReclaimMomentum:F2} reclaimMode={ReclaimMode} earlyObservedOnly={EarlyOnly}",
            symbol,
            entryPrice,
            st.Phase,
            mid,
            dislocationBps,
            normalizedDislocation,
            effectiveVolatilityBps,
            spreadBps,
            liquidityUsd,
            imbalance,
            micropriceEdgeBps,
            exhaustionCount,
            reclaimMomentumBps,
            st.EntryReclaimMode ?? "confirmed",
            earlyObservedOnly);
    }

    private void LogResult(
        string symbol,
        SymbolState st,
        decimal exitPrice,
        decimal realizedBps,
        TimeSpan hold,
        TimeSpan? timeToFirstPositiveMfe,
        string reason)
    {
        _log.Information(
            "[MR-RESULT] {Sym} exit={Reason} px={Px:F2} realized={Realized:F2}bps hold={Hold:F1}s mfe={Mfe:F2}bps mae={Mae:F2}bps timeToFirstMfe={TimeToFirstMfe} giveback={Giveback:F2}bps entryNorm={EntryNorm:F2} entryDislocation={EntryDislocation:F2}bps reclaimMode={ReclaimMode}",
            symbol,
            reason,
            exitPrice,
            realizedBps,
            hold.TotalSeconds,
            st.MaxRealizedBps,
            st.MinRealizedBps,
            timeToFirstPositiveMfe.HasValue ? $"{timeToFirstPositiveMfe.Value.TotalSeconds:F1}s" : "n/a",
            st.MaxMfeGivebackBps,
            st.EntryNormalizedDislocation ?? 0m,
            st.EntryDislocationBps ?? 0m,
            st.EntryReclaimMode ?? "n/a");
    }

    private SymbolState GetOrCreateState(string symbol)
    {
        if (!_state.TryGetValue(symbol, out var st))
        {
            st = new SymbolState();
            _state[symbol] = st;
        }

        return st;
    }

    private static decimal Clamp(decimal value, decimal min, decimal max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    private static decimal Ema(decimal? current, decimal next, int period)
    {
        if (!current.HasValue)
        {
            return next;
        }

        var alpha = 2m / (period + 1m);
        return current.Value + ((next - current.Value) * alpha);
    }

    private static void TrimRecentQuotes(List<QuoteSample> recentQuotes, int maxCount)
    {
        if (recentQuotes.Count <= maxCount)
        {
            return;
        }

        recentQuotes.RemoveRange(0, recentQuotes.Count - maxCount);
    }

    private static decimal AverageNegativeMagnitude(IEnumerable<decimal> values)
    {
        var negatives = values.Where(x => x < 0m).Select(Math.Abs).ToArray();
        return negatives.Length == 0 ? 0m : negatives.Average();
    }

    private static decimal GetLatestDeltaMidBps(SymbolState st)
    {
        return st.RecentQuotes.Count == 0 ? 0m : st.RecentQuotes[^1].DeltaMidBps;
    }

    private sealed class SymbolState
    {
        public List<QuoteSample> RecentQuotes { get; } = new();
        public Phase Phase { get; set; } = Phase.Idle;
        public OrderBookUpdate? LastOrderBook { get; set; }
        public decimal? PreviousMid { get; set; }
        public decimal? FairValueMid { get; set; }
        public decimal? RecentVolatilityBps { get; set; }
        public DateTime? LastBlockedLogUtc { get; set; }
        public DateTime? LastSnapshotLogUtc { get; set; }
        public DateTime? DislocationStartUtc { get; set; }
        public DateTime? ArmedSinceUtc { get; set; }
        public DateTime? CooldownUntilUtc { get; set; }
        public decimal? LowestMidInMove { get; set; }
        public decimal? LowestNormalizedDislocation { get; set; }
        public decimal? WorstImbalance { get; set; }
        public decimal? WorstMicropriceEdgeBps { get; set; }
        public decimal? LastSeenImbalance { get; set; }
        public decimal? LastSeenMicropriceEdgeBps { get; set; }
        public DateTime? LastObservedEarlyReclaimUtc { get; set; }
        public DateTime? LastObservedConfirmedReclaimUtc { get; set; }
        public bool ShotTakenForCurrentMove { get; set; }
        public decimal? EntryPrice { get; set; }
        public DateTime? EntryTimeUtc { get; set; }
        public decimal? EntryDislocationBps { get; set; }
        public decimal? EntryNormalizedDislocation { get; set; }
        public decimal? EntryEffectiveVolatilityBps { get; set; }
        public decimal? EntryImbalance { get; set; }
        public decimal? EntryMicropriceEdgeBps { get; set; }
        public decimal? EntryFairValueMid { get; set; }
        public string? EntryReclaimMode { get; set; }
        public decimal MaxRealizedBps { get; set; }
        public decimal MinRealizedBps { get; set; }
        public decimal? BestExitPrice { get; set; }
        public decimal? WorstExitPrice { get; set; }
        public DateTime? FirstPositiveMfeUtc { get; set; }
        public DateTime? LastProgressUtc { get; set; }
        public decimal MaxMfeGivebackBps { get; set; }
        public Symbol? LastQuoteSymbol { get; set; }
        public decimal? LastQuoteLast { get; set; }
    }

    private enum Phase
    {
        Idle,
        Dislocated,
        Armed,
        InPosition,
        Cooldown
    }

    private readonly record struct QuoteSample(
        DateTime TimestampUtc,
        decimal Mid,
        decimal Bid,
        decimal Ask,
        decimal? Last,
        decimal SpreadBps,
        decimal DeltaMidBps);

    private readonly record struct BookMetrics(
        decimal MinLiquidityUsd,
        decimal Imbalance,
        decimal MicropriceEdgeBps,
        TimeSpan BookAge);
}
