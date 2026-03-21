namespace Denis.TradingEngine.Core.Swing;

/// <summary>
/// Swing konfiguracija za crypto trading (bez RTH provera, 24/7).
/// </summary>
public sealed class CryptoSwingConfig
{
    public CryptoSwingMode Mode { get; init; } = CryptoSwingMode.Swing;
    public int MaxHoldingDays { get; init; } = 10;
    public decimal MaxSingleTradeRiskPct { get; init; } = 0.01m;
    /// <summary>True: dozvoli više ordera po simbolu (svaki signal svoj limit+stop); layered OCO po fill-u.</summary>
    public bool MultipleOrders { get; init; }
}

public enum CryptoSwingMode
{
    IntradayOnly = 0,
    Swing = 1,
    Off = 2
}

