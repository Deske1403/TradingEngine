#nullable enable

using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Denis.TradingEngine.Exchange.Crypto.Bitfinex.Funding;
using Denis.TradingEngine.Exchange.Crypto.Bitfinex.Funding.Api;
using Denis.TradingEngine.Exchange.Crypto.Bitfinex.Funding.Models;
using Serilog;

namespace Denis.TradingEngine.Exchange.Crypto.Bitfinex.Funding.Stream;

public sealed class BitfinexFundingPrivateWebSocketFeed : IAsyncDisposable
{
    private readonly Uri _wsUri;
    private readonly string _apiKey;
    private readonly string _apiSecret;
    private readonly ILogger _log;
    private readonly string[] _filters;
    private readonly BitfinexAuthNonceProvider _nonceProvider;

    private ClientWebSocket? _ws;

    public event Action<FundingOfferInfo[]>? OfferSnapshot;
    public event Action<FundingOfferInfo>? OfferNew;
    public event Action<FundingOfferInfo>? OfferUpdate;
    public event Action<FundingOfferInfo>? OfferClose;
    public event Action<FundingWalletBalance[]>? WalletSnapshot;
    public event Action<FundingWalletBalance>? WalletUpdate;
    public event Action<string>? Notification;

    public DateTime LastMessageUtc { get; private set; }

    public BitfinexFundingPrivateWebSocketFeed(
        string wsUrl,
        string apiKey,
        string apiSecret,
        IEnumerable<string> fundingSymbols,
        ILogger log,
        BitfinexAuthNonceProvider? nonceProvider = null)
    {
        _wsUri = new Uri(wsUrl);
        _apiKey = apiKey ?? string.Empty;
        _apiSecret = apiSecret ?? string.Empty;
        _log = log ?? Log.ForContext<BitfinexFundingPrivateWebSocketFeed>();
        _nonceProvider = nonceProvider ?? new BitfinexAuthNonceProvider();

        var filters = new List<string> { "wallet", "notify" };
        foreach (var symbol in fundingSymbols.Where(static s => !string.IsNullOrWhiteSpace(s)))
        {
            filters.Add($"funding-{BitfinexFundingSymbolNormalizer.Normalize(symbol)}");
        }

        _filters = filters.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_apiSecret))
        {
            _log.Warning("[BFX-FUND-WS] Missing api key/secret. Private funding WS disabled.");
            return;
        }

        if (_ws is not null)
        {
            try { _ws.Dispose(); } catch { }
            _ws = null;
        }

        var ws = new ClientWebSocket();
        _ws = ws;

        try
        {
            await ws.ConnectAsync(_wsUri, ct).ConfigureAwait(false);
            _log.Information("[BFX-FUND-WS] Connected {Url}", _wsUri);

            await SendAuthAsync(ct).ConfigureAwait(false);

            var buffer = new byte[1024 * 64];
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var message = await ReceiveTextAsync(ws, buffer, ct).ConfigureAwait(false);
                if (message == null)
                    break;

                LastMessageUtc = DateTime.UtcNow;
                HandleMessage(message);
            }

            _log.Information("[BFX-FUND-WS] Loop ended. State={State}", ws.State);
        }
        finally
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "loop-end", CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch
            {
            }

            try { ws.Dispose(); } catch { }
            if (ReferenceEquals(_ws, ws))
                _ws = null;
        }
    }

    private async Task SendAuthAsync(CancellationToken ct)
    {
        var nonce = _nonceProvider.NextNonceMicros();
        var payload = "AUTH" + nonce;
        var signature = HmacSha384Hex(_apiSecret, payload);

        var auth = new
        {
            @event = "auth",
            apiKey = _apiKey,
            authNonce = nonce,
            authPayload = payload,
            authSig = signature,
            filter = _filters
        };

        var json = JsonSerializer.Serialize(auth);
        await SendTextAsync(_ws!, json, ct).ConfigureAwait(false);
        _log.Information("[BFX-FUND-WS] Sent auth filters={Filters}", string.Join(",", _filters));
    }

    private void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                HandleObjectMessage(root, json);
                return;
            }

            if (root.ValueKind == JsonValueKind.Array)
            {
                HandleArrayMessage(root, json);
                return;
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[BFX-FUND-WS] Failed to parse message raw={Raw}", Trunc(json, 1200));
        }
    }

    private void HandleObjectMessage(JsonElement root, string rawJson)
    {
        if (!root.TryGetProperty("event", out var ev))
        {
            _log.Debug("[BFX-FUND-WS] obj: {Json}", rawJson);
            return;
        }

        var eventType = ev.GetString();
        if (string.Equals(eventType, "auth", StringComparison.OrdinalIgnoreCase))
        {
            _log.Information("[BFX-FUND-WS] auth event: {Json}", rawJson);
            return;
        }

        if (string.Equals(eventType, "error", StringComparison.OrdinalIgnoreCase))
        {
            _log.Warning("[BFX-FUND-WS] error event: {Json}", rawJson);
            return;
        }

        if (string.Equals(eventType, "info", StringComparison.OrdinalIgnoreCase))
        {
            _log.Information("[BFX-FUND-WS] info event: {Json}", rawJson);
            return;
        }

        _log.Debug("[BFX-FUND-WS] event={Event} raw={Json}", eventType, rawJson);
    }

    private void HandleArrayMessage(JsonElement array, string rawJson)
    {
        if (array.GetArrayLength() < 2)
            return;

        var eventType = array[1].ValueKind == JsonValueKind.String ? array[1].GetString() : null;
        if (string.IsNullOrWhiteSpace(eventType) || string.Equals(eventType, "hb", StringComparison.OrdinalIgnoreCase))
            return;

        switch (eventType)
        {
            case "fos":
                HandleOfferSnapshot(array);
                break;
            case "fon":
                HandleOfferSingle(array, OfferNew, "NEW");
                break;
            case "fou":
                HandleOfferSingle(array, OfferUpdate, "UPDATE");
                break;
            case "foc":
                HandleOfferSingle(array, OfferClose, "CLOSE");
                break;
            case "ws":
                HandleWalletSnapshot(array);
                break;
            case "wu":
                HandleWalletUpdate(array);
                break;
            case "n":
                Notification?.Invoke(rawJson);
                _log.Information("[BFX-FUND-WS] notification: {Json}", rawJson);
                break;
            default:
                _log.Debug("[BFX-FUND-WS] event={Event} raw={Json}", eventType, rawJson);
                break;
        }
    }

    private void HandleOfferSnapshot(JsonElement array)
    {
        if (array.GetArrayLength() < 3 || array[2].ValueKind != JsonValueKind.Array)
            return;

        var offers = new List<FundingOfferInfo>();
        foreach (var item in array[2].EnumerateArray())
        {
            var offer = BitfinexFundingApi.ParseFundingOffer(item);
            if (offer is not null)
                offers.Add(offer);
        }

        OfferSnapshot?.Invoke(offers.ToArray());
        _log.Information("[BFX-FUND-WS] offer snapshot count={Count}", offers.Count);
    }

    private void HandleOfferSingle(JsonElement array, Action<FundingOfferInfo>? handler, string label)
    {
        if (array.GetArrayLength() < 3 || array[2].ValueKind != JsonValueKind.Array)
            return;

        var offer = BitfinexFundingApi.ParseFundingOffer(array[2]);
        if (offer is null)
            return;

        handler?.Invoke(offer);
        _log.Information("[BFX-FUND-WS] offer {Label} id={Id} symbol={Symbol} status={Status} amount={Amount} rate={Rate:E6}",
            label, offer.OfferId, offer.Symbol, offer.Status, offer.Amount, offer.Rate);
    }

    private void HandleWalletSnapshot(JsonElement array)
    {
        if (array.GetArrayLength() < 3 || array[2].ValueKind != JsonValueKind.Array)
            return;

        var wallets = new List<FundingWalletBalance>();
        foreach (var item in array[2].EnumerateArray())
        {
            var wallet = ParseWallet(item);
            if (wallet is not null)
                wallets.Add(wallet);
        }

        if (wallets.Count == 0)
            return;

        WalletSnapshot?.Invoke(wallets.ToArray());
        _log.Information("[BFX-FUND-WS] wallet snapshot count={Count}", wallets.Count);
    }

    private void HandleWalletUpdate(JsonElement array)
    {
        if (array.GetArrayLength() < 3)
            return;

        var wallet = ParseWallet(array[2]);
        if (wallet is null)
            return;

        WalletUpdate?.Invoke(wallet);
        _log.Information("[BFX-FUND-WS] wallet update wallet={Wallet} ccy={Currency} total={Total:F4} available={Available:F4}",
            wallet.WalletType, wallet.Currency, wallet.Total, wallet.Available);
    }

    private static FundingWalletBalance? ParseWallet(JsonElement walletArray)
    {
        if (walletArray.ValueKind != JsonValueKind.Array || walletArray.GetArrayLength() < 3)
            return null;

        var walletType = walletArray[0].ValueKind == JsonValueKind.String ? walletArray[0].GetString() : null;
        var currency = walletArray[1].ValueKind == JsonValueKind.String ? walletArray[1].GetString() : null;
        var total = ParseDecimalElement(walletArray, 2);
        var available = walletArray.GetArrayLength() >= 5 ? ParseDecimalElement(walletArray, 4) : null;

        if (string.IsNullOrWhiteSpace(walletType) || string.IsNullOrWhiteSpace(currency) || !total.HasValue)
            return null;

        var free = available ?? total.Value;
        var reserved = total.Value - free;
        if (reserved < 0m)
            reserved = 0m;

        return new FundingWalletBalance(
            WalletType: walletType!,
            Currency: currency!,
            Total: total.Value,
            Available: free,
            Reserved: reserved);
    }

    private static decimal? ParseDecimalElement(JsonElement array, int index)
    {
        if (array.GetArrayLength() <= index)
            return null;

        var element = array[index];
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var value))
            return value;

        if (element.ValueKind == JsonValueKind.String &&
            decimal.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string HmacSha384Hex(string secret, string payload)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var bytes = Encoding.UTF8.GetBytes(payload);

        using var hmac = new HMACSHA384(key);
        var hash = hmac.ComputeHash(bytes);

        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static async Task SendTextAsync(ClientWebSocket ws, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    private static async Task<string?> ReceiveTextAsync(ClientWebSocket ws, byte[] buffer, CancellationToken ct)
    {
        var segment = new ArraySegment<byte>(buffer);
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await ws.ReceiveAsync(segment, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                break;
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string Trunc(string? text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max)
            return text ?? string.Empty;

        return text.Substring(0, max) + "...";
    }

    public async ValueTask DisposeAsync()
    {
        if (_ws is not null)
        {
            try
            {
                if (_ws.State == WebSocketState.Open)
                {
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "dispose", CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch
            {
            }

            try { _ws.Dispose(); } catch { }
            _ws = null;
        }
    }
}
