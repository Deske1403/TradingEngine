#nullable enable
using Denis.TradingEngine.Core.Trading;

namespace Denis.TradingEngine.Strategy.Signals
{
    public static class SignalHelpers
    {
        /// <summary>
        /// Procentualni spread (Ask - Bid) / Mid. Ako nema bid/ask, vraća null.
        /// </summary>
        public static decimal? SpreadFraction(MarketQuote q)
        {
            if (!q.Bid.HasValue || !q.Ask.HasValue || !q.Mid.HasValue) return null;
            var spread = q.Ask.Value - q.Bid.Value;
            if (q.Mid.Value == 0m) return null;
            return spread / q.Mid.Value;
        }

        /// <summary>
        /// Limit cena ispod trenutnog mid-a za ulaz (popust).
        /// </summary>
        public static decimal SuggestedLimitBelowMid(MarketQuote q, decimal discountFraction)
        {
            var mid = q.Mid ?? q.Last ?? q.Bid ?? q.Ask ?? 0m;
            return mid * (1m - discountFraction);
        }
    }
}