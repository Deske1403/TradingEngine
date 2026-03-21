#nullable enable
using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Denis.TradingEngine.Exchange.Crypto.Common;

/// <summary>
/// Robustna bazna klasa za WS:
/// - Jedan background "runner" koji drži konekciju živom
/// - Auto-reconnect na greške/close
/// - Idle-timeout reconnect (silent-dead detekcija)
/// - Hook OnConnectedAsync za resubscribe
/// - Single-writer send lock
/// </summary>
public abstract class WebSocketConnectionBase : IAsyncDisposable
{
    private readonly Uri _uri;
    private readonly ILogger _log;
    private readonly TimeSpan _reconnectDelay;
    private readonly TimeSpan _idleTimeout;

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private CancellationTokenSource _stopCts = new();
    private Task? _runnerTask;

    private ClientWebSocket? _ws;

    private TaskCompletionSource<bool>? _firstConnectedTcs;

    public bool IsConnected => _ws is { State: WebSocketState.Open };

    public event Action<string>? RawMessageReceived;

    protected WebSocketConnectionBase(
        string url,
        ILogger log,
        TimeSpan? reconnectDelay = null,
        TimeSpan? idleTimeout = null)
    {
        _uri = new Uri(url ?? throw new ArgumentNullException(nameof(url)));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        _reconnectDelay = reconnectDelay ?? TimeSpan.FromSeconds(5);
        _idleTimeout = idleTimeout ?? TimeSpan.FromSeconds(90); // default: 90s
    }

    public async Task ConnectAsync(CancellationToken externalCt)
    {
        await _lifecycleGate.WaitAsync(externalCt).ConfigureAwait(false);
        try
        {
            if (_runnerTask != null)
                return;

            if (_stopCts.IsCancellationRequested)
            {
                _stopCts.Dispose();
                _stopCts = new CancellationTokenSource();
            }

            _firstConnectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var linked = CancellationTokenSource.CreateLinkedTokenSource(externalCt, _stopCts.Token);
            _runnerTask = Task.Run(() => RunAsync(linked.Token), CancellationToken.None);

            using var reg = externalCt.Register(
                () => _firstConnectedTcs.TrySetCanceled(externalCt),
                useSynchronizationContext: false);

            await _firstConnectedTcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _stopCts.Cancel();

            if (_runnerTask != null)
            {
                try { await _runnerTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _log.Error(ex, "[WS] Runner crashed during shutdown."); }
                finally { _runnerTask = null; }
            }

            await CloseAndDisposeSocketAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CloseAndDisposeSocketAsync().ConfigureAwait(false);

                _log.Information("[WS] Connecting to {Url}", _uri);

                var ws = new ClientWebSocket();
                await ws.ConnectAsync(_uri, ct).ConfigureAwait(false);

                _ws = ws;

                _log.Information("[WS] Connected to {Url}", _uri);

                _firstConnectedTcs?.TrySetResult(true);

                try
                {
                    await OnConnectedAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "[WS] OnConnectedAsync threw (continuing).");
                }

                await ReceiveLoopAsync(ws, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _firstConnectedTcs?.TrySetException(ex);

                _log.Error(ex, "[WS] Connection/loop failed. Reconnect in {Delay}.", _reconnectDelay);

                try
                {
                    await Task.Delay(_reconnectDelay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var sb = new StringBuilder();

                while (true)
                {
                    // Idle-timeout: ako nema NIJEDNOG frame-a predugo -> reconnect
                    using var idleCts = _idleTimeout > TimeSpan.Zero
                        ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                        : null;

                    if (idleCts != null)
                        idleCts.CancelAfter(_idleTimeout);

                    WebSocketReceiveResult result;
                    try
                    {
                        result = await ws.ReceiveAsync(
                            buffer,
                            idleCts?.Token ?? ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested && idleCts != null && idleCts.IsCancellationRequested)
                    {
                        _log.Warning("[WS] Idle timeout ({Timeout}) – forcing reconnect.", _idleTimeout);
                        return; // runner će reconnect
                    }

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _log.Warning("[WS] Close frame received, status={Status} desc={Desc}",
                            result.CloseStatus, result.CloseStatusDescription);

                        await HandleClosedAsync(result.CloseStatus, result.CloseStatusDescription).ConfigureAwait(false);
                        return;
                    }

                    var chunk = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    sb.Append(chunk);

                    if (result.EndOfMessage)
                    {
                        var json = sb.ToString();
                        RawMessageReceived?.Invoke(json);

                        await HandleMessageAsync(json, ct).ConfigureAwait(false);
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (WebSocketException wsex)
            {
                _log.Error(wsex, "[WS] WebSocketException in receive loop. Will reconnect.");
                return;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[WS] Error in receive loop. Will reconnect.");
                return;
            }
        }
    }

    protected virtual Task OnConnectedAsync(CancellationToken ct) => Task.CompletedTask;

    protected abstract Task HandleMessageAsync(string rawJson, CancellationToken ct);

    protected virtual Task HandleClosedAsync(WebSocketCloseStatus? status, string? description)
    {
        _log.Warning("[WS] Closed: {Status} {Desc}", status, description);
        return Task.CompletedTask;
    }

    public async Task SendAsync(string text, CancellationToken ct)
    {
        var ws = _ws;
        if (ws == null || ws.State != WebSocketState.Open)
            throw new InvalidOperationException("WebSocket is not open.");

        var bytes = Encoding.UTF8.GetBytes(text);

        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ws.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken: ct).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task CloseAndDisposeSocketAsync()
    {
        var ws = _ws;
        _ws = null;

        if (ws == null)
            return;

        try
        {
            if (ws.State == WebSocketState.Open)
            {
                await ws.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Client disconnect",
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
            // ignore
        }
        finally
        {
            ws.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _stopCts.Dispose();
        _lifecycleGate.Dispose();
        _sendGate.Dispose();
    }
}
