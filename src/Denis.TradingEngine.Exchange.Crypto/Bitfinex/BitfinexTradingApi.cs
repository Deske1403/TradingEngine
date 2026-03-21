#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Denis.TradingEngine.Core.Crypto;
using Denis.TradingEngine.Exchange.Crypto.Abstractions;
using Denis.TradingEngine.Exchange.Crypto.Common;
using Serilog;

namespace Denis.TradingEngine.Exchange.Crypto.Bitfinex;

/// <summary>
/// Bitfinex REST v2 trading API (authed).
/// Auth shema (v2):
///   - URL:  https://api.bitfinex.com/v2/{path}
///   - Headers:
///       bfx-nonce:     <nonce>
///       bfx-apikey:    <apiKey>
///       bfx-signature: hex( HMAC-SHA384( "/api" + path + nonce + bodyJson, apiSecret ) )
///   - body: JSON, npr. {}
///
/// Kriticno:
/// - nonce mora biti strogo rastuci. U praksi najbolje microseconds + monotonic guard.
/// </summary>
public sealed class BitfinexTradingApi : RestClientBase, ICryptoTradingApi
{
    public sealed record OrderTradeSnapshot(decimal FilledQuantity, decimal Price, DateTime TimestampUtc);

    private readonly ILogger _log;
    private readonly HttpClient _http;

    private readonly string _apiKey;
    private readonly string _apiSecret;

    private readonly BitfinexAuthNonceProvider _nonceProvider;

    public CryptoExchangeId ExchangeId => CryptoExchangeId.Bitfinex;

    public BitfinexTradingApi(
        string baseUrl,
        string apiKey,
        string apiSecret,
        ILogger log,
        BitfinexAuthNonceProvider? nonceProvider = null)
        : base(baseUrl, log)
    {
        _log = log;

        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };

        _apiKey = apiKey ?? string.Empty;
        _apiSecret = apiSecret ?? string.Empty;
        _nonceProvider = nonceProvider ?? new BitfinexAuthNonceProvider();
    }

    // --------------------------------------------------------------------
    // ICryptoTradingApi
    // --------------------------------------------------------------------

    /// <summary>OCO flag: One-Cancels-Other (Bitfinex flag value 16384).</summary>
    public const int OcoFlag = 16384;

    public async Task<PlaceOrderResult> PlaceLimitOrderAsync(
       CryptoSymbol symbol,
       CryptoOrderSide side,
       decimal quantity,
       decimal price,
       CancellationToken ct,
       int flags = 0,
       int? gid = null,
       decimal? priceOcoStop = null)
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_apiSecret))
        {
            _log.Warning("[BITFINEX-REST] ApiKey/ApiSecret nisu podeseni. Ne mogu da posaljem nalog");
            return new PlaceOrderResult(false, null, "Missing API key/secret");
        }

        if (quantity <= 0m)
            return new PlaceOrderResult(false, null, "Invalid quantity");

        if (price <= 0m)
            return new PlaceOrderResult(false, null, "Invalid price");

        const string path = "/v2/auth/w/order/submit";

        // Preferiraj eksplicitni NativeSymbol iz config-a (npr. tXAUT:UST),
        // fallback je legacy tBASEQUOTE mapiranje.
        var pair = ResolvePairForOrder(symbol);

        // Bitfinex: amount signed (+ buy, - sell)
        var signedAmount = side == CryptoOrderSide.Sell ? -quantity : quantity;

        // Payload: OCO (flags=16384) + gid + price_oco_stop (stop cena para) kad je potrebno
        var payload = BuildLimitPayload(pair, price, signedAmount, flags, gid, priceOcoStop);

        var json = await SendAuthAsync(path, payload, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
            return new PlaceOrderResult(false, null, "Empty response");

        if (TryParseBfxError(json, out var err))
            return new PlaceOrderResult(false, null, err);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array)
                return new PlaceOrderResult(false, null, "Unexpected response (not array)");

            var status = GetStringSafe(root, 6);
            var text = GetStringSafe(root, 7);

            if (!string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
            {
                var reason = !string.IsNullOrWhiteSpace(text) ? text! : "Order rejected";
                return new PlaceOrderResult(false, null, reason);
            }

            if (root.GetArrayLength() <= 4 || root[4].ValueKind != JsonValueKind.Array)
                return new PlaceOrderResult(false, null, "SUCCESS but missing order payload");

            var ordersWrapper = root[4];
            if (ordersWrapper.GetArrayLength() < 1)
                return new PlaceOrderResult(false, null, "SUCCESS but empty orders list");

            var first = ordersWrapper[0];
            if (first.ValueKind != JsonValueKind.Array || first.GetArrayLength() < 1)
                return new PlaceOrderResult(false, null, "SUCCESS but invalid order array");

            var idStr = GetLongAsStringSafe(first, 0);
            if (string.IsNullOrWhiteSpace(idStr))
                return new PlaceOrderResult(false, null, "SUCCESS but missing order id");

            return new PlaceOrderResult(true, idStr, null);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[BITFINEX-REST] PlaceLimitOrder parse failed. Raw={Raw}", Trunc(json, 1200));
            return new PlaceOrderResult(false, null, "Failed to parse order response");
        }
    }

    public async Task<PlaceOrderResult> PlaceStopOrderAsync(
    CryptoSymbol symbol,
    CryptoOrderSide side,
    decimal quantity,
    decimal stopPrice,
    decimal? limitPrice,
    CancellationToken ct,
    int flags = 0,
    int? gid = null)
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_apiSecret))
        {
            _log.Warning("[BITFINEX-REST] ApiKey/ApiSecret nisu podeseni. Ne mogu da posaljem stop nalog");
            return new PlaceOrderResult(false, null, "Missing API key/secret");
        }

        if (quantity <= 0m)
            return new PlaceOrderResult(false, null, "Invalid quantity");

        if (stopPrice <= 0m)
            return new PlaceOrderResult(false, null, "Invalid stop price");

        const string path = "/v2/auth/w/order/submit";

        // Preferiraj eksplicitni NativeSymbol iz config-a (npr. tXAUT:UST),
        // fallback je legacy tBASEQUOTE mapiranje.
        var pair = ResolvePairForOrder(symbol);

        // Bitfinex: amount signed (+ buy, - sell)
        var signedAmount = side == CryptoOrderSide.Sell ? -quantity : quantity;

        // Bitfinex STOP: EXCHANGE STOP LIMIT + OCO (flags=16384) + gid kad je potrebno
        var payload = BuildStopPayload(pair, stopPrice, limitPrice ?? stopPrice, signedAmount, flags, gid);

        var json = await SendAuthAsync(path, payload, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
            return new PlaceOrderResult(false, null, "Empty response");

        if (TryParseBfxError(json, out var err))
            return new PlaceOrderResult(false, null, err);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array)
                return new PlaceOrderResult(false, null, "Unexpected response (not array)");

            var status = GetStringSafe(root, 6);
            var text = GetStringSafe(root, 7);

            if (!string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
            {
                var reason = !string.IsNullOrWhiteSpace(text) ? text! : "Order rejected";
                return new PlaceOrderResult(false, null, reason);
            }

            if (root.GetArrayLength() <= 4 || root[4].ValueKind != JsonValueKind.Array)
                return new PlaceOrderResult(false, null, "SUCCESS but missing order payload");

            var ordersWrapper = root[4];
            if (ordersWrapper.GetArrayLength() < 1)
                return new PlaceOrderResult(false, null, "SUCCESS but empty orders list");

            var first = ordersWrapper[0];
            if (first.ValueKind != JsonValueKind.Array || first.GetArrayLength() < 1)
                return new PlaceOrderResult(false, null, "SUCCESS but invalid order array");

            var idStr = GetLongAsStringSafe(first, 0);
            if (string.IsNullOrWhiteSpace(idStr))
                return new PlaceOrderResult(false, null, "SUCCESS but missing order id");

            return new PlaceOrderResult(true, idStr, null);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[BITFINEX-REST] PlaceStopOrder parse failed. Raw={Raw}", Trunc(json, 1200));
            return new PlaceOrderResult(false, null, "Failed to parse order response");
        }
    }


    public async Task<bool> CancelOrderAsync(string exchangeOrderId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_apiSecret))
        {
            _log.Warning("[BITFINEX-REST] ApiKey/ApiSecret nisu podeseni. Ne mogu da cancelujem nalog");
            return false;
        }

        if (string.IsNullOrWhiteSpace(exchangeOrderId))
            return false;

        if (!long.TryParse(exchangeOrderId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            _log.Warning("[BITFINEX-REST] CancelOrderAsync: invalid orderId='{OrderId}'", exchangeOrderId);
            return false;
        }

        const string path = "/v2/auth/w/order/cancel";
        var payload = new { id };

        var json = await SendAuthAsync(path, payload, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
            return false;

        // Idempotency: ako je order veÄ‡ canceled/ne postoji, tretiraj kao success
        if (TryParseBfxError(json, out var errMsg))
        {
            if (errMsg.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0 ||
                errMsg.IndexOf("already", StringComparison.OrdinalIgnoreCase) >= 0 ||
                errMsg.IndexOf("canceled", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _log.Debug("[BITFINEX-REST] Cancel idempotent (order already gone): {Msg}", errMsg);
                return true;
            }
            _log.Warning("[BITFINEX-REST] Cancel error: {Msg}", errMsg);
            return false;
        }

        // Tipican cancel response:
        // [ MTS, "oc-req", null, null, [ORDER_ARRAY...], null, "SUCCESS", "Order canceled" ]
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array)
                return false;

            var status = GetStringSafe(root, 6);
            var text = GetStringSafe(root, 7);

            var ok = string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase);
            if (!ok)
                _log.Warning("[BITFINEX-REST] Cancel failed status={Status} text={Text} raw={Raw}", status, text, Trunc(json, 1200));

            return ok;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[BITFINEX-REST] Cancel parse failed. Raw={Raw}", Trunc(json, 1200));
            return false;
        }
    }

    public async Task<IReadOnlyList<OpenOrderInfo>> GetOpenOrdersAsync(CancellationToken ct)
    {
        IReadOnlyList<OpenOrderInfo> empty = Array.Empty<OpenOrderInfo>();

        if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_apiSecret))
        {
            _log.Warning("[BITFINEX-REST] ApiKey/ApiSecret nisu podeseni. Preskacem open orders");
            return empty;
        }

        const string path = "/v2/auth/r/orders";
        var payload = new { };

        var json = await SendAuthAsync(path, payload, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
            return empty;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array)
                return empty;

            var list = new List<OpenOrderInfo>();

            foreach (var arr in root.EnumerateArray())
            {
                if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() < 18)
                    continue;

                // ORDER_ARRAY indices (v2):
                // 0 ID
                // 3 SYMBOL (e.g. "tETHUSD")
                // 4 MTS_CREATE (ms)
                // 5 MTS_UPDATE (ms)
                // 6 AMOUNT (remaining, signed)
                // 7 AMOUNT_ORIG (original, signed)
                // 13 ORDER_STATUS (string)
                // 16 PRICE
                var idStr = GetLongAsStringSafe(arr, 0);
                if (string.IsNullOrWhiteSpace(idStr))
                    continue;

                var native = GetStringSafe(arr, 3);
                if (string.IsNullOrWhiteSpace(native))
                    continue;

                if (!TryResolveCryptoSymbolFromNative(native!, out var cryptoSymbol))
                    continue;

                var amountRem = GetDecimalSafe(arr, 6);
                var amountOrig = GetDecimalSafe(arr, 7);
                var price = GetDecimalSafe(arr, 16);

                if (!amountOrig.HasValue)
                    continue;

                var side = amountOrig.Value >= 0m ? CryptoOrderSide.Buy : CryptoOrderSide.Sell;

                var qty = Math.Abs(amountOrig.Value);
                var rem = amountRem.HasValue ? Math.Abs(amountRem.Value) : qty;
                var filled = Math.Max(0m, qty - rem);

                var statusStr = GetStringSafe(arr, 13) ?? string.Empty;
                var status = MapBitfinexStatus(statusStr);

                var createdUtc = FromUnixMsToUtc(arr, 4) ?? DateTime.UtcNow;
                var updatedUtc = FromUnixMsToUtc(arr, 5);

                list.Add(new OpenOrderInfo(
                    ExchangeOrderId: idStr!,
                    Symbol: cryptoSymbol,
                    Side: side,
                    Status: status,
                    Price: price ?? 0m,
                    Quantity: qty,
                    FilledQuantity: filled,
                    CreatedUtc: createdUtc,
                    UpdatedUtc: updatedUtc
                ));
            }

            return list;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[BITFINEX-REST] GetOpenOrders parse failed. Raw={Raw}", Trunc(json, 1500));
            return empty;
        }
    }

    /// <summary>
    /// Wallets (/v2/auth/r/wallets).
    /// Response: [WALLET_TYPE, CURRENCY, BALANCE, UNSETTLED_INTEREST, BALANCE_AVAILABLE, ...].
    /// Koristimo BALANCE_AVAILABLE (index 4) za Free â€“ kad ima otvorenih ordera, Bitfinex zakljuÄava koliÄinu.
    /// </summary>
    public async Task<IReadOnlyList<BalanceInfo>> GetBalancesAsync(CancellationToken ct)
    {
        IReadOnlyList<BalanceInfo> empty = Array.Empty<BalanceInfo>();

        if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_apiSecret))
        {
            _log.Warning("[BITFINEX-REST] ApiKey/ApiSecret nisu podeseni. Preskacem wallets");
            return empty;
        }

        const string path = "/v2/auth/r/wallets";
        var payload = new { };

        var json = await SendAuthAsync(path, payload, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
            return empty;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array)
            {
                _log.Warning("[BITFINEX-REST] Wallets nije array: {Raw}", Trunc(json, 1200));
                return empty;
            }

            var balances = new List<BalanceInfo>();

            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 3)
                    continue;

                var type = item[0].ValueKind == JsonValueKind.String ? item[0].GetString() : null; // "exchange"
                var currency = item[1].ValueKind == JsonValueKind.String ? item[1].GetString() : null;

                decimal? totalBal = ParseDecimalElement(item, 2);
                if (string.IsNullOrWhiteSpace(currency) || !totalBal.HasValue)
                    continue;

                if (!string.Equals(type, "exchange", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Index 4 = BALANCE_AVAILABLE (slobodno za nove ordere; ostalo je u open orders)
                decimal? availableBal = item.GetArrayLength() >= 5 ? ParseDecimalElement(item, 4) : null;
                decimal free = availableBal ?? totalBal.Value;
                decimal locked = totalBal.Value - free;
                if (locked < 0m) locked = 0m;

                // PreskaÄemo samo ako su i total i free <= 0 (nema smisla prikazivati)
                if (totalBal.Value <= 0m && free <= 0m)
                    continue;

                balances.Add(new BalanceInfo(
                    ExchangeId: CryptoExchangeId.Bitfinex,
                    Asset: currency!,
                    Free: free,
                    Locked: locked
                ));
            }

            _log.Debug("[BITFINEX-REST] Wallets parsed: {Count} assets", balances.Count);
            return balances;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[BITFINEX-REST] Greska pri parsiranju Wallets JSON-a. Raw={Raw}", Trunc(json, 1200));
            return empty;
        }
    }

    private static decimal? ParseDecimalElement(JsonElement item, int index)
    {
        var el = item[index];
        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString();
            if (decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return d;
        }
        else if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var dn))
        {
            return dn;
        }
        return null;
    }

    // --------------------------------------------------------------------
    // OCO payload helpers â€” spec: https://docs.bitfinex.com/reference/rest-auth-submit-order
    // OCO: jedan LIMIT nalog sa price_oco_stop = stop cena para; Bitfinex kreira oba naloga.
    // --------------------------------------------------------------------

    private static object BuildLimitPayload(string pair, decimal price, decimal signedAmount, int flags, int? gid, decimal? priceOcoStop = null)
    {
        var payload = new Dictionary<string, object>
        {
            ["type"] = "EXCHANGE LIMIT",
            ["symbol"] = pair,
            ["price"] = price.ToString(CultureInfo.InvariantCulture),
            ["amount"] = signedAmount.ToString(CultureInfo.InvariantCulture),
            ["flags"] = flags
        };
        if (gid.HasValue && gid.Value != 0)
            payload["gid"] = gid.Value;
        if (priceOcoStop.HasValue && priceOcoStop.Value > 0m && (flags & OcoFlag) != 0)
            payload["price_oco_stop"] = priceOcoStop.Value.ToString(CultureInfo.InvariantCulture);
        return payload;
    }

    private static object BuildStopPayload(string pair, decimal stopPrice, decimal priceAuxLimit, decimal signedAmount, int flags, int? gid)
    {
        var payload = new Dictionary<string, object>
        {
            ["type"] = "EXCHANGE STOP LIMIT",
            ["symbol"] = pair,
            ["price"] = stopPrice.ToString(CultureInfo.InvariantCulture),
            ["amount"] = signedAmount.ToString(CultureInfo.InvariantCulture),
            ["price_aux_limit"] = priceAuxLimit.ToString(CultureInfo.InvariantCulture),
            ["flags"] = flags
        };
        if (gid.HasValue && gid.Value != 0)
            payload["gid"] = gid.Value;
        return payload;
    }

    // --------------------------------------------------------------------
    // Interno: auth poziv
    // --------------------------------------------------------------------

    /// <summary>
    /// Genericki helper za Bitfinex v2 auth POST.
    /// Radi minimalni backoff kad je rate limit / 429.
    /// </summary>
    private async Task<string> SendAuthAsync(string path, object payload, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_apiSecret))
        {
            _log.Warning("[BITFINEX-REST] ApiKey/ApiSecret nisu podeseni. Preskacem {Path}", path);
            return string.Empty;
        }

        // JSON body (Bitfinex ocekuje JSON)
        var bodyJson = JsonSerializer.Serialize(
            payload,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        // Minimalni retry za rate limit / transient
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var nonce = _nonceProvider.NextNonceMicros();
            var sigPayload = "/api" + path + nonce + bodyJson;
            var signature = CreateSignature(sigPayload);

            if (signature is null)
            {
                _log.Error("[BITFINEX-REST] Ne mogu da napravim signature za {Path}", path);
                return string.Empty;
            }

            using var req = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
            };

            req.Headers.Add("bfx-nonce", nonce);
            req.Headers.Add("bfx-apikey", _apiKey);
            req.Headers.Add("bfx-signature", signature);

            _log.Debug("[BITFINEX-REST] â†’ POST {Path} attempt={Attempt}", path, attempt);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var content = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            _log.Debug("[BITFINEX-REST] â† {Path} status: {StatusCode}", path, resp.StatusCode);
            _log.Debug("[BITFINEX-REST] â† {Path} body: {Body}", path, Trunc(content, 1400));

            if (resp.IsSuccessStatusCode)
                return content;

            // Rate limit: 429 ili body sa ERR_RATE_LIMIT
            var isRate =
                resp.StatusCode == (HttpStatusCode)429 ||
                content.IndexOf("ERR_RATE_LIMIT", StringComparison.OrdinalIgnoreCase) >= 0;

            if (isRate && attempt < 3)
            {
                var delay = attempt switch
                {
                    1 => TimeSpan.FromSeconds(1),
                    2 => TimeSpan.FromSeconds(3),
                    _ => TimeSpan.FromSeconds(6)
                };

                _log.Warning("[BITFINEX-REST] Rate limit detected. Backoff {Delay} then retry.", delay);
                await Task.Delay(delay, ct).ConfigureAwait(false);
                continue;
            }

            // nonce issue: ako dobijes nonce error, monotonic generator bi trebao da resi,
            // ali i dalje: jedini siguran fix je jedan proces po API key-u.
            return content;
        }

        return string.Empty;
    }

    /// <summary>
    /// GET auth zahtev za Bitfinex v2 (za read-only operacije kao Å¡to je GetOrder).
    /// </summary>
    private async Task<string> SendAuthGetAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_apiSecret))
        {
            _log.Warning("[BITFINEX-REST] ApiKey/ApiSecret nisu podeseni. Preskacem {Path}", path);
            return string.Empty;
        }

        // GET zahtevi nemaju body, ali nonce i signature su i dalje potrebni
        var nonce = _nonceProvider.NextNonceMicros();
        var sigPayload = "/api" + path + nonce;
        var signature = CreateSignature(sigPayload);

        if (signature is null)
        {
            _log.Error("[BITFINEX-REST] Ne mogu da napravim signature za {Path}", path);
            return string.Empty;
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Add("bfx-nonce", nonce);
        req.Headers.Add("bfx-apikey", _apiKey);
        req.Headers.Add("bfx-signature", signature);

        _log.Debug("[BITFINEX-REST] â†’ GET {Path}", path);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var content = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        _log.Debug("[BITFINEX-REST] â† {Path} status: {StatusCode}", path, resp.StatusCode);
        _log.Debug("[BITFINEX-REST] â† {Path} body: {Body}", path, Trunc(content, 1400));

        if (resp.IsSuccessStatusCode)
            return content;

        return string.Empty;
    }

    public async Task<string> GetUserInfoRawAsync(CancellationToken ct)
    {
        const string path = "/v2/auth/r/info/user";
        var payload = new { };
        return await SendAuthAsync(path, payload, ct).ConfigureAwait(false);
    }

    public async Task<OpenOrderInfo?> GetOrderAsync(string exchangeOrderId, CancellationToken ct)
    {
        OpenOrderInfo? none = null;

        if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_apiSecret))
        {
            _log.Warning("[BITFINEX-REST] ApiKey/ApiSecret nisu podeseni. Preskacem GetOrder");
            return none;
        }

        if (string.IsNullOrWhiteSpace(exchangeOrderId))
            return none;

        if (!long.TryParse(exchangeOrderId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            _log.Warning("[BITFINEX-REST] GetOrderAsync: invalid orderId='{OrderId}'", exchangeOrderId);
            return none;
        }

        // Bitfinex v2: POST /v2/auth/r/orders sa filterom po ID-u
        const string path = "/v2/auth/r/orders";

        // Bitfinex oÄekuje LISTU ID-eva, ne jedan broj -> inaÄe dobijes ["error",10020,"id: invalid"]
        var payload = new { id = new[] { id } };

        var json = await SendAuthAsync(path, payload, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
            return none;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Odgovor je array order-a, uz filter je obicno 0 ili 1 element
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                return none;

            var arr = root[0];
            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() < 18)
                return none;

            var idStr = GetLongAsStringSafe(arr, 0);
            if (string.IsNullOrWhiteSpace(idStr))
                return none;

            var native = GetStringSafe(arr, 3);
            if (string.IsNullOrWhiteSpace(native))
                return none;

            if (!TryResolveCryptoSymbolFromNative(native!, out var cryptoSymbol))
                return none;

            var amountRem = GetDecimalSafe(arr, 6);
            var amountOrig = GetDecimalSafe(arr, 7);
            var price = GetDecimalSafe(arr, 16);

            if (!amountOrig.HasValue)
                return none;

            var side = amountOrig.Value >= 0m ? CryptoOrderSide.Buy : CryptoOrderSide.Sell;

            var qty = Math.Abs(amountOrig.Value);
            var rem = amountRem.HasValue ? Math.Abs(amountRem.Value) : qty;
            var filled = Math.Max(0m, qty - rem);

            var statusStr = GetStringSafe(arr, 13) ?? string.Empty;
            var status = MapBitfinexStatus(statusStr);

            var createdUtc = FromUnixMsToUtc(arr, 4) ?? DateTime.UtcNow;
            var updatedUtc = FromUnixMsToUtc(arr, 5);

            return new OpenOrderInfo(
                ExchangeOrderId: idStr!,
                Symbol: cryptoSymbol,
                Side: side,
                Status: status,
                Price: price ?? 0m,
                Quantity: qty,
                FilledQuantity: filled,
                CreatedUtc: createdUtc,
                UpdatedUtc: updatedUtc
            );
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[BITFINEX-REST] GetOrderAsync parse failed. Raw={Raw}", Trunc(json, 1500));
            return none;
        }
    }

    /// <summary>
    /// Fallback lookup: proverava trades history i vraÄ‡a poslednji trade za konkretan order ID.
    /// Koristi se kada private WS event izostane i GetOrderAsync ne vrati final status.
    /// </summary>
    public async Task<OrderTradeSnapshot?> GetLatestTradeForOrderAsync(string exchangeOrderId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_apiSecret))
            return null;

        if (!long.TryParse(exchangeOrderId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
            return null;

        const string path = "/v2/auth/r/trades/hist";
        var payload = new
        {
            limit = 250,
            sort = -1
        };

        var json = await SendAuthAsync(path, payload, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
                return null;

            OrderTradeSnapshot? best = null;

            foreach (var arr in root.EnumerateArray())
            {
                if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() < 6)
                    continue;

                var orderIdStr = GetLongAsStringSafe(arr, 3);
                if (!string.Equals(orderIdStr, exchangeOrderId, StringComparison.Ordinal))
                    continue;

                var amount = GetDecimalSafe(arr, 4);
                var px = GetDecimalSafe(arr, 5);
                var ts = FromUnixMsToUtc(arr, 2);

                if (!amount.HasValue || !px.HasValue || !ts.HasValue)
                    continue;

                var qty = Math.Abs(amount.Value);
                if (qty <= 0m || px.Value <= 0m)
                    continue;

                var snapshot = new OrderTradeSnapshot(qty, px.Value, ts.Value);
                if (best is null || snapshot.TimestampUtc > best.TimestampUtc)
                    best = snapshot;
            }

            return best;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[BITFINEX-REST] GetLatestTradeForOrderAsync parse failed id={OrderId} raw={Raw}",
                exchangeOrderId, Trunc(json, 1200));
            return null;
        }
    }

    // --------------------------------------------------------------------
    // Helpers
    // --------------------------------------------------------------------

    private static bool TryParseBfxError(string json, out string message)
    {
        message = string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array &&
                root.GetArrayLength() >= 3 &&
                root[0].ValueKind == JsonValueKind.String &&
                string.Equals(root[0].GetString(), "error", StringComparison.OrdinalIgnoreCase))
            {
                // root[1] = error code, root[2] = message
                var code = root[1].ValueKind == JsonValueKind.Number && root[1].TryGetInt32(out var c) 
                    ? c.ToString(CultureInfo.InvariantCulture) 
                    : root[1].ValueKind == JsonValueKind.String ? root[1].GetString() ?? "" : "";
                var msg = root[2].ValueKind == JsonValueKind.String ? root[2].GetString() ?? "" : "";
                message = string.IsNullOrWhiteSpace(code) 
                    ? msg 
                    : $"[{code}] {msg}";
                return !string.IsNullOrWhiteSpace(msg);
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
    private string? CreateSignature(string sigPayload)
    {
        if (string.IsNullOrWhiteSpace(_apiSecret))
            return null;

        var keyBytes = Encoding.UTF8.GetBytes(_apiSecret);
        var payloadBytes = Encoding.UTF8.GetBytes(sigPayload);

        byte[] hash;
        using (var hmac = new HMACSHA384(keyBytes))
        {
            hash = hmac.ComputeHash(payloadBytes);
        }

        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
            sb.AppendFormat("{0:x2}", b);

        return sb.ToString();
    }
    private static string? GetStringSafe(JsonElement arr, int index)
    {
        if (arr.ValueKind != JsonValueKind.Array || index >= arr.GetArrayLength())
            return null;

        var el = arr[index];
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }
    private static string? GetLongAsStringSafe(JsonElement arr, int index)
    {
        if (arr.ValueKind != JsonValueKind.Array || index >= arr.GetArrayLength())
            return null;

        var el = arr[index];

        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var v))
            return v.ToString(CultureInfo.InvariantCulture);

        if (el.ValueKind == JsonValueKind.String)
            return el.GetString();

        return null;
    }
    private static decimal? GetDecimalSafe(JsonElement arr, int index)
    {
        if (arr.ValueKind != JsonValueKind.Array || index >= arr.GetArrayLength())
            return null;

        var el = arr[index];

        if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var d))
            return d;

        if (el.ValueKind == JsonValueKind.String &&
            decimal.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ds))
            return ds;

        return null;
    }
    private static DateTime? FromUnixMsToUtc(JsonElement arr, int index)
    {
        if (arr.ValueKind != JsonValueKind.Array || index >= arr.GetArrayLength())
            return null;

        var el = arr[index];
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var ms) && ms > 0)
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

        return null;
    }
    private static CryptoOrderStatus MapBitfinexStatus(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return CryptoOrderStatus.Unknown;

        if (s.IndexOf("EXECUTED", StringComparison.OrdinalIgnoreCase) >= 0)
            return CryptoOrderStatus.Filled;

        if (s.IndexOf("CANCELED", StringComparison.OrdinalIgnoreCase) >= 0 ||
            s.IndexOf("CANCELLED", StringComparison.OrdinalIgnoreCase) >= 0)
            return CryptoOrderStatus.Canceled;

        if (s.IndexOf("REJECT", StringComparison.OrdinalIgnoreCase) >= 0)
            return CryptoOrderStatus.Rejected;

        if (s.IndexOf("PART", StringComparison.OrdinalIgnoreCase) >= 0)
            return CryptoOrderStatus.PartiallyFilled;

        if (s.IndexOf("ACTIVE", StringComparison.OrdinalIgnoreCase) >= 0)
            return CryptoOrderStatus.New;

        return CryptoOrderStatus.Unknown;
    }
    private bool TryResolveCryptoSymbolFromNative(string native, out CryptoSymbol symbol)
    {
        symbol = default!;

        if (string.IsNullOrWhiteSpace(native))
            return false;

        // Bitfinex spot symbols: "tETHUSD", "tBTCUST", ...
        if (native[0] != 't')
            return false;

        var rest = native.Substring(1);
        // Bitfinex neke spot parove vraca sa ":" separatorom (npr. XAUT:UST).
        var restNormalized = rest.Replace(":", string.Empty, StringComparison.Ordinal);

        // Poznati quote-ovi (duÅ¾i prvo da USDT ne upadne kao USD)
        var knownQuotes = new[] { "USDT", "USDC", "UST", "USD", "EUR", "GBP", "JPY" };

        string? quote = knownQuotes.FirstOrDefault(q => restNormalized.EndsWith(q, StringComparison.OrdinalIgnoreCase));
        if (quote is null)
        {
            // fallback: zadnja 3 slova
            if (restNormalized.Length < 6) return false;
            quote = restNormalized.Substring(restNormalized.Length - 3);
        }

        var baseAsset = restNormalized.Substring(0, restNormalized.Length - quote.Length);
        if (string.IsNullOrWhiteSpace(baseAsset))
            return false;

        // Normalizacija: Bitfinex "UST" tretiraj kao "USDT" interno
        if (quote.Equals("UST", StringComparison.OrdinalIgnoreCase))
            quote = "USDT";

        symbol = new CryptoSymbol(
            ExchangeId: CryptoExchangeId.Bitfinex,
            BaseAsset: baseAsset,
            QuoteAsset: quote,
            NativeSymbol: native
        );

        return true;
    }
    private static string Trunc(string? s, int max)
    {
        if (string.IsNullOrEmpty(s))
            return string.Empty;

        if (s.Length <= max)
            return s;

        return s.Substring(0, max) + "...(truncated)";
    }
    private static string MapQuoteForBitfinex(string quote)
    {
        if (string.IsNullOrWhiteSpace(quote))
            return quote;

        // Bitfinex Äesto koristi UST za Tether (USDT).
        if (quote.Equals("USDT", StringComparison.OrdinalIgnoreCase))
            return "UST";

        return quote;
    }

    private static string ResolvePairForOrder(CryptoSymbol symbol)
    {
        if (!string.IsNullOrWhiteSpace(symbol.NativeSymbol))
            return symbol.NativeSymbol.Trim();

        var baseAsset = symbol.BaseAsset;
        var quoteAsset = MapQuoteForBitfinex(symbol.QuoteAsset);
        return $"t{baseAsset}{quoteAsset}";
    }
}

