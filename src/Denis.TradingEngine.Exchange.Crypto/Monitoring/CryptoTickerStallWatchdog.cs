#nullable enable
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Denis.TradingEngine.Exchange.Crypto.Abstractions;
using Denis.TradingEngine.Logging.Discord;
using Serilog;

namespace Denis.TradingEngine.Exchange.Crypto.Monitoring;

/// <summary>
/// Detects per-symbol ticker stream stalls (no updates for too long) and sends Discord alerts.
/// Intended to catch silent/partial WS data loss even when connection stays technically "up".
/// </summary>
internal sealed class CryptoTickerStallWatchdog
{
    private readonly string _exchange;
    private readonly ILogger _log;
    private readonly DiscordNotifier? _discordNotifier;
    private readonly TimeSpan _staleAfter;
    private readonly TimeSpan _checkInterval;
    private readonly TimeSpan _alertCooldown;

    private readonly ConcurrentDictionary<string, byte> _expectedSymbols = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _lastTickerUtcBySymbol = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _lastAlertUtcBySymbol = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _inAlertState = new(StringComparer.OrdinalIgnoreCase);

    public CryptoTickerStallWatchdog(
        string exchange,
        ILogger log,
        DiscordNotifier? discordNotifier,
        TimeSpan? staleAfter = null,
        TimeSpan? checkInterval = null,
        TimeSpan? alertCooldown = null)
    {
        _exchange = string.IsNullOrWhiteSpace(exchange) ? "UNKNOWN" : exchange.ToUpperInvariant();
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _discordNotifier = discordNotifier;
        _staleAfter = staleAfter ?? TimeSpan.FromMinutes(5);
        _checkInterval = checkInterval ?? TimeSpan.FromSeconds(30);
        _alertCooldown = alertCooldown ?? TimeSpan.FromMinutes(15);
    }

    public void RegisterExpectedSymbol(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return;

        _expectedSymbols.TryAdd(symbol.Trim(), 0);
    }

    public void ObserveTicker(TickerUpdate ticker)
    {
        var symbol = ticker.Symbol.PublicSymbol;
        if (string.IsNullOrWhiteSpace(symbol))
            return;

        var tsUtc = ticker.TimestampUtc == default ? DateTime.UtcNow : ticker.TimestampUtc;
        _lastTickerUtcBySymbol[symbol] = tsUtc;

        if (_inAlertState.TryRemove(symbol, out _))
        {
            _log.Information(
                "[MD-WATCHDOG] {Exchange} ticker stream recovered for {Symbol} (last update {Ts:o})",
                _exchange, symbol, tsUtc);

            _ = NotifyRecoveryAsync(symbol, tsUtc);
        }
    }

    public Task RunAsync(CancellationToken ct)
    {
        return Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_checkInterval, ct).ConfigureAwait(false);
                    await CheckAsync(DateTime.UtcNow, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[MD-WATCHDOG] {Exchange} ticker stall watchdog loop error", _exchange);
                }
            }
        }, ct);
    }

    private async Task CheckAsync(DateTime nowUtc, CancellationToken ct)
    {
        foreach (var kvp in _expectedSymbols)
        {
            var symbol = kvp.Key;
            if (!_lastTickerUtcBySymbol.TryGetValue(symbol, out var lastUtc))
            {
                // Skip symbols we haven't seen yet (startup/warmup).
                continue;
            }

            var age = nowUtc - lastUtc;
            if (age <= _staleAfter)
                continue;

            if (_lastAlertUtcBySymbol.TryGetValue(symbol, out var lastAlertUtc) && (nowUtc - lastAlertUtc) < _alertCooldown)
                continue;

            _lastAlertUtcBySymbol[symbol] = nowUtc;
            _inAlertState[symbol] = 0;

            _log.Warning(
                "[MD-WATCHDOG] {Exchange} ticker stream stale for {Symbol}: last ticker {Last:o} (age={AgeSec:F0}s, threshold={ThresholdSec:F0}s)",
                _exchange, symbol, lastUtc, age.TotalSeconds, _staleAfter.TotalSeconds);

            await NotifyStallAsync(symbol, lastUtc, age, ct).ConfigureAwait(false);
        }
    }

    private async Task NotifyStallAsync(string symbol, DateTime lastUtc, TimeSpan age, CancellationToken ct)
    {
        if (_discordNotifier is null)
            return;

        try
        {
            await _discordNotifier.NotifyWarningAsync(
                title: $"[CRYPTO] { _exchange } stale data stream",
                description: $"No ticker updates for {symbol} > {_staleAfter.TotalMinutes:F0}m",
                details: $"symbol={symbol}\nexchange={_exchange}\nlastTickerUtc={lastUtc:O}\nageSec={(int)Math.Round(age.TotalSeconds)}",
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[MD-WATCHDOG] Failed to send Discord stale-stream alert for {Exchange}/{Symbol}", _exchange, symbol);
        }
    }

    private async Task NotifyRecoveryAsync(string symbol, DateTime recoveredAtUtc)
    {
        if (_discordNotifier is null)
            return;

        try
        {
            await _discordNotifier.NotifyWarningAsync(
                title: $"[CRYPTO] { _exchange } data stream recovered",
                description: $"Ticker updates resumed for {symbol}",
                details: $"symbol={symbol}\nexchange={_exchange}\nrecoveredAtUtc={recoveredAtUtc:O}",
                ct: CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[MD-WATCHDOG] Failed to send Discord recovery alert for {Exchange}/{Symbol}", _exchange, symbol);
        }
    }
}
