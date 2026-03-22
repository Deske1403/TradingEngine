#nullable enable

using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Denis.TradingEngine.Exchange.Crypto.Bitfinex.Funding;
using Denis.TradingEngine.Exchange.Crypto.Bitfinex.Funding.Models;
using Serilog;

namespace Denis.TradingEngine.Exchange.Crypto.Bitfinex.Funding.Api;

public sealed class BitfinexFundingApi : IBitfinexFundingApi
{
    private readonly ILogger _log;
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _apiSecret;
    private readonly BitfinexAuthNonceProvider _nonceProvider;
    private readonly SemaphoreSlim _authRequestGate = new(1, 1);
    private int _missingAuthWarningLogged;

    public BitfinexFundingApi(
        string baseUrl,
        string apiKey,
        string apiSecret,
        ILogger log,
        BitfinexAuthNonceProvider? nonceProvider = null)
    {
        _log = log ?? Log.ForContext<BitfinexFundingApi>();
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };

        _apiKey = apiKey ?? string.Empty;
        _apiSecret = apiSecret ?? string.Empty;
        _nonceProvider = nonceProvider ?? new BitfinexAuthNonceProvider();
    }

    public async Task<IReadOnlyList<FundingWalletBalance>> GetWalletBalancesAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_apiSecret))
        {
            if (Interlocked.Exchange(ref _missingAuthWarningLogged, 1) == 0)
            {
                _log.Warning("[BFX-FUND] ApiKey/ApiSecret nisu podeseni. Funding wallet read vraca prazan rezultat.");
            }

            return Array.Empty<FundingWalletBalance>();
        }

        const string path = "/v2/auth/r/wallets";
        var json = await SendAuthAsync(path, new { }, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<FundingWalletBalance>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
                return Array.Empty<FundingWalletBalance>();

            var balances = new List<FundingWalletBalance>();
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 3)
                    continue;

                var walletType = item[0].ValueKind == JsonValueKind.String ? item[0].GetString() : null;
                var currency = item[1].ValueKind == JsonValueKind.String ? item[1].GetString() : null;
                var total = ParseDecimalElement(item, 2);
                var available = item.GetArrayLength() >= 5 ? ParseDecimalElement(item, 4) : null;

                if (string.IsNullOrWhiteSpace(walletType) || string.IsNullOrWhiteSpace(currency) || !total.HasValue)
                    continue;

                var free = available ?? total.Value;
                var reserved = total.Value - free;
                if (reserved < 0m)
                    reserved = 0m;

                balances.Add(new FundingWalletBalance(
                    WalletType: walletType!,
                    Currency: currency!,
                    Total: total.Value,
                    Available: free,
                    Reserved: reserved
                ));
            }

            return balances;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[BFX-FUND] Failed to parse wallet balances.");
            return Array.Empty<FundingWalletBalance>();
        }
    }

    public async Task<IReadOnlyList<FundingOfferInfo>> GetActiveOffersAsync(
        IReadOnlyCollection<string> symbols,
        CancellationToken ct)
    {
        if (symbols == null || symbols.Count == 0)
            return Array.Empty<FundingOfferInfo>();

        if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_apiSecret))
            return Array.Empty<FundingOfferInfo>();

        var normalized = symbols
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Select(BitfinexFundingSymbolNormalizer.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalized.Length == 0)
            return Array.Empty<FundingOfferInfo>();

        var tasks = normalized.Select(symbol => GetActiveOffersBySymbolAsync(symbol, ct)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.SelectMany(static x => x).ToArray();
    }

    public async Task<IReadOnlyList<FundingTickerSnapshot>> GetFundingTickerSnapshotsAsync(
        IReadOnlyCollection<string> symbols,
        CancellationToken ct)
    {
        if (symbols == null || symbols.Count == 0)
            return Array.Empty<FundingTickerSnapshot>();

        var normalized = symbols
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Select(BitfinexFundingSymbolNormalizer.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalized.Length == 0)
            return Array.Empty<FundingTickerSnapshot>();

        var json = await SendPublicGetAsync($"/v2/tickers?symbols={string.Join(",", normalized)}", ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<FundingTickerSnapshot>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
                return Array.Empty<FundingTickerSnapshot>();

            var snapshots = new List<FundingTickerSnapshot>();
            var nowUtc = DateTime.UtcNow;

            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 8)
                    continue;

                var symbol = item[0].ValueKind == JsonValueKind.String ? item[0].GetString() : null;
                if (string.IsNullOrWhiteSpace(symbol))
                    continue;

                snapshots.Add(new FundingTickerSnapshot(
                    Symbol: BitfinexFundingSymbolNormalizer.Normalize(symbol),
                    Frr: item.GetArrayLength() > 1 ? ParseDecimalElement(item, 1) : null,
                    BidRate: item.GetArrayLength() > 2 ? ParseDecimalElement(item, 2) ?? 0m : 0m,
                    BidPeriodDays: item.GetArrayLength() > 3 ? ParseIntElement(item, 3) ?? 0 : 0,
                    BidSize: item.GetArrayLength() > 4 ? ParseDecimalElement(item, 4) ?? 0m : 0m,
                    AskRate: item.GetArrayLength() > 5 ? ParseDecimalElement(item, 5) ?? 0m : 0m,
                    AskPeriodDays: item.GetArrayLength() > 6 ? ParseIntElement(item, 6) ?? 0 : 0,
                    AskSize: item.GetArrayLength() > 7 ? ParseDecimalElement(item, 7) ?? 0m : 0m,
                    TimestampUtc: nowUtc
                ));
            }

            return snapshots;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[BFX-FUND] Failed to parse public funding tickers.");
            return Array.Empty<FundingTickerSnapshot>();
        }
    }

    public async Task<IReadOnlyList<FundingCreditInfo>> GetActiveCreditsAsync(
        IReadOnlyCollection<string> symbols,
        CancellationToken ct)
    {
        return await GetFundingStateBySymbolAsync(symbols, "credits", ParseFundingCredit, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FundingCreditInfo>> GetCreditHistoryAsync(
        IReadOnlyCollection<string> symbols,
        CancellationToken ct)
    {
        return await GetFundingHistoryBySymbolAsync(symbols, "credits", ParseFundingCredit, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FundingLoanInfo>> GetActiveLoansAsync(
        IReadOnlyCollection<string> symbols,
        CancellationToken ct)
    {
        return await GetFundingStateBySymbolAsync(symbols, "loans", ParseFundingLoan, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FundingLoanInfo>> GetLoanHistoryAsync(
        IReadOnlyCollection<string> symbols,
        CancellationToken ct)
    {
        return await GetFundingHistoryBySymbolAsync(symbols, "loans", ParseFundingLoan, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FundingTradeInfo>> GetFundingTradeHistoryAsync(
        IReadOnlyCollection<string> symbols,
        CancellationToken ct)
    {
        return await GetFundingHistoryBySymbolAsync(symbols, "trades", ParseFundingTrade, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FundingLedgerEntry>> GetLedgerEntriesAsync(
        IReadOnlyCollection<string> currencies,
        CancellationToken ct)
    {
        if (currencies == null || currencies.Count == 0)
            return Array.Empty<FundingLedgerEntry>();

        if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_apiSecret))
            return Array.Empty<FundingLedgerEntry>();

        var normalized = currencies
            .Where(static c => !string.IsNullOrWhiteSpace(c))
            .Select(static c => c.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalized.Length == 0)
            return Array.Empty<FundingLedgerEntry>();

        var tasks = normalized.Select(currency => GetLedgerEntriesByCurrencyAsync(currency, ct)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.SelectMany(static x => x).ToArray();
    }

    public async Task<FundingOfferActionResult> SubmitOfferAsync(
        FundingOfferRequest request,
        CancellationToken ct)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));

        if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_apiSecret))
        {
            return CreateActionResult(
                action: "submit_offer",
                success: false,
                symbol: request.Symbol,
                offerId: null,
                status: "MISSING_AUTH",
                message: "Missing Bitfinex API key/secret.");
        }

        if (string.IsNullOrWhiteSpace(request.Symbol) ||
            request.Amount <= 0m ||
            request.Rate <= 0m ||
            request.PeriodDays <= 0)
        {
            return CreateActionResult(
                action: "submit_offer",
                success: false,
                symbol: request.Symbol,
                offerId: null,
                status: "INVALID_REQUEST",
                message: "Funding submit request is invalid.");
        }

        const string path = "/v2/auth/w/funding/offer/submit";
        var payload = new Dictionary<string, object>
        {
            ["type"] = string.IsNullOrWhiteSpace(request.OfferType)
                ? "LIMIT"
                : request.OfferType.Trim().ToUpperInvariant(),
            ["symbol"] = BitfinexFundingSymbolNormalizer.Normalize(request.Symbol),
            ["amount"] = decimal.Round(request.Amount, 8, MidpointRounding.ToZero).ToString(CultureInfo.InvariantCulture),
            ["rate"] = request.Rate.ToString(CultureInfo.InvariantCulture),
            ["period"] = request.PeriodDays,
            ["flags"] = request.Flags
        };

        var json = await SendAuthAsync(path, payload, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return CreateActionResult(
                action: "submit_offer",
                success: false,
                symbol: request.Symbol,
                offerId: null,
                status: "EMPTY_RESPONSE",
                message: "Bitfinex returned an empty response for funding offer submit.");
        }

        if (TryParseBfxError(json, out var submitError))
        {
            return CreateActionResult(
                action: "submit_offer",
                success: false,
                symbol: request.Symbol,
                offerId: null,
                status: "ERROR",
                message: submitError);
        }

        return ParseActionResponse(
            action: "submit_offer",
            symbol: request.Symbol,
            json: json,
            defaultSuccessMessage: "Funding offer submit request accepted.");
    }

    public async Task<FundingOfferActionResult> CancelOfferAsync(
        string symbol,
        string offerId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_apiSecret))
        {
            return CreateActionResult(
                action: "cancel_offer",
                success: false,
                symbol: symbol,
                offerId: offerId,
                status: "MISSING_AUTH",
                message: "Missing Bitfinex API key/secret.");
        }

        if (!long.TryParse(offerId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            return CreateActionResult(
                action: "cancel_offer",
                success: false,
                symbol: symbol,
                offerId: offerId,
                status: "INVALID_REQUEST",
                message: "Funding offer id is invalid.");
        }

        const string path = "/v2/auth/w/funding/offer/cancel";
        var json = await SendAuthAsync(path, new { id }, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return CreateActionResult(
                action: "cancel_offer",
                success: false,
                symbol: symbol,
                offerId: offerId,
                status: "EMPTY_RESPONSE",
                message: "Bitfinex returned an empty response for funding offer cancel.");
        }

        if (TryParseBfxError(json, out var cancelError))
        {
            var idempotent =
                cancelError.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cancelError.IndexOf("already", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cancelError.IndexOf("cancel", StringComparison.OrdinalIgnoreCase) >= 0;

            return CreateActionResult(
                action: "cancel_offer",
                success: idempotent,
                symbol: symbol,
                offerId: offerId,
                status: idempotent ? "SUCCESS" : "ERROR",
                message: cancelError);
        }

        return ParseActionResponse(
            action: "cancel_offer",
            symbol: symbol,
            json: json,
            defaultSuccessMessage: "Funding offer cancel request accepted.",
            fallbackOfferId: offerId);
    }

    private async Task<IReadOnlyList<FundingOfferInfo>> GetActiveOffersBySymbolAsync(string symbol, CancellationToken ct)
    {
        var path = $"/v2/auth/r/funding/offers/{BitfinexFundingSymbolNormalizer.Normalize(symbol)}";
        var json = await SendAuthAsync(path, new { }, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<FundingOfferInfo>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
                return Array.Empty<FundingOfferInfo>();

            var offers = new List<FundingOfferInfo>();
            foreach (var item in root.EnumerateArray())
            {
                var offer = ParseFundingOffer(item);
                if (offer is not null)
                    offers.Add(offer);
            }

            return offers;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[BFX-FUND] Failed to parse active funding offers for {Symbol}.", symbol);
            return Array.Empty<FundingOfferInfo>();
        }
    }

    private async Task<IReadOnlyList<T>> GetFundingStateBySymbolAsync<T>(
        IReadOnlyCollection<string> symbols,
        string stateKind,
        Func<JsonElement, T?> parser,
        CancellationToken ct)
        where T : class
    {
        if (symbols == null || symbols.Count == 0)
            return Array.Empty<T>();

        if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_apiSecret))
            return Array.Empty<T>();

        var normalized = symbols
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Select(BitfinexFundingSymbolNormalizer.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalized.Length == 0)
            return Array.Empty<T>();

        var tasks = normalized.Select(symbol => GetFundingStateBySymbolAsync(symbol, stateKind, parser, ct)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.SelectMany(static x => x).ToArray();
    }

    private async Task<IReadOnlyList<T>> GetFundingHistoryBySymbolAsync<T>(
        IReadOnlyCollection<string> symbols,
        string historyKind,
        Func<JsonElement, T?> parser,
        CancellationToken ct)
        where T : class
    {
        if (symbols == null || symbols.Count == 0)
            return Array.Empty<T>();

        if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_apiSecret))
            return Array.Empty<T>();

        var normalized = symbols
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Select(BitfinexFundingSymbolNormalizer.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalized.Length == 0)
            return Array.Empty<T>();

        var tasks = normalized.Select(symbol => GetFundingHistoryBySymbolAsync(symbol, historyKind, parser, ct)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.SelectMany(static x => x).ToArray();
    }

    private async Task<IReadOnlyList<T>> GetFundingStateBySymbolAsync<T>(
        string symbol,
        string stateKind,
        Func<JsonElement, T?> parser,
        CancellationToken ct)
        where T : class
    {
        var path = $"/v2/auth/r/funding/{stateKind}/{BitfinexFundingSymbolNormalizer.Normalize(symbol)}";
        return await GetArrayResponseAsync(path, new { }, parser, $"active funding {stateKind}", ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<T>> GetFundingHistoryBySymbolAsync<T>(
        string symbol,
        string historyKind,
        Func<JsonElement, T?> parser,
        CancellationToken ct)
        where T : class
    {
        var path = $"/v2/auth/r/funding/{historyKind}/{BitfinexFundingSymbolNormalizer.Normalize(symbol)}/hist";
        return await GetArrayResponseAsync(path, new { limit = 250 }, parser, $"funding {historyKind} history", ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<FundingLedgerEntry>> GetLedgerEntriesByCurrencyAsync(string currency, CancellationToken ct)
    {
        var path = $"/v2/auth/r/ledgers/{currency.Trim().ToUpperInvariant()}/hist";
        return await GetArrayResponseAsync(path, new { limit = 250 }, ParseLedgerEntry, $"ledger history for {currency}", ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<T>> GetArrayResponseAsync<T>(
        string path,
        object payload,
        Func<JsonElement, T?> parser,
        string logDescription,
        CancellationToken ct)
        where T : class
    {
        var json = await SendAuthAsync(path, payload, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<T>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
                return Array.Empty<T>();

            var items = new List<T>();
            foreach (var item in root.EnumerateArray())
            {
                var parsed = parser(item);
                if (parsed is not null)
                    items.Add(parsed);
            }

            return items;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[BFX-FUND] Failed to parse {Description}.", logDescription);
            return Array.Empty<T>();
        }
    }

    private async Task<string> SendPublicGetAsync(string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var content = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (resp.IsSuccessStatusCode)
            return content;

        _log.Warning("[BFX-FUND] Public GET failed path={Path} status={StatusCode} body={Body}",
            path, resp.StatusCode, Trunc(content, 800));
        return string.Empty;
    }

    private async Task<string> SendAuthAsync(string path, object payload, CancellationToken ct)
    {
        var bodyJson = JsonSerializer.Serialize(payload);
        await _authRequestGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                var nonce = _nonceProvider.NextNonceMicros();
                var signature = CreateSignature("/api" + path + nonce + bodyJson);

                using var req = new HttpRequestMessage(HttpMethod.Post, path)
                {
                    Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
                };

                req.Headers.Add("bfx-nonce", nonce);
                req.Headers.Add("bfx-apikey", _apiKey);
                req.Headers.Add("bfx-signature", signature);

                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                var content = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (resp.IsSuccessStatusCode)
                    return content;

                var isRateLimited =
                    resp.StatusCode == (HttpStatusCode)429 ||
                    content.IndexOf("ERR_RATE_LIMIT", StringComparison.OrdinalIgnoreCase) >= 0;

                if (isRateLimited && attempt < 3)
                {
                    var delay = attempt switch
                    {
                        1 => TimeSpan.FromSeconds(1),
                        2 => TimeSpan.FromSeconds(3),
                        _ => TimeSpan.FromSeconds(6)
                    };

                    _log.Warning("[BFX-FUND] Rate limit on {Path}. Retry after {Delay}.", path, delay);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    continue;
                }

                _log.Warning("[BFX-FUND] Auth POST failed path={Path} status={StatusCode} body={Body}",
                    path, resp.StatusCode, Trunc(content, 800));
                return string.Empty;
            }

            return string.Empty;
        }
        finally
        {
            _authRequestGate.Release();
        }
    }

    private FundingOfferActionResult ParseActionResponse(
        string action,
        string symbol,
        string json,
        string defaultSuccessMessage,
        string? fallbackOfferId = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array)
            {
                return CreateActionResult(
                    action: action,
                    success: false,
                    symbol: symbol,
                    offerId: fallbackOfferId,
                    status: "UNEXPECTED_RESPONSE",
                    message: "Funding action response was not an array.");
            }

            var status = GetStringSafe(root, 6);
            var text = GetStringSafe(root, 7);
            var payload = root.GetArrayLength() > 4 ? root[4] : default;
            var offer = TryParseOfferPayload(payload);
            var offerId = offer?.OfferId ?? TryExtractOfferId(payload) ?? fallbackOfferId;
            var success = string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase);

            return CreateActionResult(
                action: action,
                success: success,
                symbol: offer?.Symbol ?? symbol,
                offerId: offerId,
                status: string.IsNullOrWhiteSpace(status) ? (success ? "SUCCESS" : "UNKNOWN") : status!,
                message: string.IsNullOrWhiteSpace(text)
                    ? (success ? defaultSuccessMessage : "Funding action did not return a success status.")
                    : text!,
                offer: offer);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[BFX-FUND] Failed to parse funding action response action={Action} raw={Raw}",
                action, Trunc(json, 1200));

            return CreateActionResult(
                action: action,
                success: false,
                symbol: symbol,
                offerId: fallbackOfferId,
                status: "PARSE_ERROR",
                message: "Failed to parse Bitfinex funding action response.");
        }
    }

    private FundingOfferActionResult CreateActionResult(
        string action,
        bool success,
        string symbol,
        string? offerId,
        string status,
        string message,
        FundingOfferInfo? offer = null)
    {
        return new FundingOfferActionResult(
            Action: action,
            Success: success,
            IsDryRun: false,
            Symbol: symbol ?? string.Empty,
            OfferId: offerId,
            Status: status,
            Message: message,
            Offer: offer,
            TimestampUtc: DateTime.UtcNow
        );
    }

    private string CreateSignature(string payload)
    {
        var key = Encoding.UTF8.GetBytes(_apiSecret);
        var bytes = Encoding.UTF8.GetBytes(payload);

        using var hmac = new HMACSHA384(key);
        var hash = hmac.ComputeHash(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
            sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static decimal? ParseDecimalElement(JsonElement array, int index)
    {
        if (array.GetArrayLength() <= index)
            return null;

        var element = array[index];
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var dec))
            return dec;

        if (element.ValueKind == JsonValueKind.String &&
            decimal.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static int? ParseIntElement(JsonElement array, int index)
    {
        if (array.GetArrayLength() <= index)
            return null;

        var element = array[index];
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value))
            return value;

        if (element.ValueKind == JsonValueKind.String &&
            int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string? GetStringSafe(JsonElement array, int index)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() <= index)
            return null;

        return array[index].ValueKind == JsonValueKind.String ? array[index].GetString() : null;
    }

    private static bool? ParseBoolElement(JsonElement array, int index)
    {
        if (array.GetArrayLength() <= index)
            return null;

        var element = array[index];
        if (element.ValueKind == JsonValueKind.True)
            return true;

        if (element.ValueKind == JsonValueKind.False)
            return false;

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var numeric))
            return numeric != 0;

        if (element.ValueKind == JsonValueKind.String &&
            bool.TryParse(element.GetString(), out var parsedBool))
        {
            return parsedBool;
        }

        if (element.ValueKind == JsonValueKind.String &&
            int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt))
        {
            return parsedInt != 0;
        }

        return null;
    }

    internal static FundingOfferInfo? ParseFundingOffer(JsonElement offerArray)
    {
        if (offerArray.ValueKind != JsonValueKind.Array || offerArray.GetArrayLength() < 16)
            return null;

        var id = ParseLongElement(offerArray, 0);
        var symbol = offerArray.GetArrayLength() > 1 && offerArray[1].ValueKind == JsonValueKind.String
            ? offerArray[1].GetString()
            : null;

        if (id <= 0 || string.IsNullOrWhiteSpace(symbol))
            return null;

        return new FundingOfferInfo(
            OfferId: id.ToString(CultureInfo.InvariantCulture),
            Symbol: BitfinexFundingSymbolNormalizer.Normalize(symbol),
            CreatedUtc: ParseUnixMillisUtc(offerArray, 2),
            UpdatedUtc: ParseUnixMillisUtc(offerArray, 3),
            Amount: ParseDecimalElement(offerArray, 4) ?? 0m,
            OriginalAmount: ParseDecimalElement(offerArray, 5) ?? 0m,
            OfferType: offerArray.GetArrayLength() > 6 && offerArray[6].ValueKind == JsonValueKind.String
                ? offerArray[6].GetString() ?? string.Empty
                : string.Empty,
            Flags: ParseIntElement(offerArray, 9) ?? 0,
            Status: offerArray.GetArrayLength() > 10 && offerArray[10].ValueKind == JsonValueKind.String
                ? offerArray[10].GetString() ?? string.Empty
                : string.Empty,
            Rate: ParseDecimalElement(offerArray, 14) ?? 0m,
            PeriodDays: ParseIntElement(offerArray, 15) ?? 0,
            Notify: ParseIntElement(offerArray, 16) == 1,
            Hidden: ParseIntElement(offerArray, 17) == 1,
            Renew: ParseIntElement(offerArray, 19) == 1,
            RateReal: ParseDecimalElement(offerArray, 20)
        );
    }

    internal static FundingCreditInfo? ParseFundingCredit(JsonElement creditArray)
    {
        if (creditArray.ValueKind != JsonValueKind.Array || creditArray.GetArrayLength() < 13)
            return null;

        var id = ParseLongElement(creditArray, 0);
        var symbol = GetStringSafe(creditArray, 1);
        if (id <= 0 || string.IsNullOrWhiteSpace(symbol))
            return null;

        return new FundingCreditInfo(
            CreditId: id,
            Symbol: BitfinexFundingSymbolNormalizer.Normalize(symbol),
            Side: ParseSide(creditArray, 2),
            Status: GetStringSafe(creditArray, 7) ?? string.Empty,
            Amount: ParseDecimalElement(creditArray, 5) ?? 0m,
            OriginalAmount: ParseDecimalElement(creditArray, 6),
            Rate: ParseDecimalElement(creditArray, 11),
            PeriodDays: ParseIntElement(creditArray, 12),
            CreatedUtc: ParseUnixMillisUtc(creditArray, 3),
            UpdatedUtc: ParseUnixMillisUtc(creditArray, 4),
            OpenedUtc: ParseUnixMillisUtc(creditArray, 13),
            LastPayoutUtc: ParseUnixMillisUtc(creditArray, 14),
            FundingType: GetStringSafe(creditArray, 8),
            RateReal: ParseDecimalElement(creditArray, 19),
            Notify: ParseIntElement(creditArray, 15) == 1,
            Renew: ParseIntElement(creditArray, 17) == 1,
            NoClose: ParseIntElement(creditArray, 20) == 1,
            PositionPair: GetStringSafe(creditArray, 21),
            Metadata: new
            {
                RawStatus = GetStringSafe(creditArray, 7),
                RawType = GetStringSafe(creditArray, 8),
                RawFlags = ParseIntElement(creditArray, 6),
                RawMtsCreate = ParseLongElement(creditArray, 3),
                RawMtsUpdate = ParseLongElement(creditArray, 4),
                RawMtsOpening = ParseLongElement(creditArray, 13),
                RawMtsLastPayout = ParseLongElement(creditArray, 14)
            });
    }

    internal static FundingLoanInfo? ParseFundingLoan(JsonElement loanArray)
    {
        if (loanArray.ValueKind != JsonValueKind.Array || loanArray.GetArrayLength() < 13)
            return null;

        var id = ParseLongElement(loanArray, 0);
        var symbol = GetStringSafe(loanArray, 1);
        if (id <= 0 || string.IsNullOrWhiteSpace(symbol))
            return null;

        return new FundingLoanInfo(
            LoanId: id,
            Symbol: BitfinexFundingSymbolNormalizer.Normalize(symbol),
            Side: ParseSide(loanArray, 2),
            Status: GetStringSafe(loanArray, 7) ?? string.Empty,
            Amount: ParseDecimalElement(loanArray, 5) ?? 0m,
            OriginalAmount: ParseDecimalElement(loanArray, 6),
            Rate: ParseDecimalElement(loanArray, 11),
            PeriodDays: ParseIntElement(loanArray, 12),
            CreatedUtc: ParseUnixMillisUtc(loanArray, 3),
            UpdatedUtc: ParseUnixMillisUtc(loanArray, 4),
            OpenedUtc: ParseUnixMillisUtc(loanArray, 13),
            LastPayoutUtc: ParseUnixMillisUtc(loanArray, 14),
            FundingType: GetStringSafe(loanArray, 8),
            RateReal: ParseDecimalElement(loanArray, 19),
            Notify: ParseIntElement(loanArray, 15) == 1,
            Renew: ParseIntElement(loanArray, 17) == 1,
            NoClose: ParseIntElement(loanArray, 20) == 1,
            PositionPair: GetStringSafe(loanArray, 21),
            Metadata: new
            {
                RawStatus = GetStringSafe(loanArray, 7),
                RawType = GetStringSafe(loanArray, 8),
                RawFlags = ParseIntElement(loanArray, 6),
                RawMtsCreate = ParseLongElement(loanArray, 3),
                RawMtsUpdate = ParseLongElement(loanArray, 4),
                RawMtsOpening = ParseLongElement(loanArray, 13),
                RawMtsLastPayout = ParseLongElement(loanArray, 14)
            });
    }

    internal static FundingTradeInfo? ParseFundingTrade(JsonElement tradeArray)
    {
        if (tradeArray.ValueKind != JsonValueKind.Array || tradeArray.GetArrayLength() < 7)
            return null;

        var id = ParseLongElement(tradeArray, 0);
        var symbol = GetStringSafe(tradeArray, 1);
        var utc = ParseUnixMillisUtc(tradeArray, 2);

        if (id <= 0 || string.IsNullOrWhiteSpace(symbol) || !utc.HasValue)
            return null;

        return new FundingTradeInfo(
            FundingTradeId: id,
            Symbol: BitfinexFundingSymbolNormalizer.Normalize(symbol),
            Utc: utc.Value,
            OfferId: ParseNullableLongElement(tradeArray, 3),
            Amount: ParseDecimalElement(tradeArray, 4) ?? 0m,
            Rate: ParseDecimalElement(tradeArray, 5),
            PeriodDays: ParseIntElement(tradeArray, 6),
            Maker: ParseBoolElement(tradeArray, 7),
            Metadata: null);
    }

    internal static FundingLedgerEntry? ParseLedgerEntry(JsonElement ledgerArray)
    {
        if (ledgerArray.ValueKind != JsonValueKind.Array || ledgerArray.GetArrayLength() < 9)
            return null;

        var ledgerId = ParseLongElement(ledgerArray, 0);
        var currency = GetStringSafe(ledgerArray, 1);
        var walletType = GetStringSafe(ledgerArray, 2);
        var utc = ParseUnixMillisUtc(ledgerArray, 3);
        var amount = ParseDecimalElement(ledgerArray, 5);

        if (ledgerId <= 0 || string.IsNullOrWhiteSpace(currency) || string.IsNullOrWhiteSpace(walletType) || !utc.HasValue || !amount.HasValue)
            return null;

        var description = GetStringSafe(ledgerArray, 8);
        return new FundingLedgerEntry(
            LedgerId: ledgerId,
            Currency: currency!.Trim().ToUpperInvariant(),
            WalletType: walletType!,
            Utc: utc.Value,
            Amount: amount.Value,
            BalanceAfter: ParseDecimalElement(ledgerArray, 6),
            EntryType: ClassifyLedgerEntryType(description),
            Description: description,
            Metadata: null);
    }

    private static FundingOfferInfo? TryParseOfferPayload(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Array)
            return null;

        if (ParseFundingOffer(payload) is { } directOffer)
            return directOffer;

        if (payload.GetArrayLength() > 0 &&
            payload[0].ValueKind == JsonValueKind.Array &&
            ParseFundingOffer(payload[0]) is { } nestedOffer)
        {
            return nestedOffer;
        }

        return null;
    }

    private static string? TryExtractOfferId(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Array)
            return null;

        var directId = ParseLongElement(payload, 0);
        if (directId > 0)
            return directId.ToString(CultureInfo.InvariantCulture);

        if (payload.GetArrayLength() > 0 && payload[0].ValueKind == JsonValueKind.Array)
        {
            var nestedId = ParseLongElement(payload[0], 0);
            if (nestedId > 0)
                return nestedId.ToString(CultureInfo.InvariantCulture);
        }

        return null;
    }

    private static long ParseLongElement(JsonElement array, int index)
    {
        if (array.GetArrayLength() <= index)
            return 0;

        var element = array[index];
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var value))
            return value;

        if (element.ValueKind == JsonValueKind.String &&
            long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return 0;
    }

    private static long? ParseNullableLongElement(JsonElement array, int index)
    {
        var value = ParseLongElement(array, index);
        return value > 0 ? value : null;
    }

    private static DateTime? ParseUnixMillisUtc(JsonElement array, int index)
    {
        var value = ParseLongElement(array, index);
        if (value <= 0)
            return null;

        return DateTimeOffset.FromUnixTimeMilliseconds(value).UtcDateTime;
    }

    private static string? ParseSide(JsonElement array, int index)
    {
        var side = ParseIntElement(array, index);
        return side?.ToString(CultureInfo.InvariantCulture);
    }

    private static string ClassifyLedgerEntryType(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return "unknown";

        if (description.IndexOf("Margin Funding Payment", StringComparison.OrdinalIgnoreCase) >= 0)
            return "margin_funding_payment";

        if (description.IndexOf("Transfer of", StringComparison.OrdinalIgnoreCase) >= 0)
            return "wallet_transfer";

        if (description.IndexOf("Exchange ", StringComparison.OrdinalIgnoreCase) >= 0)
            return "wallet_conversion";

        return "other";
    }

    private static string Trunc(string? text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max)
            return text ?? string.Empty;

        return text.Substring(0, max) + "...";
    }

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
                var code = root[1].ValueKind == JsonValueKind.Number && root[1].TryGetInt32(out var numericCode)
                    ? numericCode.ToString(CultureInfo.InvariantCulture)
                    : root[1].ValueKind == JsonValueKind.String
                        ? root[1].GetString() ?? string.Empty
                        : string.Empty;

                var details = root[2].ValueKind == JsonValueKind.String
                    ? root[2].GetString() ?? string.Empty
                    : string.Empty;

                message = string.IsNullOrWhiteSpace(code)
                    ? details
                    : $"[{code}] {details}";

                return !string.IsNullOrWhiteSpace(details);
            }
        }
        catch
        {
        }

        return false;
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
