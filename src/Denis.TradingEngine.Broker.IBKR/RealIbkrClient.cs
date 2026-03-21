#nullable enable
using Denis.TradingEngine.Broker.IBKR.IBKRWrapper;
using Denis.TradingEngine.Core.Trading;
using IBApi;
using Serilog;
using System.Collections.Concurrent;
using System.Globalization;

namespace Denis.TradingEngine.Broker.IBKR
{
    public sealed class RealIbkrClient : IIbkrClient, IDisposable
    {
        private readonly ILogger _log = Log.ForContext<RealIbkrClient>();
        private readonly IbkrDefaultWrapper _wrapper;

        // Lokalni brojač za TWS orderId.
        // Držimo ga kao "last used", pa koristimo Interlocked.Increment.
        private int _nextOrderId;
        private int _lastIbNextValidId;

        // twsOrderId -> internalOrderId
        private readonly ConcurrentDictionary<int, int> _twsToInternal = new();

        // internalOrderId -> twsOrderId
        private readonly ConcurrentDictionary<int, int> _internalToTws = new();

        // twsOrderId -> cleanup after (za mapiranje)
        private readonly ConcurrentDictionary<int, DateTime> _cleanupAfterUtcByTws = new();

        // twsOrderId -> isExit (za exit ordere koji ne smeju da se obrišu dok su aktivni)
        private readonly ConcurrentDictionary<int, bool> _isExitOrderByTws = new();

        // Najmanji dozvoljeni order ID (iz baze) - osigurava da ne koristimo ID-jeve koji su vec korisceni
        private int _minAllowedId = 0;

        private readonly Timer _sweepTimer;

        public event EventHandler<IbExecutionDetails>? ExecutionDetailsReceived;
        public event Action<string, decimal, string?>? CommissionReceived;

        // Emitujemo INTERNAL orderId
        public event Action<int, string, int, decimal?, string?>? OrderStatusUpdated;

        private int _accountSummaryReqId;

        private const double DefaultStockTick = 0.01;

        private static readonly TimeSpan MapCleanupDelayFilled = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan MapCleanupDelayCancel = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan SweepPeriod = TimeSpan.FromSeconds(30);

        public RealIbkrClient(IbkrDefaultWrapper wrapper)
        {
            _wrapper = wrapper ?? throw new ArgumentNullException(nameof(wrapper));

            _nextOrderId = 19999;
            _lastIbNextValidId = 0;

            _sweepTimer = new Timer(_ => SweepMaps(), null, SweepPeriod, SweepPeriod);

            _wrapper.NextValidIdReceived += id =>
            {
                _lastIbNextValidId = id;

                _log.Information("[IBKR] nextValidId={NextValidId}", id);

                // FIX: Proveri da li baza zna za veći broj (npr. imamo nalog 1009 u bazi)
                var startId = id;
                var minAllowed = Volatile.Read(ref _minAllowedId);
                
                if (minAllowed > startId)
                {
                    _log.Warning("[IBKR][WARN] IBKR sent NextValidId={IbId}, but DB has minAllowed={DbId}. Using DB value.", startId, minAllowed);
                    startId = minAllowed;
                }

                // Interlocked.Increment radi "++", pa setujemo na startId-1 da prva upotreba bude baš startId.
                var desired = startId - 1;
                var current = Volatile.Read(ref _nextOrderId);

                if (desired > current)
                {
                    Interlocked.Exchange(ref _nextOrderId, desired);
                    _log.Information("[IBKR] local orderId advanced to {Id} (from IBKR={IbId}, minAllowed={Min})", startId, id, minAllowed);
                }
                else if (startId < current)
                {
                    _log.Information("[IBKR][WARN] nextValidId({Id}) < local({Local}) ? using local counter.", startId, current);
                }
            };

            _wrapper.ExecutionArrived += OnExecutionArrived;

            _wrapper.CommissionReportArrived += cr =>
            {
                try
                {
                    var fee = (decimal)cr.CommissionAndFees;
                    var execId = cr.ExecId ?? string.Empty;
                    var cur = cr.Currency ?? "USD";

                    CommissionReceived?.Invoke(execId, fee, cur);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[IBKR][COMMISSION][ERR]");
                }
            };

            _wrapper.OrderStatusUpdated += (twsId, status, filled, avg, why) =>
            {
                try
                {
                    var internalId = twsId;
                    if (_twsToInternal.TryGetValue(twsId, out var mapped))
                    {
                        internalId = mapped;
                        _log.Debug("[IBKR][STATUS] Mapped twsId={TwsId} -> internalId={Internal} status={Status} filled={Filled}", 
                            twsId, internalId, status, filled);
                    }
                    else
                    {
                        // DEBUG: Enhanced logging for unmapped orders (potential recovery issue)
                        var mapCount = _twsToInternal.Count;
                        var hasTwsMapping = _twsToInternal.ContainsKey(twsId);
                        _log.Warning("[IBKR][STATUS][NO-MAP] No mapping found for twsId={TwsId}, using as internalId status={Status} filled={Filled} avg={Avg} mapCount={MapCount} hasTwsMapping={HasTws} - This may indicate missing recovery registration!",
                            twsId, status, filled, avg ?? 0m, mapCount, hasTwsMapping);
                    }

                    OrderStatusUpdated?.Invoke(internalId, status, filled, avg, why);

                    if (IsFinalStatus(status))
                    {
                        var delay = IsFilled(status) ? MapCleanupDelayFilled : MapCleanupDelayCancel;
                        _cleanupAfterUtcByTws[twsId] = DateTime.UtcNow + delay;
                        
                        // Kada order dobije final status, više nije aktivna exit order
                        _isExitOrderByTws.TryRemove(twsId, out _);
                    }
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[IBKR][STATUS][ERR] twsId={TwsId}", twsId);
                }
            };
        }

        private static bool IsFilled(string status)
        {
            return status.Equals("Filled", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFinalStatus(string status)
        {
            return status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)
                || status.Equals("Canceled", StringComparison.OrdinalIgnoreCase)
                || status.Equals("ApiCancelled", StringComparison.OrdinalIgnoreCase)
                || status.Equals("Rejected", StringComparison.OrdinalIgnoreCase)
                || status.Equals("Filled", StringComparison.OrdinalIgnoreCase);
        }

        private void SweepMaps()
        {
            try
            {
                var now = DateTime.UtcNow;

                foreach (var kv in _cleanupAfterUtcByTws)
                {
                    if (now < kv.Value)
                        continue;

                    var twsId = kv.Key;

                    // FIX: Ne brisati mapiranje za exit ordere dok su aktivni (GTC ordere koji čekaju izvršenje)
                    // Exit ordere brišemo samo kada dobiju final status (Filled/Cancelled)
                    if (_isExitOrderByTws.TryGetValue(twsId, out var isExit) && isExit)
                    {
                        _log.Debug("[IBKR][SWEEP] Skipping cleanup for active exit order twsId={TwsId}", twsId);
                        continue;
                    }

                    if (_cleanupAfterUtcByTws.TryRemove(twsId, out _))
                    {
                        if (_twsToInternal.TryRemove(twsId, out var internalId))
                        {
                            _internalToTws.TryRemove(internalId, out _);
                            _isExitOrderByTws.TryRemove(twsId, out _);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[IBKR] SweepMaps failed");
            }
        }

        private async Task EnsureNextValidIdAsync(TimeSpan? timeout = null)
        {
            if (Volatile.Read(ref _lastIbNextValidId) > 0)
                return;

            var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(3));

            while (DateTime.UtcNow < until)
            {
                if (Volatile.Read(ref _lastIbNextValidId) > 0)
                    return;

                await Task.Delay(50).ConfigureAwait(false);
            }

            _log.Information("[IBKR][WARN] nextValidId not received within timeout; proceeding with local counter.");
        }

        private static double RoundToTick(double px, double tick)
        {
            if (px <= 0 || tick <= 0) return px;
            return Math.Round(px / tick, 0, MidpointRounding.AwayFromZero) * tick;
        }

        private static string? GetStringProp(object obj, params string[] names)
        {
            var t = obj.GetType();
            foreach (var n in names)
            {
                var p = t.GetProperty(n);
                if (p == null) continue;
                var v = p.GetValue(obj);
                if (v == null) continue;
                return v.ToString();
            }
            return null;
        }

        private static decimal? GetDecimalProp(object obj, params string[] names)
        {
            var t = obj.GetType();
            foreach (var n in names)
            {
                var p = t.GetProperty(n);
                if (p == null) continue;
                var v = p.GetValue(obj);
                if (v == null) continue;

                if (v is decimal d) return d;
                if (v is double db) return (decimal)db;
                if (v is float f) return (decimal)f;
                if (v is int i) return i;
                if (v is long l) return l;

                if (decimal.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                    return parsed;
            }
            return null;
        }

        private static bool? GetBoolProp(object obj, params string[] names)
        {
            var t = obj.GetType();
            foreach (var n in names)
            {
                var p = t.GetProperty(n);
                if (p == null) continue;
                var v = p.GetValue(obj);
                if (v == null) continue;

                if (v is bool b) return b;
                if (bool.TryParse(v.ToString(), out var parsed))
                    return parsed;
            }
            return null;
        }

        private static string NormalizeSide(string? sideRaw)
        {
            var s = (sideRaw ?? string.Empty).Trim().ToUpperInvariant();

            // FIX: "SHORT" ne sme da se pretvara u "SELL" jer to može da kreira SHORT poziciju u cash account-u
            // SHORT pozicije zahtevaju margin account
            if (s == "SHORT")
            {
                throw new InvalidOperationException("SHORT orders are not allowed on cash account. Use SELL to close long positions.");
            }

            return s switch
            {
                "BUY" => "BUY",
                "B" => "BUY",
                "LONG" => "BUY",
                "OrderSide.Buy" => "BUY",
                "OrderSide.BUY" => "BUY",

                "SELL" => "SELL",
                "S" => "SELL",
                "OrderSide.Sell" => "SELL",
                "OrderSide.SELL" => "SELL",

                _ => s.Contains("BUY") ? "BUY" : "SELL"
            };
        }

        private static string NormalizeIbOrderType(string? t)
        {
            if (string.IsNullOrWhiteSpace(t))
                return "MKT";

            var x = t.Trim().ToUpperInvariant();

            return x switch
            {
                "MARKET" => "MKT",
                "MKT" => "MKT",

                "LIMIT" => "LMT",
                "LMT" => "LMT",

                "STOP" => "STP",
                "STP" => "STP",
                "STOPMARKET" => "STP",
                "STOP_MARKET" => "STP",

                "STOPLIMIT" => "STP LMT",
                "STOP_LIMIT" => "STP LMT",
                "STPLMT" => "STP LMT",
                "STP LMT" => "STP LMT",

                _ => x
            };
        }

        public async Task PlaceOrderAsync(int internalOrderId, object contract, object order)
        {
            await EnsureNextValidIdAsync().ConfigureAwait(false);

            Contract ibContract;
            if (contract is Contract c)
            {
                ibContract = c;
            }
            else
            {
                var sym = GetStringProp(contract, "Symbol", "Ticker");
                if (string.IsNullOrWhiteSpace(sym))
                    throw new ArgumentException("contract must have Symbol/Ticker property or be IBApi.Contract", nameof(contract));

                ibContract = IbkrInstrumentMap.ToContract(new Symbol(sym));
            }

            Order ibOrder;
            bool isExit = false;  // Deklarisan van if-else bloka da bi bio dostupan u log poruci
            if (order is Order o)
            {
                ibOrder = o;
                // Ako je već IBKR Order objekat, proveri da li ima IsExit property
                isExit = GetBoolProp(order, "IsExit", "IsExitOrder") ?? false;
            }
            else
            {
                var sideRaw = GetStringProp(order, "Side", "Action") ?? "BUY";
                var side = NormalizeSide(sideRaw);

                var qtyDec = GetDecimalProp(order, "Qty", "Quantity", "TotalQuantity") ?? 0m;
                if (qtyDec <= 0m)
                {
                    _log.Information("[IBKR][WARN] Skip order: qty <= 0. internal={Internal} sym={Sym}", internalOrderId, ibContract.Symbol);
                    return;
                }

                var pxDec = GetDecimalProp(order, "Px", "LimitPrice", "LmtPrice");
                var px = pxDec.HasValue ? (double)pxDec.Value : 0.0;

                var stopDec = GetDecimalProp(order, "StopPx", "StopPrice", "AuxPrice");
                var stopPx = stopDec.HasValue ? (double)stopDec.Value : 0.0;

                var orderTypeRaw = GetStringProp(order, "OrderType", "Type");
                var type = NormalizeIbOrderType(orderTypeRaw ?? (px > 0 ? "LMT" : "MKT"));

                var ocaGroup = GetStringProp(order, "Oco", "OcoGroupId", "OcaGroup", "OcaGroupId");
                var hasOca = !string.IsNullOrWhiteSpace(ocaGroup);

                var tifRaw = GetStringProp(order, "Tif", "TimeInForce") ?? "DAY";
                var tif = tifRaw.Trim().ToUpperInvariant();

                var transmit = GetBoolProp(order, "Transmit") ?? true;
                var outsideRth = GetBoolProp(order, "OutsideRth", "OutsideRTH") ?? true;

                var correlationId = GetStringProp(order, "CorrelationId", "OrderRef");

                // FIX: Za cash account-e, exit order-e (zatvaranje LONG pozicije) moraju imati OpenClose = "C"
                isExit = GetBoolProp(order, "IsExit", "IsExitOrder") ?? false;


                if (type == "LMT")
                {
                    if (px <= 0)
                    {
                        _log.Information("[IBKR][WARN] Skip LMT: missing/invalid limit price. internal={Internal} sym={Sym}", internalOrderId, ibContract.Symbol);
                        return;
                    }
                    px = RoundToTick(px, DefaultStockTick);
                }
                else if (type == "STP")
                {
                    if (stopPx <= 0)
                    {
                        _log.Information("[IBKR][WARN] Skip STP: missing/invalid stop price. internal={Internal} sym={Sym}", internalOrderId, ibContract.Symbol);
                        return;
                    }
                    stopPx = RoundToTick(stopPx, DefaultStockTick);
                }
                else if (type == "STP LMT")
                {
                    if (stopPx <= 0 || px <= 0)
                    {
                        _log.Information("[IBKR][WARN] Skip STP LMT: stop or limit missing. internal={Internal} sym={Sym}", internalOrderId, ibContract.Symbol);
                        return;
                    }
                    stopPx = RoundToTick(stopPx, DefaultStockTick);
                    px = RoundToTick(px, DefaultStockTick);
                }

                ibOrder = new Order
                {
                    Action = side,
                    TotalQuantity = qtyDec,
                    OrderType = type,
                    LmtPrice = (type == "LMT" || type == "STP LMT") ? px : 0.0,
                    AuxPrice = (type == "STP" || type == "STP LMT") ? stopPx : 0.0,
                    OcaGroup = hasOca ? ocaGroup : null,
                    OcaType = hasOca ? 1 : 0,
                    Tif = tif,
                    Transmit = transmit,
                    OutsideRth = outsideRth,
                    OrderRef = correlationId,
                    OpenClose = isExit ? "C" : "O"  // FIX: "C" = Close (zatvaranje pozicije), "O" = Open (otvaranje pozicije)
                };
            }

            // Ako je već IBKR Order objekat, postavi OpenClose za exit order-e
            if (ibOrder is Order existingOrder && isExit && string.IsNullOrEmpty(existingOrder.OpenClose))
            {
                existingOrder.OpenClose = "C";
            }

            var twsOrderId = Interlocked.Increment(ref _nextOrderId);

            _twsToInternal[twsOrderId] = internalOrderId;
            _internalToTws[internalOrderId] = twsOrderId;
            
            // FIX: cuvati informaciju da li je exit order - ne brisati mapiranje dok je aktivna
            if (isExit)
            {
                _isExitOrderByTws[twsOrderId] = true;
                _log.Debug("[IBKR][MAP] Marked exit order twsId={TwsId} internalId={Internal} - mapping will be preserved until filled/cancelled", 
                    twsOrderId, internalOrderId);
            }

            try
            {
                _log.Information(
                    "[IBKR][PLACE] twsId={TwsId} internal={Internal} sym={Sym} side={Side} qty={Qty} type={Type} lmt={Lmt} aux={Aux} ocaGroup={Oca} tif={Tif} transmit={Transmit} openClose={OpenClose} isExit={IsExit}",
                    twsOrderId,
                    internalOrderId,
                    ibContract.Symbol,
                    ibOrder.Action,
                    ibOrder.TotalQuantity,
                    ibOrder.OrderType,
                    ibOrder.LmtPrice,
                    ibOrder.AuxPrice,
                    ibOrder.OcaGroup ?? "null",
                    ibOrder.Tif,
                    ibOrder.Transmit,
                    ibOrder.OpenClose ?? "null",
                    isExit
                );

                _wrapper.ClientSocket.placeOrder(twsOrderId, ibContract, ibOrder);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[IBKR][PLACE][ERR] twsId={TwsId} internal={Internal}", twsOrderId, internalOrderId);

                _cleanupAfterUtcByTws[twsOrderId] = DateTime.UtcNow + MapCleanupDelayCancel;
            }
        }

        private void OnExecutionArrived(int reqId, Contract contract, Execution execution)
        {
            try
            {
                var internalId = execution.OrderId;
                var wasMapped = _twsToInternal.TryGetValue(execution.OrderId, out var mapped);
                if (wasMapped)
                    internalId = mapped;
                else
                {
                    // DEBUG: Log unmapped execution (potential recovery issue)
                    _log.Warning("[IBKR][EXEC][NO-MAP] Execution arrived for unmapped twsOrderId={TwsId} execId={ExecId} sym={Sym} qty={Qty} px={Px} mapCount={MapCount} - This may indicate missing recovery registration!",
                        execution.OrderId, execution.ExecId, contract?.Symbol ?? "n/a", execution.Shares, execution.Price, _twsToInternal.Count);
                }

                var cum = execution.CumQty;
                if (cum <= 0)
                    cum = execution.Shares;

                var dto = new IbExecutionDetails
                {
                    OrderId = internalId,
                    FilledQuantity = cum,
                    FillPrice = (decimal)execution.Price,
                    IsFinal = false,
                    ExecId = execution.ExecId
                };

                ExecutionDetailsReceived?.Invoke(this, dto);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[IBKR][EXEC][ERR]");
            }
        }

        public Task CancelOrderAsync(int internalOrderId)
        {
            var twsId = internalOrderId;
            if (_internalToTws.TryGetValue(internalOrderId, out var mapped))
                twsId = mapped;

            try
            {
                _wrapper.ClientSocket.cancelOrder(twsId, new OrderCancel());
                _log.Information("[IBKR][CANCEL] internal={Internal} twsId={TwsId}", internalOrderId, twsId);

                _cleanupAfterUtcByTws[twsId] = DateTime.UtcNow + MapCleanupDelayCancel;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[IBKR][CANCEL][ERR] internal={Internal} twsId={TwsId}", internalOrderId, twsId);
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Postavlja minimum dozvoljeni order ID na osnovu najvećeg ID-ja iz baze.
        /// Osigurava da ne koristimo ID-jeve koji su vec korisceni nakon restarta.
        /// </summary>
        public void SetMinOrderId(int minId)
        {
            // Osiguravamo da nikad ne idemo unazad
            if (minId > _minAllowedId)
            {
                var oldMin = _minAllowedId;
                _minAllowedId = minId;
                
                // Ako je trenutni nextId (koji je možda stigao od IBKR) manji od limita iz baze,
                // odmah ga dižemo na siguran nivo!
                var current = Volatile.Read(ref _nextOrderId);
                if (current < _minAllowedId)
                {
                    Interlocked.Exchange(ref _nextOrderId, _minAllowedId);
                    _log.Information("[IBKR][MIN-ID] Fast-forwarding OrderID from {Old} to {New} based on DB history (oldMin={OldMin})", 
                        current, _minAllowedId, oldMin);
                }
                else
                {
                    _log.Information("[IBKR][MIN-ID] Set minimum allowed OrderID to {Min} (current={Current})", 
                        _minAllowedId, current);
                }
            }
        }

        /// <summary>
        /// Registers existing order mapping during recovery.
        /// twsOrderId is broker-side IBKR id, internalOrderId is local broker_order_id.
        /// </summary>
        public void RegisterExistingOrderMapping(int twsOrderId, int internalOrderId, bool isExit = false)
        {
            var wasAlreadyMapped = _twsToInternal.ContainsKey(twsOrderId);
            _twsToInternal[twsOrderId] = internalOrderId;
            _internalToTws[internalOrderId] = twsOrderId;

            if (isExit)
            {
                _isExitOrderByTws[twsOrderId] = true;
            }

            _log.Information("[IBKR][REGISTER-MAP] Registered existing order mapping twsId={TwsId} -> internalId={InternalId} wasAlreadyMapped={WasMapped} isExit={IsExit} totalMappings={Total}",
                twsOrderId, internalOrderId, wasAlreadyMapped, isExit, _twsToInternal.Count);
        }

        public void RegisterExistingOrderMapping(int twsOrderId, bool isExit = false)
        {
            RegisterExistingOrderMapping(twsOrderId, twsOrderId, isExit);
        }
        // Quality-of-life helper method (not in IIbkrClient).
        public async Task<decimal?> GetAvailableFundsUsdAsync(string accountId, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<decimal?>(TaskCreationOptions.RunContinuationsAsynchronously);

            var reqId = Interlocked.Increment(ref _accountSummaryReqId);

            void OnSummary(int rId, string acc, string tag, string value, string currency)
            {
                if (rId != reqId) return;
                if (!string.Equals(acc, accountId, StringComparison.OrdinalIgnoreCase)) return;

                if (tag == "AvailableFunds")
                {
                    if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
                        tcs.TrySetResult(v);
                }
            }

            void OnSummaryEnd(int rId)
            {
                if (rId != reqId) return;
                tcs.TrySetResult(null);
            }

            _wrapper.AccountSummaryArrived += OnSummary;
            _wrapper.AccountSummaryEnd += OnSummaryEnd;

            try
            {
                _wrapper.ClientSocket.reqAccountSummary(reqId, "All", "AvailableFunds,NetLiquidation");

                using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

                var result = await tcs.Task.ConfigureAwait(false);

                _wrapper.ClientSocket.cancelAccountSummary(reqId);

                return result;
            }
            finally
            {
                _wrapper.AccountSummaryArrived -= OnSummary;
                _wrapper.AccountSummaryEnd -= OnSummaryEnd;
            }
        }

        public void Dispose()
        {
            _sweepTimer.Dispose();

            _wrapper.ExecutionArrived -= OnExecutionArrived;
        }
    }
}






