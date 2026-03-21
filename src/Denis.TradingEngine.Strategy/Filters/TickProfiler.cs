#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Denis.TradingEngine.Core.Trading;
using Denis.TradingEngine.MetricsServer;

namespace Denis.TradingEngine.Strategy.Filters
{
    /// <summary>
    /// Tick profiler: prati statistiku po simbolu i fazi dana.
    /// Omogućava auto-suggestion za MinTicksPerWindow i MaxSpreadBps.
    /// </summary>
    public sealed class TickProfiler
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, SymbolPhaseStats> _stats = new(StringComparer.OrdinalIgnoreCase);
        private readonly TimeSpan _windowSize = TimeSpan.FromMinutes(5); // 5-minutni prozori za tick rate

        /// <summary>
        /// Snapshot statistike za simbol i fazu.
        /// </summary>
        public sealed class SymbolPhaseStats
        {
            public string Symbol { get; init; } = string.Empty;
            public TradingPhase.Phase Phase { get; init; }
            
            // Tick rate tracking (rolling window)
            private readonly Queue<(DateTime Utc, int TickCount)> _tickRateWindows = new();
            private DateTime _lastTickUtc = DateTime.MinValue;
            private int _currentWindowTicks = 0;
            private DateTime _currentWindowStart = DateTime.MinValue;
            
            // Distribucije (za percentilne metrike)
            private readonly List<double> _quoteAges = new(); // u sekundama
            private readonly List<double> _spreadBps = new();
            private readonly List<double> _atrFracs = new();
            
            public void RecordTick(DateTime utcNow, double quoteAgeSeconds, double spreadBps, double atrFrac)
            {
                // Tick rate tracking
                if (_currentWindowStart == DateTime.MinValue)
                {
                    _currentWindowStart = utcNow;
                }
                
                _currentWindowTicks++;
                _lastTickUtc = utcNow;
                
                // Ako je prošlo više od windowSize, započni novi prozor
                if (utcNow - _currentWindowStart >= TimeSpan.FromMinutes(5))
                {
                    _tickRateWindows.Enqueue((_currentWindowStart, _currentWindowTicks));
                    _currentWindowTicks = 0;
                    _currentWindowStart = utcNow;
                    
                    // Održavaj samo poslednjih N prozora
                    while (_tickRateWindows.Count > 100)
                    {
                        _tickRateWindows.Dequeue();
                    }
                }
                
                // Distribucije
                _quoteAges.Add(quoteAgeSeconds);
                _spreadBps.Add(spreadBps);
                _atrFracs.Add(atrFrac);
                
                // Održavaj samo poslednjih N uzoraka
                if (_quoteAges.Count > 1000)
                {
                    _quoteAges.RemoveAt(0);
                    _spreadBps.RemoveAt(0);
                    _atrFracs.RemoveAt(0);
                }
            }
            
            /// <summary>
            /// Računa ticks/sec (p50/p95) iz rolling windows.
            /// </summary>
            public (double P50, double P95) GetTicksPerSecond()
            {
                if (_tickRateWindows.Count == 0)
                    return (0, 0);
                
                var ticksPerSec = _tickRateWindows
                    .Select(w => w.TickCount / 300.0) // 5 minuta = 300 sekundi
                    .OrderBy(x => x)
                    .ToArray();
                
                var p50 = ticksPerSec[ticksPerSec.Length / 2];
                var p95 = ticksPerSec[(int)(ticksPerSec.Length * 0.95)];
                
                return (p50, p95);
            }
            
            /// <summary>
            /// Računa percentilne metrike za distribucije.
            /// </summary>
            public (double P50, double P95) GetPercentiles(List<double> values)
            {
                if (values.Count == 0)
                    return (0, 0);
                
                var sorted = values.OrderBy(x => x).ToArray();
                var p50 = sorted[sorted.Length / 2];
                var p95 = sorted[(int)(sorted.Length * 0.95)];
                
                return (p50, p95);
            }
            
            public (double P50, double P95) GetQuoteAgePercentiles() => GetPercentiles(_quoteAges);
            public (double P50, double P95) GetSpreadBpsPercentiles() => GetPercentiles(_spreadBps);
            public (double P50, double P95) GetAtrFracPercentiles() => GetPercentiles(_atrFracs);
            
            public int SampleCount => _quoteAges.Count;
        }
        
        /// <summary>
        /// Auto-suggestion rezultat za simbol i fazu.
        /// </summary>
        public sealed class AutoSuggestion
        {
            public string Symbol { get; init; } = string.Empty;
            public TradingPhase.Phase Phase { get; init; }
            public int? SuggestedMinTicksPerWindow { get; init; }
            public double? SuggestedMaxSpreadBps { get; init; }
            public bool? ShouldDisableForPhase { get; init; }
            public string Reason { get; init; } = string.Empty;
        }
        
        /// <summary>
        /// Snima tick sa quote informacijama.
        /// </summary>
        public void RecordTick(string symbol, DateTime utcNow, double quoteAgeSeconds, double spreadBps, double atrFrac)
        {
            var phase = TradingPhase.GetPhase(utcNow);
            var key = $"{symbol}:{phase}";
            
            lock (_lock)
            {
                if (!_stats.TryGetValue(key, out var stats))
                {
                    stats = new SymbolPhaseStats
                    {
                        Symbol = symbol,
                        Phase = phase
                    };
                    _stats[key] = stats;
                }
                
                stats.RecordTick(utcNow, quoteAgeSeconds, spreadBps, atrFrac);
            }
            
            // Update Prometheus metrics
            try
            {
                var phaseStr = TradingPhase.ToString(phase);
                TickProfilerMetrics.Instance.RecordTick(symbol, phaseStr, quoteAgeSeconds, spreadBps, atrFrac);
            }
            catch
            {
                // Metrics ne smeju da sruše profiler
            }
        }
        
        /// <summary>
        /// Vraća statistiku za simbol i fazu.
        /// </summary>
        public SymbolPhaseStats? GetStats(string symbol, TradingPhase.Phase phase)
        {
            var key = $"{symbol}:{phase}";
            lock (_lock)
            {
                return _stats.TryGetValue(key, out var stats) ? stats : null;
            }
        }
        
        /// <summary>
        /// Generiše auto-suggestion za simbol i fazu.
        /// </summary>
        public AutoSuggestion? GetAutoSuggestion(string symbol, TradingPhase.Phase phase, int currentMinTicks, double currentMaxSpreadBps)
        {
            var stats = GetStats(symbol, phase);
            if (stats == null || stats.SampleCount < 100)
            {
                return null; // Nema dovoljno podataka
            }
            
            var (ticksP50, ticksP95) = stats.GetTicksPerSecond();
            var (spreadP50, spreadP95) = stats.GetSpreadBpsPercentiles();
            var (quoteAgeP50, quoteAgeP95) = stats.GetQuoteAgePercentiles();
            
            var suggestions = new List<string>();
            int? suggestedMinTicks = null;
            double? suggestedMaxSpread = null;
            bool? shouldDisable = null;
            
            // Suggestion za MinTicksPerWindow
            // Ako je p50 tick rate < 0.1 ticks/sec, predloži veći MinTicksPerWindow
            // Ako je p95 tick rate > 10 ticks/sec, možemo smanjiti MinTicksPerWindow
            if (ticksP50 < 0.1)
            {
                // Vrlo mrtvo tržište - treba veći prozor
                suggestedMinTicks = Math.Max(currentMinTicks, (int)(ticksP50 * 300 * 0.5)); // 50% od očekivanog u 5-min prozoru
                suggestions.Add($"low tick rate (p50={ticksP50:F2}/s), suggest MinTicksPerWindow={suggestedMinTicks}");
            }
            else if (ticksP95 > 10)
            {
                // Vrlo živ tržište - možemo smanjiti
                suggestedMinTicks = Math.Max(10, currentMinTicks - 5);
                suggestions.Add($"high tick rate (p95={ticksP95:F2}/s), suggest MinTicksPerWindow={suggestedMinTicks}");
            }
            
            // Suggestion za MaxSpreadBps
            // Ako je p95 spread > currentMaxSpreadBps * 1.5, predloži povećanje
            // Ako je p50 spread < currentMaxSpreadBps * 0.5, možemo smanjiti
            if (spreadP95 > currentMaxSpreadBps * 1.5)
            {
                suggestedMaxSpread = spreadP95 * 1.2; // 20% buffer iznad p95
                suggestions.Add($"wide spread (p95={spreadP95:F1}bps), suggest MaxSpreadBps={suggestedMaxSpread:F1}");
            }
            else if (spreadP50 < currentMaxSpreadBps * 0.5)
            {
                suggestedMaxSpread = spreadP50 * 1.5; // 50% buffer iznad p50
                suggestions.Add($"tight spread (p50={spreadP50:F1}bps), suggest MaxSpreadBps={suggestedMaxSpread:F1}");
            }
            
            // Suggestion za disable symbol u fazi
            // Ako je tick rate vrlo nizak I spread vrlo širok I quote age vrlo star
            if (ticksP50 < 0.05 && spreadP95 > 50 && quoteAgeP95 > 10)
            {
                shouldDisable = true;
                suggestions.Add($"very poor liquidity (tick={ticksP50:F2}/s, spread={spreadP95:F1}bps, age={quoteAgeP95:F1}s), suggest disable for {phase}");
            }
            
            return new AutoSuggestion
            {
                Symbol = symbol,
                Phase = phase,
                SuggestedMinTicksPerWindow = suggestedMinTicks,
                SuggestedMaxSpreadBps = suggestedMaxSpread,
                ShouldDisableForPhase = shouldDisable,
                Reason = string.Join("; ", suggestions)
            };
        }
        
        /// <summary>
        /// Vraća sve statistike (za reporting).
        /// </summary>
        public IReadOnlyDictionary<string, SymbolPhaseStats> GetAllStats()
        {
            lock (_lock)
            {
                return new Dictionary<string, SymbolPhaseStats>(_stats);
            }
        }
    }
}

