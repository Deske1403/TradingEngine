#nullable enable
namespace Denis.TradingEngine.Exchange.Crypto.Bitfinex.Config;

public sealed class BitfinexApiSettings
{
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ApiSecret { get; set; } = "";
}