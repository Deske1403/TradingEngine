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

    Task<IReadOnlyList<FundingCreditInfo>> GetActiveCreditsAsync(
        IReadOnlyCollection<string> symbols,
        CancellationToken ct);

    Task<IReadOnlyList<FundingCreditInfo>> GetCreditHistoryAsync(
        IReadOnlyCollection<string> symbols,
        CancellationToken ct);

    Task<IReadOnlyList<FundingLoanInfo>> GetActiveLoansAsync(
        IReadOnlyCollection<string> symbols,
        CancellationToken ct);

    Task<IReadOnlyList<FundingLoanInfo>> GetLoanHistoryAsync(
        IReadOnlyCollection<string> symbols,
        CancellationToken ct);

    Task<IReadOnlyList<FundingTradeInfo>> GetFundingTradeHistoryAsync(
        IReadOnlyCollection<string> symbols,
        CancellationToken ct);

    Task<IReadOnlyList<FundingLedgerEntry>> GetLedgerEntriesAsync(
        IReadOnlyCollection<string> currencies,
        CancellationToken ct);

    Task<FundingOfferActionResult> SubmitOfferAsync(
        FundingOfferRequest request,
        CancellationToken ct);

    Task<FundingOfferActionResult> CancelOfferAsync(
        string symbol,
        string offerId,
        CancellationToken ct);
}
