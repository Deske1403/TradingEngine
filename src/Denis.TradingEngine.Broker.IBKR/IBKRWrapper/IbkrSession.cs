// src/Denis.TradingEngine.Broker.IBKR/IBKRWrapper/IbkrSession.cs
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using IBApi;
using Denis.TradingEngine.Core.Trading;
using Denis.TradingEngine.Broker.IBKR; // IbkrInstrumentMap, ISubscriptionService
using Denis.TradingEngine.Logging;
using Serilog;

namespace Denis.TradingEngine.Broker.IBKR.IBKRWrapper
{
    public sealed class IbkrSession : IDisposable
    {
        private static readonly ILogger Log = AppLog.ForContext<IbkrSession>();
        private readonly IbkrDefaultWrapper _wrapper;
        private readonly EReaderSignal _signal;
        private readonly EClientSocket _client;
        private EReader? _reader;
        private Thread? _pumpThread;

        // [RECONNECT]
        public event Action? Reconnected;
        private volatile bool _shouldReconnect = true;
        private CancellationTokenSource? _reconnectCts;

        private readonly TaskCompletionSource<int> _tcsNextValidId =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource<DateTimeOffset> _tcsCurrentTime =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        // reqId brojač i mape simbol<->reqId
        private int _nextReqId = 1000;

        private readonly Dictionary<string, int> _reqIdByTicker =
            new(StringComparer.OrdinalIgnoreCase);

        // parametri poslednjeg connect-a (za reconnect)
        private string? _lastHost;
        private int? _lastPort;
        private int? _lastClientId;

        public IbkrSession(IbkrDefaultWrapper wrapper)
        {
            _wrapper = wrapper;
            _signal = new EReaderMonitorSignal();
            _client = new EClientSocket(_wrapper, _signal);

            _wrapper.Client = _client;
            _wrapper.Subscriptions = new Dictionary<int, Contract>();

            // Hook za inicijalne signale
            _wrapper.NextValidIdReceived += id => _tcsNextValidId.TrySetResult(id);
            _wrapper.CurrentTimeReceived += t => _tcsCurrentTime.TrySetResult(t);

            _wrapper.ConnectionClosed += () =>
            {
                if (_shouldReconnect)
                    StartReconnectLoop();
            };
        }

        public EClientSocket Client => _client;

        public Dictionary<int, Contract> Subscriptions => _wrapper.Subscriptions!;

        public async Task ConnectAsync(string host, int port, int clientId, CancellationToken ct)
        {
            _lastHost = host;
            _lastPort = port;
            _lastClientId = clientId;

            string? lastError = null;
            var connectStartedUtc = DateTime.UtcNow;

            void OnError(int id, int errorCode, string errorMsg)
            {
                lastError = $"id={id}, code={errorCode}, msg={errorMsg}";
            }

            _wrapper.ErrorReceived += OnError;

            Log.Information(
                "[IBKR-CONNECT] Starting connect host={Host} port={Port} clientId={ClientId}",
                host,
                port,
                clientId);

            _client.eConnect(host, port, clientId);
            try
            {
                var socketConnected = await WaitUntilConnectedAsync(TimeSpan.FromSeconds(8), ct);
                if (!socketConnected)
                {
                    var details = lastError is null ? "no IB error received yet" : lastError;
                    throw new InvalidOperationException(
                        $"IBKR socket did not enter connected state for {host}:{port} clientId={clientId}. LastError={details}");
                }

                Log.Information(
                    "[IBKR-CONNECT] Socket connected host={Host} port={Port} clientId={ClientId} elapsedMs={ElapsedMs}",
                    host,
                    port,
                    clientId,
                    (DateTime.UtcNow - connectStartedUtc).TotalMilliseconds);

                _reader = new EReader(_client, _signal);
                _reader.Start();

                _pumpThread = new Thread(() =>
                {
                    while (_client.IsConnected())
                    {
                        _signal.waitForSignal();
                        _reader?.processMsgs();
                    }
                })
                {
                    IsBackground = true,
                    Name = "IBKR-Pump"
                };
                _pumpThread.Start();

                Log.Information("[IBKR-CONNECT] Requesting init signals nextValidId + currentTime");

                // zatraži nextValidId i server time
                _client.reqIds(-1);
                _client.reqCurrentTime();

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var timeout = Task.Delay(TimeSpan.FromSeconds(30), cts.Token);

                var nextIdTask = _tcsNextValidId.Task;
                var timeTask = _tcsCurrentTime.Task;

                var waitAll = Task.WhenAll(nextIdTask, timeTask);
                var completed = await Task.WhenAny(waitAll, timeout);

                if (completed == timeout)
                {
                    Log.Warning(
                        "[IBKR-CONNECT] Init signal timeout host={Host} port={Port} clientId={ClientId} nextValidIdReceived={HasNextId} currentTimeReceived={HasCurrentTime} lastError={LastError}",
                        host,
                        port,
                        clientId,
                        nextIdTask.IsCompleted,
                        timeTask.IsCompleted,
                        lastError ?? "n/a");

                    if (!nextIdTask.IsCompleted && !timeTask.IsCompleted)
                        throw new TimeoutException(
                            $"Timed out waiting for nextValidId/currentTime from IBKR. LastError={lastError ?? "n/a"}");

                    Console.WriteLine(
                        "[WARN] Timed out waiting for one of the init signals. Continuing with what we have");
                }
                else
                {
                    cts.Cancel();
                    Log.Information(
                        "[IBKR-CONNECT] Init handshake complete nextValidId={NextId} serverTime={ServerTime:O}",
                        nextIdTask.Result,
                        timeTask.Result);
                }

                _shouldReconnect = true;
            }
            finally
            {
                _wrapper.ErrorReceived -= OnError;
            }
        }

        private void StartReconnectLoop()
        {
            if (_reconnectCts != null) return;

            _reconnectCts = new CancellationTokenSource();
            var ct = _reconnectCts.Token;

            _ = Task.Run(async () =>
            {
                var delays = new[]
                {
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(10)
                };

                var i = 0;

                while (!ct.IsCancellationRequested && _shouldReconnect)
                {
                    try
                    {
                        if (!_client.IsConnected())
                        {
                            var host = _lastHost ?? "127.0.0.1";
                            var port = _lastPort ?? 4002;
                            var clientId = _lastClientId ?? 1;

                            Log.Warning(
                                "[IBKR-RECONNECT] Attempt={Attempt} host={Host} port={Port} clientId={ClientId}",
                                i + 1,
                                host,
                                port,
                                clientId);

                            _client.eConnect(host, port, clientId);

                            if (_client.IsConnected())
                            {
                                _reader = new EReader(_client, _signal);
                                _reader.Start();

                                _pumpThread = new Thread(() =>
                                {
                                    while (_client.IsConnected())
                                    {
                                        _signal.waitForSignal();
                                        _reader?.processMsgs();
                                    }
                                })
                                {
                                    IsBackground = true,
                                    Name = "IBKR-Pump"
                                };
                                _pumpThread.Start();

                                _client.reqIds(-1);
                                _client.reqCurrentTime();

                                Log.Information(
                                    "[IBKR-RECONNECT] Reconnected host={Host} port={Port} clientId={ClientId}",
                                    host,
                                    port,
                                    clientId);

                                try
                                {
                                    Reconnected?.Invoke();
                                }
                                catch
                                {
                                }

                                i = 0;
                            }
                            else
                            {
                                Log.Warning(
                                    "[IBKR-RECONNECT] Socket still disconnected after attempt={Attempt} host={Host} port={Port} clientId={ClientId}",
                                    i + 1,
                                    host,
                                    port,
                                    clientId);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "[IBKR-RECONNECT] Attempt failed");
                    }

                    var delay = delays[Math.Min(i, delays.Length - 1)];
                    i++;

                    try
                    {
                        await Task.Delay(delay, ct);
                    }
                    catch
                    {
                        break;
                    }
                }

                _reconnectCts = null;
            });
        }

        public int NextOrderId =>
            _tcsNextValidId.Task.IsCompleted ? _tcsNextValidId.Task.Result : -1;

        private async Task<bool> WaitUntilConnectedAsync(TimeSpan timeout, CancellationToken ct)
        {
            var started = DateTime.UtcNow;
            while (!ct.IsCancellationRequested && DateTime.UtcNow - started < timeout)
            {
                if (_client.IsConnected())
                    return true;

                await Task.Delay(100, ct);
            }

            return _client.IsConnected();
        }

        // ------------------------------------------------------------
        // Legacy helpers
        // ------------------------------------------------------------

        public void SubscribeStock(int tickerId, Symbol symbol)
        {
            // koristi centralnu mapu (NVDA, EUNA, itd.)
            var c = IbkrInstrumentMap.ToContract(symbol);

            Subscriptions[tickerId] = c;
            _client.reqMarketDataType(1); 
            _client.reqMktData(tickerId, c, "", false, false, null);
            Console.WriteLine($"[SUB] {symbol} (id={tickerId})");
        }

        public void SnapshotStock(int tickerId, Symbol symbol)
        {
            var c = IbkrInstrumentMap.ToContract(symbol);

            Subscriptions[tickerId] = c;
            _client.reqMarketDataType(1); 
            _client.reqMktData(tickerId, c, "", true, false, null);
            Console.WriteLine($"[SNAP] {symbol} (id={tickerId}) requested");
        }

        public void SubscribeOrderBook(
            int reqId,
            Symbol symbol,
            int rows = 10,
            bool smartDepth = true,
            string? directExchange = null)
        {
            var c = IbkrInstrumentMap.ToContract(symbol);

            if (!smartDepth && !string.IsNullOrWhiteSpace(directExchange))
            {
                // ako eksplicitno tražiš direct exchange – respektuj
                c.Exchange = directExchange;
            }

            Subscriptions[reqId] = c;

            try
            {
                _client.reqMarketDepth(reqId, c, rows, smartDepth, null);
                Console.WriteLine(
                    $"[SUB-L2] {symbol} (id={reqId}) rows={rows} smartDepth={smartDepth} exch={c.Exchange}");
            }
            catch (MissingMethodException)
            {
                Console.WriteLine(
                    "[SUB-L2-ERR] DLL nema reqMarketDepth (proveri IB C# API verziju ≥ 9.74/10.x).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SUB-L2-ERR] {symbol} (id={reqId}): {ex.Message}");
            }
        }

        public void UnsubscribeDepth(int reqId, bool smartDepth = true)
        {
            try
            {
                CancelMktDepthCompat(reqId, smartDepth);
            }
            catch
            {
            }
            finally
            {
                Subscriptions.Remove(reqId);
            }

            Console.WriteLine($"[UNSUB-L2] id={reqId}");
        }

        public void SubscribeTicks(
            int reqId,
            Symbol symbol,
            string type = "Last",
            int numberOfTicks = 0,
            bool ignoreSize = false)
        {
            var c = IbkrInstrumentMap.ToContract(symbol);

            Subscriptions[reqId] = c;
            _client.reqTickByTickData(reqId, c, type, numberOfTicks, ignoreSize);
            Console.WriteLine(
                $"[SUB-TBT] {symbol} (id={reqId}) type={type} ticks={(numberOfTicks <= 0 ? "stream" : numberOfTicks)}");
        }

        public void UnsubscribeTicks(int reqId)
        {
            try
            {
                _client.cancelTickByTickData(reqId);
            }
            catch
            {
            }
            finally
            {
                Subscriptions.Remove(reqId);
            }

            Console.WriteLine($"[UNSUB-TBT] id={reqId}");
        }

        // Kompat sloj za različite IBApi.dll overload-e:
        private void CancelMktDepthCompat(int reqId, bool smartDepth)
        {
            var t = _client.GetType();

            var m2 = t.GetMethod("cancelMktDepth", new[] { typeof(int), typeof(bool) });
            if (m2 != null)
            {
                m2.Invoke(_client, new object[] { reqId, smartDepth });
                return;
            }

            var m1 = t.GetMethod("cancelMktDepth", new[] { typeof(int) });
            if (m1 != null)
            {
                m1.Invoke(_client, new object[] { reqId });
                return;
            }

            Console.WriteLine("[WARN] cancelMktDepth overload not found.");
        }

        private void ReqMktDepthCompat(int reqId, Contract c, int rows, bool smartDepth)
        {
            var t = _client.GetType();
            var methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => m.Name == "reqMktDepth")
                .OrderByDescending(m => m.GetParameters().Length)
                .ToList();

            if (methods.Count == 0)
                throw new MissingMethodException("EClientSocket.reqMktDepth not found on this DLL.");

            Exception? last = null;

            foreach (var m in methods)
            {
                var ps = m.GetParameters();
                try
                {
                    object?[] args;

                    if (ps.Length == 6)
                    {
                        var listInstance =
                            ps[4].ParameterType.IsAssignableFrom(typeof(List<TagValue>))
                                ? new List<TagValue>()
                                : (ps[4].ParameterType.IsInterface
                                    ? Activator.CreateInstance(typeof(List<TagValue>))
                                    : null);

                        args = new object?[] { reqId, c, rows, smartDepth, listInstance, false };
                    }
                    else if (ps.Length == 5)
                    {
                        var listInstance =
                            ps[4].ParameterType.IsAssignableFrom(typeof(List<TagValue>))
                                ? new List<TagValue>()
                                : (ps[4].ParameterType.IsInterface
                                    ? Activator.CreateInstance(typeof(List<TagValue>))
                                    : null);

                        args = new object?[] { reqId, c, rows, smartDepth, listInstance };
                    }
                    else if (ps.Length == 4)
                    {
                        var listInstance =
                            ps[3].ParameterType.IsAssignableFrom(typeof(List<TagValue>))
                                ? new List<TagValue>()
                                : (ps[3].ParameterType.IsInterface
                                    ? Activator.CreateInstance(typeof(List<TagValue>))
                                    : null);

                        args = new object?[] { reqId, c, rows, listInstance };
                    }
                    else
                    {
                        continue;
                    }

                    m.Invoke(_client, args);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }

            throw new MissingMethodException("EClientSocket.reqMktDepth: no compatible overload worked.", last);
        }

        // ------------------------------------------------------------
        // ISubscriptionService implementacija — Engine koristi ovo
        // ------------------------------------------------------------
        public void SubscribeSymbol(Symbol symbol)
        {
            if (_reqIdByTicker.ContainsKey(symbol.Ticker))
                return;

            var reqId = ++_nextReqId;
            _reqIdByTicker[symbol.Ticker] = reqId;

            var c = IbkrInstrumentMap.ToContract(symbol);

            _client.reqMarketDataType(1);
            _client.reqMktData(reqId, c, "", false, false, null);

            _wrapper.RegisterSubscription(reqId, symbol);
        }

        public void UnsubscribeSymbol(Symbol symbol)
        {
            if (!_reqIdByTicker.TryGetValue(symbol.Ticker, out var reqId))
                return;

            _client.cancelMktData(reqId);
            _wrapper.UnregisterSubscription(reqId);
            _reqIdByTicker.Remove(symbol.Ticker);
        }

        public void Dispose()
        {
            try
            {
                _shouldReconnect = false;
                _reconnectCts?.Cancel();
                _reconnectCts = null;
            }
            catch
            {
            }

            try
            {
                _client.eDisconnect();
            }
            catch
            {
            }

            try
            {
                if (_pumpThread != null && _pumpThread.IsAlive)
                    _pumpThread.Join(TimeSpan.FromSeconds(1));
            }
            catch
            {
            }
        }

        // Debug helper koji možeš da koristiš ručno kad ti treba
        public void DebugGetDetailsEuna()
        {
            var searchContract = new Contract
            {
                Symbol = "EUNA",
                Currency = "EUR",
                SecType = "STK",
                Exchange = "SMART"
            };

            Console.WriteLine("[DEBUG] Saljem zahtev za contractDetails za EUNA");
            _client.reqContractDetails(999, searchContract);
        }
    }
}
