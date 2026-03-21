using System;

namespace Denis.TradingEngine.App.Config
{
    /// <summary>
    /// Globalni režim rada engine-a:
    /// - Off           => potpuno isključena swing logika, sve radi kao do sada.
    /// - IntradayOnly  => ne želimo da držimo pozicije preko noći,
    ///                    kasnije možemo da dodamo agresivno flatten pred kraj sesije.
    /// - Swing         => dopuštamo držanje preko noći, vikend logiku,
    ///                    max holding horizon itd.
    /// </summary>
    public enum SwingMode
    {
        IntradayOnly = 0,
        Swing = 1,
        Off = 2
    }

    /// <summary>
    /// Osnovna swing konfiguracija.
    /// Mapira se direktno na sekciju "SwingTrading" u appsettings.json.
    /// </summary>
    public sealed class SwingTradingConfig
    {
        public SwingMode Mode { get; init; } = SwingMode.Swing;

        public bool CloseBeforeWeekend { get; init; } = true;
        public TimeSpan WeekendCutoffUtc { get; init; } = new(20, 30, 0);
        public bool ForceExitAtSessionEnd { get; init; } = false;
        public bool AllowOvernight { get; init; } = true;
        public int MaxHoldingDays { get; init; } = 10;
        public decimal MaxOvernightGapLossPct { get; init; } = 0.04m;
        public decimal MaxSingleTradeRiskPct { get; init; } = 0.01m;

        // 🔽 NOVO:
        /// <summary>
        /// Da li engine sme da šalje real exit naloge na osnovu swing pravila.
        /// Default=false (sve radi samo kao upozorenje).
        /// </summary>
        public bool AutoExitReal { get; init; } = false;

        /// <summary>
        /// Ako je true i AutoExitReal=true, pozicije koje pređu MaxHoldingDays
        /// dobiće automatski exit nalog.
        /// </summary>
        public bool AutoExitOnMaxHoldingDays { get; init; } = false;

        /// <summary>
        /// Ako je true i AutoExitReal=true, u petak posle WeekendCutoffUtc
        /// sve otvorene pozicije mogu dobiti auto exit nalog.
        /// </summary>
        public bool AutoExitBeforeWeekend { get; init; } = false;

        /// <summary>
        /// Tag koji stavljamo u reason string za auto-exit naloge, čisto da
        /// lakše filtriraš u logovima / DB-u.
        /// </summary>
        public string AutoExitReasonTag { get; init; } = "SWING-AUTO";

        /// <summary>
        /// Ako je true, ne otvaramo novu poziciju (buy) ako već postoji long u tom simbolu.
        /// Jedna pozicija po simbolu – nema scale-in, nema više lotova.
        /// </summary>
        public bool MaxOnePositionPerSymbol { get; init; } = true;
    }
}