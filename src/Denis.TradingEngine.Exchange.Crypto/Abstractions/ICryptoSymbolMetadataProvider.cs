using System.Collections.Generic;
using Denis.TradingEngine.Core.Crypto;

namespace Denis.TradingEngine.Exchange.Crypto.Abstractions;

/// <summary>
/// Servis koji daje objedinjene informacije o simbolima
/// (min qty, korak, max risk, max exposure...) za određenu berzu.
/// Core (risk, sizing, adapteri) ne treba direktno da čita config fajl,
/// već koristi ovaj provider.
/// </summary>
public interface ICryptoSymbolMetadataProvider
{
    /// <summary>
    /// Vraća sve simbole koje engine treba da trejduje (po svim berzama).
    /// </summary>
    IReadOnlyList<CryptoSymbol> GetAllSymbols();

    /// <summary>
    /// Pokušava da pronađe simbol po exchange-u i "public" nazivu,
    /// npr. (Kraken, "BTCUSDT").
    /// </summary>
    bool TryGetSymbol(CryptoExchangeId exchangeId, string publicSymbol, out CryptoSymbol symbol);

    /// <summary>
    /// Pokušava da vrati detaljne metapodatke za simbol.
    /// </summary>
    bool TryGetMetadata(CryptoSymbol symbol, out CryptoSymbolMetadata metadata);
}