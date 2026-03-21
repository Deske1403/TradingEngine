using Denis.TradingEngine.Core.Crypto;

namespace Denis.TradingEngine.Exchange.Crypto.Abstractions;

/// <summary>
/// Spoj neutralnog CryptoSymbol-a i risk/limit podešavanja za taj simbol.
/// Dobija se iz konfiguracije (CryptoExchangeSettings + CryptoSymbolSettings).
/// </summary>
public sealed record CryptoSymbolMetadata(
    CryptoSymbol Symbol,
    decimal MinQuantity,
    decimal QuantityStep,
    decimal MaxRiskFractionPerTrade,
    decimal MaxExposureFraction);