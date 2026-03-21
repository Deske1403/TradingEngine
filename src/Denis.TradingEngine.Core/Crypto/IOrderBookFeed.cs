namespace Denis.TradingEngine.Core.Crypto;

/// <summary>
/// Interfejs za feed koji emituje orderbook update-e.
/// Koristi se u Data projektu bez circular dependency.
/// </summary>
public interface IOrderBookFeed
{
    event Action<OrderBookUpdate>? OrderBookUpdated;
    
    CryptoExchangeId ExchangeId { get; }
}

