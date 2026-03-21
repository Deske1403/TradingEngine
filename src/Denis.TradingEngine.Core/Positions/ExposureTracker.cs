#nullable enable
using System;
using System.Collections.Generic;
using Denis.TradingEngine.Core.Trading;

namespace Denis.TradingEngine.Core.Positions
{
    /// <summary>
    /// Thread-safe implementacija izloženosti po simbolu.
    /// Čuva ukupni USD exposure per simbol u odnosu na TotalBaselineCapitalUsd.
    /// </summary>
    public sealed class ExposureTracker : IExposureTracker
    {
        private readonly Dictionary<string, decimal> _exposureUsd =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly object _sync = new();

        public decimal TotalBaselineCapitalUsd { get; }

        public ExposureTracker(decimal totalBaselineCapitalUsd)
        {
            if (totalBaselineCapitalUsd <= 0)
                throw new ArgumentOutOfRangeException(nameof(totalBaselineCapitalUsd), "Baseline capital must be > 0.");

            TotalBaselineCapitalUsd = totalBaselineCapitalUsd;
        }

        public decimal GetExposureUsd(Symbol symbol)
        {
            if (symbol is null) throw new ArgumentNullException(nameof(symbol));

            lock (_sync)
            {
                return _exposureUsd.TryGetValue(symbol.Ticker, out var v) ? v : 0m;
            }
        }

        public decimal GetExposureFraction(Symbol symbol)
        {
            if (symbol is null) throw new ArgumentNullException(nameof(symbol));

            lock (_sync)
            {
                var usd = _exposureUsd.TryGetValue(symbol.Ticker, out var v) ? v : 0m;
                return usd / TotalBaselineCapitalUsd;
            }
        }

        public bool CanAllocate(Symbol symbol, decimal addUsd, decimal maxPerSymbolFraction, out string? reason)
        {
            if (symbol is null) throw new ArgumentNullException(nameof(symbol));

            if (addUsd <= 0m)
            {
                reason = "Non-positive allocation.";
                return false;
            }

            lock (_sync)
            {
                _exposureUsd.TryGetValue(symbol.Ticker, out var current);
                var next = current + addUsd;
                var frac = next / TotalBaselineCapitalUsd;

                if (frac > maxPerSymbolFraction)
                {
                    reason = $"Exposure limit: next={frac:P2} > max={maxPerSymbolFraction:P2}.";
                    return false;
                }

                reason = null;
                return true;
            }
        }

        public void Reserve(Symbol symbol, decimal usd)
        {
            if (symbol is null) throw new ArgumentNullException(nameof(symbol));
            if (usd <= 0m) return;

            lock (_sync)
            {
                _exposureUsd.TryGetValue(symbol.Ticker, out var cur);
                _exposureUsd[symbol.Ticker] = cur + usd;
            }
        }

        public void Release(Symbol symbol, decimal usd)
        {
            if (symbol is null) throw new ArgumentNullException(nameof(symbol));
            if (usd <= 0m) return;

            lock (_sync)
            {
                if (_exposureUsd.TryGetValue(symbol.Ticker, out var cur))
                {
                    var next = cur - usd;
                    if (next <= 0m)
                    {
                        // očisti ključ ako padne na 0 – čistoća mape
                        _exposureUsd.Remove(symbol.Ticker);
                    }
                    else
                    {
                        _exposureUsd[symbol.Ticker] = next;
                    }
                }
            }
        }

        public override string ToString()
        {
            lock (_sync)
            {
                // lagani summary za Heartbeat log ako zatreba
                return $"ExposureTracker(TotalCap={TotalBaselineCapitalUsd:F2}, Symbols={_exposureUsd.Count})";
            }
        }
    }
}