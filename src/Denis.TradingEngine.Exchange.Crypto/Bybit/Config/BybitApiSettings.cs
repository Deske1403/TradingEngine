namespace Denis.TradingEngine.Exchange.Crypto.Bybit.Config;

public sealed class BybitApiSettings
{
    public string BaseUrl { get; set; } = "https://api.bybit.com";
    public string ApiKey { get; set; } = "";
    public string ApiSecret { get; set; } = "";

    // Bybit default je 5000ms
    public int RecvWindowMs { get; set; } = 5000;

    // "spot" ili "linear" (perp)
    public string DefaultCategory { get; set; } = "spot";
}
