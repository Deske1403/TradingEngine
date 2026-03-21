using System;
using System.Collections.Generic;

namespace Denis.TradingEngine.Strategy.Filters
{
    public sealed class MicroSignalFilterConfig
    {
        // Glavni ON/OFF prekidač za hard reject
        public bool Enabled { get; init; } = true;

        // Minimalni micro trend (na dužem prozoru) – npr. slope20 > 0
        public decimal MinSlope20 { get; init; } = 0.0m;

        // Minimalni atr/price da ne ulazimo u ultra-mrtvo tržište
        public decimal MinAtrFractionOfPrice { get; init; } = 0.000020m; // 0.002%

        // Maksimalan spread u baznim poenima
        public decimal MaxSpreadBps { get; init; } = 14.0m;

        // Minimalan broj tickova u activity prozoru
        public int MinTicksPerWindow { get; init; } = 114;

        // Normalizovan slope (bps per-tick): (slope20 / price) * 10000
        // Ako je setovan, koristi se umesto MinSlope20 (raw $).
        public decimal? MinSlope20Bps { get; init; } = null;

        // Kratki entry momentum (bps per-tick): (slope5 / price) * 10000.
        // Ako je setovan, signal mora da ima bar ovoliki live slope5 na entry-ju.
        public decimal? MinSlope5Bps { get; init; } = null;

    }

    public readonly struct MicroSignalFilterInput
    {
        public string Symbol { get; init; }
        public decimal Price { get; init; }

        // PA indikatori
        public decimal? Slope5 { get; init; }
        public decimal? Slope20 { get; init; }

        // Volatilnost i likvidnost
        public decimal AtrFractionOfPrice { get; init; } // atr / price
        public decimal SpreadBps { get; init; }          // u bps
        public int TicksPerWindow { get; init; }

        // Režim samo ako ti zatreba kasnije (LOW/NORMAL/HIGH)
        public string Regime { get; init; }              // "LOW" / "NORMAL" / "HIGH"

        public DateTime UtcNow { get; init; }
    }

    public readonly struct MicroSignalFilterResult
    {
        public bool Accepted { get; init; }
        public string? Reason { get; init; }
    }

    /// <summary>
    /// Minimalni, deterministički micro-filter:
    /// - blokira očigledno loše setupe (negativan trend, premali ATR, prevelik spread, premalo tickova)
    /// - ako Enabled=false → uvek pušta signal (koristi se samo za logove)
    /// </summary>
    public sealed class MicroSignalFilter
    {
        private readonly MicroSignalFilterConfig _config;

        public MicroSignalFilter(MicroSignalFilterConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public MicroSignalFilterResult Evaluate(MicroSignalFilterInput input)
        {
            // Ako je filter ugasen → uvek Accept
            if (!_config.Enabled)
            {
                return new MicroSignalFilterResult
                {
                    Accepted = true,
                    Reason = null
                };
            }

            var reasons = new List<string>();

            // 1) Kratki momentum: live slope5 na entry-ju.
            if (_config.MinSlope5Bps.HasValue)
            {
                if (!input.Slope5.HasValue || input.Price <= 0m)
                {
                    reasons.Add("slope5=missing");
                }
                else
                {
                    var s5Bps = (input.Slope5.Value / input.Price) * 10000m;
                    if (s5Bps < _config.MinSlope5Bps.Value)
                        reasons.Add($"slope5bps={s5Bps:F3} < min={_config.MinSlope5Bps.Value:F3}");
                }
            }

            // 2) Trend: normalizovan (bps) da radi isto za sve cene
            if (input.Slope20.HasValue && input.Price > 0m)
            {
                var s20 = input.Slope20.Value;

                if (_config.MinSlope20Bps.HasValue)
                {
                    var s20Bps = (s20 / input.Price) * 10000m; // bps per-tick
                    if (s20Bps < _config.MinSlope20Bps.Value)
                        reasons.Add($"slope20bps={s20Bps:F3} < min={_config.MinSlope20Bps.Value:F3} (raw={s20:F6})");

                    // Range-reclaim guard:
                    // In LOW/NORMAL regime reject clearly negative slope20 (dead-zone threshold).
                    if (!string.Equals(input.Regime, "HIGH", StringComparison.OrdinalIgnoreCase) && s20Bps < -0.07m)
                        reasons.Add($"range-slope-gate regime={input.Regime} slope20bps={s20Bps:F3} < floor=-0.070");
                }
                else
                {
                    // Legacy (raw $) – koristi samo ako MinSlope20Bps nije setovan
                    if (s20 < _config.MinSlope20)
                        reasons.Add($"slope20={s20:F6} < min={_config.MinSlope20:F6}");
                }
            }


            // 3) Volatilnost: nećemo u ultra-mrtvo tržište
            if (input.AtrFractionOfPrice < _config.MinAtrFractionOfPrice)
            {
                reasons.Add(
                    $"atrFrac={input.AtrFractionOfPrice:E4} < min={_config.MinAtrFractionOfPrice:E4}"
                );
            }

            // 4) Spread: nećemo u preširok spread
            if (input.SpreadBps > _config.MaxSpreadBps)
            {
                reasons.Add(
                    $"spread={input.SpreadBps:F1}bps > max={_config.MaxSpreadBps:F1}bps"
                );
            }

            // 5) Aktivnost: nećemo tamo gde skoro niko ne trguje
            if (input.TicksPerWindow < _config.MinTicksPerWindow)
            {
                reasons.Add(
                    $"ticks={input.TicksPerWindow} < min={_config.MinTicksPerWindow}"
                );
            }

            var accepted = reasons.Count == 0;

            return new MicroSignalFilterResult
            {
                Accepted = accepted,
                Reason = accepted ? null : string.Join("; ", reasons)
            };
        }
    }
}
