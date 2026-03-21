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

namespace Denis.TradingEngine.Exchange.Crypto.Bitfinex;

/// <summary>
/// Adapter koji prevodi Bitfinex TickerUpdate u MarketQuote,
/// i implementira IMarketDataFeed interfejs za glavni engine.
/// </summary>
public sealed class BitfinexMarketDataFeed : IMarketDataFeed
{
    private readonly BitfinexWebSocketFeed _ws;
    private readonly ILogger _log;

    private readonly object _sync = new();

    // CryptoSymbol -> core Symbol
    private readonly Dictionary<CryptoSymbol, Symbol> _engineSymbolByCrypto = new();

    // core Symbol -> CryptoSymbol
    private readonly Dictionary<Symbol, CryptoSymbol> _cryptoByEngineSymbol = new();

    public event Action<MarketQuote>? MarketQuoteUpdated;

    public BitfinexMarketDataFeed(BitfinexWebSocketFeed ws, ILogger log)
    {
        _ws = ws ?? throw new ArgumentNullException(nameof(ws));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        _ws.TickerUpdated += OnTickerUpdated;
    }

    public void AddSymbol(CryptoSymbol cryptoSymbol)
    {
        var engineSymbol = new Symbol(
            Ticker: cryptoSymbol.PublicSymbol,
            Currency: cryptoSymbol.QuoteAsset,
            Exchange: cryptoSymbol.ExchangeId.ToString());

        lock (_sync)
        {
            _engineSymbolByCrypto[cryptoSymbol] = engineSymbol;
            _cryptoByEngineSymbol[engineSymbol] = cryptoSymbol;
        }

        _log.Information(
            "[BITFINEX-MD] Registrujem simbol: {Crypto} → {Engine}",
            cryptoSymbol,
            engineSymbol);
    }

    public void SubscribeQuotes(Symbol symbol)
    {
        CryptoSymbol cryptoSymbol;

        lock (_sync)
        {
            if (!_cryptoByEngineSymbol.TryGetValue(symbol, out cryptoSymbol!))
            {
                _log.Error(
                    "[BITFINEX-MD] SubscribeQuotes: nema mapiranog CryptoSymbol za {Symbol}",
                    symbol);
                return;
            }
        }

        _log.Information(
            "[BITFINEX-MD] Subscribujem ticker za {EngineSymbol} ({CryptoSymbol})",
            symbol,
            cryptoSymbol);

        _ = SubscribeTickerInternalAsync(cryptoSymbol);
    }

    public void UnsubscribeQuotes(Symbol symbol)
    {
        // Bitfinex NEMA unsubscribe (WS v2) – ignorišemo
        _log.Debug(
            "[BITFINEX-MD] UnsubscribeQuotes za {Ticker} (nije implementirano ka WS).",
            symbol.Ticker);
    }

    private async Task SubscribeTickerInternalAsync(CryptoSymbol cryptoSymbol)
    {
        try
        {
            await _ws.SubscribeTickerAsync(cryptoSymbol, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[BITFINEX-MD] Greška pri SubscribeTickerAsync za {Symbol}", cryptoSymbol);
        }
    }

    private void OnTickerUpdated(TickerUpdate t)
    {
        Symbol engineSymbol;

        lock (_sync)
        {
            if (!_engineSymbolByCrypto.TryGetValue(t.Symbol, out engineSymbol!))
            {
                _log.Debug(
                    "[BITFINEX-MD] TickerUpdate za {Symbol}, ali nije registrovan u mapiranju",
                    t.Symbol);
                return;
            }
        }

        var q = new MarketQuote(
            Symbol: engineSymbol,
            Bid: t.Bid,
            Ask: t.Ask,
            Last: t.Last,
            BidSize: t.BidSize,
            AskSize: t.AskSize,
            TimestampUtc: t.TimestampUtc);

        try
        {
            MarketQuoteUpdated?.Invoke(q);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[BITFINEX-MD] MarketQuoteUpdated handler threw for {Ticker}", engineSymbol.Ticker);
        }
    }
}
