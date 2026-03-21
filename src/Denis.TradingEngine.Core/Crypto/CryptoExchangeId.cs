namespace Denis.TradingEngine.Core.Crypto;

/// <summary>
/// Identifikator kripto berze / brokera u okviru sistema.
/// Držimo ga kao enum zbog tip-sigurnosti, ali u DB i logovima
/// možemo koristiti string reprezentaciju (ToString()).
/// </summary>
public enum CryptoExchangeId
{
    Unknown = 0,

    // Klasični broker (radi preko Core-a, za referencu)
    Ibkr = 1,

    // Kripto berze koje planiramo
    Kraken = 2,
    Bitfinex = 3,
    Deribit = 4,
    Bybit =5

    // Buduće berze – ovde lako dodajemo nove (Binance, Bybit, itd.)
}

