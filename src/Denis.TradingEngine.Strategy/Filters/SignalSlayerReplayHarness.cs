#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Denis.TradingEngine.Core.Trading;
using Denis.TradingEngine.Strategy.Signals;

namespace Denis.TradingEngine.Strategy.Filters
{
    /// <summary>
    /// Mini replay harness for deterministic testing of SignalSlayer.
    /// Records quotes and replays them to verify consistent decision output.
    /// </summary>
    public sealed class SignalSlayerReplayHarness
    {
        private readonly List<RecordedQuote> _recordedQuotes = new();
        private readonly SignalSlayer _slayer;
        private readonly string _strategyName;
        private readonly Dictionary<string, decimal?> _atrBySymbol = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, decimal> _atrFracBySymbol = new(StringComparer.OrdinalIgnoreCase);

        public SignalSlayerReplayHarness(SignalSlayer slayer, string strategyName)
        {
            _slayer = slayer ?? throw new ArgumentNullException(nameof(slayer));
            _strategyName = strategyName ?? throw new ArgumentNullException(nameof(strategyName));
        }

        /// <summary>
        /// Records a quote for later replay.
        /// </summary>
        public void RecordQuote(MarketQuote quote, decimal? atr, decimal atrFrac, int activityTicks, decimal? slope5, decimal? slope20, string regime)
        {
            if (quote?.Symbol == null)
                return;

            _recordedQuotes.Add(new RecordedQuote
            {
                Quote = quote,
                Atr = atr,
                AtrFrac = atrFrac,
                ActivityTicks = activityTicks,
                Slope5 = slope5,
                Slope20 = slope20,
                Regime = regime,
                RecordedAt = DateTime.UtcNow
            });

            // Cache ATR for replay
            if (atr.HasValue)
                _atrBySymbol[quote.Symbol.Ticker] = atr;
            _atrFracBySymbol[quote.Symbol.Ticker] = atrFrac;
        }

        /// <summary>
        /// Replays all recorded quotes and returns decisions.
        /// Useful for verifying deterministic behavior.
        /// </summary>
        public List<ReplayResult> Replay()
        {
            var results = new List<ReplayResult>();

            foreach (var recorded in _recordedQuotes)
            {
                var q = recorded.Quote;
                if (q?.Symbol == null)
                    continue;

                var spreadFrac = SignalHelpers.SpreadFraction(q);
                var spreadBps = spreadFrac.HasValue ? spreadFrac.Value * 10000m : (decimal?)null;

                var ctx = new SignalSlayerContext(
                    Symbol: q.Symbol.Ticker,
                    Price: q.Last ?? (q.Bid.HasValue && q.Ask.HasValue ? (q.Bid.Value + q.Ask.Value) / 2m : 0m),
                    Atr: recorded.Atr,
                    SpreadBps: spreadBps,
                    ActivityTicks: recorded.ActivityTicks,
                    UtcNow: recorded.RecordedAt,
                    StrategyName: _strategyName,
                    Regime: recorded.Regime,
                    Slope5: recorded.Slope5,
                    Slope20: recorded.Slope20,
                    AtrFractionOfPrice: recorded.AtrFrac
                );

                var decision = _slayer.ShouldAccept(ctx);

                results.Add(new ReplayResult
                {
                    Symbol = q.Symbol.Ticker,
                    Price = ctx.Price,
                    Timestamp = recorded.RecordedAt,
                    Decision = decision,
                    Context = ctx
                });
            }

            return results;
        }

        /// <summary>
        /// Clears all recorded quotes.
        /// </summary>
        public void Clear()
        {
            _recordedQuotes.Clear();
            _atrBySymbol.Clear();
            _atrFracBySymbol.Clear();
        }

        /// <summary>
        /// Returns summary statistics from replay.
        /// </summary>
        public ReplaySummary GetSummary()
        {
            var replay = Replay();
            var accepted = replay.Count(r => r.Decision.Accepted);
            var rejected = replay.Count - accepted;

            var reasons = replay
                .Where(r => !r.Decision.Accepted)
                .SelectMany(r => ExtractReasonCodes(r.Decision.Reasons))
                .GroupBy(code => code)
                .ToDictionary(g => g.Key, g => g.Count());

            return new ReplaySummary
            {
                TotalQuotes = replay.Count,
                Accepted = accepted,
                Rejected = rejected,
                RejectionReasons = reasons
            };
        }

        private static List<string> ExtractReasonCodes(SignalBlockReason reasons)
        {
            var codes = new List<string>();

            if (reasons.HasFlag(SignalBlockReason.AtrTooSmall))
                codes.Add(SignalBlockReasonCode.ATR_TOO_LOW);

            if (reasons.HasFlag(SignalBlockReason.AtrTooBig))
                codes.Add(SignalBlockReasonCode.ATR_TOO_HIGH);

            if (reasons.HasFlag(SignalBlockReason.SpreadTooWide))
                codes.Add(SignalBlockReasonCode.SPREAD_TOO_WIDE);

            if (reasons.HasFlag(SignalBlockReason.ActivityTooLow))
                codes.Add(SignalBlockReasonCode.TICKS_TOO_LOW);

            if (reasons.HasFlag(SignalBlockReason.SymbolDailyCapHit))
                codes.Add(SignalBlockReasonCode.CAP_REACHED);

            if (reasons.HasFlag(SignalBlockReason.MicroFilterRejected))
                codes.Add(SignalBlockReasonCode.MICRO_FILTER_REJECTED);

            if (reasons.HasFlag(SignalBlockReason.RejectionSpeed))
                codes.Add(SignalBlockReasonCode.REJECTION_SPEED);

            if (reasons.HasFlag(SignalBlockReason.OpenFakeBreakout))
                codes.Add(SignalBlockReasonCode.OPEN_FAKE_BREAKOUT);

            return codes;
        }

        private sealed class RecordedQuote
        {
            public MarketQuote Quote { get; init; } = null!;
            public decimal? Atr { get; init; }
            public decimal AtrFrac { get; init; }
            public int ActivityTicks { get; init; }
            public decimal? Slope5 { get; init; }
            public decimal? Slope20 { get; init; }
            public string Regime { get; init; } = "";
            public DateTime RecordedAt { get; init; }
        }

        public sealed class ReplayResult
        {
            public string Symbol { get; init; } = "";
            public decimal Price { get; init; }
            public DateTime Timestamp { get; init; }
            public SignalSlayerDecision Decision { get; init; } = null!;
            public SignalSlayerContext Context { get; init; } = null!;
        }

        public sealed class ReplaySummary
        {
            public int TotalQuotes { get; init; }
            public int Accepted { get; init; }
            public int Rejected { get; init; }
            public Dictionary<string, int> RejectionReasons { get; init; } = new();
        }
    }
}

