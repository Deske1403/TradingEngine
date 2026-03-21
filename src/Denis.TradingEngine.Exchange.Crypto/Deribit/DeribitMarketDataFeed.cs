#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Denis.TradingEngine.Core.Interfaces;
using Denis.TradingEngine.Core.Trading;
using Denis.TradingEngine.Core.Crypto;
using Denis.TradingEngine.Exchange.Crypto.Abstractions;
using Serilog;

namespace Denis.TradingEngine.Exchange.Crypto.Deribit
{
    /// <summary>
    /// Adapter: DeribitWebSocketFeed -> IMarketDataFeed (MarketQuote za engine).
    /// </summary>
    public sealed class DeribitMarketDataFeed : IMarketDataFeed
    {
        private readonly DeribitWebSocketFeed _ws;
        private readonly ILogger _log;

        // "BTCUSD" -> CryptoSymbol (Deribit meta)
        private readonly Dictionary<string, CryptoSymbol> _symbolsByTicker =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly object _sync = new();
        
        // Cache poslednjeg orderbook-a po simbolu (za bid/ask size)
        private readonly Dictionary<string, OrderBookUpdate> _lastOrderBook = 
            new(StringComparer.OrdinalIgnoreCase);

        public event Action<MarketQuote>? MarketQuoteUpdated;

        public DeribitMarketDataFeed(DeribitWebSocketFeed ws, ILogger log)
        {
            _ws = ws ?? throw new ArgumentNullException(nameof(ws));
            _log = log ?? throw new ArgumentNullException(nameof(log));

            _ws.TickerUpdated += OnTickerUpdated;
            _ws.OrderBookUpdated += OnOrderBookUpdated;
        }
        
        private void OnOrderBookUpdated(OrderBookUpdate ob)
        {
            lock (_sync)
            {
                _lastOrderBook[ob.Symbol.PublicSymbol] = ob;
            }
        }

        /// <summary>
        /// Dodaje kripto simbol u lokalni mapping (pozivaš iz programa kada čitaš config).
        /// </summary>
        public void AddSymbol(CryptoSymbol symbol)
        {
            var publicTicker = symbol.PublicSymbol;

            lock (_sync)
            {
                _symbolsByTicker[publicTicker] = symbol;
            }

            _log.Information(
                "[DERIBIT-MD] Dodajem simbol {Symbol} (publicTicker={Ticker})",
                symbol,
                publicTicker);
        }

        public void SubscribeQuotes(Symbol symbol)
        {
            CryptoSymbol? cryptoSymbol;

            lock (_sync)
            {
                if (!_symbolsByTicker.TryGetValue(symbol.Ticker, out cryptoSymbol))
                {
                    _log.Warning(
                        "[DERIBIT-MD] SubscribeQuotes: ne poznajem ticker={Ticker} za Exchange={Ex}",
                        symbol.Ticker,
                        symbol.Exchange);
                    return;
                }
            }

            _ = SubscribeTickerInternalAsync(cryptoSymbol);
        }

        public void UnsubscribeQuotes(Symbol symbol)
        {
            // Za sada ne radimo unsubscribe ka Deribitu
            _log.Debug(
                "[DERIBIT-MD] UnsubscribeQuotes za {Ticker} (nije implementirano ka WS).",
                symbol.Ticker);
        }

        private async Task SubscribeTickerInternalAsync(CryptoSymbol cryptoSymbol)
        {
            try
            {
                _log.Information("[DERIBIT-MD] Subscribujem ticker za {Symbol}", cryptoSymbol);
                await _ws.SubscribeTickerAsync(cryptoSymbol, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[DERIBIT-MD] Greška pri SubscribeTickerAsync za {Symbol}", cryptoSymbol);
            }
        }

        private void OnTickerUpdated(TickerUpdate tick)
        {
            var publicTicker = tick.Symbol.PublicSymbol;

            var coreSymbol = new Symbol(
                Ticker: publicTicker,
                Currency: tick.Symbol.QuoteAsset,
                Exchange: tick.Symbol.ExchangeId.ToString());

            // Pokušaj da uzmeš bid/ask size iz orderbook-a ako nema u ticker-u
            decimal? bidSize = tick.BidSize;
            decimal? askSize = tick.AskSize;
            
            if (!bidSize.HasValue || !askSize.HasValue)
            {
                lock (_sync)
                {
                    if (_lastOrderBook.TryGetValue(publicTicker, out var ob))
                    {
                        // Uzmi prvi bid/ask size iz orderbook-a
                        if (!bidSize.HasValue && ob.Bids.Count > 0)
                        {
                            bidSize = ob.Bids[0].Quantity;
                        }
                        if (!askSize.HasValue && ob.Asks.Count > 0)
                        {
                            askSize = ob.Asks[0].Quantity;
                        }
                    }
                }
            }

            var quote = new MarketQuote(
                Symbol: coreSymbol,
                Bid: tick.Bid,
                Ask: tick.Ask,
                Last: tick.Last,
                BidSize: bidSize,
                AskSize: askSize,
                TimestampUtc: tick.TimestampUtc);

            try
            {
                MarketQuoteUpdated?.Invoke(quote);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[DERIBIT-MD] MarketQuoteUpdated handler threw for {Ticker}", coreSymbol.Ticker);
            }
        }
    }
}
