#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Denis.TradingEngine.Core.Interfaces;
using Denis.TradingEngine.Core.Trading;
using Denis.TradingEngine.Strategy.Scalp;
using Serilog;

namespace Denis.TradingEngine.Strategy.Adaptive;

public sealed class AdaptiveMicroPullbackSelector : ITradingStrategy
{
    public event Action<TradeSignal>? TradeSignalGenerated;

    private readonly ILogger _log = Log.ForContext<AdaptiveMicroPullbackSelector>();
    private readonly ITradingStrategy _pullbackStrategy;
    private readonly MicroPullbackReversionStrategy? _microPullbackStrategy;
    private readonly HashSet<string>? _microPullbackSymbolAllowlist;
    private readonly bool _microPullbackDryRun;
    private readonly decimal _highVolatilityThreshold;
    private readonly int _minTicksForMicroPullback;
    private readonly decimal _maxSpreadBpsForMicroPullback;
    private readonly TimeSpan _minMicroPullbackReadyTime;
    private readonly int _minMicroPullbackReadyQuotes;
    private readonly Dictionary<string, SymbolMarketState> _symbolState = new(StringComparer.OrdinalIgnoreCase);

    public AdaptiveMicroPullbackSelector(
        ITradingStrategy pullbackStrategy,
        MicroPullbackReversionStrategy? microPullbackStrategy = null,
        IEnumerable<string>? microPullbackSymbols = null,
        bool microPullbackDryRun = false,
        decimal highVolatilityThreshold = 0.001m,
        int minTicksForMicroPullback = 100,
        decimal maxSpreadBpsForMicroPullback = 10.0m,
        TimeSpan? minMicroPullbackReadyTime = null,
        int minMicroPullbackReadyQuotes = 3)
    {
        _pullbackStrategy = pullbackStrategy ?? throw new ArgumentNullException(nameof(pullbackStrategy));
        _microPullbackStrategy = microPullbackStrategy;
        _microPullbackSymbolAllowlist = microPullbackSymbols?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _microPullbackDryRun = microPullbackDryRun && microPullbackStrategy != null;
        _highVolatilityThreshold = highVolatilityThreshold;
        _minTicksForMicroPullback = minTicksForMicroPullback;
        _maxSpreadBpsForMicroPullback = maxSpreadBpsForMicroPullback;
        _minMicroPullbackReadyTime = minMicroPullbackReadyTime ?? TimeSpan.FromSeconds(20);
        _minMicroPullbackReadyQuotes = Math.Max(1, minMicroPullbackReadyQuotes);

        _pullbackStrategy.TradeSignalGenerated += signal => TradeSignalGenerated?.Invoke(signal);
        if (_microPullbackStrategy != null)
        {
            _microPullbackStrategy.TradeSignalGenerated += OnMicroPullbackSignalGenerated;
        }
    }

    public void OnQuote(MarketQuote q)
    {
        if (q?.Symbol is null)
        {
            return;
        }

        var symbol = q.Symbol.Ticker;
        var now = q.TimestampUtc == default ? DateTime.UtcNow : q.TimestampUtc;
        UpdateMarketState(symbol, q, now);

        if (_microPullbackStrategy != null && IsMicroPullbackEnabledForSymbol(symbol))
        {
            var microOwnsPosition = _microPullbackStrategy.IsManagingSymbol(symbol);
            if (!microOwnsPosition)
            {
                _microPullbackStrategy.ObserveQuote(q);
            }
        }

        var strategy = SelectStrategy(symbol, q);
        var microOwnsSymbol = _microPullbackStrategy?.IsManagingSymbol(symbol) == true;
        var shouldDispatchToMicro =
            _microPullbackStrategy != null &&
            ((IsMicroPullbackEnabledForSymbol(symbol) && strategy == StrategyType.MicroPullback) || microOwnsSymbol);

        if (_microPullbackDryRun)
        {
            _pullbackStrategy.OnQuote(q);
            if (shouldDispatchToMicro)
            {
                LogMicroDispatch(symbol, q, strategy, true, microOwnsSymbol);
                _microPullbackStrategy!.OnQuote(q);
            }

            return;
        }

        if (shouldDispatchToMicro)
        {
            LogMicroDispatch(symbol, q, strategy, false, microOwnsSymbol);
            _microPullbackStrategy?.OnQuote(q);
            return;
        }

        _pullbackStrategy.OnQuote(q);
    }

    private void OnMicroPullbackSignalGenerated(TradeSignal signal)
    {
        if (!_microPullbackDryRun)
        {
            TradeSignalGenerated?.Invoke(signal);
            return;
        }

        var action = signal.ShouldEnter ? "would-enter" : "would-exit";
        _log.Information(
            "[MR-DRYRUN] {Action} {Sym} side={Side} px={Px} reason={Reason}",
            action,
            signal.Symbol.Ticker,
            signal.Side,
            signal.SuggestedLimitPrice,
            signal.Reason);
    }

    private StrategyType SelectStrategy(string symbol, MarketQuote q)
    {
        if (!IsMicroPullbackEnabledForSymbol(symbol))
        {
            return StrategyType.Pullback;
        }

        if (!_symbolState.TryGetValue(symbol, out var state))
        {
            return StrategyType.Pullback;
        }

        var now = q.TimestampUtc == default ? DateTime.UtcNow : q.TimestampUtc;
        var coreReady =
            state.AtrFraction > _highVolatilityThreshold &&
            state.TicksPerWindow >= _minTicksForMicroPullback &&
            state.SpreadBps <= _maxSpreadBpsForMicroPullback &&
            _microPullbackStrategy != null;

        UpdateReadiness(state, now, coreReady);
        var readyDuration = state.ReadySinceUtc.HasValue ? now - state.ReadySinceUtc.Value : TimeSpan.Zero;
        var ready =
            coreReady &&
            readyDuration >= _minMicroPullbackReadyTime &&
            state.ConsecutiveReadyQuotes >= _minMicroPullbackReadyQuotes;

        if (ready)
        {
            if (state.LastStrategy != StrategyType.MicroPullback)
            {
                _log.Information(
                    "[ADAPTIVE-MR] {Sym} switching to MICRO-PULLBACK: atrFrac={AtrFrac:E6} ticks={Ticks} spread={Spread:F1}bps readyFor={ReadyFor:F1}s readyQuotes={ReadyQuotes}",
                    symbol,
                    state.AtrFraction,
                    state.TicksPerWindow,
                    state.SpreadBps,
                    readyDuration.TotalSeconds,
                    state.ConsecutiveReadyQuotes);
                state.LastStrategy = StrategyType.MicroPullback;
            }

            return StrategyType.MicroPullback;
        }

        var shouldLogRejection =
            !state.LastRejectLogUtc.HasValue ||
            (now - state.LastRejectLogUtc.Value).TotalSeconds >= 60;
        if (shouldLogRejection && _microPullbackStrategy != null)
        {
            var reasons = new List<string>(4);
            if (state.AtrFraction <= _highVolatilityThreshold)
            {
                reasons.Add($"atrFrac={state.AtrFraction:E6}<={_highVolatilityThreshold:E6}");
            }

            if (state.TicksPerWindow < _minTicksForMicroPullback)
            {
                reasons.Add($"ticks={state.TicksPerWindow}<{_minTicksForMicroPullback}");
            }

            if (state.SpreadBps > _maxSpreadBpsForMicroPullback)
            {
                reasons.Add($"spread={state.SpreadBps:F1}>{_maxSpreadBpsForMicroPullback:F1}bps");
            }

            if (coreReady)
            {
                if (readyDuration < _minMicroPullbackReadyTime)
                {
                    reasons.Add($"readyFor={readyDuration.TotalSeconds:F1}s<{_minMicroPullbackReadyTime.TotalSeconds:F1}s");
                }

                if (state.ConsecutiveReadyQuotes < _minMicroPullbackReadyQuotes)
                {
                    reasons.Add($"readyQuotes={state.ConsecutiveReadyQuotes}<{_minMicroPullbackReadyQuotes}");
                }
            }

            _log.Information("[ADAPTIVE-MR] {Sym} micro-pullback-rejected: {Reasons}", symbol, string.Join(", ", reasons));
            state.LastRejectLogUtc = now;
        }

        if (state.LastStrategy != StrategyType.Pullback)
        {
            _log.Information(
                "[ADAPTIVE-MR] {Sym} switching to PULLBACK: atrFrac={AtrFrac:E6} ticks={Ticks} spread={Spread:F1}bps",
                symbol,
                state.AtrFraction,
                state.TicksPerWindow,
                state.SpreadBps);
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

        state.TickTimes.Enqueue(now);
        while (state.TickTimes.Count > 0 && (now - state.TickTimes.Peek()).TotalSeconds > 60)
        {
            state.TickTimes.Dequeue();
        }

        state.TicksPerWindow = state.TickTimes.Count;
        if (q.Bid.HasValue && q.Ask.HasValue && q.Bid.Value > 0m)
        {
            state.SpreadBps = ((q.Ask.Value - q.Bid.Value) / q.Bid.Value) * 10000m;
        }

        if (q.Last.HasValue && state.LastPrice.HasValue && state.LastPrice.Value > 0m)
        {
            var changeFrac = Math.Abs(q.Last.Value - state.LastPrice.Value) / state.LastPrice.Value;
            state.AtrFraction = (state.AtrFraction * 0.9m) + (changeFrac * 0.1m);
        }

        if (q.Last.HasValue)
        {
            state.LastPrice = q.Last.Value;
        }
    }

    private bool IsMicroPullbackEnabledForSymbol(string symbol)
    {
        return _microPullbackStrategy != null &&
            (_microPullbackSymbolAllowlist == null || _microPullbackSymbolAllowlist.Count == 0 || _microPullbackSymbolAllowlist.Contains(symbol));
    }

    private void UpdateReadiness(SymbolMarketState state, DateTime now, bool coreReady)
    {
        if (!coreReady)
        {
            state.ReadySinceUtc = null;
            state.ConsecutiveReadyQuotes = 0;
            return;
        }

        state.ReadySinceUtc ??= now;
        state.ConsecutiveReadyQuotes++;
    }

    private void LogMicroDispatch(string symbol, MarketQuote q, StrategyType preferredStrategy, bool dryRun, bool activePosition)
    {
        if (!_symbolState.TryGetValue(symbol, out var state))
        {
            return;
        }

        if (state.LastDispatchLogUtc.HasValue && (q.TimestampUtc - state.LastDispatchLogUtc.Value).TotalSeconds < 60)
        {
            return;
        }

        _log.Information(
            "[ADAPTIVE-MR-HANDOFF] {Sym} microPullback.OnQuote dispatched preferred={Preferred} dryRun={DryRun} activePosition={ActivePosition} ticks={Ticks} atrFrac={AtrFrac:E6} spread={Spread:F1}bps last={Last}",
            symbol,
            preferredStrategy,
            dryRun,
            activePosition,
            state.TicksPerWindow,
            state.AtrFraction,
            state.SpreadBps,
            q.Last);

        state.LastDispatchLogUtc = q.TimestampUtc;
    }

    private enum StrategyType
    {
        Pullback,
        MicroPullback
    }

    private sealed class SymbolMarketState
    {
        public Queue<DateTime> TickTimes { get; } = new();
        public int TicksPerWindow { get; set; }
        public decimal SpreadBps { get; set; }
        public decimal AtrFraction { get; set; }
        public decimal? LastPrice { get; set; }
        public DateTime? LastRejectLogUtc { get; set; }
        public DateTime? LastDispatchLogUtc { get; set; }
        public DateTime? ReadySinceUtc { get; set; }
        public int ConsecutiveReadyQuotes { get; set; }
        public StrategyType LastStrategy { get; set; } = StrategyType.Pullback;
    }
}
