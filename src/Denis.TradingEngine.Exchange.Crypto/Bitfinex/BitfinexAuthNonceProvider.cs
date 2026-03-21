#nullable enable

using System.Globalization;
using System.Threading;

namespace Denis.TradingEngine.Exchange.Crypto.Bitfinex;

/// <summary>
/// Shared monotonic nonce generator for all Bitfinex authenticated REST/WS clients
/// that run inside the same process and API-key scope.
/// </summary>
public sealed class BitfinexAuthNonceProvider
{
    private long _lastNonceMicros;

    public string NextNonceMicros()
    {
        var nowMicros = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;

        while (true)
        {
            var previous = Interlocked.Read(ref _lastNonceMicros);
            var next = nowMicros > previous ? nowMicros : previous + 1;

            if (Interlocked.CompareExchange(ref _lastNonceMicros, next, previous) == previous)
                return next.ToString(CultureInfo.InvariantCulture);

            nowMicros = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        }
    }
}
