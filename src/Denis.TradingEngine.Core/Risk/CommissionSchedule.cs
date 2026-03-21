#nullable enable

namespace Denis.TradingEngine.Core.Risk
{
    /// <summary>
    /// Gruba procena troškova (po nalogu i round-trip).
    /// </summary>
    public sealed record CommissionSchedule(
        decimal EstimatedPerOrderUsd,   // npr. 0.35 USD po nalogu
        decimal EstimatedRoundTripUsd   // npr. 0.35 * 2 = 0.70 USD za kupovinu i prodaju
    );
}