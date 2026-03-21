#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Denis.TradingEngine.Core.Trading;

namespace Denis.TradingEngine.Core.Risk
{
    /// <summary>
    /// In-memory implementacija dnevnih kočnica. Reset po UTC danu.
    /// </summary>
    public sealed class DayGuards : IDayGuardService
    {
        private readonly DayGuardLimits _limits;

        private DateOnly _day = DateOnly.FromDateTime(DateTime.UtcNow);
        private readonly Dictionary<string, int> _tradesPerSymbol = new(StringComparer.OrdinalIgnoreCase);
        private int _tradesTotal;
        private decimal _realizedPnlUsd;
        private bool _dayLocked;

        public DayGuards(DayGuardLimits limits)
        {
            _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        }

        public void ResetIfNewDay(DateTime utcNow)
        {
            var nowDay = DateOnly.FromDateTime(utcNow);
            if (nowDay != _day)
            {
                _day = nowDay;
                _tradesPerSymbol.Clear();
                _tradesTotal = 0;
                _realizedPnlUsd = 0m;
                _dayLocked = false;
            }
        }

        public bool CanTrade(Symbol symbol, DateTime utcNow, out string? reason)
        {
            ResetIfNewDay(utcNow);

            if (_dayLocked)
            {
                reason = "DayLocked: daily loss limit reached.";
                return false;
            }

            // limit po simbolu
            _tradesPerSymbol.TryGetValue(symbol.Ticker, out var perSymCount);
            if (_limits.MaxTradesPerSymbol > 0 && perSymCount >= _limits.MaxTradesPerSymbol)
            {
                reason = $"MaxTradesPerSymbol reached ({symbol.Ticker}={perSymCount}).";
                return false;
            }

            // globalni limit
            if (_limits.MaxTradesTotal > 0 && _tradesTotal >= _limits.MaxTradesTotal)
            {
                reason = $"MaxTradesTotal reached ({_tradesTotal}).";
                return false;
            }

            // dnevni gubitak
            if (_limits.DailyLossStopUsd > 0 && _realizedPnlUsd <= -_limits.DailyLossStopUsd)
            {
                _dayLocked = true;
                reason = "DayLocked: daily loss threshold tripped.";
                return false;
            }

            reason = null;
            return true;
        }

        public void OnOrderPlaced(Symbol symbol, DateTime utcNow)
        {
            ResetIfNewDay(utcNow);
            var beforeTotal = _tradesTotal;
            _tradesPerSymbol.TryGetValue(symbol.Ticker, out var cur);
            var beforePerSym = cur;
            
            _tradesTotal++;
            _tradesPerSymbol[symbol.Ticker] = cur + 1;
            
            // Debug log - može se ukloniti kasnije
            System.Diagnostics.Debug.WriteLine($"[DayGuards] OnOrderPlaced {symbol.Ticker}: Total {beforeTotal}->{_tradesTotal} PerSymbol {beforePerSym}->{cur + 1}");
        }

        public void OnEntryOrderVoided(Symbol symbol, DateTime utcNow)
        {
            ResetIfNewDay(utcNow);

            if (_tradesTotal > 0)
                _tradesTotal--;

            if (_tradesPerSymbol.TryGetValue(symbol.Ticker, out var cur))
            {
                if (cur <= 1)
                    _tradesPerSymbol.Remove(symbol.Ticker);
                else
                    _tradesPerSymbol[symbol.Ticker] = cur - 1;
            }
        }

        public void OnRealizedPnl(decimal realizedPnlUsd, DateTime utcNow)
        {
            ResetIfNewDay(utcNow);
            _realizedPnlUsd += realizedPnlUsd;

            if (_limits.DailyLossStopUsd > 0 && _realizedPnlUsd <= -_limits.DailyLossStopUsd)
                _dayLocked = true;
        }

        /// <summary>
        /// Restorira stanje iz baze (koristi se pri restart-u aplikacije).
        /// </summary>
        public void RestoreState(
            IReadOnlyDictionary<string, int> tradesPerSymbol,
            int tradesTotal,
            decimal realizedPnlUsd,
            DateTime utcNow)
        {
            ResetIfNewDay(utcNow); // osiguraj da je dan ispravan
            
            // Restoriraj trade counts
            _tradesPerSymbol.Clear();
            foreach (var kvp in tradesPerSymbol)
            {
                _tradesPerSymbol[kvp.Key] = kvp.Value;
            }
            
            _tradesTotal = tradesTotal;
            _realizedPnlUsd = realizedPnlUsd;
            
            // Proveri da li je day locked
            if (_limits.DailyLossStopUsd > 0 && _realizedPnlUsd <= -_limits.DailyLossStopUsd)
            {
                _dayLocked = true;
            }
        }

        public IReadOnlyDictionary<string, int> CurrentTradeCountPerSymbol => _tradesPerSymbol;
        public int CurrentTradeCountTotal => _tradesTotal;
        public decimal CurrentRealizedPnlUsd => _realizedPnlUsd;
        public bool IsDayLocked => _dayLocked;
    }
}
