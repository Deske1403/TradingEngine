#nullable enable
using Denis.TradingEngine.Core.Trading;
using Denis.TradingEngine.Core.Interfaces;
using Denis.TradingEngine.Broker.IBKR.IBKRWrapper;
using Serilog;

// Napomena:
// - Oslanjamo se na IbkrSession.SubscribeStock(int reqId, Symbol symbol)
// - i na event-ove u IbkrDefaultWrapper: TickPriceArrived / TickSizeArrived / TickStringArrived
//   sa potpisima (int reqId, int field, double price / int size / string value)

namespace Denis.TradingEngine.Broker.IBKR
{
    public sealed class MarketDataFeedIbkr : IMarketDataFeed, IDisposable
    {
        public event Action<MarketQuote>? MarketQuoteUpdated;

        private readonly IbkrDefaultWrapper _wrapper;
        private readonly IbkrSession _session;
        private readonly ILogger _log = Log.ForContext<MarketDataFeedIbkr>();

        // Re-subscribe tracking
        private readonly object _sync = new();
        private readonly List<Symbol> _subscribed = new();

        private int _nextReqId = 1000;

        private sealed class QuoteState
        {
            public Symbol Symbol = new Symbol("?");
            public decimal? Bid;
            public decimal? Ask;
            public decimal? Last;
            public decimal? BidSize;
            public decimal? AskSize;
        }

        // reqId -> state
        private readonly Dictionary<int, QuoteState> _state = new();

        // ticker -> reqId (da ne dupliramo pretplate)
        private readonly Dictionary<string, int> _reqByTicker =
            new(StringComparer.OrdinalIgnoreCase);

        public MarketDataFeedIbkr(IbkrDefaultWrapper wrapper, IbkrSession session)
        {
            _wrapper = wrapper ?? throw new ArgumentNullException(nameof(wrapper));
            _session = session ?? throw new ArgumentNullException(nameof(session));

            // Hook na IB wrapper tickove
            _wrapper.TickPriceArrived += OnTickPrice;
            _wrapper.TickSizeArrived += OnTickSize;
            _wrapper.TickStringArrived += OnTickString;

            // Auto re-subscribe na reconnect
            _session.Reconnected += () =>
            {
                // async re-subscribe da ne blokira IBKR pump
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // mali odmor da IBKR završi handshake
                        await Task.Delay(500);
                        ResubscribeAll();
                    }
                    catch (Exception ex)
                    {
                        _log.Error(ex, "[MD] Re-subscribe failed");
                    }
                });
            };
        }

        // ============================
        //  Subscribe / Unsubscribe
        // ============================

        public void SubscribeQuotes(Symbol symbol)
        {
            lock (_sync)
            {
                if (_reqByTicker.ContainsKey(symbol.Ticker))
                    return;

                if (!_subscribed.Exists(s =>
                        s.Ticker.Equals(symbol.Ticker, StringComparison.OrdinalIgnoreCase)))
                {
                    _subscribed.Add(symbol);
                }

                var reqId = ++_nextReqId;

                _reqByTicker[symbol.Ticker] = reqId;
                _state[reqId] = new QuoteState { Symbol = symbol };

                _session.SubscribeStock(reqId, symbol);
            }

            _log.Information("[MD-SUB] {Sym} (reqId={ReqId})",
                symbol.Ticker,
                _reqByTicker[symbol.Ticker]);
        }

        public void UnsubscribeQuotes(Symbol symbol)
        {
            int reqId;
            lock (_sync)
            {
                if (!_reqByTicker.TryGetValue(symbol.Ticker, out reqId))
                    return;

                _reqByTicker.Remove(symbol.Ticker);
                _state.Remove(reqId);

                _subscribed.RemoveAll(s => s.Ticker.Equals(symbol.Ticker, StringComparison.OrdinalIgnoreCase));
            }

            try
            {
                _session.Client.cancelMktData(reqId);
            }
            catch
            {
                // ignore
            }

            _log.Information("[MD-UNSUB] {Sym} (reqId={ReqId})", symbol.Ticker, reqId);
        }

        // ============================
        //  Handleri iz wrapper-a
        // ============================

        // IB TickType (osnovno):
        // 0=BID_SIZE, 1=BID, 2=ASK, 3=ASK_SIZE, 4=LAST, 5=LAST_SIZE
        // Delayed varijante: 66..70 (66=BID, 67=ASK, 68=LAST, 69=BID_SIZE, 70=ASK_SIZE)
        private void OnTickPrice(int reqId, int field, double price)
        {
            QuoteState? s;
            lock (_sync)
            {
                if (!_state.TryGetValue(reqId, out s))
                    return;

                var p = (decimal)price;
                switch (field)
                {
                    case 1:
                    case 66:
                        s.Bid = p;
                        break;
                    case 2:
                    case 67:
                        s.Ask = p;
                        break;
                    case 4:
                    case 68:
                        s.Last = p;
                        break;
                }
            }

            EmitQuoteIfReady(reqId);
        }

        private void OnTickSize(int reqId, int field, int size)
        {
            QuoteState? s;
            lock (_sync)
            {
                if (!_state.TryGetValue(reqId, out s))
                    return;

                var v = (decimal)size;
                switch (field)
                {
                    case 0:
                    case 69:
                        s.BidSize = v;
                        break;
                    case 3:
                    case 70:
                        s.AskSize = v;
                        break;
                }
            }

            EmitQuoteIfReady(reqId);
        }

        // Korisno za RT_VOLUME timestamp; nije obavezno za quotes,
        // ali možemo logovati da vidimo da stream “diše”.
        private void OnTickString(int reqId, int field, string value)
        {
            // RT_VOLUME je najčešće field=48 (ili 77 u novijim mapiranjima);
            // tvoj wrapper već loguje [STR] RT_VOLUME_TS=
            // Ovdje ne menjamo state, samo opcioni debug.
        }

        // ============================
        //  Emisija MarketQuote
        // ============================

        private void EmitQuoteIfReady(int reqId)
        {
            MarketQuote q;
            Symbol sym;

            lock (_sync)
            {
                if (!_state.TryGetValue(reqId, out var s))
                    return;

                var hasAny = (s.Last ?? s.Bid ?? s.Ask) is not null;
                if (!hasAny) return;

                sym = s.Symbol;
                q = new MarketQuote(
                    Symbol: s.Symbol,
                    Bid: s.Bid,
                    Ask: s.Ask,
                    Last: s.Last,
                    BidSize: s.BidSize,
                    AskSize: s.AskSize,
                    TimestampUtc: DateTime.UtcNow
                );
            }

            MarketQuoteUpdated?.Invoke(q);

            if ((q.Last ?? q.Bid ?? q.Ask) is decimal px && px > 0)
            {
                _log.Debug(
                    "[MD-QUOTE] {Sym} last={Last} bid={Bid} ask={Ask} bidSz={BidSz} askSz={AskSz}",
                    sym.Ticker, q.Last, q.Bid, q.Ask, q.BidSize, q.AskSize
                );
            }
        }

        // ============================
        //  Re-subscribe na reconnect
        // ============================

        private void ResubscribeAll()
        {
            List<Symbol> list;

            lock (_sync)
            {
                list = new List<Symbol>(_subscribed);

                // Resetujemo mapiranja i state, id-evi će biti novi
                _reqByTicker.Clear();
                _state.Clear();
            }

            if (list.Count == 0)
            {
                _log.Information("[MD] Reconnected, no symbols to re-subscribe");
                return;
            }

            _log.Information("[MD] Reconnected — re-subscribing {Count} symbols", list.Count);

            foreach (var s in list)
            {
                try
                {
                    SubscribeQuotes(s);
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "[MD] Re-subscribe failed for {Sym}", s.Ticker);
                }
            }
        }

        public void Dispose()
        {
            try
            {
                _wrapper.TickPriceArrived -= OnTickPrice;
                _wrapper.TickSizeArrived -= OnTickSize;
                _wrapper.TickStringArrived -= OnTickString;
            }
            catch
            {
                // ignore
            }
        }
    }
}