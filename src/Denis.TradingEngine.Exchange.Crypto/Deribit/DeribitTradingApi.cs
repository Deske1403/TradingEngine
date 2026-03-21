#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Denis.TradingEngine.Core.Crypto;
using Denis.TradingEngine.Exchange.Crypto.Abstractions;

namespace Denis.TradingEngine.Exchange.Crypto.Deribit
{
    public sealed class DeribitTradingApi : ICryptoTradingApi
    {
        private readonly ILogger _log;
        private readonly DeribitWebSocketFeed _ws;

        // Za info, zasad token samo čuvamo i logujemo – Deribit veže auth za WS konekciju
        private string? _accessToken;

        public CryptoExchangeId ExchangeId => CryptoExchangeId.Deribit;

        public DeribitTradingApi(DeribitWebSocketFeed ws, ILogger log)
        {
            _ws = ws;
            _log = log;
        }

        // ============================================================
        //  AUTH LOGIN (public/auth)
        // ============================================================
        public async Task<bool> AuthenticateAsync(string apiKey, string apiSecret, CancellationToken ct)
        {
            var msg = new
            {
                jsonrpc = "2.0",
                id = 100,
                method = "public/auth",
                @params = new
                {
                    grant_type = "client_credentials",
                    client_id = apiKey,
                    client_secret = apiSecret
                }
            };

            var json = JsonSerializer.Serialize(msg);
            _log.Information("[DERIBIT-AUTH] Šaljem public/auth zahtev...");
            await _ws.SendAsync(json, ct);

            var resp = await _ws.WaitResponseAsync(100, ct);
            if (resp is null)
            {
                _log.Error("[DERIBIT-AUTH] Nema odgovora na auth (timeout ili otkazano)");
                return false;
            }

            var root = resp.Value;

            // Ako postoji error čvor – prijavi
            if (root.TryGetProperty("error", out var errorElem) && errorElem.ValueKind == JsonValueKind.Object)
            {
                _log.Error("[DERIBIT-AUTH] Auth error: {Json}", root.ToString());
                return false;
            }

            // result.access_token
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("result", out var resultElem) &&
                resultElem.ValueKind == JsonValueKind.Object &&
                resultElem.TryGetProperty("access_token", out var tokenElem))
            {
                _accessToken = tokenElem.GetString();
                _log.Information("[DERIBIT-AUTH] Auth uspešan, token primljen (length={Len})",
                    _accessToken?.Length ?? 0);
                return true;
            }

            _log.Error("[DERIBIT-AUTH] Neočekivan auth odgovor: {Json}", root.ToString());
            return false;
        }

        // ============================================================
        //  GET BALANCE (private/get_account_summary)
        // ============================================================
        public async Task<string?> GetBalanceAsync(string currency, CancellationToken ct)
        {
            if (_accessToken is null)
            {
                _log.Warning("[DERIBIT-REST] GetBalanceAsync pozvan bez prethodnog AuthenticateAsync");
            }

            var msg = new
            {
                jsonrpc = "2.0",
                id = 200,
                method = "private/get_account_summary",
                @params = new { currency }
            };

            var json = JsonSerializer.Serialize(msg);
            _log.Information("[DERIBIT-WS] → private/get_account_summary ({Currency})", currency);
            await _ws.SendAsync(json, ct);

            var resp = await _ws.WaitResponseAsync(200, ct);
            return resp?.ToString();
        }

        // ============================================================
        //  GET BALANCES (ICryptoTradingApi)
        // ============================================================
        public async Task<IReadOnlyList<BalanceInfo>> GetBalancesAsync(CancellationToken ct)
        {
            IReadOnlyList<BalanceInfo> empty = Array.Empty<BalanceInfo>();

            if (_accessToken is null)
            {
                _log.Warning("[DERIBIT] GetBalancesAsync pozvan bez prethodnog AuthenticateAsync");
                return empty;
            }

            // Deribit podržava samo BTC i ETH za sada
            var currencies = new[] { "BTC", "ETH" };
            var balances = new List<BalanceInfo>();

            foreach (var currency in currencies)
            {
                try
                {
                    var json = await GetBalanceAsync(currency, ct);
                    if (string.IsNullOrWhiteSpace(json))
                        continue;

                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    // Deribit vraća: { "result": { "balance": 1000.0, "available_withdrawal": 950.0, ... } }
                    if (root.TryGetProperty("result", out var resElem) &&
                        resElem.ValueKind == JsonValueKind.Object)
                    {
                        decimal? balance = null;
                        decimal? available = null;

                        if (resElem.TryGetProperty("balance", out var balElem) &&
                            balElem.ValueKind == JsonValueKind.Number)
                        {
                            balance = balElem.GetDecimal();
                        }

                        if (resElem.TryGetProperty("available_withdrawal", out var availElem) &&
                            availElem.ValueKind == JsonValueKind.Number)
                        {
                            available = availElem.GetDecimal();
                        }

                        if (balance.HasValue && balance.Value > 0m)
                        {
                            var free = available ?? balance.Value;
                            var locked = balance.Value - free;

                            balances.Add(new BalanceInfo(
                                ExchangeId: CryptoExchangeId.Deribit,
                                Asset: currency,
                                Free: free,
                                Locked: locked
                            ));
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[DERIBIT] Greška pri parsiranju Balance za {Currency}", currency);
                }
            }

            _log.Information("[DERIBIT] Balances parsed: {Count} assets", balances.Count);
            return balances;
        }

        // ============================================================
        //  GET POSITIONS (private/get_positions)
        // ============================================================
        public async Task<string?> GetPositionsAsync(string currency, CancellationToken ct)
        {
            if (_accessToken is null)
            {
                _log.Warning("[DERIBIT-REST] GetPositionsAsync pozvan bez prethodnog AuthenticateAsync");
            }

            var msg = new
            {
                jsonrpc = "2.0",
                id = 210,
                method = "private/get_positions",
                @params = new { currency }
            };

            var json = JsonSerializer.Serialize(msg);
            _log.Information("[DERIBIT-WS] → private/get_positions ({Currency})", currency);
            await _ws.SendAsync(json, ct);

            var resp = await _ws.WaitResponseAsync(210, ct);
            return resp?.ToString();
        }

        // ============================================================
        //  ICryptoTradingApi IMPLEMENTATION
        // ============================================================
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
            if (_accessToken is null)
            {
                _log.Warning("[DERIBIT] PlaceLimitOrderAsync pozvan bez prethodnog AuthenticateAsync");
                return new PlaceOrderResult(false, null, "Not authenticated");
            }

            var method = side == CryptoOrderSide.Buy ? "private/buy" : "private/sell";

            var msg = new
            {
                jsonrpc = "2.0",
                id = 300,
                method = method,
                @params = new
                {
                    instrument_name = symbol.NativeSymbol,
                    amount = (double)quantity,
                    type = "limit",
                    price = (double)price
                }
            };

            var json = JsonSerializer.Serialize(msg);
            _log.Information("[DERIBIT] → {Method} {Symbol} x{Qty} @ {Px}", method, symbol.PublicSymbol, quantity, price);

            await _ws.SendAsync(json, ct);

            var resp = await _ws.WaitResponseAsync(300, ct);
            if (resp == null)
            {
                return new PlaceOrderResult(false, null, "No response from Deribit");
            }

            try
            {
                using var doc = JsonDocument.Parse(resp.ToString()!);
                var root = doc.RootElement;

                if (root.TryGetProperty("error", out var errElem))
                {
                    var errMsg = errElem.ToString();
                    _log.Warning("[DERIBIT] Order error: {Err}", errMsg);
                    return new PlaceOrderResult(false, null, errMsg);
                }

                if (root.TryGetProperty("result", out var resElem) &&
                    resElem.ValueKind == JsonValueKind.Object)
                {
                    string? orderId = null;
                    if (resElem.TryGetProperty("order", out var orderElem) &&
                        orderElem.ValueKind == JsonValueKind.Object &&
                        orderElem.TryGetProperty("order_id", out var orderIdElem))
                    {
                        orderId = orderIdElem.GetString();
                    }

                    if (!string.IsNullOrWhiteSpace(orderId))
                    {
                        _log.Information("[DERIBIT] Order placed: {OrderId}", orderId);
                        return new PlaceOrderResult(true, orderId, null);
                    }
                }

                return new PlaceOrderResult(false, null, "No order_id in response");
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[DERIBIT] Greška pri parsiranju order response");
                return new PlaceOrderResult(false, null, "Exception parsing response");
            }
        }

        public Task<PlaceOrderResult> PlaceStopOrderAsync(CryptoSymbol symbol, CryptoOrderSide side, decimal quantity, decimal stopPrice,
            decimal? limitPrice, CancellationToken ct, int flags = 0, int? gid = null)
        {
            throw new NotImplementedException();
        }

        public async Task<bool> CancelOrderAsync(string exchangeOrderId, CancellationToken ct)
        {
            if (_accessToken is null)
            {
                _log.Warning("[DERIBIT] CancelOrderAsync pozvan bez prethodnog AuthenticateAsync");
                return false;
            }

            var msg = new
            {
                jsonrpc = "2.0",
                id = 400,
                method = "private/cancel",
                @params = new { order_id = exchangeOrderId }
            };

            var json = JsonSerializer.Serialize(msg);
            _log.Information("[DERIBIT] → private/cancel orderId={OrderId}", exchangeOrderId);

            await _ws.SendAsync(json, ct);

            var resp = await _ws.WaitResponseAsync(400, ct);
            if (resp == null)
                return false;

            try
            {
                using var doc = JsonDocument.Parse(resp.ToString()!);
                var root = doc.RootElement;

                if (root.TryGetProperty("error", out var errElem))
                {
                    _log.Warning("[DERIBIT] Cancel error: {Err}", errElem.ToString());
                    return false;
                }

                _log.Information("[DERIBIT] Cancel OK");
                return true;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[DERIBIT] Greška pri parsiranju cancel response");
                return false;
            }
        }

        public async Task<IReadOnlyList<OpenOrderInfo>> GetOpenOrdersAsync(CancellationToken ct)
        {
            IReadOnlyList<OpenOrderInfo> empty = Array.Empty<OpenOrderInfo>();

            if (_accessToken is null)
            {
                _log.Warning("[DERIBIT] GetOpenOrdersAsync pozvan bez prethodnog AuthenticateAsync");
                return empty;
            }

            // Deribit podržava samo BTC i ETH za sada
            var currencies = new[] { "BTC", "ETH" };
            var orders = new List<OpenOrderInfo>();

            foreach (var currency in currencies)
            {
                try
                {
                    var msg = new
                    {
                        jsonrpc = "2.0",
                        id = 500,
                        method = "private/get_open_orders_by_currency",
                        @params = new { currency }
                    };

                    var json = JsonSerializer.Serialize(msg);
                    await _ws.SendAsync(json, ct);

                    var resp = await _ws.WaitResponseAsync(500, ct);
                    if (resp == null)
                        continue;

                    // TODO: Parsiraj response u OpenOrderInfo[]
                    _log.Information("[DERIBIT] GetOpenOrders raw: {Json}", resp.ToString());
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[DERIBIT] Greška pri GetOpenOrders za {Currency}", currency);
                }
            }

            return orders;
        }
    }
}