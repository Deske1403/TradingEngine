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
}
