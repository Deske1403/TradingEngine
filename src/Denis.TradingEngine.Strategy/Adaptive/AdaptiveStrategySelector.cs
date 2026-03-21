#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Denis.TradingEngine.Core.Interfaces;
using Denis.TradingEngine.Core.Trading;
using Denis.TradingEngine.Strategy.Pullback;
using Denis.TradingEngine.Strategy.Scalp;
using Serilog;

namespace Denis.TradingEngine.Strategy.Adaptive;

/// <summary>
/// Adaptive strategy selector - automatski bira između scalp i pullback strategije
/// na osnovu market conditions (volatilnost, volume, spread, itd.)
/// </summary>
public sealed class AdaptiveStrategySelector : ITradingStrategy
{
    private readonly ILogger _log = Log.ForContext<AdaptiveStrategySelector>();
    private static readonly HashSet<string> _defaultScalpFocusSymbols = new(StringComparer.OrdinalIgnoreCase)
    {
        "BTCUSDT",
        "SOLUSDT",
        "XRPUSDT"
    };
    private readonly ITradingStrategy _pullbackStrategy;
    private readonly ITradingStrategy? _scalpStrategy;
    private readonly HashSet<string>? _scalpSymbolAllowlist;
    private readonly bool _scalpDryRun;
    
    // Adaptive selection thresholds
    private readonly decimal _highVolatilityThreshold;
    private readonly int _minTicksForScalp;
    private readonly decimal _maxSpreadBpsForScalp;
    private readonly TimeSpan _minScalpReadyTime;
    private readonly int _minScalpReadyQuotes;
    
    // Per-symbol state
    private readonly Dictionary<string, SymbolMarketState> _symbolState = new(StringComparer.OrdinalIgnoreCase);
    
    public event Action<TradeSignal>? TradeSignalGenerated;
    
    public AdaptiveStrategySelector(
        ITradingStrategy pullbackStrategy,
        ITradingStrategy? scalpStrategy = null,
        IEnumerable<string>? scalpSymbols = null,
        bool scalpDryRun = false,
        decimal highVolatilityThreshold = 0.001m,
        int minTicksForScalp = 100,
        decimal maxSpreadBpsForScalp = 10.0m,
        TimeSpan? minScalpReadyTime = null,
        int minScalpReadyQuotes = 3)
    {
        _pullbackStrategy = pullbackStrategy ?? throw new ArgumentNullException(nameof(pullbackStrategy));
        _scalpStrategy = scalpStrategy;
        _scalpSymbolAllowlist = scalpSymbols?
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _scalpDryRun = scalpDryRun && scalpStrategy != null;
        _highVolatilityThreshold = highVolatilityThreshold;
        _minTicksForScalp = minTicksForScalp;
        _maxSpreadBpsForScalp = maxSpreadBpsForScalp;
        _minScalpReadyTime = minScalpReadyTime ?? TimeSpan.FromSeconds(20);
        _minScalpReadyQuotes = Math.Max(1, minScalpReadyQuotes);
        
        _pullbackStrategy.TradeSignalGenerated += OnPullbackSignalGenerated;
        if (_scalpStrategy != null)
        {
            _scalpStrategy.TradeSignalGenerated += OnScalpSignalGenerated;
        }
    }
    
    private void OnPullbackSignalGenerated(TradeSignal signal)
    {
        TradeSignalGenerated?.Invoke(signal);
    }

    private void OnScalpSignalGenerated(TradeSignal signal)
    {
        if (!_scalpDryRun)
        {
            TradeSignalGenerated?.Invoke(signal);
            return;
        }

        var action = signal.ShouldEnter ? "would-enter" : "would-exit";
        _log.Information(
            "[SCALP-DRYRUN] {Action} {Sym} side={Side} px={Px} reason={Reason} preferred={Preferred}",
            action,
            signal.Symbol.Ticker,
            signal.Side,
            signal.SuggestedLimitPrice,
            signal.Reason,
            GetPreferredStrategyLabel(signal.Symbol.Ticker));
    }
    
    public void OnQuote(MarketQuote q)
    {
        if (q?.Symbol is null) return;
        
        var symbol = q.Symbol.Ticker;
        var now = q.TimestampUtc == default ? DateTime.UtcNow : q.TimestampUtc;
        
        // Update market state
        UpdateMarketState(symbol, q, now);
        
        // Determine which strategy to use
        var strategy = SelectStrategy(symbol, q);
        var scalpOwnsSymbol = (_scalpStrategy as ScalpStrategy)?.IsManagingSymbol(symbol) == true;
        var shouldDispatchToScalp = _scalpStrategy != null &&
            ((IsScalpEnabledForSymbol(symbol) && strategy == StrategyType.Scalp) || scalpOwnsSymbol);

        if (_scalpDryRun)
        {
            // Pullback remains the live execution owner while scalp observes in parallel.
            _pullbackStrategy.OnQuote(q);
            if (shouldDispatchToScalp)
            {
                LogScalpDispatch(symbol, q, strategy, dryRun: true, scalpOwnsSymbol);
                _scalpStrategy?.OnQuote(q);
            }
            return;
        }

        if (shouldDispatchToScalp)
        {
            LogScalpDispatch(symbol, q, strategy, dryRun: false, scalpOwnsSymbol);
            _scalpStrategy?.OnQuote(q);
        }
        else if (strategy == StrategyType.Pullback)
        {
            _pullbackStrategy.OnQuote(q);
        }
        else
        {
            // Fallback to pullback if scalp not available
            _pullbackStrategy.OnQuote(q);
        }
    }

    private string GetPreferredStrategyLabel(string symbol)
    {
        if (_symbolState.TryGetValue(symbol, out var state))
        {
            return state.LastStrategy.ToString();
        }

        return StrategyType.Pullback.ToString();
    }
    
    private StrategyType SelectStrategy(string symbol, MarketQuote q)
    {
        if (!IsScalpEnabledForSymbol(symbol))
        {
            return StrategyType.Pullback;
        }

        if (!_symbolState.TryGetValue(symbol, out var state))
        {
            // Default to pullback until we have enough data
            return StrategyType.Pullback;
        }

        var now = q.TimestampUtc == default ? DateTime.UtcNow : q.TimestampUtc;
        
        // Decision logic:
        // 1. HIGH volatility + high activity + tight spread → SCALP
        // 2. LOW/NORMAL volatility → PULLBACK
        // 3. Low activity → PULLBACK
        // 4. Wide spread → PULLBACK
        
        var atrFrac = state.AtrFraction;
        var ticksPerWindow = state.TicksPerWindow;
        var spreadBps = state.SpreadBps;
        var coreScalpReady =
            atrFrac > _highVolatilityThreshold &&
            ticksPerWindow >= _minTicksForScalp &&
            spreadBps <= _maxSpreadBpsForScalp &&
            _scalpStrategy != null;

        UpdateScalpReadiness(state, now, coreScalpReady);
        var scalpReadyDuration = state.ScalpReadySinceUtc.HasValue
            ? now - state.ScalpReadySinceUtc.Value
            : TimeSpan.Zero;
        var scalpReady = coreScalpReady &&
            scalpReadyDuration >= _minScalpReadyTime &&
            state.ConsecutiveScalpReadyQuotes >= _minScalpReadyQuotes;
        
        // Scalp conditions:
        // - HIGH volatility (atrFrac > high threshold)
        // - High activity (ticksPerWindow >= minTicksForScalp)
        // - Tight spread (spreadBps <= maxSpreadBpsForScalp)
        if (scalpReady)
        {
            if (state.LastStrategy != StrategyType.Scalp)
            {
                _log.Information(
                    "[ADAPTIVE] {Sym} switching to SCALP: atrFrac={AtrFrac:E6} ticks={Ticks} spread={Spread:F1}bps readyFor={ReadyFor:F1}s readyQuotes={ReadyQuotes}",
                    symbol, atrFrac, ticksPerWindow, spreadBps, scalpReadyDuration.TotalSeconds, state.ConsecutiveScalpReadyQuotes);
                state.LastStrategy = StrategyType.Scalp;
            }
            return StrategyType.Scalp;
        }

        if (_scalpStrategy != null)
        {
            var shouldLogRejection =
                !state.LastScalpRejectLogUtc.HasValue ||
                (now - state.LastScalpRejectLogUtc.Value).TotalSeconds >= 60;

            if (shouldLogRejection)
            {
                var rejectionReasons = new List<string>(3);
                if (atrFrac <= _highVolatilityThreshold)
                {
                    rejectionReasons.Add($"atrFrac={atrFrac:E6}<={_highVolatilityThreshold:E6}");
                }

                if (ticksPerWindow < _minTicksForScalp)
                {
                    rejectionReasons.Add($"ticks={ticksPerWindow}<{_minTicksForScalp}");
                }

                if (spreadBps > _maxSpreadBpsForScalp)
                {
                    rejectionReasons.Add($"spread={spreadBps:F1}>{_maxSpreadBpsForScalp:F1}bps");
                }

                if (coreScalpReady)
                {
                    if (scalpReadyDuration < _minScalpReadyTime)
                    {
                        rejectionReasons.Add($"readyFor={scalpReadyDuration.TotalSeconds:F1}s<{_minScalpReadyTime.TotalSeconds:F1}s");
                    }

                    if (state.ConsecutiveScalpReadyQuotes < _minScalpReadyQuotes)
                    {
                        rejectionReasons.Add($"readyQuotes={state.ConsecutiveScalpReadyQuotes}<{_minScalpReadyQuotes}");
                    }
                }

                _log.Information(
                    "[ADAPTIVE] {Sym} scalp-rejected: {Reasons}",
                    symbol,
                    string.Join(", ", rejectionReasons));

                state.LastScalpRejectLogUtc = now;
            }
        }
        
        // Pullback conditions (default):
        // - LOW/NORMAL volatility
        // - Or low activity
        // - Or wide spread
        if (state.LastStrategy != StrategyType.Pullback)
        {
            _log.Information(
                "[ADAPTIVE] {Sym} switching to PULLBACK: atrFrac={AtrFrac:E6} ticks={Ticks} spread={Spread:F1}bps",
                symbol, atrFrac, ticksPerWindow, spreadBps);
            state.LastStrategy = StrategyType.Pullback;
        }
        return StrategyType.Pullback;
    }
    
    private void UpdateMarketState(string symbol, MarketQuote q, DateTime now)
    {
        if (!_symbolState.TryGetValue(symbol, out var state))
        {
            state = new SymbolMarketState();
            _symbolState[symbol] = state;
        }
        
        // Update tick count
        state.TickTimes.Enqueue(now);
        while (state.TickTimes.Count > 0 && (now - state.TickTimes.Peek()).TotalSeconds > 60)
        {
            state.TickTimes.Dequeue();
        }
        state.TicksPerWindow = state.TickTimes.Count;
        
        // Update spread
        if (q.Bid.HasValue && q.Ask.HasValue && q.Bid.Value > 0m)
        {
            var spread = q.Ask.Value - q.Bid.Value;
            var spreadFrac = spread / q.Bid.Value;
            state.SpreadBps = spreadFrac * 10000m;
        }
        
        // ATR fraction would come from strategy's internal state
        // For now, we'll estimate based on price movement
        if (q.Last.HasValue && state.LastPrice.HasValue)
        {
            var priceChange = Math.Abs(q.Last.Value - state.LastPrice.Value);
            if (state.LastPrice.Value > 0m)
            {
                var changeFrac = priceChange / state.LastPrice.Value;
                // Simple moving average of price change as ATR proxy
                state.AtrFraction = (state.AtrFraction * 0.9m) + (changeFrac * 0.1m);
            }
        }
        
        if (q.Last.HasValue)
        {
            state.LastPrice = q.Last.Value;
        }
    }

    private bool IsScalpEnabledForSymbol(string symbol)
    {
        return _scalpStrategy != null &&
            (_scalpSymbolAllowlist == null || _scalpSymbolAllowlist.Count == 0 || _scalpSymbolAllowlist.Contains(symbol));
    }

    private bool ShouldLogScalpSymbol(string symbol)
    {
        if (_scalpSymbolAllowlist != null && _scalpSymbolAllowlist.Count > 0)
        {
            return _scalpSymbolAllowlist.Contains(symbol);
        }

        return _defaultScalpFocusSymbols.Contains(symbol);
    }

    private void UpdateScalpReadiness(SymbolMarketState state, DateTime now, bool coreScalpReady)
    {
        if (!coreScalpReady)
        {
            state.ScalpReadySinceUtc = null;
            state.ConsecutiveScalpReadyQuotes = 0;
            return;
        }

        state.ScalpReadySinceUtc ??= now;
        state.ConsecutiveScalpReadyQuotes++;
    }

    private void LogScalpDispatch(string symbol, MarketQuote q, StrategyType preferredStrategy, bool dryRun, bool activePosition)
    {
        if (!ShouldLogScalpSymbol(symbol) || _scalpStrategy == null)
        {
            return;
        }

        if (!_symbolState.TryGetValue(symbol, out var state))
        {
            return;
        }

        if (state.LastScalpDispatchLogUtc.HasValue &&
            (q.TimestampUtc - state.LastScalpDispatchLogUtc.Value).TotalSeconds < 60)
        {
            return;
        }

        _log.Information(
            "[ADAPTIVE-HANDOFF] {Sym} scalp.OnQuote dispatched preferred={Preferred} dryRun={DryRun} activePosition={ActivePosition} ticks={Ticks} atrFrac={AtrFrac:E6} spread={Spread:F1}bps last={Last}",
            symbol,
            preferredStrategy,
            dryRun,
            activePosition,
            state.TicksPerWindow,
            state.AtrFraction,
            state.SpreadBps,
            q.Last);

        state.LastScalpDispatchLogUtc = q.TimestampUtc;
    }
    
    private enum StrategyType
    {
        Pullback,
        Scalp
    }
    
    private class SymbolMarketState
    {
        public Queue<DateTime> TickTimes { get; } = new();
        public int TicksPerWindow { get; set; }
        public decimal SpreadBps { get; set; }
        public decimal AtrFraction { get; set; }
        public decimal? LastPrice { get; set; }
        public DateTime? LastScalpRejectLogUtc { get; set; }
        public DateTime? LastScalpDispatchLogUtc { get; set; }
        public DateTime? ScalpReadySinceUtc { get; set; }
        public int ConsecutiveScalpReadyQuotes { get; set; }
        public StrategyType LastStrategy { get; set; } = StrategyType.Pullback;
    }
}

