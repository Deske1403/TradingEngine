#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Denis.TradingEngine.Strategy.Pullback
{
    // Default set, pun, ne-nullable – ovo mapira na "Defaults" u JSON-u
    public sealed class PullbackDefaultsConfig
    {
        public bool Enabled { get; set; } = true;

        // ATR
        public int AtrPeriod { get; set; } = 14;
        public decimal MinAtrFractionOfPrice { get; set; } = 0.000005m;

        // EMA
        public int EmaFastPeriod { get; set; } = 20;
        public int EmaSlowPeriod { get; set; } = 50;

        // Aktivnost
        public int ActivityWindowSeconds { get; set; } = 60;
        public int MinTicksPerWindow { get; set; } = 3;

        // Likvidnost
        public decimal MaxSpreadBps { get; set; } = 30.0m;

        // Pullback geometrija
        public decimal MinPullbackBelowFastPct { get; set; } = 0.0003m;
        public decimal MaxBelowSlowPct { get; set; } = 0.02m;
        /// <summary>Minimalna dubina pullback-a (pbHi - pbLo) / price; ispod = jitter, ne emituj signal. Default 0.01%.</summary>
        public decimal MinPullbackDepthPct { get; set; } = 0.0001m;

        // Trajanje pullback-a
        public double MinPullbackDurationSec { get; set; } = 3;
        public double MaxPullbackDurationSec { get; set; } = 300;

        // Breakout buffer
        public decimal BreakoutBufferPct { get; set; } = 0.0005m;

        // Razmak između signala
        public double MinTimeBetweenSignalsSec { get; set; } = 60;

        // Debug
        public bool DebugLogging { get; set; } = false;

        // Macro trend gate (optional override; fallback = Trading.TrendMinPoints)
        public int? TrendMinPoints { get; set; }

        // Micro-filter (Entry micro-filter) - defaults
        public bool MicroFilterEnabled { get; set; } = true;
        public decimal? MicroFilterMinSlope5Bps { get; set; } = null;
        public decimal MicroFilterMinSlope20Bps { get; set; } = -0.70m;
        public decimal MicroFilterMinAtrFractionOfPrice { get; set; } = 0.000010m; // 0.001%
        public decimal MicroFilterMaxSpreadBps { get; set; } = 20.0m;
        public int MicroFilterMinTicksPerWindow { get; set; } = 65;
        public decimal? MicroFilterMiddayMaxSpreadBps { get; set; } = null;
        public int? MicroFilterMiddayMinTicksPerWindow { get; set; } = null;
    }

    // Override po simbolu; sve je nullable, osim Symbol
    // JSON: elementi u "Symbols" listi
    public sealed class PullbackSymbolConfig
    {
        public string Symbol { get; set; } = string.Empty;

        public bool? Enabled { get; set; }

        // ATR
        public int? AtrPeriod { get; set; }
        public decimal? MinAtrFractionOfPrice { get; set; }

        // EMA
        public int? EmaFastPeriod { get; set; }
        public int? EmaSlowPeriod { get; set; }

        // Aktivnost
        public int? ActivityWindowSeconds { get; set; }
        public int? MinTicksPerWindow { get; set; }

        // Likvidnost
        public decimal? MaxSpreadBps { get; set; }

        // Pullback geo
        public decimal? MinPullbackBelowFastPct { get; set; }
        public decimal? MaxBelowSlowPct { get; set; }
        public decimal? MinPullbackDepthPct { get; set; }

        // Trajanje pullback-a
        public double? MinPullbackDurationSec { get; set; }
        public double? MaxPullbackDurationSec { get; set; }

        // Breakout buffer
        public decimal? BreakoutBufferPct { get; set; }

        // Razmak između signala
        public double? MinTimeBetweenSignalsSec { get; set; }

        // Debug
        public bool? DebugLogging { get; set; }

        // Macro trend gate (optional override; fallback = Trading.TrendMinPoints)
        public int? TrendMinPoints { get; set; }

        // Micro-filter (Entry micro-filter)
        public bool? MicroFilterEnabled { get; set; }
        public decimal? MicroFilterMinSlope5Bps { get; set; }
        public decimal? MicroFilterMinSlope20Bps { get; set; }
        public decimal? MicroFilterMinAtrFractionOfPrice { get; set; }
        public decimal? MicroFilterMaxSpreadBps { get; set; }
        public int? MicroFilterMinTicksPerWindow { get; set; }
        public decimal? MicroFilterMiddayMaxSpreadBps { get; set; }
        public int? MicroFilterMiddayMinTicksPerWindow { get; set; }
    }

    // Rezolovana config struktura koju koristi strategija u runtime-u (sve non-null)
    public sealed class PullbackRuntimeConfig
    {
        public string Symbol { get; init; } = string.Empty;
        public bool Enabled { get; init; }

        public int AtrPeriod { get; init; }
        public decimal MinAtrFractionOfPrice { get; init; }

        public int EmaFastPeriod { get; init; }
        public int EmaSlowPeriod { get; init; }

        public int ActivityWindowSeconds { get; init; }
        public int MinTicksPerWindow { get; init; }

        public decimal MaxSpreadBps { get; init; }

        public decimal MinPullbackBelowFastPct { get; init; }
        public decimal MaxBelowSlowPct { get; init; }
        public decimal MinPullbackDepthPct { get; init; }

        public double MinPullbackDurationSec { get; init; }
        public double MaxPullbackDurationSec { get; init; }

        public decimal BreakoutBufferPct { get; init; }

        public double MinTimeBetweenSignalsSec { get; init; }

        public bool DebugLogging { get; init; }

        // Macro trend gate (optional override; fallback = Trading.TrendMinPoints)
        public int? TrendMinPoints { get; init; }

        // Micro-filter (Entry micro-filter)
        public bool MicroFilterEnabled { get; init; }
        public decimal? MicroFilterMinSlope5Bps { get; init; }
        public decimal MicroFilterMinSlope20Bps { get; init; }
        public decimal MicroFilterMinAtrFractionOfPrice { get; init; }
        public decimal MicroFilterMaxSpreadBps { get; init; }
        public int MicroFilterMinTicksPerWindow { get; init; }
        public decimal? MicroFilterMiddayMaxSpreadBps { get; init; }
        public int? MicroFilterMiddayMinTicksPerWindow { get; init; }
    }

    // Exchange-specifična konfiguracija (defaults + symbols za jednu menjacnicu)
    public sealed class PullbackExchangeConfig
    {
        public PullbackDefaultsConfig? Defaults { get; set; }
        public List<PullbackSymbolConfig> Symbols { get; set; } = new();
    }

    // Root koji mapira na JSON fajl
    // {
    //   "Defaults": { },  // Globalni defaults (fallback)
    //   "Symbols": [ { "Symbol": "NVDA", }, ],  // Legacy: globalni symbols (fallback)
    //   "Exchanges": {
    //     "Kraken": { "Defaults": { }, "Symbols": [ ... ] },
    //     "Bitfinex": { "Defaults": { }, "Symbols": [ ... ] },
    //     "Deribit": { "Defaults": { }, "Symbols": [ ... ] },
    //     "IBKR": { "Defaults": { }, "Symbols": [ ... ] }
    //   }
    // }
    public sealed class PullbackConfigRoot
    {
        public PullbackDefaultsConfig Defaults { get; set; } = new();
        public List<PullbackSymbolConfig> Symbols { get; set; } = new(); // Legacy support
        public Dictionary<string, PullbackExchangeConfig>? Exchanges { get; set; }

        /// <summary>
        /// Rezolvuje konfiguraciju za exchange i symbol.
        /// Prioritet: Exchange-specific config -> Legacy global Symbols -> Global Defaults
        /// </summary>
        public PullbackRuntimeConfig Resolve(string exchange, string symbol)
        {
            PullbackSymbolConfig? symCfg = null;
            PullbackDefaultsConfig d = Defaults;

            // Normalizuj exchange: "SMART" -> "IBKR" (IBKR koristi "SMART" kao exchange string)
            var normalizedExchange = string.IsNullOrWhiteSpace(exchange) 
                ? string.Empty 
                : exchange.Equals("SMART", StringComparison.OrdinalIgnoreCase) 
                    ? "IBKR" 
                    : exchange;

            // 1) Prvo traži u Exchange-specific konfiguraciji
            if (!string.IsNullOrWhiteSpace(normalizedExchange) && Exchanges != null && 
                Exchanges.TryGetValue(normalizedExchange, out var exchCfg) && exchCfg != null)
            {
                // Koristi exchange-specific defaults ako postoje
                if (exchCfg.Defaults != null)
                    d = exchCfg.Defaults;

                // Traži symbol u exchange-specific listi
                symCfg = exchCfg.Symbols
                    .FirstOrDefault(s => string.Equals(s.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
            }

            // 2) Fallback na legacy global Symbols ako nije pronađen u exchange-specific
            if (symCfg == null)
            {
                symCfg = Symbols
                    .FirstOrDefault(s => string.Equals(s.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
            }

            // 3) Ako nema symbol config, koristi samo defaults
            if (symCfg == null)
            {
                return CreateRuntimeConfig(symbol, d, null);
            }

            return CreateRuntimeConfig(symbol, d, symCfg);
        }

        /// <summary>
        /// Legacy overload - koristi samo symbol (bez exchange). Koristi globalne defaults i symbols.
        /// </summary>
        public PullbackRuntimeConfig Resolve(string symbol)
        {
            return Resolve(string.Empty, symbol);
        }

        private static PullbackRuntimeConfig CreateRuntimeConfig(
            string symbol, 
            PullbackDefaultsConfig defaults, 
            PullbackSymbolConfig? symbolConfig)
        {
            if (symbolConfig == null)
            {
                return new PullbackRuntimeConfig
                {
                    Symbol = symbol,
                    Enabled = defaults.Enabled,
                    AtrPeriod = defaults.AtrPeriod,
                    MinAtrFractionOfPrice = defaults.MinAtrFractionOfPrice,
                    EmaFastPeriod = defaults.EmaFastPeriod,
                    EmaSlowPeriod = defaults.EmaSlowPeriod,
                    ActivityWindowSeconds = defaults.ActivityWindowSeconds,
                    MinTicksPerWindow = defaults.MinTicksPerWindow,
                    MaxSpreadBps = defaults.MaxSpreadBps,
                    MinPullbackBelowFastPct = defaults.MinPullbackBelowFastPct,
                    MaxBelowSlowPct = defaults.MaxBelowSlowPct,
                    MinPullbackDepthPct = defaults.MinPullbackDepthPct,
                    MinPullbackDurationSec = defaults.MinPullbackDurationSec,
                    MaxPullbackDurationSec = defaults.MaxPullbackDurationSec,
                    BreakoutBufferPct = defaults.BreakoutBufferPct,
                    MinTimeBetweenSignalsSec = defaults.MinTimeBetweenSignalsSec,
                    DebugLogging = defaults.DebugLogging,
                    TrendMinPoints = defaults.TrendMinPoints,
                    MicroFilterEnabled = defaults.MicroFilterEnabled,
                    MicroFilterMinSlope5Bps = defaults.MicroFilterMinSlope5Bps,
                    MicroFilterMinSlope20Bps = defaults.MicroFilterMinSlope20Bps,
                    MicroFilterMinAtrFractionOfPrice = defaults.MicroFilterMinAtrFractionOfPrice,
                    MicroFilterMaxSpreadBps = defaults.MicroFilterMaxSpreadBps,
                    MicroFilterMinTicksPerWindow = defaults.MicroFilterMinTicksPerWindow,
                    MicroFilterMiddayMaxSpreadBps = defaults.MicroFilterMiddayMaxSpreadBps,
                    MicroFilterMiddayMinTicksPerWindow = defaults.MicroFilterMiddayMinTicksPerWindow
                };
            }

            return new PullbackRuntimeConfig
            {
                Symbol = symbol,
                Enabled = symbolConfig.Enabled ?? defaults.Enabled,
                AtrPeriod = symbolConfig.AtrPeriod ?? defaults.AtrPeriod,
                MinAtrFractionOfPrice = symbolConfig.MinAtrFractionOfPrice ?? defaults.MinAtrFractionOfPrice,
                EmaFastPeriod = symbolConfig.EmaFastPeriod ?? defaults.EmaFastPeriod,
                EmaSlowPeriod = symbolConfig.EmaSlowPeriod ?? defaults.EmaSlowPeriod,
                ActivityWindowSeconds = symbolConfig.ActivityWindowSeconds ?? defaults.ActivityWindowSeconds,
                MinTicksPerWindow = symbolConfig.MinTicksPerWindow ?? defaults.MinTicksPerWindow,
                MaxSpreadBps = symbolConfig.MaxSpreadBps ?? defaults.MaxSpreadBps,
                MinPullbackBelowFastPct = symbolConfig.MinPullbackBelowFastPct ?? defaults.MinPullbackBelowFastPct,
                MaxBelowSlowPct = symbolConfig.MaxBelowSlowPct ?? defaults.MaxBelowSlowPct,
                MinPullbackDepthPct = symbolConfig.MinPullbackDepthPct ?? defaults.MinPullbackDepthPct,
                MinPullbackDurationSec = symbolConfig.MinPullbackDurationSec ?? defaults.MinPullbackDurationSec,
                MaxPullbackDurationSec = symbolConfig.MaxPullbackDurationSec ?? defaults.MaxPullbackDurationSec,
                BreakoutBufferPct = symbolConfig.BreakoutBufferPct ?? defaults.BreakoutBufferPct,
                MinTimeBetweenSignalsSec = symbolConfig.MinTimeBetweenSignalsSec ?? defaults.MinTimeBetweenSignalsSec,
                DebugLogging = symbolConfig.DebugLogging ?? defaults.DebugLogging,
                TrendMinPoints = symbolConfig.TrendMinPoints ?? defaults.TrendMinPoints,
                MicroFilterEnabled = symbolConfig.MicroFilterEnabled ?? defaults.MicroFilterEnabled,
                MicroFilterMinSlope5Bps = symbolConfig.MicroFilterMinSlope5Bps ?? defaults.MicroFilterMinSlope5Bps,
                MicroFilterMinSlope20Bps = symbolConfig.MicroFilterMinSlope20Bps ?? defaults.MicroFilterMinSlope20Bps,
                MicroFilterMinAtrFractionOfPrice = symbolConfig.MicroFilterMinAtrFractionOfPrice ?? defaults.MicroFilterMinAtrFractionOfPrice,
                MicroFilterMaxSpreadBps = symbolConfig.MicroFilterMaxSpreadBps ?? defaults.MicroFilterMaxSpreadBps,
                MicroFilterMinTicksPerWindow = symbolConfig.MicroFilterMinTicksPerWindow ?? defaults.MicroFilterMinTicksPerWindow,
                MicroFilterMiddayMaxSpreadBps = symbolConfig.MicroFilterMiddayMaxSpreadBps ?? defaults.MicroFilterMiddayMaxSpreadBps,
                MicroFilterMiddayMinTicksPerWindow = symbolConfig.MicroFilterMiddayMinTicksPerWindow ?? defaults.MicroFilterMiddayMinTicksPerWindow
            };
        }
    }
}
