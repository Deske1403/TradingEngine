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

    public List<BitfinexFundingSymbolProfile> SymbolProfiles { get; set; } = new();

    public decimal MinOfferAmount { get; set; } = 150m;

    public decimal MaxOfferAmount { get; set; } = 250m;

    public decimal ReserveAmount { get; set; } = 100m;

    public decimal MinDailyRate { get; set; } = 0.00005m;

    public decimal MaxDailyRate { get; set; } = 0.00020m;

    public string LiveRateMode { get; set; } = "SmartRegime";

    public string LivePlacementPolicyMode { get; set; } = "Immediate";

    public string ManagedOfferTargetMode { get; set; } = "Live";

    public string ManagedOfferPolicyMode { get; set; } = "Immediate";

    public bool LiveUseFrrAsFloor { get; set; } = true;

    public decimal LiveLowRegimeRateMultiplier { get; set; } = 1.00m;

    public decimal LiveNormalRegimeRateMultiplier { get; set; } = 1.02m;

    public decimal LiveHotRegimeRateMultiplier { get; set; } = 1.06m;

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

    public int ManagedOfferFallbackCarryForwardMinutes { get; set; } = 10;

    public int MaxActiveOffersPerSymbol { get; set; } = 1;

    public decimal ReplaceMinRateDelta { get; set; } = 0.00001m;

    public decimal ReplaceMinAmountDeltaFraction { get; set; } = 0.20m;

    public bool UseFundingWalletOnly { get; set; } = true;

    public bool UsePrivateWebSocket { get; set; } = true;

    public bool AllowManagingExternalOffers { get; set; }

    public bool LayeredShadowEnabled { get; set; } = true;

    public decimal MotorAllocationFraction { get; set; } = 0.70m;

    public decimal OpportunisticAllocationFraction { get; set; } = 0.30m;

    public decimal SniperAllocationFraction { get; set; } = 0.10m;

    public decimal MotorRateMultiplier { get; set; } = 0.97m;

    public decimal OpportunisticRateMultiplier { get; set; } = 1.08m;

    public decimal SniperRateMultiplier { get; set; } = 1.18m;

    public int MotorMaxWaitMinutesLowRegime { get; set; } = 20;

    public int MotorMaxWaitMinutesNormalRegime { get; set; } = 12;

    public int MotorMaxWaitMinutesHotRegime { get; set; } = 5;

    public int OpportunisticMaxWaitMinutesLowRegime { get; set; } = 120;

    public int OpportunisticMaxWaitMinutesNormalRegime { get; set; } = 60;

    public int OpportunisticMaxWaitMinutesHotRegime { get; set; } = 20;

    public int SniperMaxWaitMinutesLowRegime { get; set; } = 240;

    public int SniperMaxWaitMinutesNormalRegime { get; set; } = 120;

    public int SniperMaxWaitMinutesHotRegime { get; set; } = 30;
}
