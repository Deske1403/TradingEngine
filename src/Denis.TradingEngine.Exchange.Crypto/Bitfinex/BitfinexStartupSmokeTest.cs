#nullable enable
using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Denis.TradingEngine.Core.Crypto;
using Denis.TradingEngine.Exchange.Crypto.Abstractions;
using Denis.TradingEngine.Exchange.Crypto.Bitfinex;
using Denis.TradingEngine.Exchange.Crypto.Common;
using Denis.TradingEngine.Exchange.Crypto.Config;
using Serilog;

namespace Denis.TradingEngine.Exchange.Crypto.Bitfinex;

public static class BitfinexStartupSmokeTest
{
    public static async Task RunAsync(CryptoExchangeSettings ex, ILogger log, CancellationToken ct)
    {
        if (ex.ExchangeId != CryptoExchangeId.Bitfinex)
            return;

        if (string.IsNullOrWhiteSpace(ex.ApiKey) || string.IsNullOrWhiteSpace(ex.ApiSecret))
        {
            log.Warning("[BITFINEX-SMOKE] Missing ApiKey/ApiSecret in config. Skipping");
            return;
        }

        log.Information("[BITFINEX-SMOKE] Starting REST smoke test...");

        var api = new BitfinexTradingApi(ex.RestBaseUrl, ex.ApiKey, ex.ApiSecret, log);

        // 1) Balances
        var balances = await api.GetBalancesAsync(ct).ConfigureAwait(false);
        log.Information("[BITFINEX-SMOKE] Balances: {Count}", balances.Count);

        foreach (var b in balances.OrderBy(x => x.Asset, StringComparer.OrdinalIgnoreCase))
            log.Information("[BITFINEX-SMOKE]  {Asset} free={Free}", b.Asset, b.Free);

        // Choose first enabled symbol from your exchange settings
        var symbolSettings = ex.Symbols?.FirstOrDefault(s => s.Enabled);
        if (symbolSettings == null)
        {
            log.Warning("[BITFINEX-SMOKE] No enabled symbols in config. Skipping order test");
            return;
        }

        var symbol = new CryptoSymbol(
            ExchangeId: CryptoExchangeId.Bitfinex,
            BaseAsset: symbolSettings.BaseAsset,
            QuoteAsset: symbolSettings.QuoteAsset,
            NativeSymbol: symbolSettings.NativeSymbol
        );

        var metaProvider = new CryptoSymbolMetadataProvider(new[] { ex });
        if (!metaProvider.TryGetMetadata(symbol, out var meta))
        {
            log.Warning("[BITFINEX-SMOKE] Missing metadata for {Sym}. Skipping order test", symbol.PublicSymbol);
            return;
        }

        // 2) Get last price (public endpoint) to compute "ridiculous" prices
        var last = await GetLastPriceAsync(ex.RestBaseUrl, symbol.NativeSymbol, log, ct).ConfigureAwait(false);
        if (last <= 0m)
        {
            log.Warning("[BITFINEX-SMOKE] Could not fetch last price for {Native}. Skipping order test", symbol.NativeSymbol);
            return;
        }

        // Ridiculous prices: far away so they should not execute
        var buyPrice = RoundPx(last * 0.10m);
        var sellPrice = RoundPx(last * 10.0m);

        var qty = meta.MinQuantity;

        // 3) Place BUY far below market, then cancel it
        log.Information("[BITFINEX-SMOKE] Placing FAR BUY {Sym} qty={Qty} px={Px} (last={Last})",
            symbol.PublicSymbol, qty, buyPrice, last);

        var buyRes = await api.PlaceLimitOrderAsync(symbol, CryptoOrderSide.Buy, qty, buyPrice, ct).ConfigureAwait(false);

        if (!buyRes.Accepted || string.IsNullOrWhiteSpace(buyRes.ExchangeOrderId))
        {
            log.Warning("[BITFINEX-SMOKE] BUY rejected: {Reason}", buyRes.RejectReason ?? "unknown");
            return;
        }

        log.Information("[BITFINEX-SMOKE] BUY accepted. orderId={OrderId}", buyRes.ExchangeOrderId);

        var cancelOk = await api.CancelOrderAsync(buyRes.ExchangeOrderId!, ct).ConfigureAwait(false);
        log.Information("[BITFINEX-SMOKE] BUY cancel ok={Ok}", cancelOk);

        // 4) SELL test only if we actually have base asset balance (otherwise it will just fail)
        var baseBal = balances.FirstOrDefault(b =>
            string.Equals(b.Asset, symbol.BaseAsset, StringComparison.OrdinalIgnoreCase));

        if (baseBal == null || baseBal.Free < qty)
        {
            log.Warning("[BITFINEX-SMOKE] Skipping SELL test: need {Need} {Asset}, have {Have}",
                qty, symbol.BaseAsset, baseBal?.Free ?? 0m);
            log.Information("[BITFINEX-SMOKE] Smoke test completed (balances + buy+cancel)");
            return;
        }

        log.Information("[BITFINEX-SMOKE] Placing FAR SELL {Sym} qty={Qty} px={Px} (last={Last})",
            symbol.PublicSymbol, qty, sellPrice, last);

        var sellRes = await api.PlaceLimitOrderAsync(symbol, CryptoOrderSide.Sell, qty, sellPrice, ct).ConfigureAwait(false);

        if (!sellRes.Accepted || string.IsNullOrWhiteSpace(sellRes.ExchangeOrderId))
        {
            log.Warning("[BITFINEX-SMOKE] SELL rejected: {Reason}", sellRes.RejectReason ?? "unknown");
            log.Information("[BITFINEX-SMOKE] Smoke test completed (balances + buy+cancel)");
            return;
        }

        log.Information("[BITFINEX-SMOKE] SELL accepted. orderId={OrderId}", sellRes.ExchangeOrderId);

        var cancelSellOk = await api.CancelOrderAsync(sellRes.ExchangeOrderId!, ct).ConfigureAwait(false);
        log.Information("[BITFINEX-SMOKE] SELL cancel ok={Ok}", cancelSellOk);

        log.Information("[BITFINEX-SMOKE] Smoke test completed OKß");
    }

    private static async Task<decimal> GetLastPriceAsync(string baseUrl, string nativeSymbol, ILogger log, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient
            {
                BaseAddress = new Uri(baseUrl),
                Timeout = TimeSpan.FromSeconds(10)
            };

            // Bitfinex public ticker: /v2/ticker/{symbol}
            var path = "/v2/ticker/" + nativeSymbol;
            var json = await http.GetStringAsync(path, ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 7)
                return 0m;

            // Index 6 = LAST_PRICE
            var lastEl = root[6];
            if (lastEl.ValueKind == JsonValueKind.Number && lastEl.TryGetDecimal(out var last))
                return last;

            if (lastEl.ValueKind == JsonValueKind.String &&
                decimal.TryParse(lastEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var last2))
                return last2;

            return 0m;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[BITFINEX-SMOKE] Failed to fetch last price for {Native}", nativeSymbol);
            return 0m;
        }
    }

    private static decimal RoundPx(decimal px)
    {
        // Safe default (Bitfinex accepts varying decimals per pair; this is just for test orders)
        return Math.Round(px, 8, MidpointRounding.AwayFromZero);
    }
}