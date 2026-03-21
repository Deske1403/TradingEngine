using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Denis.TradingEngine.Core.Positions
{
    public sealed class PositionBook
    {
        // Jedina istina: običan Dictionary pod jednim lock-om
        private readonly Dictionary<string, Position> _positions =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly object _sync = new();

        private static readonly ILogger _log = Log.ForContext<PositionBook>();

        /// <summary>
        /// Vraća postojeću ili kreira novu poziciju za simbol.
        /// </summary>
        public Position GetOrCreate(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                throw new ArgumentNullException(nameof(symbol));

            lock (_sync)
            {
                if (!_positions.TryGetValue(symbol, out var pos))
                {
                    pos = new Position(symbol);
                    _positions[symbol] = pos;
                }

                return pos;
            }
        }

        /// <summary>
        /// Snapshot svih pozicija (kopija).
        /// </summary>
        public IEnumerable<Position> AllPositions
        {
            get
            {
                lock (_sync)
                {
                    return _positions.Values.ToArray();
                }
            }
        }

        public bool TryGet(string symbol, out Position pos)
        {
            if (string.IsNullOrWhiteSpace(symbol))
            {
                pos = null!;
                return false;
            }

            lock (_sync)
            {
                return _positions.TryGetValue(symbol, out pos!);
            }
        }

        public decimal ApplyBuyFill(string symbol, decimal qty, decimal price)
        {
            if (qty <= 0m)
                throw new ArgumentOutOfRangeException(nameof(qty));
            if (price <= 0m)
                throw new ArgumentOutOfRangeException(nameof(price));

            lock (_sync)
            {
                var p = GetOrCreate(symbol);
                var (_, _, realized) = p.ApplyBuy(qty, price);
                return realized;
            }
        }

        public decimal ApplySellFill(string symbol, decimal qty, decimal price)
        {
            if (qty <= 0m)
                throw new ArgumentOutOfRangeException(nameof(qty));
            if (price <= 0m)
                throw new ArgumentOutOfRangeException(nameof(price));

            lock (_sync)
            {
                var p = GetOrCreate(symbol);
                var (_, _, realized) = p.ApplySell(qty, price);
                return realized;
            }
        }

        /// <summary>
        /// Apply a buy fill for crypto: handles fractional quantities correctly.
        /// </summary>
        public decimal ApplyBuyFillCrypto(string symbol, decimal qty, decimal price)
        {
            if (qty <= 0m)
                throw new ArgumentOutOfRangeException(nameof(qty));
            if (price <= 0m)
                throw new ArgumentOutOfRangeException(nameof(price));

            lock (_sync)
            {
                var p = GetOrCreate(symbol);
                var (_, _, realized) = p.ApplyBuyCrypto(qty, price);
                return realized;
            }
        }

        /// <summary>
        /// Apply a sell fill for crypto: handles fractional quantities correctly.
        /// </summary>
        public decimal ApplySellFillCrypto(string symbol, decimal qty, decimal price)
        {
            if (qty <= 0m)
                throw new ArgumentOutOfRangeException(nameof(qty));
            if (price <= 0m)
                throw new ArgumentOutOfRangeException(nameof(price));

            lock (_sync)
            {
                var p = GetOrCreate(symbol);
                var (_, _, realized) = p.ApplySellCrypto(qty, price);
                return realized;
            }
        }

        public decimal GetTotalRealizedPnl()
        {
            lock (_sync)
            {
                decimal sum = 0m;
                foreach (var p in _positions.Values)
                {
                    sum += p.RealizedPnlUsd;
                }
                return sum;
            }
        }

        public decimal GetTotalUnrealizedPnl(IDictionary<string, decimal> lastPrices)
        {
            if (lastPrices is null)
                throw new ArgumentNullException(nameof(lastPrices));

            lock (_sync)
            {
                decimal sum = 0m;

                foreach (var kv in lastPrices)
                {
                    if (_positions.TryGetValue(kv.Key, out var p))
                    {
                        sum += p.GetUnrealizedPnl(kv.Value);
                    }
                }

                return sum;
            }
        }

        public Position? Get(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return null;

            lock (_sync)
            {
                _positions.TryGetValue(symbol, out var p);
                return p;
            }
        }

        /// <summary>
        /// Snapshot svih pozicija kao lista (kopija).
        /// </summary>
        public IReadOnlyList<Position> Snapshot()
        {
            lock (_sync)
            {
                return _positions.Values.ToList();
            }
        }

        /// <summary>
        /// Import spoljne pozicije (npr. IBKR) – hard override u naš book.
        /// </summary>
        public void Import(string symbol, decimal qty, decimal avgPrice)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return;

            bool removed = false;
            bool upserted = false;

            lock (_sync)
            {
                if (qty == 0m)
                {
                    removed = _positions.Remove(symbol);
                    // Ako nema pozicije, nema šta da radimo.
                }
                else
                {
                    if (_positions.TryGetValue(symbol, out var existing))
                    {
                        existing.ImportFromExternal(qty, avgPrice);
                    }
                    else
                    {
                        var pos = new Position(symbol);
                        pos.ImportFromExternal(qty, avgPrice);
                        _positions[symbol] = pos;
                    }

                    upserted = true;
                }
            }

            if (removed)
            {
                _log.Information("[POS-IMPORT] {Sym} qty=0 -> REMOVED from book", symbol);
                return;
            }

            if (upserted)
            {
                _log.Information("[POS-IMPORT] {Sym} qty={Qty} avg={Avg}", symbol, qty, avgPrice);
            }
        }

    }
}