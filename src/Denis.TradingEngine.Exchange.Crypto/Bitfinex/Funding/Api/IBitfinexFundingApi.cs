#nullable enable

using Denis.TradingEngine.Exchange.Crypto.Bitfinex.Funding.Models;

namespace Denis.TradingEngine.Exchange.Crypto.Bitfinex.Funding.Api;

public interface IBitfinexFundingApi : IAsyncDisposable
{
    Task<IReadOnlyList<FundingWalletBalance>> GetWalletBalancesAsync(CancellationToken ct);

    Task<IReadOnlyList<FundingOfferInfo>> GetActiveOffersAsync(
        IReadOnlyCollection<string> symbols,
        CancellationToken ct);

    Task<IReadOnlyList<FundingTickerSnapshot>> GetFundingTickerSnapshotsAsync(
        IReadOnlyCollection<string> symbols,
        CancellationToken ct);

    Task<FundingOfferActionResult> SubmitOfferAsync(
        FundingOfferRequest request,
        CancellationToken ct);

    Task<FundingOfferActionResult> CancelOfferAsync(
        string symbol,
        string offerId,
        CancellationToken ct);
}
