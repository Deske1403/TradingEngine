#nullable enable
using System;
using Denis.TradingEngine.Core.Trading;

namespace Denis.TradingEngine.App.Trading.EodSkim
{
    public sealed record IbkrEodSkimPositionCandidate(
        string Symbol,
        decimal Quantity,
        decimal AveragePrice,
        bool IsExternalIbkrPosition);

    public sealed record IbkrEodSkimDecision(
        string Symbol,
        decimal Quantity,
        decimal AveragePrice,
        decimal CandidateLimitPrice,
        decimal EstimatedNetProfitUsd,
        int RetryCount,
        string ReasonTag,
        bool IsRetry);

    public sealed record IbkrEodSkimPlaceRequest(
        string Symbol,
        decimal Quantity,
        decimal LimitPrice,
        string CorrelationId,
        string ReasonTag);

    public sealed record IbkrEodSkimPlaceResult(
        string CorrelationId,
        string? BrokerOrderId);

    public sealed record IbkrEodSkimOrderUpdate(
        string? BrokerOrderId,
        string Status,
        string? CorrelationId);

    public sealed record IbkrEodSkimQuoteLookupResult(
        bool Ok,
        MarketQuote? Quote,
        string? Reason);

    internal sealed class SkimSymbolState
    {
        public string Symbol { get; set; } = string.Empty;
        public string? ActiveCorrelationId { get; set; }
        public string? ActiveBrokerOrderId { get; set; }
        public decimal? LastLimitPrice { get; set; }
        public int RetryCount { get; set; }
        public DateTime? LastActionUtc { get; set; }
        public DateTime? LastCancelRequestUtc { get; set; }
        public bool CancelRequested { get; set; }
    }
}
