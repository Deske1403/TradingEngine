#nullable enable
using System;

namespace Denis.TradingEngine.Core.Orders
{
    /// <summary>
    /// Rezultat izvršenja naloga — pratimo status, cenu popune i provizije.
    /// </summary>
    public sealed record OrderResult(
        string BrokerOrderId,       // ID naloga kod brokera
        string Status,              // PreSubmitted, Filled, Rejected, Cancelled
        decimal FilledQuantity,         // Koliko je popunjeno
        decimal? AverageFillPrice,  // Prosečna cena popune
        decimal? CommissionAndFees, // Provizija i naknade
        string? Message,            // Opciona poruka o toku naloga
        DateTime TimestampUtc);     // Uvek UTC
}