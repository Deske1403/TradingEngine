#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Denis.TradingEngine.Core.Crypto;
using Denis.TradingEngine.Core.Interfaces;
using Denis.TradingEngine.Core.Trading;
using Denis.TradingEngine.Strategy.Signals;
using Serilog;

namespace Denis.TradingEngine.Strategy.Scalp;

/// <summary>
/// Scalp strategija - koristi orderbooks za brze, kratkoročne trade-ove.
/// Entry: kada je spread mali, likvidnost dobra, i ima momentum
/// Exit: brzi profit (0.1-0.5%) ili stop loss (0.1-0.2%)
/// </summary>
public sealed class ScalpStrategy : ITradingStrategy
{
    public event Action<TradeSignal>? TradeSignalGenerated;
    
    private readonly ILogger _log = Log.ForContext<ScalpStrategy>();
    
    // Config parametri
    private readonly decimal _maxSpreadBps = 10.0m; // Max spread za scalp (10 bps)
    private readonly decimal _minLiquidityUsd = 1000m; // Min likvidnost u orderbook-u (USD)
    private readonly decimal _profitTargetPct = 0.002m; // 0.2% profit target
    private readonly decimal _stopLossPct = 0.001m; // 0.1% stop loss
    private readonly int _minTicksForEntry = 5; // Min tick-ova za entry
    private readonly TimeSpan _maxHoldTime = TimeSpan.FromMinutes(5); // Max vreme držanja
    private readonly decimal _minImbalanceRatio = 0.05m; // Bid pressure threshold
    private readonly decimal _minMomentumBps = 0.5m; // Minimalni momentum pre entry-ja
    private readonly decimal _minMicropriceEdgeBps = 0.2m; // Prednost microprice nad mid cenom
    private readonly TimeSpan _maxBookAge = TimeSpan.FromMilliseconds(1500); // Orderbook ne sme biti stale
    private readonly int _momentumLookbackQuotes = 3; // Momentum preko vise quote-ova, ne jednog tick-a
    private readonly TimeSpan _momentumLookbackWindow = TimeSpan.FromSeconds(45);
    private readonly int _entryConfirmationQuotes = 2; // Setup mora da prezivi vise quote evaluacija
    private readonly TimeSpan _minSignalPersistence = TimeSpan.FromSeconds(15);
    private readonly decimal _exitMaxSpreadBps = 12.0m;
    private readonly decimal _exitMinImbalanceRatio = 0.10m;
    private readonly decimal _exitMinMicropriceEdgeBps = 0.05m;
    private readonly decimal _exitMinMomentumBps = 0.0m;
    private readonly int _edgeLossFailureThreshold = 2;
    
    // Per-symbol state
    private sealed class SymbolState
    {
        public Queue<DateTime> TickTimes { get; } = new();
        public Queue<(DateTime Utc, decimal SpreadBps)> RecentSpreads { get; } = new();
        public Queue<(DateTime Utc, decimal Price)> RecentReferencePrices { get; } = new();
        public OrderBookUpdate? LastOrderBook { get; set; }
        public decimal? LastObservedPrice { get; set; }
        public DateTime? LastBlockedLogUtc { get; set; }
        public DateTime? LastSnapshotLogUtc { get; set; }
        public decimal? EntryPrice { get; set; }
        public DateTime? EntryTime { get; set; }
        public decimal? BestPrice { get; set; } // Najbolja cena od entry-ja
        public DateTime? BestPriceTime { get; set; }
        public decimal? WorstPrice { get; set; }
        public DateTime? WorstPriceTime { get; set; }
        public string? EntryReason { get; set; }
        public SignalSnapshot? ArmedSnapshot { get; set; }
        public SignalSnapshot? EntrySnapshot { get; set; }
        public bool InPosition { get; set; }
        public DateTime? EntryReadySinceUtc { get; set; }
        public DateTime? LastReadyQuoteUtc { get; set; }
        public int ConsecutiveReadyQuotes { get; set; }
    }

    private readonly record struct BookMetrics(
        decimal BestBid,
        decimal BestAsk,
        decimal SpreadBps,
        decimal MinLiquidityUsd,
        decimal Imbalance,
        decimal MicropriceEdgeBps);

    private readonly record struct SignalSnapshot(
        decimal BestBid,
        decimal BestAsk,
        decimal SpreadBps,
        decimal AvgSpreadBps,
        decimal MinLiquidityUsd,
        decimal Imbalance,
        decimal MicropriceEdgeBps,
        decimal MomentumBps,
        double BookAgeMs,
        decimal ReferencePrice,
        int MomentumSamples,
        int TickCount);
    
    private readonly Dictionary<string, SymbolState> _state = new(StringComparer.OrdinalIgnoreCase);
    
    public ScalpStrategy(
        decimal? maxSpreadBps = null,
        decimal? minLiquidityUsd = null,
        decimal? profitTargetPct = null,
        decimal? stopLossPct = null,
        int? minTicksForEntry = null,
        TimeSpan? maxHoldTime = null,
        decimal? minImbalanceRatio = null,
        decimal? minMomentumBps = null,
        decimal? minMicropriceEdgeBps = null,
        TimeSpan? maxBookAge = null,
        int? momentumLookbackQuotes = null,
        TimeSpan? momentumLookbackWindow = null,
        int? entryConfirmationQuotes = null,
        TimeSpan? minSignalPersistence = null,
        decimal? exitMaxSpreadBps = null,
        decimal? exitMinImbalanceRatio = null,
        decimal? exitMinMicropriceEdgeBps = null,
        decimal? exitMinMomentumBps = null,
        int? edgeLossFailureThreshold = null)
    {
        if (maxSpreadBps.HasValue) _maxSpreadBps = maxSpreadBps.Value;
        if (minLiquidityUsd.HasValue) _minLiquidityUsd = minLiquidityUsd.Value;
        if (profitTargetPct.HasValue) _profitTargetPct = profitTargetPct.Value;
        if (stopLossPct.HasValue) _stopLossPct = stopLossPct.Value;
        if (minTicksForEntry.HasValue) _minTicksForEntry = minTicksForEntry.Value;
        if (maxHoldTime.HasValue) _maxHoldTime = maxHoldTime.Value;
        if (minImbalanceRatio.HasValue) _minImbalanceRatio = minImbalanceRatio.Value;
        if (minMomentumBps.HasValue) _minMomentumBps = minMomentumBps.Value;
        if (minMicropriceEdgeBps.HasValue) _minMicropriceEdgeBps = minMicropriceEdgeBps.Value;
        if (maxBookAge.HasValue) _maxBookAge = maxBookAge.Value;
        if (momentumLookbackQuotes.HasValue) _momentumLookbackQuotes = Math.Max(2, momentumLookbackQuotes.Value);
        if (momentumLookbackWindow.HasValue) _momentumLookbackWindow = momentumLookbackWindow.Value;
        if (entryConfirmationQuotes.HasValue) _entryConfirmationQuotes = Math.Max(1, entryConfirmationQuotes.Value);
        if (minSignalPersistence.HasValue) _minSignalPersistence = minSignalPersistence.Value;
        _exitMaxSpreadBps = exitMaxSpreadBps ?? (_maxSpreadBps + 2.0m);
        if (exitMinImbalanceRatio.HasValue) _exitMinImbalanceRatio = exitMinImbalanceRatio.Value;
        if (exitMinMicropriceEdgeBps.HasValue) _exitMinMicropriceEdgeBps = exitMinMicropriceEdgeBps.Value;
        if (exitMinMomentumBps.HasValue) _exitMinMomentumBps = exitMinMomentumBps.Value;
        if (edgeLossFailureThreshold.HasValue) _edgeLossFailureThreshold = Math.Max(1, edgeLossFailureThreshold.Value);
    }
    
    public void OnQuote(MarketQuote q)
    {
        if (q?.Symbol is null) return;
        
        var symbol = q.Symbol.Ticker;
        var now = q.TimestampUtc == default ? DateTime.UtcNow : q.TimestampUtc;
        
        if (!_state.TryGetValue(symbol, out var st))
        {
            _state[symbol] = st = new SymbolState();
        }
        
        try
        {
            // Exit logika (ako smo u poziciji)
            if (st.InPosition && st.EntryPrice.HasValue && st.EntryTime.HasValue)
            {
                EvaluateExit(symbol, q, st, now);
                return;
            }
            
            // Entry logika (ako nismo u poziciji). EvaluateEntry sam loguje i
            // razlikuje missing-orderbook od ostalih blokera, pa ne preskačemo
            // evaluaciju kada book još nije stigao.
            if (!st.InPosition)
            {
                EvaluateEntry(symbol, q, st, now);
            }
        }
        finally
        {
            var referencePrice = GetReferencePrice(q);
            if (referencePrice.HasValue)
            {
                st.LastObservedPrice = referencePrice.Value;
                st.RecentReferencePrices.Enqueue((now, referencePrice.Value));
                TrimReferencePrices(st, now);
            }
        }
    }
    
    /// <summary>
    /// Metoda za primanje orderbook update-a (poziva se iz orchestrator-a)
    /// </summary>
    public void OnOrderBook(OrderBookUpdate ob)
    {
        if (ob?.Symbol is null) return;
        
        var symbol = ob.Symbol.PublicSymbol;
        if (!_state.TryGetValue(symbol, out var st))
        {
            _state[symbol] = st = new SymbolState();
        }
        
        var hasUsableBook = ob.Bids.Count > 0 && ob.Asks.Count > 0;
        if (!hasUsableBook)
        {
            return;
        }

        st.TickTimes.Enqueue(ob.TimestampUtc);
        while (st.TickTimes.Count > 0 && (ob.TimestampUtc - st.TickTimes.Peek()).TotalSeconds > 60)
        {
            st.TickTimes.Dequeue();
        }

        // Ne pregazuj poslednji validni book praznim snapshot-om.
        // Stale-book guard u entry logici i dalje kontroliše koliko dugo je book upotrebljiv.
        st.LastOrderBook = ob;

        if (hasUsableBook)
        {
            var bestBid = ob.Bids[0].Price;
            var bestAsk = ob.Asks[0].Price;
            var midPrice = (bestBid + bestAsk) / 2m;
            if (midPrice > 0m)
            {
                var spreadBps = ((bestAsk - bestBid) / midPrice) * 10000m;
                st.RecentSpreads.Enqueue((ob.TimestampUtc, spreadBps));
                while (st.RecentSpreads.Count > 0 && (ob.TimestampUtc - st.RecentSpreads.Peek().Utc).TotalSeconds > 60)
                {
                    st.RecentSpreads.Dequeue();
                }
            }
        }
    }

    public bool IsManagingSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        return _state.TryGetValue(symbol, out var st) && st.InPosition;
    }
    
    private void EvaluateEntry(string symbol, MarketQuote q, SymbolState st, DateTime now)
    {
        if (st.LastOrderBook == null)
        {
            ResetEntryReadiness(symbol, st, now, "missing-orderbook");
            LogSnapshot(symbol, st, now, null, null, null, null, null, null, null, "pre-missing-orderbook-block");
            LogBlocked(symbol, st, now, "missing-orderbook");
            return;
        }

        var ob = st.LastOrderBook;
        var hasUsableBook = ob.Bids.Count > 0 && ob.Asks.Count > 0;
        var bookAgeMs = (now - ob.TimestampUtc).TotalMilliseconds;
        decimal? spreadBps = null;
        var avgSpreadBps = st.RecentSpreads.Count > 0 ? st.RecentSpreads.Average(x => x.SpreadBps) : (decimal?)null;
        decimal? minLiquidity = null;
        decimal? imbalance = null;
        decimal? micropriceEdgeBps = null;
        decimal? momentumBps = null;
        var referencePrice = GetReferencePrice(q);

        if (TryGetBookMetrics(ob, out var preMetrics))
        {
            spreadBps = preMetrics.SpreadBps;
            avgSpreadBps ??= spreadBps;
            minLiquidity = preMetrics.MinLiquidityUsd;
            imbalance = preMetrics.Imbalance;
            micropriceEdgeBps = preMetrics.MicropriceEdgeBps;
        }

        if (st.TickTimes.Count < _minTicksForEntry)
        {
            ResetEntryReadiness(symbol, st, now, $"ticks={st.TickTimes.Count}<{_minTicksForEntry}");
            LogSnapshot(symbol, st, now, spreadBps, avgSpreadBps, minLiquidity, imbalance, micropriceEdgeBps, null, bookAgeMs, "pre-ticks-block");
            LogBlocked(symbol, st, now, $"ticks={st.TickTimes.Count}<{_minTicksForEntry}");
            return;
        }

        if (!hasUsableBook)
        {
            ResetEntryReadiness(symbol, st, now, "empty-book");
            LogSnapshot(symbol, st, now, spreadBps, avgSpreadBps, minLiquidity, imbalance, micropriceEdgeBps, null, bookAgeMs, "pre-empty-book-block");
            LogBlocked(symbol, st, now, "empty-book");
            return;
        }

        var bookAge = TimeSpan.FromMilliseconds(bookAgeMs);
        if (bookAge > _maxBookAge)
        {
            ResetEntryReadiness(symbol, st, now, $"stale-book={bookAge.TotalMilliseconds:F0}ms>{_maxBookAge.TotalMilliseconds:F0}ms");
            LogSnapshot(symbol, st, now, spreadBps, avgSpreadBps, minLiquidity, imbalance, micropriceEdgeBps, null, bookAgeMs, "pre-stale-book-block");
            LogBlocked(symbol, st, now, $"stale-book={bookAge.TotalMilliseconds:F0}ms>{_maxBookAge.TotalMilliseconds:F0}ms");
            return;
        }

        if (!TryGetBookMetrics(ob, out var metrics))
        {
            ResetEntryReadiness(symbol, st, now, "empty-book");
            LogSnapshot(symbol, st, now, spreadBps, avgSpreadBps, minLiquidity, imbalance, micropriceEdgeBps, null, bookAge.TotalMilliseconds, "pre-empty-book-block");
            LogBlocked(symbol, st, now, "empty-book");
            return;
        }

        var bestBid = metrics.BestBid;
        var bestAsk = metrics.BestAsk;
        spreadBps = metrics.SpreadBps;
        avgSpreadBps = st.RecentSpreads.Count > 0 ? st.RecentSpreads.Average(x => x.SpreadBps) : spreadBps;
        minLiquidity = metrics.MinLiquidityUsd;
        imbalance = metrics.Imbalance;
        micropriceEdgeBps = metrics.MicropriceEdgeBps;
        
        if (spreadBps > _maxSpreadBps)
        {
            ResetEntryReadiness(symbol, st, now, $"spread={spreadBps:F1}>{_maxSpreadBps:F1}bps");
            LogSnapshot(symbol, st, now, spreadBps, avgSpreadBps, null, null, null, null, bookAge.TotalMilliseconds, "pre-spread-block");
            LogBlocked(symbol, st, now, $"spread={spreadBps:F1}>{_maxSpreadBps:F1}bps");
            return;
        }
        
        if (minLiquidity < _minLiquidityUsd)
        {
            ResetEntryReadiness(symbol, st, now, $"liq={minLiquidity:F0}<{_minLiquidityUsd:F0}USD");
            LogSnapshot(symbol, st, now, spreadBps, avgSpreadBps, minLiquidity, null, null, null, bookAge.TotalMilliseconds, "pre-liq-block");
            LogBlocked(symbol, st, now, $"liq={minLiquidity:F0}<{_minLiquidityUsd:F0}USD");
            return;
        }

        if (!referencePrice.HasValue)
        {
            ResetEntryReadiness(symbol, st, now, "missing-last");
            LogSnapshot(symbol, st, now, spreadBps, avgSpreadBps, minLiquidity, imbalance, micropriceEdgeBps, null, bookAge.TotalMilliseconds, "pre-missing-last-block");
            LogBlocked(symbol, st, now, "missing-last");
            return;
        }

        if (!TryGetMomentumBps(st, now, referencePrice.Value, out var computedMomentumBps, out var momentumSamples))
        {
            ResetEntryReadiness(symbol, st, now, $"momentumSamples={momentumSamples}<{_momentumLookbackQuotes}");
            LogSnapshot(symbol, st, now, spreadBps, avgSpreadBps, minLiquidity, imbalance, micropriceEdgeBps, null, bookAge.TotalMilliseconds, "pre-momentum-window-block");
            LogBlocked(symbol, st, now, $"momentumSamples={momentumSamples}<{_momentumLookbackQuotes}");
            return;
        }

        momentumBps = computedMomentumBps;
        if (momentumBps < _minMomentumBps)
        {
            ResetEntryReadiness(symbol, st, now, $"momentumBps={momentumBps:F2}<{_minMomentumBps:F2}");
            LogSnapshot(symbol, st, now, spreadBps, avgSpreadBps, minLiquidity, null, null, momentumBps, bookAge.TotalMilliseconds, "pre-momentum-block");
            LogBlocked(symbol, st, now, $"momentumBps={momentumBps:F2}<{_minMomentumBps:F2}");
            return;
        }

        if (imbalance < _minImbalanceRatio)
        {
            ResetEntryReadiness(symbol, st, now, $"imbalance={imbalance:F3}<{_minImbalanceRatio:F3}");
            LogSnapshot(symbol, st, now, spreadBps, avgSpreadBps, minLiquidity, imbalance, null, momentumBps, bookAge.TotalMilliseconds, "pre-imbalance-block");
            LogBlocked(symbol, st, now, $"imbalance={imbalance:F3}<{_minImbalanceRatio:F3}");
            return;
        }

        if (micropriceEdgeBps < _minMicropriceEdgeBps)
        {
            ResetEntryReadiness(symbol, st, now, $"microEdge={micropriceEdgeBps:F2}<{_minMicropriceEdgeBps:F2}");
            LogSnapshot(symbol, st, now, spreadBps, avgSpreadBps, minLiquidity, imbalance, micropriceEdgeBps, momentumBps, bookAge.TotalMilliseconds, "pre-microedge-block");
            LogBlocked(symbol, st, now, $"microEdge={micropriceEdgeBps:F2}<{_minMicropriceEdgeBps:F2}");
            return;
        }

        var signalSnapshot = new SignalSnapshot(
            BestBid: bestBid,
            BestAsk: bestAsk,
            SpreadBps: spreadBps.Value,
            AvgSpreadBps: avgSpreadBps ?? spreadBps.Value,
            MinLiquidityUsd: minLiquidity.Value,
            Imbalance: imbalance.Value,
            MicropriceEdgeBps: micropriceEdgeBps.Value,
            MomentumBps: momentumBps.Value,
            BookAgeMs: bookAge.TotalMilliseconds,
            ReferencePrice: referencePrice.Value,
            MomentumSamples: momentumSamples,
            TickCount: st.TickTimes.Count);

        MarkEntryReady(symbol, st, now, signalSnapshot);
        var readyDuration = st.EntryReadySinceUtc.HasValue
            ? now - st.EntryReadySinceUtc.Value
            : TimeSpan.Zero;
        if (st.ConsecutiveReadyQuotes < _entryConfirmationQuotes || readyDuration < _minSignalPersistence)
        {
            LogSnapshot(symbol, st, now, spreadBps, avgSpreadBps, minLiquidity, imbalance, micropriceEdgeBps, momentumBps, bookAge.TotalMilliseconds, "pre-confirmation-block");
            LogBlocked(
                symbol,
                st,
                now,
                $"confirm={st.ConsecutiveReadyQuotes}<{_entryConfirmationQuotes},persist={readyDuration.TotalSeconds:F1}s<{_minSignalPersistence.TotalSeconds:F1}s");
            return;
        }

        LogSnapshot(symbol, st, now, spreadBps, avgSpreadBps, minLiquidity, imbalance, micropriceEdgeBps, momentumBps, bookAge.TotalMilliseconds, "entry-ready");
        
        // Entry signal: kupi ako cena raste (momentum up)
        if (momentumBps > 0m)
        {
            var entryPrice = bestAsk; // Market buy na ask
            var reason =
                $"scalp-entry: spread={spreadBps:F1}bps avgSpread={avgSpreadBps:F1}bps liq={minLiquidity:F0}USD " +
                $"imbalance={imbalance:F3} microEdge={micropriceEdgeBps:F2}bps momentum={momentumBps:F2}bps";
            
            _log.Information("[SCALP-ENTRY] {Sym} BUY @ {Px:F2} {Reason}", 
                symbol, entryPrice, reason);

            LogEntryDetail(symbol, st, now, entryPrice, signalSnapshot, readyDuration);
            
            var signal = new TradeSignal(
                Symbol: q.Symbol,
                ShouldEnter: true,
                Side: OrderSide.Buy,
                SuggestedLimitPrice: entryPrice,
                Reason: reason,
                TimestampUtc: now
            );
            st.EntryPrice = entryPrice;
            st.EntryTime = now;
            st.BestPrice = entryPrice;
            st.BestPriceTime = now;
            st.WorstPrice = entryPrice;
            st.WorstPriceTime = now;
            st.EntryReason = reason;
            st.EntrySnapshot = signalSnapshot;
            st.InPosition = true;
            ClearEntryReadinessState(st);
            
            TradeSignalGenerated?.Invoke(signal);
            return;
        }

        LogSnapshot(symbol, st, now, spreadBps, avgSpreadBps, minLiquidity, imbalance, micropriceEdgeBps, momentumBps, bookAge.TotalMilliseconds, "pre-nonpositive-momentum-block");
        LogBlocked(symbol, st, now, $"momentumBps={momentumBps:F2}<=0");
    }
    
    private void EvaluateExit(string symbol, MarketQuote q, SymbolState st, DateTime now)
    {
        if (!st.EntryPrice.HasValue || !st.EntryTime.HasValue) return;
        if (!TryGetExecutableExitPrice(q, st, now, out var executableExitPrice)) return;
        
        var entryPrice = st.EntryPrice.Value;
        var currentPrice = executableExitPrice;
        var holdTime = now - st.EntryTime.Value;
        
        UpdatePositionExtrema(st, currentPrice, now);
        
        // Profit target
        var profitPct = (currentPrice - entryPrice) / entryPrice;
        if (profitPct >= _profitTargetPct)
        {
            _log.Information("[SCALP-EXIT] {Sym} TP @ {Px:F2} profit={Profit:P3} hold={Hold:F1}s", 
                symbol, currentPrice, profitPct, holdTime.TotalSeconds);
            
            ExitPosition(symbol, q, st, now, "scalp-tp");
            return;
        }
        
        // Stop loss
        if (profitPct <= -_stopLossPct)
        {
            _log.Information("[SCALP-EXIT] {Sym} SL @ {Px:F2} loss={Loss:P3} hold={Hold:F1}s", 
                symbol, currentPrice, profitPct, holdTime.TotalSeconds);
            
            ExitPosition(symbol, q, st, now, "scalp-sl");
            return;
        }

        var edgeLossReason = EvaluateEdgeLoss(q, st, now);
        if (edgeLossReason != null)
        {
            _log.Information("[SCALP-EXIT] {Sym} EDGE @ {Px:F2} reason={Reason} hold={Hold:F1}s", 
                symbol, currentPrice, edgeLossReason, holdTime.TotalSeconds);

            ExitPosition(symbol, q, st, now, edgeLossReason);
            return;
        }

        // Time-based exit
        if (holdTime >= _maxHoldTime)
        {
            _log.Information("[SCALP-EXIT] {Sym} TIME @ {Px:F2} profit={Profit:P3} hold={Hold:F1}s", 
                symbol, currentPrice, profitPct, holdTime.TotalSeconds);
            
            ExitPosition(symbol, q, st, now, "scalp-time");
            return;
        }
    }
    
    private void ExitPosition(string symbol, MarketQuote q, SymbolState st, DateTime now, string reason)
    {
        if (!st.EntryPrice.HasValue || !TryGetExecutableExitPrice(q, st, now, out var exitPrice)) return;
        
        var signal = new TradeSignal(
            Symbol: q.Symbol,
            ShouldEnter: false, // Exit signal
            Side: OrderSide.Sell,
            SuggestedLimitPrice: exitPrice,
            Reason: reason,
            TimestampUtc: now
        );

        LogExitResult(symbol, st, q, now, exitPrice, reason);
        
        // Reset state
        st.InPosition = false;
        st.EntryPrice = null;
        st.EntryTime = null;
        st.BestPrice = null;
        st.BestPriceTime = null;
        st.WorstPrice = null;
        st.WorstPriceTime = null;
        st.EntryReason = null;
        st.EntrySnapshot = null;
        ClearEntryReadinessState(st);
        
        TradeSignalGenerated?.Invoke(signal);
    }

    private void ResetEntryReadiness(string symbol, SymbolState st, DateTime now, string reason)
    {
        if (st.EntryReadySinceUtc.HasValue && st.ArmedSnapshot.HasValue)
        {
            var armedFor = now - st.EntryReadySinceUtc.Value;
            var snapshot = st.ArmedSnapshot.Value;
            _log.Information(
                "[SCALP-SETUP] {Sym} CLEARED reason={Reason} armedFor={ArmedFor:F1}s readyQuotes={ReadyQuotes} spread={Spread:F1} avgSpread={AvgSpread:F1} liq={Liq:F0} imbalance={Imb:F3} microEdge={Micro:F2} momentum={Momentum:F2} bookAgeMs={BookAge:F0}",
                symbol,
                reason,
                armedFor.TotalSeconds,
                st.ConsecutiveReadyQuotes,
                snapshot.SpreadBps,
                snapshot.AvgSpreadBps,
                snapshot.MinLiquidityUsd,
                snapshot.Imbalance,
                snapshot.MicropriceEdgeBps,
                snapshot.MomentumBps,
                snapshot.BookAgeMs);
        }

        ClearEntryReadinessState(st);
    }

    private static void ClearEntryReadinessState(SymbolState st)
    {
        st.EntryReadySinceUtc = null;
        st.LastReadyQuoteUtc = null;
        st.ConsecutiveReadyQuotes = 0;
        st.ArmedSnapshot = null;
    }

    private void MarkEntryReady(string symbol, SymbolState st, DateTime now, SignalSnapshot snapshot)
    {
        var wasArmed = st.EntryReadySinceUtc.HasValue;
        if (!st.LastReadyQuoteUtc.HasValue || (now - st.LastReadyQuoteUtc.Value) > _momentumLookbackWindow)
        {
            st.EntryReadySinceUtc = now;
            st.ConsecutiveReadyQuotes = 1;
        }
        else
        {
            st.EntryReadySinceUtc ??= now;
            st.ConsecutiveReadyQuotes++;
        }

        st.LastReadyQuoteUtc = now;
        st.ArmedSnapshot = snapshot;

        if (!wasArmed)
        {
            _log.Information(
                "[SCALP-SETUP] {Sym} ARMED spread={Spread:F1} avgSpread={AvgSpread:F1} liq={Liq:F0} imbalance={Imb:F3} microEdge={Micro:F2} momentum={Momentum:F2} momentumSamples={Samples} bookAgeMs={BookAge:F0} ticks={Ticks}",
                symbol,
                snapshot.SpreadBps,
                snapshot.AvgSpreadBps,
                snapshot.MinLiquidityUsd,
                snapshot.Imbalance,
                snapshot.MicropriceEdgeBps,
                snapshot.MomentumBps,
                snapshot.MomentumSamples,
                snapshot.BookAgeMs,
                snapshot.TickCount);
        }
    }

    private static decimal? GetReferencePrice(MarketQuote q)
    {
        return q.Mid ?? q.Last;
    }

    private void TrimReferencePrices(SymbolState st, DateTime now)
    {
        while (st.RecentReferencePrices.Count > 0 &&
               (now - st.RecentReferencePrices.Peek().Utc) > _momentumLookbackWindow)
        {
            st.RecentReferencePrices.Dequeue();
        }
    }

    private bool TryGetMomentumBps(SymbolState st, DateTime now, decimal currentReferencePrice, out decimal momentumBps, out int sampleCount)
    {
        TrimReferencePrices(st, now);

        sampleCount = st.RecentReferencePrices.Count + 1;
        momentumBps = 0m;

        if (currentReferencePrice <= 0m || st.RecentReferencePrices.Count < (_momentumLookbackQuotes - 1))
        {
            return false;
        }

        var samples = st.RecentReferencePrices.ToArray();
        var anchorIndex = Math.Max(0, sampleCount - _momentumLookbackQuotes);
        var anchor = samples[anchorIndex].Price;
        if (anchor <= 0m)
        {
            return false;
        }

        momentumBps = ((currentReferencePrice - anchor) / anchor) * 10000m;
        return true;
    }

    private static bool TryGetBookMetrics(OrderBookUpdate? ob, out BookMetrics metrics)
    {
        metrics = default;
        if (ob == null || ob.Bids.Count == 0 || ob.Asks.Count == 0)
        {
            return false;
        }

        var bestBid = ob.Bids[0].Price;
        var bestAsk = ob.Asks[0].Price;
        var midPrice = (bestBid + bestAsk) / 2m;
        if (midPrice <= 0m)
        {
            return false;
        }

        var bidLiquidity = ob.Bids.Take(3).Sum(b => b.Price * b.Quantity);
        var askLiquidity = ob.Asks.Take(3).Sum(a => a.Price * a.Quantity);
        var topBidQty = ob.Bids.Take(3).Sum(b => b.Quantity);
        var topAskQty = ob.Asks.Take(3).Sum(a => a.Quantity);
        var totalTopQty = topBidQty + topAskQty;
        if (totalTopQty <= 0m)
        {
            return false;
        }

        var spreadBps = ((bestAsk - bestBid) / midPrice) * 10000m;
        var imbalance = (topBidQty - topAskQty) / totalTopQty;
        var microprice = ((bestAsk * topBidQty) + (bestBid * topAskQty)) / totalTopQty;
        var micropriceEdgeBps = ((microprice - midPrice) / midPrice) * 10000m;

        metrics = new BookMetrics(
            BestBid: bestBid,
            BestAsk: bestAsk,
            SpreadBps: spreadBps,
            MinLiquidityUsd: Math.Min(bidLiquidity, askLiquidity),
            Imbalance: imbalance,
            MicropriceEdgeBps: micropriceEdgeBps);
        return true;
    }

    private bool TryGetExecutableExitPrice(MarketQuote q, SymbolState st, DateTime now, out decimal exitPrice)
    {
        if (q.Bid.HasValue && q.Bid.Value > 0m)
        {
            exitPrice = q.Bid.Value;
            return true;
        }

        if (st.LastOrderBook != null &&
            st.LastOrderBook.Bids.Count > 0 &&
            (now - st.LastOrderBook.TimestampUtc) <= _maxBookAge)
        {
            exitPrice = st.LastOrderBook.Bids[0].Price;
            return true;
        }

        exitPrice = 0m;
        return false;
    }

    private static void UpdatePositionExtrema(SymbolState st, decimal currentPrice, DateTime now)
    {
        if (!st.BestPrice.HasValue || currentPrice > st.BestPrice.Value)
        {
            st.BestPrice = currentPrice;
            st.BestPriceTime = now;
        }

        if (!st.WorstPrice.HasValue || currentPrice < st.WorstPrice.Value)
        {
            st.WorstPrice = currentPrice;
            st.WorstPriceTime = now;
        }
    }

    private static decimal GetBps(decimal fromPrice, decimal toPrice)
    {
        if (fromPrice <= 0m)
        {
            return 0m;
        }

        return ((toPrice - fromPrice) / fromPrice) * 10000m;
    }

    private string? EvaluateEdgeLoss(MarketQuote q, SymbolState st, DateTime now)
    {
        var referencePrice = GetReferencePrice(q);
        decimal? momentumBps = null;
        if (referencePrice.HasValue && TryGetMomentumBps(st, now, referencePrice.Value, out var computedMomentumBps, out _))
        {
            momentumBps = computedMomentumBps;
        }

        var failures = 0;
        var reasons = new List<string>(4);
        var bookAgeMs = st.LastOrderBook != null
            ? (now - st.LastOrderBook.TimestampUtc).TotalMilliseconds
            : double.PositiveInfinity;

        if (!TryGetBookMetrics(st.LastOrderBook, out var metrics) || bookAgeMs > _maxBookAge.TotalMilliseconds)
        {
            failures++;
            reasons.Add(bookAgeMs > _maxBookAge.TotalMilliseconds ? $"staleBook={bookAgeMs:F0}ms" : "missing-book");
        }
        else
        {
            if (metrics.SpreadBps > _exitMaxSpreadBps)
            {
                failures++;
                reasons.Add($"spread={metrics.SpreadBps:F1}>{_exitMaxSpreadBps:F1}bps");
            }

            if (metrics.Imbalance < _exitMinImbalanceRatio)
            {
                failures++;
                reasons.Add($"imbalance={metrics.Imbalance:F3}<{_exitMinImbalanceRatio:F3}");
            }

            if (metrics.MicropriceEdgeBps < _exitMinMicropriceEdgeBps)
            {
                failures++;
                reasons.Add($"microEdge={metrics.MicropriceEdgeBps:F2}<{_exitMinMicropriceEdgeBps:F2}");
            }
        }

        if (momentumBps.HasValue && momentumBps.Value < _exitMinMomentumBps)
        {
            failures++;
            reasons.Add($"momentum={momentumBps.Value:F2}<{_exitMinMomentumBps:F2}");
        }

        if (failures < _edgeLossFailureThreshold)
        {
            return null;
        }

        return "scalp-edge-lost:" + string.Join(",", reasons);
    }

    private void LogEntryDetail(string symbol, SymbolState st, DateTime now, decimal entryPrice, SignalSnapshot snapshot, TimeSpan readyDuration)
    {
        _log.Information(
            "[SCALP-ENTRY-DETAIL] {Sym} px={Px:F2} readyFor={ReadyFor:F1}s readyQuotes={ReadyQuotes} spread={Spread:F1} avgSpread={AvgSpread:F1} liq={Liq:F0} imbalance={Imb:F3} microEdge={Micro:F2} momentum={Momentum:F2} momentumSamples={Samples} refPx={RefPx:F6} bookAgeMs={BookAge:F0} ticks={Ticks}",
            symbol,
            entryPrice,
            readyDuration.TotalSeconds,
            st.ConsecutiveReadyQuotes,
            snapshot.SpreadBps,
            snapshot.AvgSpreadBps,
            snapshot.MinLiquidityUsd,
            snapshot.Imbalance,
            snapshot.MicropriceEdgeBps,
            snapshot.MomentumBps,
            snapshot.MomentumSamples,
            snapshot.ReferencePrice,
            snapshot.BookAgeMs,
            snapshot.TickCount);
    }

    private void LogExitResult(string symbol, SymbolState st, MarketQuote q, DateTime now, decimal exitPrice, string reason)
    {
        if (!st.EntryPrice.HasValue || !st.EntryTime.HasValue)
        {
            return;
        }

        var entryPrice = st.EntryPrice.Value;
        var realizedBps = GetBps(entryPrice, exitPrice);
        var holdTime = now - st.EntryTime.Value;
        var mfeBps = st.BestPrice.HasValue ? GetBps(entryPrice, st.BestPrice.Value) : 0m;
        var maeBps = st.WorstPrice.HasValue ? GetBps(entryPrice, st.WorstPrice.Value) : 0m;

        decimal? exitSpreadBps = null;
        decimal? exitLiquidityUsd = null;
        decimal? exitImbalance = null;
        decimal? exitMicropriceEdgeBps = null;
        if (TryGetBookMetrics(st.LastOrderBook, out var exitMetrics))
        {
            exitSpreadBps = exitMetrics.SpreadBps;
            exitLiquidityUsd = exitMetrics.MinLiquidityUsd;
            exitImbalance = exitMetrics.Imbalance;
            exitMicropriceEdgeBps = exitMetrics.MicropriceEdgeBps;
        }

        decimal? exitMomentumBps = null;
        var referencePrice = GetReferencePrice(q);
        if (referencePrice.HasValue && TryGetMomentumBps(st, now, referencePrice.Value, out var computedMomentumBps, out _))
        {
            exitMomentumBps = computedMomentumBps;
        }

        var entrySnapshot = st.EntrySnapshot;
        _log.Information(
            "[SCALP-RESULT] {Sym} reason={Reason} entryPx={EntryPx:F2} exitPx={ExitPx:F2} realizedBps={Realized:F2} hold={Hold:F1}s mfeBps={Mfe:F2} maeBps={Mae:F2} bestPx={BestPx} worstPx={WorstPx} entrySpread={EntrySpread} entryLiq={EntryLiq} entryImb={EntryImb} entryMicro={EntryMicro} entryMomentum={EntryMomentum} exitSpread={ExitSpread} exitLiq={ExitLiq} exitImb={ExitImb} exitMicro={ExitMicro} exitMomentum={ExitMomentum}",
            symbol,
            reason,
            entryPrice,
            exitPrice,
            realizedBps,
            holdTime.TotalSeconds,
            mfeBps,
            maeBps,
            st.BestPrice?.ToString("F2") ?? "n/a",
            st.WorstPrice?.ToString("F2") ?? "n/a",
            entrySnapshot?.SpreadBps.ToString("F1") ?? "n/a",
            entrySnapshot?.MinLiquidityUsd.ToString("F0") ?? "n/a",
            entrySnapshot?.Imbalance.ToString("F3") ?? "n/a",
            entrySnapshot?.MicropriceEdgeBps.ToString("F2") ?? "n/a",
            entrySnapshot?.MomentumBps.ToString("F2") ?? "n/a",
            exitSpreadBps?.ToString("F1") ?? "n/a",
            exitLiquidityUsd?.ToString("F0") ?? "n/a",
            exitImbalance?.ToString("F3") ?? "n/a",
            exitMicropriceEdgeBps?.ToString("F2") ?? "n/a",
            exitMomentumBps?.ToString("F2") ?? "n/a");
    }

    private void LogBlocked(string symbol, SymbolState st, DateTime now, string reason)
    {
        if (st.LastBlockedLogUtc.HasValue && (now - st.LastBlockedLogUtc.Value).TotalSeconds < 60)
        {
            return;
        }

        _log.Information("[SCALP-BLOCKED] {Sym} {Reason}", symbol, reason);
        st.LastBlockedLogUtc = now;
    }

    private void LogSnapshot(
        string symbol,
        SymbolState st,
        DateTime now,
        decimal? spreadBps,
        decimal? avgSpreadBps,
        decimal? minLiquidityUsd,
        decimal? imbalance,
        decimal? micropriceEdgeBps,
        decimal? momentumBps,
        double? bookAgeMs,
        string stage)
    {
        if (st.LastSnapshotLogUtc.HasValue && (now - st.LastSnapshotLogUtc.Value).TotalSeconds < 60)
        {
            return;
        }

        var readyDurationSeconds = st.EntryReadySinceUtc.HasValue
            ? (now - st.EntryReadySinceUtc.Value).TotalSeconds
            : 0d;

        _log.Information(
            "[SCALP-SNAPSHOT] {Sym} stage={Stage} ticks={Ticks} readyQuotes={ReadyQuotes} readyFor={ReadyFor} spread={Spread} avgSpread={AvgSpread} liq={Liq} imbalance={Imb} microEdge={Micro} momentum={Momentum} bookAgeMs={BookAge}",
            symbol,
            stage,
            st.TickTimes.Count,
            st.ConsecutiveReadyQuotes,
            readyDurationSeconds.ToString("F1"),
            spreadBps?.ToString("F1") ?? "n/a",
            avgSpreadBps?.ToString("F1") ?? "n/a",
            minLiquidityUsd?.ToString("F0") ?? "n/a",
            imbalance?.ToString("F3") ?? "n/a",
            micropriceEdgeBps?.ToString("F2") ?? "n/a",
            momentumBps?.ToString("F2") ?? "n/a",
            bookAgeMs?.ToString("F0") ?? "n/a");

        st.LastSnapshotLogUtc = now;
    }
}

