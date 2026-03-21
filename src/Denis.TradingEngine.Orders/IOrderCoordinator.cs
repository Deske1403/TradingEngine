#nullable enable
using System;

namespace Denis.TradingEngine.Orders
{
    /// <summary>
    /// Minimalni ugovor za koordinaciju "pending" naloga.
    /// Orchestrator ga koristi da:
    ///  - ubaci pending čim napravi OrderRequest,
    ///  - kasnije mu upiše brokerOrderId kada broker vrati,
    ///  - pročita ga (snapshot / pojedinačno),
    ///  - skine ga kad se nalog popuni ili istekne.
    /// Ovo je potpuno nezavisno od konkretnog brokera.
    /// </summary>
    public interface IOrderCoordinator
    {
        /// <summary>
        /// Pokušaj da dodaš pending.
        /// Vraća false ako već postoji isti CorrelationId (da ne dupliramo).
        /// </summary>
        bool TryAdd(PendingOrder po);

        /// <summary>
        /// Ukloni po CorrelationId i vrati ga napolje (da orchestrator može da unreserve itd.).
        /// </summary>
        bool TryRemove(string correlationId, out PendingOrder? removed);

        /// <summary>
        /// Snapshot svih pending naloga (za heartbeat, snapshot writer, debug).
        /// </summary>
        PendingOrder[] Snapshot();

        /// <summary>
        /// Ukloni sve koji su stariji od ttl (AtUtc + ttl &lt;= now).
        /// Orchestrator onda za svaki oslobodi keš, exposure i eventualno pošalje cancel.
        /// </summary>
        PendingOrder[] RemoveExpired(DateTime nowUtc, TimeSpan ttl);

        /// <summary>
        /// Kad broker vrati svoj orderId (npr. "20021"), ovde ga upišemo u postojeći pending.
        /// </summary>
        bool TrySetBrokerOrderId(string correlationId, string brokerOrderId);

        /// <summary>
        /// Pročitaj jedan pending po CorrelationId, bez skidanja.
        /// Korisno kad ti treba samo BrokerOrderId.
        /// </summary>
        bool TryGet(string correlationId, out PendingOrder? po);

        /// <summary>
        /// (opciono) Upis poslednje provizije i execId-a u pending.
        /// Ne moraš da koristiš odmah, ali orchestratior/OrderService mogu.
        /// </summary>
        bool TrySetFee(string correlationId, decimal feeUsd, string? execId = null);
    }
}