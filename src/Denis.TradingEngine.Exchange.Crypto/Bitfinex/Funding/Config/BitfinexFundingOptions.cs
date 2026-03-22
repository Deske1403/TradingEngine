#nullable enable

namespace Denis.TradingEngine.Exchange.Crypto.Bitfinex.Funding.Config;

public sealed class BitfinexFundingOptions
{
    public static readonly IReadOnlyList<string> DefaultPreferredSymbols = new[]
    {
        "fUSD",
        "fUST"
    };

    public bool Enabled { get; set; }

    public bool DryRun { get; set; } = true;

    public string ApiKeyOverride { get; set; } = string.Empty;

    public string ApiSecretOverride { get; set; } = string.Empty;

    public string WsApiKeyOverride { get; set; } = string.Empty;

    public string WsApiSecretOverride { get; set; } = string.Empty;

    public List<string> PreferredSymbols { get; set; } = new();

    public decimal MinOfferAmount { get; set; } = 150m;

    public decimal MaxOfferAmount { get; set; } = 250m;

    public decimal ReserveAmount { get; set; } = 100m;

    public decimal MinDailyRate { get; set; } = 0.00005m;

    public decimal MaxDailyRate { get; set; } = 0.00020m;

    public int MinPeriodDays { get; set; } = 2;

    public int DefaultPeriodDays { get; set; } = 2;

    public int MaxPeriodDays { get; set; } = 2;

    public string OfferType { get; set; } = "LIMIT";

    public int OfferFlags { get; set; }

    public int RepriceIntervalMinutes { get; set; } = 15;

    public int RestOfferSyncIntervalSeconds { get; set; } = 60;

    public int RestLifecycleSyncIntervalSeconds { get; set; } = 180;

    public int HistoryLookbackDays { get; set; } = 7;

    public int StartupDelaySeconds { get; set; } = 5;

    public int MinManagedOfferAgeSecondsBeforeReplace { get; set; } = 300;

    public decimal ReplaceMinRateDelta { get; set; } = 0.00001m;

    public decimal ReplaceMinAmountDeltaFraction { get; set; } = 0.20m;

    public bool UseFundingWalletOnly { get; set; } = true;

    public bool UsePrivateWebSocket { get; set; } = true;

    public bool AllowManagingExternalOffers { get; set; }
}
