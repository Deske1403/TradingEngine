#nullable enable
using Denis.TradingEngine.Core.Orders;
using Denis.TradingEngine.Core.Trading;
using Denis.TradingEngine.Logging;
using Serilog;
using System;
using System.Collections.Generic;

namespace Denis.TradingEngine.Simulation
{
    /// <summary>
    /// Jednostavan PAPER izvršivač koji "puni" LIMIT naloge kada
    /// limit postane tržno ispunjiv prema poslednjem BBO (bid/ask).
    /// - BUY se puni ako LimitPrice >= trenutni ask
    /// - SELL se puni ako LimitPrice <= trenutni bid
    /// Ako nema bid/ask, kao fallback koristi 'last' (oprezno).
    /// </summary>
    public sealed class PaperExecutionSimulator
    {
        public event Action<OrderRequest, decimal>? Filled;
        private readonly ILogger _log = AppLog.ForContext<PaperExecutionSimulator>();

        // Ključ: ticker; čuvamo aktivne LIMIT naloge po simbolu
        private readonly Dictionary<string, List<OrderRequest>> _pending = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Registruj novi LIMIT nalog za potencijalno punjenje.</summary>
        public void Register(OrderRequest req)
        {
            if (req.Type != OrderType.Limit || req.LimitPrice is null || req.Quantity <= 0)
                return;

            if (!_pending.TryGetValue(req.Symbol.Ticker, out var list))
            {
                list = new List<OrderRequest>();
                _pending[req.Symbol.Ticker] = list;
            }
            list.Add(req);
        }

        /// <summary>
        /// Prosledi novi quote. Ako je limit tržno ispunjiv, emituje Filled.
        /// </summary>
        public void OnQuote(Symbol symbol, decimal? last, decimal? bid, decimal? ask, MarketQuote q)
        {


            // skipuj zastarele/delayed quote-ove
            var age = DateTime.UtcNow - q.TimestampUtc;
            if (age > TimeSpan.FromSeconds(5))
            {
                // opciono: nisko-šumni log
                 _log.Debug("[STRAT] stale quote {Sym} age={Age}ms", q.Symbol.Ticker, age.TotalMilliseconds);
                return;
            }

            if (!_pending.TryGetValue(symbol.Ticker, out var list) || list.Count == 0)
                return;

            var snapshot = list.ToArray();
            foreach (var req in snapshot)
            {
                if (req.Type != OrderType.Limit || req.LimitPrice is null)
                    continue;

                var px = DecideFillPrice(req.Side, req.LimitPrice.Value, bid, ask, last);
                if (px is null)
                    continue;

                // log da znamo da se papirni fill desio
                Console.WriteLine($"[PAPER-FILL] {symbol.Ticker} {req.Side} {req.Quantity} @ {px.Value}");

                Filled?.Invoke(req, px.Value);
                list.Remove(req);

                // VAŽNO: ne punimo više od jednog naloga ovim jednim tikom
                break;
            }

            if (list.Count == 0)
                _pending.Remove(symbol.Ticker);
        }

        /// <summary>
        /// Malo realniji PAPER fill:
        /// - BUY: mora da postoji ASK i limit >= ASK → puni na ASK (eventualno +slip)
        /// - SELL: mora da postoji BID i limit <= BID → puni na BID (eventualno -slip)
        /// - ako nema bid/ask → nema fill
        /// </summary>
        private static decimal? DecideFillPrice( OrderSide side, decimal limit, decimal? bid, decimal? ask, decimal? last)
        {
            const decimal tick = 0.01m; // možeš kasnije da primiš iz IBKR-a pa da ne bude hardcode

            if (side == OrderSide.Buy)
            {
                if (!ask.HasValue)
                    return null;

                // da li nas limit stvarno hvata ask
                if (limit >= ask.Value)
                {
                    var fill = ask.Value;

                    // ako baš hoćeš slippage:
                    // var fill = ask.Value + tick;
                    // if (fill > limit) fill = ask.Value; // ne idi preko svog limita

                    return fill;
                }

                return null;
            }
            else // SELL
            {
                if (!bid.HasValue)
                    return null;

                if (limit <= bid.Value)
                {
                    var fill = bid.Value;

                    // slippage u dole ako želiš:
                    // var fill = bid.Value - tick;
                    // if (fill <= 0m || fill < limit) fill = bid.Value;

                    return fill;
                }

                return null;
            }
        }
    }
}