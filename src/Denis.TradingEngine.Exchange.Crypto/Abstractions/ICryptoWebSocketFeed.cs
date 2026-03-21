using Denis.TradingEngine.Core.Crypto;

namespace Denis.TradingEngine.Exchange.Crypto.Abstractions;

/// <summary>
/// Apstraktni WebSocket feed za kripto berzu.
/// Konkretne implementacije (Kraken, Bitfinex, Deribit) 
/// brinu o reconnect-u, ping/pong, resubscribe itd.
/// </summary>
public interface ICryptoWebSocketFeed : IAsyncDisposable, IOrderBookFeed
{
    event Action<TradeTick>? TradeReceived;
    event Action<TickerUpdate>? TickerUpdated;

    Task ConnectAsync(CancellationToken ct);
    Task DisconnectAsync();

    Task SubscribeOrderBookAsync(CryptoSymbol symbol, CancellationToken ct);
    Task SubscribeTradesAsync(CryptoSymbol symbol, CancellationToken ct);
    Task SubscribeTickerAsync(CryptoSymbol symbol, CancellationToken ct);
}