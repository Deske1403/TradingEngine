#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Denis.TradingEngine.Core.Crypto;
using Denis.TradingEngine.Exchange.Crypto.Abstractions;
using Denis.TradingEngine.Exchange.Crypto.Common;
using Denis.TradingEngine.Exchange.Crypto.Kraken.Config;
using Serilog;

namespace Denis.TradingEngine.Exchange.Crypto.Kraken;

/// <summary>
/// REST trading API za Kraken.
/// - Private endpointi: /0/private/*
/// - API-Key i API-Sign headeri
/// - API-Sign = base64( HMAC-SHA512( path + SHA256(nonce + data), base64Decode(ApiSecret) ) )
/// Ovde koristimo JSON telo (kao u njihovom novom primeru) za sve private pozive.
/// </summary>
public sealed class KrakenTradingApi : RestClientBase, ICryptoTradingApi
{
    private readonly ILogger _log;
    private readonly HttpClient _http;

    private readonly string _apiKey;
    private readonly byte[] _apiSecretBytes;

    public CryptoExchangeId ExchangeId => CryptoExchangeId.Kraken;

    /// <summary>
    /// Stari ctor, ako se koristi negde.
    /// </summary>
    public KrakenTradingApi(string baseUrl, ILogger log)
        : base(baseUrl, log)
    {
        _log = log;
        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };

        _apiKey = string.Empty;
        _apiSecretBytes = Array.Empty<byte>();
    }

    /// <summary>
    /// Novi ctor koji koristi KrakenApiSettings iz appsettings.crypto.kraken.json.
    /// </summary>
    public KrakenTradingApi(KrakenApiSettings settings, ILogger log)
        : base(settings.BaseUrl, log)
    {
        _log = log;
        _http = new HttpClient { BaseAddress = new Uri(settings.BaseUrl) };

        _apiKey = settings.ApiKey ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(settings.ApiSecret))
        {
            try
            {
                _apiSecretBytes = Convert.FromBase64String(settings.ApiSecret);
            }
            catch (FormatException)
            {
                _log.Error("[KRAKEN-REST] ApiSecret nije validan Base64 string.");
                _apiSecretBytes = Array.Empty<byte>();
            }
        }
        else
        {
            _apiSecretBytes = Array.Empty<byte>();
        }
    }

    // =========================================================
    //  PLACE LIMIT ORDER
    // =========================================================
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
        if (string.IsNullOrWhiteSpace(_apiKey) || _apiSecretBytes.Length == 0)
        {
            _log.Warning("[KRAKEN-REST] ApiKey/ApiSecret nisu podešeni. Ne mogu da pošaljem nalog.");
            return new PlaceOrderResult(false, null, "Missing API key/secret");
        }

        const string path = "/0/private/AddOrder";

        // Za pair koristimo NativeSymbol iz configa: "XBT/USD", "ETH/USD" itd.
        // Kraken API za JSON telo dozvoljava string "pair": "XBT/USD" ili altname "XBTUSD".
        var pair = symbol.NativeSymbol;

        var sideStr   = side == CryptoOrderSide.Buy ? "buy" : "sell";
        var priceStr  = price.ToString(CultureInfo.InvariantCulture);
        var volumeStr = quantity.ToString(CultureInfo.InvariantCulture);

        var body = new Dictionary<string, object>
        {
            ["pair"]      = pair,
            ["type"]      = sideStr,
            ["ordertype"] = "limit",
            ["price"]     = priceStr,
            ["volume"]    = volumeStr
        };

        _log.Information(
            "[KRAKEN-REST] AddOrder: pair={Pair}, side={Side}, price={Price}, volume={Volume}",
            pair, sideStr, priceStr, volumeStr);

        var json = await SendPrivateAsync(path, body, ct);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new PlaceOrderResult(false, null, "Empty response from AddOrder");
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // error array
            if (root.TryGetProperty("error", out var errElem) &&
                errElem.ValueKind == JsonValueKind.Array &&
                errElem.GetArrayLength() > 0)
            {
                var errStr = errElem.ToString();
                _log.Warning("[KRAKEN-REST] AddOrder error: {Err}", errStr);
                return new PlaceOrderResult(false, null, errStr);
            }

            if (!root.TryGetProperty("result", out var resElem) ||
                resElem.ValueKind != JsonValueKind.Object)
            {
                _log.Warning("[KRAKEN-REST] AddOrder bez result: {Json}", json);
                return new PlaceOrderResult(false, null, "No result in AddOrder response");
            }

            string? orderId = null;

            // txid je obično array stringova
            if (resElem.TryGetProperty("txid", out var txidElem) &&
                txidElem.ValueKind == JsonValueKind.Array &&
                txidElem.GetArrayLength() > 0)
            {
                orderId = txidElem[0].GetString();
            }

            if (string.IsNullOrWhiteSpace(orderId))
            {
                _log.Warning("[KRAKEN-REST] AddOrder: nema txid u result: {Json}", json);
                return new PlaceOrderResult(false, null, "No txid in AddOrder result");
            }

            _log.Information("[KRAKEN-REST] AddOrder OK, txid={Txid}", orderId);
            return new PlaceOrderResult(true, orderId, null);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[KRAKEN-REST] Greška pri parsiranju AddOrder JSON-a.");
            return new PlaceOrderResult(false, null, "Exception parsing AddOrder response");
        }
    }

    public Task<PlaceOrderResult> PlaceStopOrderAsync(CryptoSymbol symbol, CryptoOrderSide side, decimal quantity, decimal stopPrice,
        decimal? limitPrice, CancellationToken ct, int flags = 0, int? gid = null)
    {
        throw new NotImplementedException();
    }

    // =========================================================
    //  CANCEL ORDER
    // =========================================================
    public async Task<bool> CancelOrderAsync(string exchangeOrderId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || _apiSecretBytes.Length == 0)
        {
            _log.Warning("[KRAKEN-REST] ApiKey/ApiSecret nisu podešeni. Preskačem CancelOrder.");
            return false;
        }

        const string path = "/0/private/CancelOrder";

        var body = new Dictionary<string, object>
        {
            // Prema dokumentaciji: param se zove "txid" ili "ordertype" u nekim varijantama,
            // ali klasično je "txid": "<order-id>"
            ["txid"] = exchangeOrderId
        };

        _log.Information("[KRAKEN-REST] CancelOrder txid={Txid}", exchangeOrderId);

        var json = await SendPrivateAsync(path, body, ct);
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errElem) &&
                errElem.ValueKind == JsonValueKind.Array &&
                errElem.GetArrayLength() > 0)
            {
                _log.Warning("[KRAKEN-REST] CancelOrder error: {Err}", errElem.ToString());
                return false;
            }

            if (!root.TryGetProperty("result", out var resElem) ||
                resElem.ValueKind != JsonValueKind.Object)
            {
                _log.Warning("[KRAKEN-REST] CancelOrder bez result: {Json}", json);
                return false;
            }

            // tipično: { "count": 1, "pending": ... }
            if (resElem.TryGetProperty("count", out var countElem) &&
                countElem.ValueKind == JsonValueKind.Number &&
                countElem.GetInt32() > 0)
            {
                _log.Information("[KRAKEN-REST] CancelOrder uspešan, count={Count}", countElem.GetInt32());
                return true;
            }

            _log.Warning("[KRAKEN-REST] CancelOrder result bez count>0: {Json}", json);
            return false;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[KRAKEN-REST] Greška pri parsiranju CancelOrder JSON-a.");
            return false;
        }
    }

    // =========================================================
    //  OPEN ORDERS / BALANCES (light)
    // =========================================================
    public async Task<IReadOnlyList<OpenOrderInfo>> GetOpenOrdersAsync(CancellationToken ct)
    {
        const string path = "/0/private/OpenOrders";
        IReadOnlyList<OpenOrderInfo> empty = Array.Empty<OpenOrderInfo>();

        if (string.IsNullOrWhiteSpace(_apiKey) || _apiSecretBytes.Length == 0)
        {
            _log.Warning("[KRAKEN-REST] ApiKey/ApiSecret nisu podešeni. Preskačem OpenOrders.");
            return empty;
        }

        var json = await SendPrivateAsync(path, new Dictionary<string, object>(), ct);
        if (string.IsNullOrWhiteSpace(json))
            return empty;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // error array
            if (root.TryGetProperty("error", out var errElem) &&
                errElem.ValueKind == JsonValueKind.Array &&
                errElem.GetArrayLength() > 0)
            {
                _log.Warning("[KRAKEN-REST] OpenOrders error: {Err}", errElem.ToString());
                return empty;
            }

            if (!root.TryGetProperty("result", out var resElem) ||
                resElem.ValueKind != JsonValueKind.Object)
            {
                _log.Warning("[KRAKEN-REST] OpenOrders bez result: {Json}", json);
                return empty;
            }

            // REST: result.open = { "<orderId>": { ... }, ... }
            if (!resElem.TryGetProperty("open", out var openElem) ||
                openElem.ValueKind != JsonValueKind.Object)
            {
                _log.Information("[KRAKEN-REST] OpenOrders: nema result.open (0 naloga).");
                return empty;
            }

            var orders = new List<OpenOrderInfo>();

            foreach (var orderProp in openElem.EnumerateObject())
            {
                var orderId = orderProp.Name;
                var o = orderProp.Value;
                if (o.ValueKind != JsonValueKind.Object)
                    continue;

                // status
                var statusRaw = o.TryGetProperty("status", out var st) ? st.GetString() : null;

                // descr
                string? pair = null;
                string? sideRaw = null;
                string? priceRaw = null;

                if (o.TryGetProperty("descr", out var descr) && descr.ValueKind == JsonValueKind.Object)
                {
                    pair = descr.TryGetProperty("pair", out var p) ? p.GetString() : null;
                    sideRaw = descr.TryGetProperty("type", out var t) ? t.GetString() : null;
                    priceRaw = descr.TryGetProperty("price", out var pr) ? pr.GetString() : null;
                }

                // vol / vol_exec
                var volRaw = o.TryGetProperty("vol", out var volE) ? volE.GetString() : null;
                var volExecRaw = o.TryGetProperty("vol_exec", out var ve) ? ve.GetString() : null;

                var qty = ParseDec(volRaw);
                var filled = ParseDec(volExecRaw);

                // price: prefer descr.price, fallback to top-level price/limitprice if present
                var price = ParseDec(priceRaw);
                if (price <= 0m)
                {
                    var topPriceRaw =
                        (o.TryGetProperty("price", out var tp) ? tp.GetString() : null) ??
                        (o.TryGetProperty("limitprice", out var lp) ? lp.GetString() : null);

                    price = ParseDec(topPriceRaw);
                }

                // times
                var opentmRaw = o.TryGetProperty("opentm", out var ot) ? ot.GetString() : null;
                var createdUtc = ParseUnixSecondsToUtc(opentmRaw) ?? DateTime.UtcNow;

                DateTime? updatedUtc = null;
                if (o.TryGetProperty("lastupdated", out var lu))
                    updatedUtc = ParseUnixSecondsToUtc(lu.GetString());

                // side
                var side = (sideRaw ?? "").Trim().ToLowerInvariant() switch
                {
                    "buy" => CryptoOrderSide.Buy,
                    "sell" => CryptoOrderSide.Sell,
                    _ => CryptoOrderSide.Buy
                };

                // status -> CryptoOrderStatus
                var status = MapKrakenStatus(statusRaw, qty, filled);

                // symbol
                // Kraken vraća "XBT/USD" itd. (što ti već koristiš kao NativeSymbol).
                var (baseAsset, quoteAsset) = SplitPair(pair);
                var symbol = new CryptoSymbol(
                    ExchangeId: CryptoExchangeId.Kraken,
                    BaseAsset: baseAsset,
                    QuoteAsset: quoteAsset,
                    NativeSymbol: pair ?? string.Empty
                );

                orders.Add(new OpenOrderInfo(
                    ExchangeOrderId: orderId,
                    Symbol: symbol,
                    Side: side,
                    Status: status,
                    Price: price,
                    Quantity: qty,
                    FilledQuantity: filled,
                    CreatedUtc: createdUtc,
                    UpdatedUtc: updatedUtc
                ));
            }

            _log.Information("[KRAKEN-REST] OpenOrders parsed: {Count}", orders.Count);
            return orders;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[KRAKEN-REST] Greška pri parsiranju OpenOrders JSON-a.");
            return empty;
        }

        static decimal ParseDec(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0m;
            return decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0m;
        }

        static DateTime? ParseUnixSecondsToUtc(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var sec)) return null;
            var ms = (long)Math.Round(sec * 1000.0, MidpointRounding.AwayFromZero);
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
        }

        static (string baseAsset, string quoteAsset) SplitPair(string? pair)
        {
            if (string.IsNullOrWhiteSpace(pair)) return (string.Empty, string.Empty);

            // očekujemo "XBT/USD" (kao u docs primerima)
            var parts = pair.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2) return (parts[0], parts[1]);

            // fallback: ne pokušavamo pametno da pogađamo "XBTUSD"
            return (pair, string.Empty);
        }

        static CryptoOrderStatus MapKrakenStatus(string? statusRaw, decimal qty, decimal filled)
        {
            var s = (statusRaw ?? "").Trim().ToLowerInvariant();

            if (s == "open")
                return filled > 0m ? CryptoOrderStatus.PartiallyFilled : CryptoOrderStatus.New;

            if (s is "canceled" or "cancelled")
                return CryptoOrderStatus.Canceled;

            if (s == "expired")
                return CryptoOrderStatus.Expired;

            if (s == "rejected")
                return CryptoOrderStatus.Rejected;

            if (s == "closed")
            {
                // closed može biti fill ili cancel; heuristika:
                if (qty > 0m && filled >= qty)
                    return CryptoOrderStatus.Filled;

                return CryptoOrderStatus.Canceled;
            }

            return CryptoOrderStatus.Unknown;
        }
    }


    public async Task<IReadOnlyList<BalanceInfo>> GetBalancesAsync(CancellationToken ct)
    {
        IReadOnlyList<BalanceInfo> empty = Array.Empty<BalanceInfo>();

        if (string.IsNullOrWhiteSpace(_apiKey) || _apiSecretBytes.Length == 0)
        {
            _log.Warning("[KRAKEN-REST] ApiKey/ApiSecret nisu podešeni. Preskačem Balance.");
            return empty;
        }

        const string path = "/0/private/Balance";

        var json = await SendPrivateAsync(path, new Dictionary<string, object>(), ct);
        if (string.IsNullOrWhiteSpace(json))
            return empty;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errElem) &&
                errElem.ValueKind == JsonValueKind.Array &&
                errElem.GetArrayLength() > 0)
            {
                _log.Warning("[KRAKEN-REST] Balance error: {Err}", errElem.ToString());
                return empty;
            }

            if (!root.TryGetProperty("result", out var resElem) ||
                resElem.ValueKind != JsonValueKind.Object)
            {
                _log.Warning("[KRAKEN-REST] Balance bez result: {Json}", json);
                return empty;
            }

            var balances = new List<BalanceInfo>();

            // Kraken vraća: { "result": { "ZUSD": "1000.00", "XXBT": "0.5", ... } }
            foreach (var prop in resElem.EnumerateObject())
            {
                var asset = prop.Name; // "ZUSD", "XXBT", "XETH", itd.
                if (prop.Value.ValueKind == JsonValueKind.String &&
                    decimal.TryParse(prop.Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var total))
                {
                    // Kraken ne razlikuje "free" i "locked" u Balance endpoint-u
                    // Svi novci su "free" (available), locked se vidi u OpenOrders
                    balances.Add(new BalanceInfo(
                        ExchangeId: CryptoExchangeId.Kraken,
                        Asset: asset,
                        Free: total,
                        Locked: 0m
                    ));
                }
            }

            _log.Information("[KRAKEN-REST] Balance parsed: {Count} assets", balances.Count);
            return balances;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[KRAKEN-REST] Greška pri parsiranju Balance JSON-a.");
            return empty;
        }
    }

    // Helper koji već koristiš u Program.cs
    public Task<string> GetDepositMethodsRawAsync(string asset, CancellationToken ct)
    {
        const string path = "/0/private/DepositMethods";

        var body = new Dictionary<string, object>
        {
            ["asset"] = asset
        };

        return SendPrivateAsync(path, body, ct);
    }

    // =========================================================
    //  PRIVATE HELPER: SendPrivateAsync + sign
    // =========================================================
    private static readonly JsonSerializerOptions KrakenJson = new()
    {
        // Bitno: stabilno i “JS-friendly” (manje eskapovanja)
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    private async Task<string> SendPrivateAsync(
        string path,
        Dictionary<string, object> body,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || _apiSecretBytes.Length == 0)
        {
            _log.Warning("[KRAKEN-REST] ApiKey/ApiSecret nisu podešeni. Preskačem {Path}", path);
            return string.Empty;
        }

        // 1) nonce (ako već nije setovan)
        if (!body.TryGetValue("nonce", out var nonceObj) || nonceObj is null || string.IsNullOrWhiteSpace(nonceObj.ToString()))
        {
            var gen = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            body["nonce"] = gen;
            nonceObj = gen;
        }
        var nonce = nonceObj!.ToString()!;

        // 2) stabilizuj body: sort keys + sve u string
        var sorted = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var kv in body)
            sorted[kv.Key] = kv.Value is null ? "" : kv.Value.ToString()!;

        var bodyString = JsonSerializer.Serialize(sorted, KrakenJson);
        var queryString = string.Empty;
        var data = queryString + bodyString;

        // 3) API-Sign
        var apiSign = CreateApiSign(path, nonce, data);
        if (apiSign is null)
        {
            _log.Error("[KRAKEN-REST] Ne mogu da napravim API-Sign za {Path}", path);
            return string.Empty;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(bodyString, Encoding.UTF8, "application/json")
        };

        req.Headers.Add("API-Key", _apiKey);
        req.Headers.Add("API-Sign", apiSign);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var content = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        _log.Information("[KRAKEN-REST] ← {Path} status: {StatusCode}", path, resp.StatusCode);
        _log.Information("[KRAKEN-REST] ← {Path} body: {Body}", path, content);

        return content;
    }


    /// <summary>
    /// API-Sign = base64( HMAC-SHA512( path + SHA256(nonce + data), base64Decode(ApiSecret) ) )
    /// </summary>
    private string? CreateApiSign(string path, string nonce, string data)
    {
        if (_apiSecretBytes.Length == 0)
        {
            _log.Error("[KRAKEN-REST] Nema apiSecret bajtova za HMAC");
            return null;
        }

        // sha256( nonce + data )
        var toHash = nonce + data;
        var hash256 = SHA256.HashData(Encoding.UTF8.GetBytes(toHash));

        // message = path + hash256
        var pathBytes = Encoding.UTF8.GetBytes(path);
        var buffer = new byte[pathBytes.Length + hash256.Length];
        Buffer.BlockCopy(pathBytes, 0, buffer, 0, pathBytes.Length);
        Buffer.BlockCopy(hash256, 0, buffer, pathBytes.Length, hash256.Length);

        // HMAC-SHA512( message, base64Decode(secret) )
        byte[] hmac;
        using (var hmacSha512 = new HMACSHA512(_apiSecretBytes))
        {
            hmac = hmacSha512.ComputeHash(buffer);
        }

        return Convert.ToBase64String(hmac);
    }
}