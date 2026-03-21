using System.Text.Json;

namespace Denis.TradingEngine.Exchange.Crypto.Kraken;

public static class KrakenSubscriptionModels
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string BuildOrderBookSubscribe(string pair)
    {
        return JsonSerializer.Serialize(new
        {
            @event = "subscribe",
            pair = new[] { pair },
            subscription = new
            {
                name = "book",
                depth = 10
            }
        }, JsonOptions);
    }

    public static string BuildTradesSubscribe(string pair)
    {
        return JsonSerializer.Serialize(new
        {
            @event = "subscribe",
            pair = new[] { pair },
            subscription = new
            {
                name = "trade"
            }
        }, JsonOptions);
    }

    public static string BuildTickerSubscribe(string pair)
    {
        return JsonSerializer.Serialize(new
        {
            @event = "subscribe",
            pair = new[] { pair },
            subscription = new
            {
                name = "ticker"
            }
        }, JsonOptions);
    }
}