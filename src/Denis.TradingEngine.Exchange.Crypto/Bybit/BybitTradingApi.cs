#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Denis.TradingEngine.Core.Crypto;
using Denis.TradingEngine.Exchange.Crypto.Abstractions;
using Denis.TradingEngine.Exchange.Crypto.Bybit.Config;
using Denis.TradingEngine.Exchange.Crypto.Common;
using Serilog;

namespace Denis.TradingEngine.Exchange.Crypto.Bybit;

public sealed class BybitTradingApi : RestClientBase, ICryptoTradingApi
{
    private readonly ILogger _log;
    private readonly BybitApiSettings _settings;

    public CryptoExchangeId ExchangeId => CryptoExchangeId.Bybit;

    public BybitTradingApi(BybitApiSettings settings, ILogger log)
        : base(settings.BaseUrl, log)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    // --------------------------------------------------------------------
    // Auth signing (Bybit V5)
    //  - GET:  ts + apiKey + recvWindow + queryString
    //  - POST: ts + apiKey + recvWindow + jsonBodyString
    // Signature: HMAC_SHA256(secret, signPayload) -> lower hex
    // Headers: X-BAPI-API-KEY, X-BAPI-TIMESTAMP, X-BAPI-RECV-WINDOW, X-BAPI-SIGN
    // Optional: X-BAPI-SIGN-TYPE: 2
    // --------------------------------------------------------------------
    protected override async Task PrepareRequestAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey) || string.IsNullOrWhiteSpace(_settings.ApiSecret))
            return;

        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var recvWindow = _settings.RecvWindowMs.ToString(CultureInfo.InvariantCulture);

        string queryString = "";
        if (request.RequestUri != null)
        {
            queryString = request.RequestUri.Query;
            if (!string.IsNullOrEmpty(queryString) && queryString[0] == '?')
                queryString = queryString.Substring(1);
        }

        string body = "";
        if (request.Content != null)
        {
            body = await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }

        var signPayload = request.Method == HttpMethod.Get
            ? ts + _settings.ApiKey + recvWindow + queryString
            : ts + _settings.ApiKey + recvWindow + body;

        var sign = ComputeHmacSha256HexLower(_settings.ApiSecret, signPayload);

        request.Headers.Remove("X-BAPI-API-KEY");
        request.Headers.Remove("X-BAPI-TIMESTAMP");
        request.Headers.Remove("X-BAPI-RECV-WINDOW");
        request.Headers.Remove("X-BAPI-SIGN");
        request.Headers.Remove("X-BAPI-SIGN-TYPE");

        request.Headers.Add("X-BAPI-API-KEY", _settings.ApiKey);
        request.Headers.Add("X-BAPI-TIMESTAMP", ts);
        request.Headers.Add("X-BAPI-RECV-WINDOW", recvWindow);
        request.Headers.Add("X-BAPI-SIGN", sign);
        request.Headers.Add("X-BAPI-SIGN-TYPE", "2");
    }

    private static string ComputeHmacSha256HexLower(string secret, string payload)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);

        byte[] hash;
        using (var hmac = new HMACSHA256(keyBytes))
        {
            hash = hmac.ComputeHash(payloadBytes);
        }

        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
            sb.AppendFormat("{0:x2}", b);

        return sb.ToString();
    }

    private string CategoryOrDefault()
        => string.IsNullOrWhiteSpace(_settings.DefaultCategory) ? "spot" : _settings.DefaultCategory;

    private bool HasKeys()
        => !string.IsNullOrWhiteSpace(_settings.ApiKey) && !string.IsNullOrWhiteSpace(_settings.ApiSecret);

    // --------------------------------------------------------------------
    // ICryptoTradingApi
    // --------------------------------------------------------------------

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
        if (!HasKeys())
        {
            _log.Warning("[BYBIT-REST] ApiKey/ApiSecret nisu podešeni. Ne mogu da pošaljem nalog.");
            return new PlaceOrderResult(false, null, "Missing API key/secret");
        }

        if (quantity <= 0m)
            return new PlaceOrderResult(false, null, "Invalid quantity");

        if (price <= 0m)
            return new PlaceOrderResult(false, null, "Invalid price");

        const string path = "/v5/order/create";

        var category = CategoryOrDefault();
        var sideStr = side == CryptoOrderSide.Buy ? "Buy" : "Sell";

        var payload = new
        {
            category,
            symbol = symbol.NativeSymbol,
            side = sideStr,
            orderType = "Limit",
            qty = quantity.ToString(CultureInfo.InvariantCulture),
            price = price.ToString(CultureInfo.InvariantCulture),
            timeInForce = "GTC"
        };

        try
        {
            var resp = await PostAsync<BybitBaseResponse<BybitPlaceOrderResult>>(path, payload, ct)
                .ConfigureAwait(false);

            if (resp.RetCode != 0)
                return new PlaceOrderResult(false, null, $"Bybit retCode={resp.RetCode} retMsg={resp.RetMsg}");

            var orderId = resp.Result?.OrderId;
            if (string.IsNullOrWhiteSpace(orderId))
                return new PlaceOrderResult(false, null, "Bybit: missing orderId in response");

            return new PlaceOrderResult(true, orderId, null);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[BYBIT-REST] PlaceLimitOrderAsync failed");
            return new PlaceOrderResult(false, null, ex.Message);
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
        if (!HasKeys())
        {
            _log.Warning("[BYBIT-REST] ApiKey/ApiSecret nisu podešeni. Ne mogu da pošaljem stop nalog.");
            return new PlaceOrderResult(false, null, "Missing API key/secret");
        }

        if (quantity <= 0m)
            return new PlaceOrderResult(false, null, "Invalid quantity");

        if (stopPrice <= 0m)
            return new PlaceOrderResult(false, null, "Invalid stop price");

        const string path = "/v5/order/create";

        var category = CategoryOrDefault();
        var sideStr = side == CryptoOrderSide.Buy ? "Buy" : "Sell";

        var lim = limitPrice ?? stopPrice;

        // V5: conditional/stop order = triggerPrice + orderFilter=StopOrder
        // price = limit price after trigger, orderType=Limit.
        var payload = new Dictionary<string, object?>
        {
            ["category"] = category,
            ["symbol"] = symbol.NativeSymbol,
            ["side"] = sideStr,
            ["orderType"] = "Limit",
            ["qty"] = quantity.ToString(CultureInfo.InvariantCulture),
            ["price"] = lim.ToString(CultureInfo.InvariantCulture),
            ["timeInForce"] = "GTC",

            ["triggerPrice"] = stopPrice.ToString(CultureInfo.InvariantCulture),
            ["triggerBy"] = "LastPrice",
            ["orderFilter"] = "StopOrder"
        };

        try
        {
            var resp = await PostAsync<BybitBaseResponse<BybitPlaceOrderResult>>(path, payload, ct)
                .ConfigureAwait(false);

            if (resp.RetCode != 0)
                return new PlaceOrderResult(false, null, $"Bybit retCode={resp.RetCode} retMsg={resp.RetMsg}");

            var orderId = resp.Result?.OrderId;
            if (string.IsNullOrWhiteSpace(orderId))
                return new PlaceOrderResult(false, null, "Bybit: missing orderId in response");

            return new PlaceOrderResult(true, orderId, null);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[BYBIT-REST] PlaceStopOrderAsync failed");
            return new PlaceOrderResult(false, null, ex.Message);
        }
    }

    public async Task<bool> CancelOrderAsync(string exchangeOrderId, CancellationToken ct)
    {
        const string path = "/v5/order/cancel";

        if (!HasKeys())
            return false;

        if (string.IsNullOrWhiteSpace(exchangeOrderId))
            return false;

        var category = CategoryOrDefault();

        var payload = new
        {
            category,
            orderId = exchangeOrderId
        };

        try
        {
            var resp = await PostAsync<BybitBaseResponse<object>>(path, payload, ct).ConfigureAwait(false);
            return resp.RetCode == 0;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[BYBIT-REST] CancelOrderAsync failed");
            return false;
        }
    }

    public async Task<IReadOnlyList<OpenOrderInfo>> GetOpenOrdersAsync(CancellationToken ct)
    {
        const string path = "/v5/order/realtime";

        if (!HasKeys())
            return Array.Empty<OpenOrderInfo>();

        var category = CategoryOrDefault();

        try
        {
            // openOnly:
            // 0 -> active orders
            // 1 -> final status in last 10 min
            // 2 -> both
            var resp = await GetAsync<BybitBaseResponse<JsonElement>>($"{path}?category={category}&openOnly=0&limit=50", ct)
                .ConfigureAwait(false);

            if (resp.RetCode != 0)
                return Array.Empty<OpenOrderInfo>();

            if (resp.Result.ValueKind == JsonValueKind.Undefined || resp.Result.ValueKind == JsonValueKind.Null)
                return Array.Empty<OpenOrderInfo>();

            if (!resp.Result.TryGetProperty("list", out var listElem) || listElem.ValueKind != JsonValueKind.Array)
                return Array.Empty<OpenOrderInfo>();

            var orders = new List<OpenOrderInfo>();

            foreach (var o in listElem.EnumerateArray())
            {
                var orderId = GetString(o, "orderId");
                var sym = GetString(o, "symbol");
                if (string.IsNullOrWhiteSpace(orderId) || string.IsNullOrWhiteSpace(sym))
                    continue;

                var sideStr = GetString(o, "side");
                var statusStr = GetString(o, "orderStatus");

                var price = GetDecimal(o, "price") ?? 0m;
                var qty = GetDecimal(o, "qty") ?? 0m;
                var cumExecQty = GetDecimal(o, "cumExecQty") ?? 0m;

                var side = string.Equals(sideStr, "Buy", StringComparison.OrdinalIgnoreCase)
                    ? CryptoOrderSide.Buy
                    : CryptoOrderSide.Sell;

                var status = MapBybitOrderStatus(statusStr);

                // Minimal: čuvamo native simbol, a base/quote možeš kasnije da razdvojiš iz sym string-a.
                var cryptoSymbol = new CryptoSymbol(
                    ExchangeId: CryptoExchangeId.Bybit,
                    BaseAsset: sym!,
                    QuoteAsset: string.Empty,
                    NativeSymbol: sym!
                );

                orders.Add(new OpenOrderInfo(
                    ExchangeOrderId: orderId!,
                    Symbol: cryptoSymbol,
                    Side: side,
                    Status: status,
                    Price: price,
                    Quantity: qty,
                    FilledQuantity: cumExecQty,
                    CreatedUtc: DateTime.UtcNow,
                    UpdatedUtc: null
                ));
            }

            return orders;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[BYBIT-REST] GetOpenOrdersAsync failed");
            return Array.Empty<OpenOrderInfo>();
        }
    }

    public async Task<IReadOnlyList<BalanceInfo>> GetBalancesAsync(CancellationToken ct)
    {
        const string path = "/v5/account/wallet-balance";

        if (!HasKeys())
            return Array.Empty<BalanceInfo>();

        try
        {
            var resp = await GetAsync<BybitBaseResponse<JsonElement>>($"{path}?accountType=UNIFIED", ct)
                .ConfigureAwait(false);

            if (resp.RetCode != 0)
                return Array.Empty<BalanceInfo>();

            if (resp.Result.ValueKind == JsonValueKind.Undefined || resp.Result.ValueKind == JsonValueKind.Null)
                return Array.Empty<BalanceInfo>();

            if (!resp.Result.TryGetProperty("list", out var listElem) || listElem.ValueKind != JsonValueKind.Array)
                return Array.Empty<BalanceInfo>();

            var balances = new List<BalanceInfo>();

            foreach (var acct in listElem.EnumerateArray())
            {
                if (!acct.TryGetProperty("coin", out var coinArr) || coinArr.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var c in coinArr.EnumerateArray())
                {
                    var asset = GetString(c, "coin");
                    if (string.IsNullOrWhiteSpace(asset))
                        continue;

                    var walletBalance = GetDecimal(c, "walletBalance");
                    var locked = GetDecimal(c, "locked");

                    if (!walletBalance.HasValue)
                        continue;

                    var total = walletBalance.Value;
                    var lockedVal = locked ?? 0m;
                    var free = total - lockedVal;

                    if (total == 0m && lockedVal == 0m)
                        continue;

                    balances.Add(new BalanceInfo(
                        ExchangeId: CryptoExchangeId.Bybit,
                        Asset: asset!,
                        Free: Math.Max(0m, free),
                        Locked: Math.Max(0m, lockedVal)
                    ));
                }
            }

            return balances;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[BYBIT-REST] GetBalancesAsync failed");
            return Array.Empty<BalanceInfo>();
        }
    }

    // --------------------------------------------------------------------
    // Helpers
    // --------------------------------------------------------------------

    private static string? GetString(JsonElement obj, string prop)
        => obj.TryGetProperty(prop, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static decimal? GetDecimal(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var e))
            return null;

        if (e.ValueKind == JsonValueKind.Number && e.TryGetDecimal(out var d))
            return d;

        if (e.ValueKind == JsonValueKind.String &&
            decimal.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ds))
            return ds;

        return null;
    }

    private static CryptoOrderStatus MapBybitOrderStatus(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return CryptoOrderStatus.Unknown;

        if (s.Equals("New", StringComparison.OrdinalIgnoreCase))
            return CryptoOrderStatus.New;

        if (s.IndexOf("Partially", StringComparison.OrdinalIgnoreCase) >= 0)
            return CryptoOrderStatus.PartiallyFilled;

        if (s.Equals("Filled", StringComparison.OrdinalIgnoreCase))
            return CryptoOrderStatus.Filled;

        if (s.IndexOf("Cancel", StringComparison.OrdinalIgnoreCase) >= 0)
            return CryptoOrderStatus.Canceled;

        if (s.IndexOf("Reject", StringComparison.OrdinalIgnoreCase) >= 0)
            return CryptoOrderStatus.Rejected;

        return CryptoOrderStatus.Unknown;
    }

    // --------------------------------------------------------------------
    // Minimal response models
    // --------------------------------------------------------------------
    private sealed class BybitBaseResponse<T>
    {
        public int RetCode { get; set; }
        public string? RetMsg { get; set; }
        public T? Result { get; set; }
        public long Time { get; set; }
    }

    private sealed class BybitPlaceOrderResult
    {
        public string? OrderId { get; set; }
        public string? OrderLinkId { get; set; }
    }
}
