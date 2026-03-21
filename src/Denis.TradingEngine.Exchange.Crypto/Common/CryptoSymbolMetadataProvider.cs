using System;
using System.Collections.Generic;
using System.Linq;
using Denis.TradingEngine.Core.Crypto;
using Denis.TradingEngine.Exchange.Crypto.Abstractions;
using Denis.TradingEngine.Exchange.Crypto.Config;

namespace Denis.TradingEngine.Exchange.Crypto.Common;

/// <summary>
/// Default implementacija ICryptoSymbolMetadataProvider-a.
/// Na startu pročita konfiguraciju (CryptoExchangeSettings listu)
/// i iz nje napravi mapu simbola i njihovih metapodataka.
/// </summary>
public sealed class CryptoSymbolMetadataProvider : ICryptoSymbolMetadataProvider
{
    private readonly Dictionary<(CryptoExchangeId ExchangeId, string PublicSymbol), CryptoSymbolMetadata> _byExchangeAndPublic;
    private readonly Dictionary<(CryptoExchangeId ExchangeId, string NativeSymbol), CryptoSymbolMetadata> _byExchangeAndNative;
    private readonly IReadOnlyList<CryptoSymbolMetadata> _all;

    public CryptoSymbolMetadataProvider(IEnumerable<CryptoExchangeSettings> exchanges)
    {
        if (exchanges == null) throw new ArgumentNullException(nameof(exchanges));

        var list = new List<CryptoSymbolMetadata>();

        foreach (var ex in exchanges.Where(e => e.Enabled))
        {
            foreach (var s in ex.Symbols.Where(s => s.Enabled))
            {
                var symbol = new CryptoSymbol(
                    ex.ExchangeId,
                    s.BaseAsset,
                    s.QuoteAsset,
                    s.NativeSymbol);

                var meta = new CryptoSymbolMetadata(
                    symbol,
                    s.MinQuantity,
                    s.QuantityStep,
                    s.MaxRiskFractionPerTrade,
                    s.MaxExposureFraction);

                list.Add(meta);
            }
        }

        _all = list;

        _byExchangeAndPublic = list
            .ToDictionary(
                m => (m.Symbol.ExchangeId, m.Symbol.PublicSymbol),
                m => m);

        _byExchangeAndNative = list
            .ToDictionary(
                m => (m.Symbol.ExchangeId, m.Symbol.NativeSymbol),
                m => m);
    }

    public IReadOnlyList<CryptoSymbol> GetAllSymbols()
        => _all.Select(m => m.Symbol).ToList();

    public bool TryGetSymbol(CryptoExchangeId exchangeId, string publicSymbol, out CryptoSymbol symbol)
    {
        if (publicSymbol == null)
        {
            symbol = null!;
            return false;
        }

        if (_byExchangeAndPublic.TryGetValue((exchangeId, publicSymbol), out var meta))
        {
            symbol = meta.Symbol;
            return true;
        }

        symbol = null!;
        return false;
    }

    public bool TryGetMetadata(CryptoSymbol symbol, out CryptoSymbolMetadata metadata)
    {
        if (symbol == null)
        {
            metadata = null!;
            return false;
        }

        // Pokušaj preko public simbola
        if (_byExchangeAndPublic.TryGetValue((symbol.ExchangeId, symbol.PublicSymbol), out metadata))
        {
            return true;
        }

        // Fallback: preko native simbola
        if (_byExchangeAndNative.TryGetValue((symbol.ExchangeId, symbol.NativeSymbol), out metadata))
        {
            return true;
        }

        metadata = null!;
        return false;
    }
}
