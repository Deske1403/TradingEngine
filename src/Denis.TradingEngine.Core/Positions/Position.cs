using System;

namespace Denis.TradingEngine.Core.Positions
{
    /// <summary>
    /// Jednostavna, threadsafe klasa koja drži poziciju za jedan simbol:
    /// - Qty (pozitivno = long)
    /// - AveragePrice (USD)
    /// - RealizedPnl (USD) akumulirano
    /// - Metode za primenu buy/sell fillova
    /// </summary>
    public sealed class Position
    {
        private readonly object _lock = new object();

        public string Symbol { get; }
        public decimal Quantity { get; private set; }               // net qty (positive = long)
        public decimal AveragePrice { get; private set; }       // weighted average price for existing qty
        public decimal RealizedPnlUsd { get; private set; }     // realized PnL (sold profits)

        public Position(string symbol)
        {
            Symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
            Quantity = 0;
            AveragePrice = 0m;
            RealizedPnlUsd = 0m;
        }

        /// <summary>
        /// Apply a buy fill: increases qty, updates average price.
        /// costUsd is price * filledQty (for precision use price * qty).
        /// Returns tuple (filledQty, avgPriceAfter, pnlRealizedFromThisFill)
        /// </summary>
        public (decimal qty, decimal avgPrice, decimal realizedPnlChange) ApplyBuy(decimal filledQty, decimal price)
        {
            if (filledQty <= 0) throw new ArgumentOutOfRangeException(nameof(filledQty));
            if (price <= 0m) throw new ArgumentOutOfRangeException(nameof(price));

            lock (_lock)
            {
                decimal realized = 0m;

                // If we are short (Quantity < 0) then buy can close shorts -> realize PnL
                if (Quantity < 0)
                {
                    var closing = Math.Min(filledQty, Math.Abs(Quantity));
                    // profit = (short avgPrice - buy price) * closingQty
                    realized += (AveragePrice - price) * closing;
                    Quantity += closing;
                    filledQty -= closing;
                }

                // Remaining filledQty increases long position
                if (filledQty > 0)
                {
                    var newQty = Quantity + filledQty;
                    // update weighted average price
                    // Math.Max(1, newQty) je OK za IBKR gde su količine >= 1, ali NE za crypto gde su mali brojevi
                    AveragePrice = (Quantity * AveragePrice + filledQty * price) / Math.Max(1, newQty);
                    Quantity = newQty;
                }

                RealizedPnlUsd += realized;
                return (Quantity, AveragePrice, realized);
            }
        }

        /// <summary>
        /// Apply a sell fill: decreases qty, may realize PnL if closing longs.
        /// Returns tuple (qtyAfter, avgPriceAfter, realizedPnlFromThisFill)
        /// </summary>
        public (decimal qty, decimal avgPrice, decimal realizedPnlChange) ApplySell(decimal filledQty, decimal price)
        {
            if (filledQty <= 0) throw new ArgumentOutOfRangeException(nameof(filledQty));
            if (price <= 0m) throw new ArgumentOutOfRangeException(nameof(price));

            lock (_lock)
            {
                decimal realized = 0m;

                // If we have long qty, selling first closes longs -> realize PnL
                if (Quantity > 0)
                {
                    var closing = Math.Min(filledQty, Quantity);
                    // profit = (sell price - avgPrice) * closingQty
                    realized += (price - AveragePrice) * closing;
                    Quantity -= closing;
                    filledQty -= closing;
                }

                // Remaining sold qty increases short position
                if (filledQty > 0)
                {
                    var newQty = Quantity - filledQty; // more negative
                    // when going from long/flat to more short we set avgPrice to sell price for the short leg
                    // Simplify: set AveragePrice to the price of new short (common enough for v1)
                    AveragePrice = price;
                    Quantity = newQty;
                }

                RealizedPnlUsd += realized;
                return (Quantity, AveragePrice, realized);
            }
        }

        /// <summary>
        /// Estimate unrealized PnL given lastPrice (price can be mid/last).
        /// </summary>
        public decimal GetUnrealizedPnl(decimal lastPrice)
        {
            lock (_lock)
            {
                if (Quantity == 0) return 0m;
                // long: (last - avg) * qty; short: (avg - last) * abs(qty)
                return (lastPrice - AveragePrice) * Quantity;
            }
        }


        public void Override(decimal qty, decimal avgPrice)
        {
            lock (_lock)
            {
                Quantity = qty;
                AveragePrice = avgPrice;
                // realized ne diramo
            }
        }


        public void ImportFromExternal(decimal qty, decimal avgPrice)
        {
            lock (_lock)
            {
                Quantity = qty;
                AveragePrice = avgPrice;
                // RealizedPnlUsd ostavljamo kakav jeste – ovo je "state sync", ne reset PnL-a
            }
        }

        /// <summary>
        /// Apply a buy fill for crypto: increases qty, updates average price.
        /// Crypto-specific version that handles fractional quantities correctly.
        /// Returns tuple (filledQty, avgPriceAfter, pnlRealizedFromThisFill)
        /// </summary>
        public (decimal qty, decimal avgPrice, decimal realizedPnlChange) ApplyBuyCrypto(decimal filledQty, decimal price)
        {
            if (filledQty <= 0) throw new ArgumentOutOfRangeException(nameof(filledQty));
            if (price <= 0m) throw new ArgumentOutOfRangeException(nameof(price));

            lock (_lock)
            {
                decimal realized = 0m;

                // If we are short (Quantity < 0) then buy can close shorts -> realize PnL
                if (Quantity < 0)
                {
                    var closing = Math.Min(filledQty, Math.Abs(Quantity));
                    // profit = (short avgPrice - buy price) * closingQty
                    realized += (AveragePrice - price) * closing;
                    Quantity += closing;
                    filledQty -= closing;
                }

                // Remaining filledQty increases long position
                if (filledQty > 0)
                {
                    var newQty = Quantity + filledQty;
                    // update weighted average price
                    // For crypto, we use newQty directly (no Math.Max) since quantities can be < 1 (e.g., 0.063 ETH)
                    AveragePrice = (Quantity * AveragePrice + filledQty * price) / newQty;
                    Quantity = newQty;
                }

                RealizedPnlUsd += realized;
                return (Quantity, AveragePrice, realized);
            }
        }

        /// <summary>
        /// Apply a sell fill for crypto: decreases qty, may realize PnL if closing longs.
        /// Crypto-specific version that handles fractional quantities correctly.
        /// Returns tuple (qtyAfter, avgPriceAfter, realizedPnlFromThisFill)
        /// </summary>
        public (decimal qty, decimal avgPrice, decimal realizedPnlChange) ApplySellCrypto(decimal filledQty, decimal price)
        {
            if (filledQty <= 0) throw new ArgumentOutOfRangeException(nameof(filledQty));
            if (price <= 0m) throw new ArgumentOutOfRangeException(nameof(price));

            lock (_lock)
            {
                decimal realized = 0m;

                // If we have long qty, selling first closes longs -> realize PnL
                if (Quantity > 0)
                {
                    var closing = Math.Min(filledQty, Quantity);
                    // profit = (sell price - avgPrice) * closingQty
                    realized += (price - AveragePrice) * closing;
                    Quantity -= closing;
                    filledQty -= closing;
                }

                // Remaining sold qty increases short position
                if (filledQty > 0)
                {
                    var newQty = Quantity - filledQty; // more negative
                    // when going from long/flat to more short we set avgPrice to sell price for the short leg
                    // Simplify: set AveragePrice to the price of new short (common enough for v1)
                    AveragePrice = price;
                    Quantity = newQty;
                }

                RealizedPnlUsd += realized;
                return (Quantity, AveragePrice, realized);
            }
        }
    }
}