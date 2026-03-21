namespace Denis.TradingEngine.Core.Interfaces
{
    public interface IExternalPositionsProvider
    {
        /// <summary>
        /// Vrati listu pozicija koje broker sada stvarno vidi.
        /// Qty = 0 => nema poziciju.
        /// </summary>
        Task<IReadOnlyList<ExternalPositionDto>> GetOpenPositionsAsync(CancellationToken ct = default);
    }

    public sealed record ExternalPositionDto(
        string Symbol,
        decimal Quantity,
        decimal AveragePrice
    );
}