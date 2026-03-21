#nullable enable
using System;
using System.Collections.Generic;

namespace Denis.TradingEngine.Orders
{
    /// <summary>
    /// Thread-safe skladište pending naloga.
    /// Ključ je uvek CorrelationId (onaj "sig-xxxx" ili "exit-xxxx").
    ///
    /// Važna stvar: PendingOrder je record → ne menjamo ga “u mestu”.
    /// Kad hoćemo da upišemo brokerOrderId ili fee, napravimo NOVI record
    /// i zamenimo ga u dictionary-ju. Tako izbegavamo half-written stanje.
    /// </summary>
    public sealed class OrderCoordinator : IOrderCoordinator
    {
        private readonly object _sync = new();

        // correlationId -> PendingOrder
        private readonly Dictionary<string, PendingOrder> _pending =
            new(StringComparer.OrdinalIgnoreCase);

        /// <inheritdoc />
        public bool TryAdd(PendingOrder po)
        {
            if (po is null) throw new ArgumentNullException(nameof(po));
            var key = po.CorrelationId;

            lock (_sync)
            {
                // ne dozvoli dupliranje istog correlationId-a
                if (_pending.ContainsKey(key))
                    return false;

                _pending[key] = po;
                return true;
            }
        }

        /// <inheritdoc />
        public bool TryRemove(string correlationId, out PendingOrder? removed)
        {
            if (correlationId is null) throw new ArgumentNullException(nameof(correlationId));

            lock (_sync)
            {
                var ok = _pending.Remove(correlationId, out var po);
                removed = po;
                return ok;
            }
        }

        /// <inheritdoc />
        public PendingOrder[] Snapshot()
        {
            lock (_sync)
            {
                var arr = new PendingOrder[_pending.Count];
                int i = 0;
                foreach (var po in _pending.Values)
                    arr[i++] = po with { }; // shallow copy record-a
                return arr;
            }
        }

        /// <inheritdoc />
        public PendingOrder[] RemoveExpired(DateTime nowUtc, TimeSpan ttl)
        {
            if (ttl <= TimeSpan.Zero)
                return Array.Empty<PendingOrder>();

            List<PendingOrder> expired;

            lock (_sync)
            {
                expired = new List<PendingOrder>();

                // uzmi kopiju ključeva da možemo da brišemo iz rečnika
                var keys = new List<string>(_pending.Keys);
                foreach (var k in keys)
                {
                    var po = _pending[k];
                    var age = nowUtc - po.AtUtc;
                    if (age >= ttl)
                    {
                        expired.Add(po);
                        _pending.Remove(k);
                    }
                }
            }

            // napolju orchestrator radi unreserve / exposure release / cancel
            return expired.ToArray();
        }

        // ======================================================
        //  DODATNE POMOĆNE METODE (podržane i u interfejsu)
        // ======================================================

        /// <inheritdoc />
        public bool TryGet(string correlationId, out PendingOrder? po)
        {
            if (correlationId is null) throw new ArgumentNullException(nameof(correlationId));

            lock (_sync)
            {
                var ok = _pending.TryGetValue(correlationId, out var found);
                po = found;
                return ok;
            }
        }

        /// <inheritdoc />
        public bool TrySetBrokerOrderId(string correlationId, string brokerOrderId)
        {
            if (string.IsNullOrWhiteSpace(correlationId))
                return false;
            if (string.IsNullOrWhiteSpace(brokerOrderId))
                return false;

            lock (_sync)
            {
                if (!_pending.TryGetValue(correlationId, out var existing))
                    return false;

                // napravi novi record sa popunjenim brokerId-jem
                var updated = new PendingOrder(
                    Req: existing.Req,
                    ReservedUsd: existing.ReservedUsd,
                    AtUtc: existing.AtUtc,
                    BrokerOrderId: brokerOrderId,
                    LastFeeUsd: existing.LastFeeUsd,
                    LastExecId: existing.LastExecId
                );

                _pending[correlationId] = updated;
                return true;
            }
        }

        /// <inheritdoc />
        public bool TrySetFee(string correlationId, decimal feeUsd, string? execId = null)
        {
            if (string.IsNullOrWhiteSpace(correlationId))
                return false;
            if (feeUsd <= 0m)
                return false;

            lock (_sync)
            {
                if (!_pending.TryGetValue(correlationId, out var existing))
                    return false;

                var updated = new PendingOrder(
                    Req: existing.Req,
                    ReservedUsd: existing.ReservedUsd,
                    AtUtc: existing.AtUtc,
                    BrokerOrderId: existing.BrokerOrderId,
                    LastFeeUsd: feeUsd,
                    LastExecId: execId
                );

                _pending[correlationId] = updated;
                return true;
            }
        }
    }
}