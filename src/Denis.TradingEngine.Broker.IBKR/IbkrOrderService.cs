#nullable enable
using Denis.TradingEngine.Core.Interfaces;
using Denis.TradingEngine.Core.Orders;
using Denis.TradingEngine.Core.Trading;
using Denis.TradingEngine.MetricsServer;
using Serilog;
using System.Globalization;


namespace Denis.TradingEngine.Broker.IBKR
{
    public sealed class IbkrOrderService : IOrderService, IDisposable
    {
        private readonly IIbkrClient _ib;
        private readonly Action<OrderRequest, decimal, DateTime> _onFill;
        private readonly ILogger _log = Log.ForContext<IbkrOrderService>();

        private readonly object _sync = new();

        private int _nextId;

        // orderId -> request (aktivni orders)
        private readonly Dictionary<int, OrderRequest> _byIbOrderId = new();

        // orderId -> request (arhivirani orders - Äuvaju se duÅ¾e za late execution details)
        private readonly Dictionary<int, OrderRequest> _archivedOrders = new();

        // orderId -> sentAt
        private readonly Dictionary<int, DateTime> _sentAtUtc = new();

        // orderId -> lastCum (za DELTA raÄunanje)
        private readonly Dictionary<int, decimal> _lastCumByOrderId = new();

        // execId -> orderId (za commission)
        private readonly Dictionary<string, int> _execToOrderId =
            new(StringComparer.OrdinalIgnoreCase);

        // TTL bookkeeping
        private readonly Dictionary<int, DateTime> _orderTouchedUtc = new();            // orderId -> last touch
        private readonly Dictionary<int, DateTime> _orderCleanupAfterUtc = new();       // orderId -> earliest cleanup
        private readonly Dictionary<string, DateTime> _execTouchedUtc = new();          // execId -> last touch

        private readonly Timer _sweepTimer;

        private static readonly TimeSpan OrderTtl = TimeSpan.FromMinutes(60);
        private static readonly TimeSpan ExecTtl = TimeSpan.FromMinutes(90);
        private static readonly TimeSpan FilledCleanupDelay = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan ArchiveTtl = TimeSpan.FromHours(24); // ÄŒuvaj arhivirane orders 24h za late execution details
        private static readonly TimeSpan SweepPeriod = TimeSpan.FromSeconds(30);

        public event Action<OrderResult>? OrderUpdated;

        public IbkrOrderService(
            IIbkrClient ibClient,
            Action<OrderRequest, decimal, DateTime> onFill,
            int startingOrderId = 1000)
        {
            _ib = ibClient ?? throw new ArgumentNullException(nameof(ibClient));
            _onFill = onFill ?? throw new ArgumentNullException(nameof(onFill));
            _nextId = startingOrderId;

            _ib.ExecutionDetailsReceived += OnExecutionDetails;
            _ib.CommissionReceived += OnCommission;
            _ib.OrderStatusUpdated += OnOrderStatusUpdate;

            _sweepTimer = new Timer(_ => Sweep(), null, SweepPeriod, SweepPeriod);
        }

        public async Task<string> PlaceAsync(OrderRequest req)
        {
            var ibOrderId = Interlocked.Increment(ref _nextId);
            var now = DateTime.UtcNow;

            var contract = IbkrMapper.ToContract(req.Symbol);
            var order = IbkrMapper.ToOrder(req);

            lock (_sync)
            {
                _byIbOrderId[ibOrderId] = req;
                _sentAtUtc[ibOrderId] = now;

                _orderTouchedUtc[ibOrderId] = now;
                _orderCleanupAfterUtc.Remove(ibOrderId);
            }

            if (req.IsExit)
                await Task.Delay(350).ConfigureAwait(false);

            await _ib.PlaceOrderAsync(ibOrderId, contract, order).ConfigureAwait(false);

            _log.Information(
                "[IB-PLACE] id={Id} {Side} {Sym} x{Qty} type={Type} lmt={Lmt} stop={Stop} oco={Oco} exit={IsExit}",
                ibOrderId,
                req.Side,
                req.Symbol.Ticker,
                req.Quantity,
                req.Type,
                req.LimitPrice,
                req.StopPrice,
                req.OcoGroupId ?? "null",
                req.IsExit
            );

            return ibOrderId.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Registruje postojeÄ‡i order u mapi (za recovery nakon restart-a).
        /// Koristi se kada se order veÄ‡ postoji u IBKR-u ali nije u mapi jer je kreiran u prethodnoj sesiji.
        /// </summary>
        public void RegisterExistingOrder(int ibOrderId, OrderRequest req, DateTime? sentAtUtc = null, int? twsOrderId = null)
        {
            if (req == null) throw new ArgumentNullException(nameof(req));
            
            var now = DateTime.UtcNow;
            var sentAt = sentAtUtc ?? now;
            var resolvedTwsOrderId = twsOrderId ?? ibOrderId;

            lock (_sync)
            {
                // Dodaj u arhivirane orders (jer je veÄ‡ postojeÄ‡i order, ne novi)
                _archivedOrders[ibOrderId] = req;
                _sentAtUtc[ibOrderId] = sentAt;
                _orderTouchedUtc[ibOrderId] = now;
                
                _log.Information(
                    "[IB-REGISTER] Registered existing order id={Id} twsId={TwsId} {Side} {Sym} x{Qty} corr={Corr} exit={Exit}",
                    ibOrderId, resolvedTwsOrderId, req.Side, req.Symbol.Ticker, req.Quantity, req.CorrelationId, req.IsExit);
            }

            // Registruj mapiranje u RealIbkrClient (za postojeÄ‡e ordere, TWS ID je i internal ID)
            if (_ib is RealIbkrClient realClient)
            {
                realClient.RegisterExistingOrderMapping(resolvedTwsOrderId, ibOrderId, isExit: req.IsExit);
            }
        }

        public async Task CancelAsync(string brokerOrderId)
        {
            if (!int.TryParse(brokerOrderId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ibId))
            {
                _log.Warning("[IB-CANCEL] Cannot parse brokerOrderId={Id}", brokerOrderId);
                return;
            }

            try
            {
                await _ib.CancelOrderAsync(ibId).ConfigureAwait(false);

                _log.Information("[IB-CANCEL] Sent cancel for order={Id}", ibId);

                Raise(new OrderResult(
                    BrokerOrderId: brokerOrderId,
                    Status: "Canceled",
                    FilledQuantity: 0,
                    AverageFillPrice: null,
                    CommissionAndFees: null,
                    Message: null,
                    TimestampUtc: DateTime.UtcNow
                ));
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[IB-CANCEL] Fail order={Id}", ibId);
                try { AppMetrics.Instance.IncGeneralException(); } catch { }
            }
        }

        // ============================
        // STATUS UPDATE
        // ============================
        private void OnOrderStatusUpdate(
            int orderId,
            string status,
            int filled,
            decimal? avgFillPrice,
            string? why)
        {
            var now = DateTime.UtcNow;
            var engineStatus = NormalizeEngineStatus(status);

            OrderRequest? original;
            lock (_sync)
            {
                _orderTouchedUtc[orderId] = now;
                if (!_byIbOrderId.TryGetValue(orderId, out original))
                {
                    // Probaj arhivirane orders (za exit ordere koji su veÄ‡ arhivirani)
                    if (_archivedOrders.TryGetValue(orderId, out original))
                    {
                        _log.Information("[IB-STATUS] Found orderId {Id} in archived orders status={Status} filled={Filled}", 
                            orderId, engineStatus, filled);
                    }
                    else
                    {
                        _log.Warning("[IB-STATUS] Unknown orderId {Id} status={Status} filled={Filled} - order not found in active or archived maps", 
                            orderId, engineStatus, filled);
                    }
                }

                if (IsFinalStatus(engineStatus))
                {
                    var delay = engineStatus.Equals("Filled", StringComparison.OrdinalIgnoreCase)
                        ? FilledCleanupDelay
                        : TimeSpan.FromMinutes(2);

                    _orderCleanupAfterUtc[orderId] = now + delay;
                }
            }

            _log.Information(
                "[IB-STATUS] rawStatus={Raw} normStatus={Norm} id={Id} filled={Filled} avg={Avg} why={Why}",
                status,
                engineStatus,
                orderId,
                filled,
                (avgFillPrice.HasValue && avgFillPrice.Value > 0) ? avgFillPrice : null,
                why ?? "n/a");

            // FALLBACK: Ako je status Filled i filled > 0, ali nema execution details,
            // pozovi _onFill direktno (IBKR ponekad ne Å¡alje execution details za stop ordere)
            if (engineStatus.Equals("Filled", StringComparison.OrdinalIgnoreCase) && 
                filled > 0 && 
                avgFillPrice.HasValue && 
                avgFillPrice.Value > 0 &&
                original != null)
            {
                // Prvo izraÄunaj deltaQty, pa tek onda aÅ¾uriraj _lastCumByOrderId
                decimal deltaQty;
                lock (_sync)
                {
                    var filledDec = (decimal)filled;
                    if (!_lastCumByOrderId.TryGetValue(orderId, out var lastCum))
                    {
                        // Prvi put vidimo ovaj fill
                        deltaQty = filledDec;
                        _lastCumByOrderId[orderId] = filledDec;
                        _orderTouchedUtc[orderId] = now;
                    }
                    else if (lastCum < filledDec)
                    {
                        // Novi fill - izraÄunaj delta
                        deltaQty = filledDec - lastCum;
                        _lastCumByOrderId[orderId] = filledDec;
                        _orderTouchedUtc[orderId] = now;
                    }
                    else
                    {
                        // VeÄ‡ obraÄ‘en (lastCum >= filledDec), preskoÄi
                        _log.Debug("[IB-STATUS-FILL] Order {Id} already processed (lastCum={LastCum} >= filled={Filled}), skipping fallback", 
                            orderId, lastCum, filledDec);
                        deltaQty = 0m;
                    }
                }

                if (deltaQty > 0m)
                {
                    _log.Information(
                        "[IB-STATUS-FILL] FALLBACK: Calling _onFill for orderId={Id} sym={Sym} side={Side} deltaQty={DeltaQty} cumQty={CumQty} isExit={IsExit} corr={Corr} (no execution details received)",
                        orderId, original.Symbol.Ticker, original.Side, deltaQty, (decimal)filled, original.IsExit, original.CorrelationId);

                    var sliceReq = new OrderRequest(
                        symbol: original.Symbol,
                        side: original.Side,
                        type: original.Type,
                        quantity: deltaQty,
                        limitPrice: original.LimitPrice,
                        tif: original.Tif,
                        correlationId: original.CorrelationId,
                        timestampUtc: original.TimestampUtc,
                        ocoGroupId: original.OcoGroupId,
                        ocoStopPrice: original.OcoStopPrice,
                        stopPrice: original.StopPrice,
                        isExit: original.IsExit
                    );

                    try
                    {
                        _onFill(sliceReq, avgFillPrice.Value, now);
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(ex, "[IB-STATUS-FILL] _onFill handler threw for orderId={Id}", orderId);
                    }
                }
            }

            Raise(new OrderResult(
                BrokerOrderId: orderId.ToString(CultureInfo.InvariantCulture),
                Status: engineStatus,
                FilledQuantity: filled,
                AverageFillPrice: (avgFillPrice.HasValue && avgFillPrice.Value > 0) ? avgFillPrice : null,
                CommissionAndFees: null,
                Message: why,
                TimestampUtc: now
            ));

            // Prometheus reject metric
            try
            {
                if (engineStatus.Equals("Rejected", StringComparison.OrdinalIgnoreCase))
                {
                    var sym = original?.Symbol.Ticker;
                    if (!string.IsNullOrWhiteSpace(sym))
                        OrderMetrics.Instance.Reject(sym);
                }
            }
            catch
            {
                // metrics never break engine
            }
        }

        // ============================
        // FILL
        // ============================
        private void OnExecutionDetails(object? sender, IbExecutionDetails e)
        {
            var now = DateTime.UtcNow;

            OrderRequest? original;
            DateTime? sentAt = null;

            decimal deltaQty;
            decimal cumQty = e.FilledQuantity; // RealIbkrClient Å¡alje CUM, ovde raÄunamo DELTA

            bool finalByCum;

            lock (_sync)
            {
                // Prvo probaj aktivni orders, pa arhivirane
                if (!_byIbOrderId.TryGetValue(e.OrderId, out original))
                {
                    if (!_archivedOrders.TryGetValue(e.OrderId, out original))
                    {
                        _log.Warning("[IB-FILL] Unknown orderId {Id} execId={ExecId} - order not found in active or archived maps", e.OrderId, e.ExecId ?? "n/a");
                        return;
                    }
                    else
                    {
                        _log.Information("[IB-FILL] Found orderId {Id} in archived orders execId={ExecId} isExit={IsExit} corr={Corr}", 
                            e.OrderId, e.ExecId ?? "n/a", original.IsExit, original.CorrelationId);
                    }
                }
                else
                {
                    _log.Debug("[IB-FILL] Found orderId {Id} in active orders execId={ExecId} isExit={IsExit} corr={Corr}", 
                        e.OrderId, e.ExecId ?? "n/a", original.IsExit, original.CorrelationId);
                }

                _orderTouchedUtc[e.OrderId] = now;

                if (!string.IsNullOrWhiteSpace(e.ExecId))
                {
                    _execToOrderId[e.ExecId] = e.OrderId;
                    _execTouchedUtc[e.ExecId] = now;
                }

                if (_sentAtUtc.TryGetValue(e.OrderId, out var s))
                    sentAt = s;

                if (!_lastCumByOrderId.TryGetValue(e.OrderId, out var lastCum))
                    lastCum = 0m;

                if (cumQty <= lastCum)
                {
                    _log.Information(
                        "[IB-FILL] duplicate/non-incremental cum ignored id={Id} cum={Cum} last={Last} execId={ExecId}",
                        e.OrderId, cumQty, lastCum, e.ExecId ?? "n/a");
                    return;
                }

                deltaQty = cumQty - lastCum;
                _lastCumByOrderId[e.OrderId] = cumQty;

                // NEW: if this fill completes the order, mark it for cleanup ASAP
                // (prevents late status updates from "downgrading" in UI/DB flows)
                finalByCum = cumQty >= original.Quantity;
                if (finalByCum)
                    _orderCleanupAfterUtc[e.OrderId] = now + FilledCleanupDelay;
            }

            if (deltaQty <= 0m)
                return;

            // latency metric
            try
            {
                if (sentAt.HasValue)
                {
                    var latencyMs = (now - sentAt.Value).TotalMilliseconds;
                    OrderMetrics.Instance.SetFillLatency(original.Symbol.Ticker, latencyMs);
                }
            }
            catch
            {
                // metrics never break engine
            }

            // prosledi DELTA orchestratoru
            var sliceReq = new OrderRequest(
                symbol: original.Symbol,
                side: original.Side,
                type: original.Type,
                quantity: deltaQty,
                limitPrice: original.LimitPrice,
                tif: original.Tif,
                correlationId: original.CorrelationId,
                timestampUtc: original.TimestampUtc,
                ocoGroupId: original.OcoGroupId,
                ocoStopPrice: original.OcoStopPrice,
                stopPrice: original.StopPrice,
                isExit: original.IsExit
            );

            _log.Information(
                "[IB-FILL] Calling _onFill for orderId={Id} sym={Sym} side={Side} deltaQty={DeltaQty} cumQty={CumQty} isExit={IsExit} corr={Corr}",
                e.OrderId, original.Symbol.Ticker, original.Side, deltaQty, cumQty, original.IsExit, original.CorrelationId);

            try
            {
                _onFill(sliceReq, e.FillPrice, now);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[IB-FILL] _onFill handler threw");
            }

            // UI/DB: emituj cumulative kao FilledQuantity
            var statusToEmit = finalByCum ? "Filled" : "PartiallyFilled";

            Raise(new OrderResult(
                BrokerOrderId: e.OrderId.ToString(CultureInfo.InvariantCulture),
                Status: statusToEmit,
                FilledQuantity: cumQty,
                AverageFillPrice: e.FillPrice,
                CommissionAndFees: null,
                Message: null,
                TimestampUtc: now
            ));
        }

        // ============================
        // COMMISSION
        // ============================
        private void OnCommission(string execId, decimal amount, string? currency = null)
        {
            var now = DateTime.UtcNow;

            string brokerOrderId = execId;
            bool foundOrderId = false;

            lock (_sync)
            {
                _execTouchedUtc[execId] = now;

                if (_execToOrderId.TryGetValue(execId, out var oid))
                {
                    brokerOrderId = oid.ToString(CultureInfo.InvariantCulture);
                    foundOrderId = true;
                    
                    // Try to get order request for better logging (prvo aktivni, pa arhivirani)
                    OrderRequest? req = null;
                    if (_byIbOrderId.TryGetValue(oid, out req) || _archivedOrders.TryGetValue(oid, out req))
                    {
                        _log.Information(
                            "[IB-COMM] execId={ExecId} orderId={Oid} fee={Fee} {Cur} symbol={Sym} side={Side} qty={Qty}",
                            execId, oid, amount, currency ?? "USD", req.Symbol.Ticker, req.Side, req.Quantity);
                    }
                    else
                    {
                        _log.Information(
                            "[IB-COMM] execId={ExecId} orderId={Oid} fee={Fee} {Cur} (order request not found in active or archived)",
                            execId, oid, amount, currency ?? "USD");
                    }
                }
                else
                {
                    _log.Warning(
                        "[IB-COMM] Unknown execId={ExecId} fee={Fee} {Cur} - commission event arrived before execution mapping. Will retry mapping later.",
                        execId, amount, currency ?? "USD");
                    
                    // FIX: PokuÅ¡aj da pronaÄ‘eÅ¡ orderId po execId u arhiviranim orders
                    // (execution detail moÅ¾da veÄ‡ stigao i mapirao execId -> orderId)
                    // Ovo je fallback - commission event moÅ¾e stiÄ‡i pre execution detail-a
                }
            }

            if (!foundOrderId)
            {
                _log.Warning(
                    "[IB-COMM] Using execId as brokerOrderId={ExecId} fee={Fee} - may not match pending order lookup",
                    execId, amount);
            }

            var commissionResult = new OrderResult(
                BrokerOrderId: brokerOrderId,
                Status: "Commission",
                FilledQuantity: 0,
                AverageFillPrice: null,
                CommissionAndFees: amount,
                Message: currency,
                TimestampUtc: now
            );
            
            _log.Information(
                "[IB-COMM-RAISE] Raising commission event execId={ExecId} brokerOrderId={Bid} fee={Fee} foundOrderId={Found}",
                execId, brokerOrderId, amount, foundOrderId);
            
            Raise(commissionResult);
        }

        // ============================
        // CLEANUP
        // ============================
        private void Sweep()
        {
            try
            {
                var now = DateTime.UtcNow;

                lock (_sync)
                {
                    // orders cleanup
                    var removeOrders = new List<int>();

                    foreach (var kv in _orderTouchedUtc)
                    {
                        var orderId = kv.Key;
                        var touched = kv.Value;

                        var ttlExpired = (now - touched) > OrderTtl;

                        var hasCleanupAt = _orderCleanupAfterUtc.TryGetValue(orderId, out var cleanupAt);
                        var cleanupDue = hasCleanupAt && now >= cleanupAt;

                        if (ttlExpired || cleanupDue)
                            removeOrders.Add(orderId);
                    }

                    foreach (var id in removeOrders)
                    {
                        // Arhiviraj order request pre brisanja (za late execution details)
                        if (_byIbOrderId.TryGetValue(id, out var req))
                        {
                            _archivedOrders[id] = req;
                            // FIX: ZadrÅ¾i touch time za arhivirani order â€“ ArchiveTtl (24h) proverava
                            // _orderTouchedUtc. Bez ovoga bi arhiva odmah obrisala order u istom
                            // Sweep ciklusu (jer Remove gore je brisao touch), pa late fill (npr. TP
                            // posle 5h) ne bi naÅ¡ao order i _onFill se ne bi pozvao.
                            _orderTouchedUtc[id] = now;
                        }
                        
                        _byIbOrderId.Remove(id);
                        _sentAtUtc.Remove(id);
                        _lastCumByOrderId.Remove(id);
                        _orderCleanupAfterUtc.Remove(id);
                    }

                    // Cleanup arhiviranih orders (nakon 24h)
                    var removeArchived = new List<int>();
                    foreach (var kv in _archivedOrders)
                    {
                        var orderId = kv.Key;
                        if (_orderTouchedUtc.TryGetValue(orderId, out var lastTouch))
                        {
                            if ((now - lastTouch) > ArchiveTtl)
                                removeArchived.Add(orderId);
                        }
                        else
                        {
                            // Ako nema touch time, obriÅ¡i odmah (ne bi trebalo da se desi)
                            removeArchived.Add(orderId);
                        }
                    }

                    foreach (var id in removeArchived)
                    {
                        _archivedOrders.Remove(id);
                    }

                    // exec cleanup
                    var removeExec = new List<string>();
                    foreach (var kv in _execTouchedUtc)
                    {
                        if ((now - kv.Value) > ExecTtl)
                            removeExec.Add(kv.Key);
                    }

                    foreach (var x in removeExec)
                    {
                        _execToOrderId.Remove(x);
                        _execTouchedUtc.Remove(x);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[IB] Sweep failed");
            }
        }

        private static bool IsFinalStatus(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;

            return s.Equals("Filled", StringComparison.OrdinalIgnoreCase)
                || s.Equals("Canceled", StringComparison.OrdinalIgnoreCase)
                || s.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)
                || s.Equals("ApiCancelled", StringComparison.OrdinalIgnoreCase)
                || s.Equals("Rejected", StringComparison.OrdinalIgnoreCase);
        }

        private void Raise(OrderResult r)
        {
            try
            {
                OrderUpdated?.Invoke(r);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[IB] OrderUpdated handler threw");
            }
        }

        public void Dispose()
        {
            _sweepTimer.Dispose();

            _ib.ExecutionDetailsReceived -= OnExecutionDetails;
            _ib.CommissionReceived -= OnCommission;
            _ib.OrderStatusUpdated -= OnOrderStatusUpdate;
        }

        private static string NormalizeEngineStatus(string status)
        {
            if (string.IsNullOrWhiteSpace(status))
                return string.Empty;

            var s = status.Trim();

            if (s.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("ApiCancelled", StringComparison.OrdinalIgnoreCase))
                return "Canceled";

            return s;
        }
    }

    internal static class IbkrMapper
    {
        private const decimal OutsideRthStopLimitSlipFraction = 0.01m; // 1.0%

        public static object ToContract(Symbol s) => new { Symbol = s.Ticker };

        public static object ToOrder(OrderRequest req)
        {
            var isExit =
                req.IsExit ||
                req.CorrelationId.StartsWith("exit-", StringComparison.OrdinalIgnoreCase);

            var isOco = !string.IsNullOrWhiteSpace(req.OcoGroupId);

            var transmit = true;

            // NEW: pass TIF through so RealIbkrClient doesn't default to DAY
            var tif = req.Tif.ToString(); // or: var tif = req.Tif; if it's already string
            
            var isOutsideRthStopLimit =
                req.StopPrice.HasValue &&
                isExit &&
                req.CorrelationId.StartsWith("exit-sl-orth-", StringComparison.OrdinalIgnoreCase);

            // Default: RTH only. Outside RTH ukljucujemo samo za namenski orth stop-limit nalog.
            var outsideRth = isOutsideRthStopLimit;

            Log.Information(
                "[IB-MAP] corr={Corr} exit={IsExit} sym={Sym} side={Side} type={Type} qty={Qty} lmt={Lmt} stop={Stop} oco={Oco} tif={Tif} outsideRth={OutsideRth} transmit={Transmit}",
                req.CorrelationId,
                isExit,
                req.Symbol.Ticker,
                req.Side,
                req.Type,
                req.Quantity,
                req.LimitPrice,
                req.StopPrice,
                req.OcoGroupId ?? "null",
                tif,
                outsideRth,
                transmit
            );

            if (req.StopPrice.HasValue)
            {
                if (isOutsideRthStopLimit)
                {
                    var stopPrice = req.StopPrice.Value;
                    var isSell = req.Side == OrderSide.Sell;
                    var slip = Math.Max(stopPrice * OutsideRthStopLimitSlipFraction, 0.01m);
                    var limitPrice = req.LimitPrice ??
                        (isSell ? (stopPrice - slip) : (stopPrice + slip));

                    Log.Information(
                        "[IB-MAP-ORTH] corr={Corr} sym={Sym} side={Side} stop={Stop} limit={Limit} outsideRth=true",
                        req.CorrelationId,
                        req.Symbol.Ticker,
                        req.Side,
                        stopPrice,
                        limitPrice);

                    return new
                    {
                        Side = req.Side.ToString(),
                        Qty = req.Quantity,
                        OrderType = "STP LMT",
                        StopPx = stopPrice,
                        Px = limitPrice,
                        Oco = isOco ? req.OcoGroupId : null,
                        Tif = tif,
                        OutsideRth = true,
                        Transmit = transmit,
                        IsExit = isExit,
                        req.CorrelationId
                    };
                }

                return new
                {
                    Side = req.Side.ToString(),
                    Qty = req.Quantity,
                    OrderType = "STP",
                    StopPx = req.StopPrice.Value,
                    Oco = isOco ? req.OcoGroupId : null,
                    Tif = tif,
                    OutsideRth = outsideRth,
                    Transmit = transmit,
                    IsExit = isExit,
                    req.CorrelationId
                };
            }

            return new
            {
                Side = req.Side.ToString(),
                Qty = req.Quantity,
                OrderType = req.LimitPrice.HasValue ? "LMT" : "MKT",
                Px = req.LimitPrice,
                Oco = isOco ? req.OcoGroupId : null,
                Tif = tif,
                OutsideRth = outsideRth,
                Transmit = transmit,
                IsExit = isExit,
                req.CorrelationId
            };
        }
    }
}

