using System;
using System.Threading.Tasks;

namespace Denis.TradingEngine.Broker.IBKR
{
    /// <summary>
    /// Minimalni IBKR klijent koji nama treba za order-e.
    /// Kasnije ga vežeš na pravi TWS/Gateway wrapper koji već imaš.
    /// </summary>
    public interface IIbkrClient
    {
        /// <summary>
        /// IBKR nam kaže "order je delimično / potpuno popunjen".
        /// </summary>
        event EventHandler<IbExecutionDetails>? ExecutionDetailsReceived;

        /// <summary>
        /// IBKR šalje info o proviziji za execId.
        /// </summary>
        event Action<string, decimal, string?>? CommissionReceived;

        /// <summary>
        /// IBKR javlja status naloga (Submitted, Filled, Canceled).
        /// </summary>
        event Action<int, string, int, decimal?, string?>? OrderStatusUpdated;

        /// <summary>
        /// Pošalji order u IB.
        /// </summary>
        Task PlaceOrderAsync(int orderId, object contract, object order);

        /// <summary>
        /// Poništi postojeći order u IBKR-u.
        /// </summary>
        Task CancelOrderAsync(int orderId);
    }
}
