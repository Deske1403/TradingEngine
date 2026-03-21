#nullable enable
using System;
using System.Threading.Tasks;
using Denis.TradingEngine.Core.Orders;

namespace Denis.TradingEngine.Core.Interfaces
{
    /// <summary>
    /// Servis za rad sa nalozima (kroz broker adapter, npr. IBKR).
    /// </summary>
    public interface IOrderService
    {
        /// <summary>
        /// Kreiranje naloga. Vraća broker-ov ID naloga.
        /// </summary>
        Task<string> PlaceAsync(OrderRequest request);

        /// <summary>
        /// Otkazivanje naloga po broker ID-ju.
        /// </summary>
        Task CancelAsync(string brokerOrderId);

        /// <summary>
        /// Događaj o promeni statusa naloga (prijem popuna, provizija itd).
        /// </summary>
        event Action<OrderResult>? OrderUpdated;
    }
}