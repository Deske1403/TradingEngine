#nullable enable

namespace Denis.TradingEngine.Exchange.Crypto.Bitfinex.Funding.Config;

public sealed class BitfinexFundingSymbolProfile
{
    public string Symbol { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public bool PauseNewOffers { get; set; }

    public decimal? MinOfferAmount { get; set; }

    public decimal? MaxOfferAmount { get; set; }

    public decimal? ReserveAmount { get; set; }

    public decimal? MinDailyRate { get; set; }

    public decimal? MaxDailyRate { get; set; }

    public string? LiveRateMode { get; set; }

    public string? LivePlacementPolicyMode { get; set; }

    public string? ManagedOfferTargetMode { get; set; }

    public string? ManagedOfferPolicyMode { get; set; }

    public bool? LiveUseFrrAsFloor { get; set; }

    public decimal? LiveLowRegimeRateMultiplier { get; set; }

    public decimal? LiveNormalRegimeRateMultiplier { get; set; }

    public decimal? LiveHotRegimeRateMultiplier { get; set; }

    public decimal? MotorAllocationFraction { get; set; }

    public decimal? OpportunisticAllocationFraction { get; set; }

    public decimal? AggressiveAllocationFraction { get; set; }

    public decimal? SniperAllocationFraction { get; set; }

    public bool? EnableLiveAggressivePromotion { get; set; }

    public bool? EnableLiveSniperPromotion { get; set; }

    public bool? EnableAdaptiveAggressiveMaxRate { get; set; }

    public decimal? AggressiveAdaptiveMaxDailyRate { get; set; }

    public bool? EnableAdaptiveSniperMaxRate { get; set; }

    public decimal? SniperAdaptiveMaxDailyRate { get; set; }

    public bool? EnableAdaptiveSingleSlotMaxRate { get; set; }

    public decimal? SingleSlotAdaptiveMaxDailyRate { get; set; }

    public bool? EnableAdaptiveMotorMaxRate { get; set; }

    public decimal? MotorAdaptiveMaxDailyRate { get; set; }

    public bool? EnableAdaptiveOpportunisticMaxRate { get; set; }

    public decimal? OpportunisticAdaptiveMaxDailyRate { get; set; }

    public int? MinManagedOfferAgeSecondsBeforeReplace { get; set; }

    public int? MinManagedOfferAgeSecondsBeforeReplaceWhenCapacityFull { get; set; }

    public decimal? MotorRateMultiplier { get; set; }

    public decimal? OpportunisticRateMultiplier { get; set; }

    public decimal? AggressiveRateMultiplier { get; set; }

    public decimal? SniperRateMultiplier { get; set; }

    public int? MotorMaxWaitMinutesLowRegime { get; set; }

    public int? MotorMaxWaitMinutesNormalRegime { get; set; }

    public int? MotorMaxWaitMinutesHotRegime { get; set; }

    public int? ManagedOfferFallbackCarryForwardMinutes { get; set; }

    public bool? EnableManagedFallbackNearMarketHold { get; set; }

    public decimal? ManagedFallbackNearMarketHoldDelta { get; set; }

    public bool? EnableFundingPerformanceReports { get; set; }

    public int? FundingPerformanceReportIntervalMinutes { get; set; }

    public int? MaxActiveOffersPerSymbol { get; set; }

    public int? OpportunisticMaxWaitMinutesLowRegime { get; set; }

    public int? OpportunisticMaxWaitMinutesNormalRegime { get; set; }

    public int? OpportunisticMaxWaitMinutesHotRegime { get; set; }

    public int? AggressiveMaxWaitMinutesLowRegime { get; set; }

    public int? AggressiveMaxWaitMinutesNormalRegime { get; set; }

    public int? AggressiveMaxWaitMinutesHotRegime { get; set; }

    public int? SniperMaxWaitMinutesLowRegime { get; set; }

    public int? SniperMaxWaitMinutesNormalRegime { get; set; }

    public int? SniperMaxWaitMinutesHotRegime { get; set; }
}
