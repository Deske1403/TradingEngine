#nullable enable
using Denis.TradingEngine.Core.Trading;
using IBApi;

namespace Denis.TradingEngine.Broker.IBKR
{
    public sealed record IbkrInstrument(
        string SecType,
        string Currency,
        string Exchange,
        string? PrimaryExch,
        int? ConId = null
    );

    public static class IbkrInstrumentMap
    {
        private static readonly Dictionary<string, IbkrInstrument> _byTicker =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // US akcije
                ["NVDA"] = new(
                    SecType: "STK",
                    Currency: "USD",
                    Exchange: "SMART",
                    PrimaryExch: "NASDAQ"
                ),

                ["AMD"] = new(
                    SecType: "STK",
                    Currency: "USD",
                    Exchange: "SMART",
                    PrimaryExch: "NASDAQ"
                    // ConId ćemo dodati kasnije kad izvučeš iz contractDetails
                ),

                ["INTC"] = new(
                    SecType: "STK",
                    Currency: "USD",
                    Exchange: "SMART",
                    PrimaryExch: "NASDAQ"
                ),

                ["PLTR"] = new(
                    SecType: "STK",
                    Currency: "USD",
                    Exchange: "SMART",
                    PrimaryExch: "NYSE" // PLTR se trguje na NYSE
                )
            };

        public static IbkrInstrument Resolve(Symbol s)
        {
            if (_byTicker.TryGetValue(s.Ticker, out var cfg))
                return cfg;

            // default za US stock ako nema ništa u mapi
            return new IbkrInstrument(
                SecType: "STK",
                Currency: "USD",
                Exchange: "SMART",
                PrimaryExch: "NASDAQ"
            );
        }

        public static Contract ToContract(Symbol s)
        {
            var cfg = Resolve(s);

            return new Contract
            {
                ConId = cfg.ConId ?? 0,
                Symbol = s.Ticker,
                SecType = cfg.SecType,
                Currency = cfg.Currency,
                Exchange = cfg.Exchange,
                PrimaryExch = cfg.PrimaryExch
            };
        }
    }
}