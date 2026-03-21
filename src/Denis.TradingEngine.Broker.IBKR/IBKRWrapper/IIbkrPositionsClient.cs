namespace Denis.TradingEngine.Broker.IBKR.IBKRWrapper
{
    /// <summary>
    /// Minimalan interfejs za povlačenje pozicija sa IBKR.
    /// Tvoj wrapper (IbkrDefaultWrapper) će ovo da implementira.
    /// </summary>
    public interface IIbkrPositionsClient
    {
        // IB šalje po jednu poziciju
        event EventHandler<IbkrPosition>? PositionReceived;

        // IB kaže "gotovo"
        event EventHandler? PositionsEnd;

        // pošalji reqPositions ka IB-u
        void RequestPositions();
    }

    /// <summary>
    /// DTO za jednu IBKR poziciju.
    /// </summary>
    public sealed class IbkrPosition
    {
        public string Account { get; init; } = string.Empty;
        public string Symbol { get; init; } = string.Empty;
        public string SecType { get; init; } = string.Empty;
        public string Currency { get; init; } = string.Empty;
        public decimal Position { get; init; }
        public decimal AvgCost { get; init; }
    }
}