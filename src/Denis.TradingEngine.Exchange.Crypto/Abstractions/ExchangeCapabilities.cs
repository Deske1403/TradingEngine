using System;
using Denis.TradingEngine.Core.Crypto;

namespace Denis.TradingEngine.Exchange.Crypto.Abstractions;

/// <summary>
/// Statičke mogućnosti berze – koristi se za konfiguraciju i risk.
/// Ne menja se često (osim ako berza menja fee ili limite).
/// </summary>
public sealed record ExchangeCapabilities(
    CryptoExchangeId ExchangeId,
    string NativeName,
    bool SupportsSpot,
    bool SupportsMargin,
    bool SupportsPerpetuals,
    bool SupportsTestnet,
    decimal? MakerFeeBps,
    decimal? TakerFeeBps,
    int MaxSymbolsPerConnection,
    int MaxWebSocketConnections);