#nullable enable
using Denis.TradingEngine.Core.Trading;
using Denis.TradingEngine.Core.Risk;
using CashState = Denis.TradingEngine.Core.Accounts.CashState;

namespace Denis.TradingEngine.Core.Interfaces
{
    /// <summary>
    /// Rezultat provere rizika: da li je dozvoljen ulaz i u kojoj količini.
    /// Ima i helper metode da kod bude čistiji u orchestratoru.
    /// </summary>
    public sealed record RiskCheckResult(
        bool Allowed,
        decimal Quantity,
        string? Reason
    )
    {
        public static RiskCheckResult AllowedEntry(decimal quantity)
            => new(true, quantity, null);

        public static RiskCheckResult Blocked(string reason)
            => new(false, 0, reason);
    }

    /// <summary>
    /// Validacija rizika i ekonomike pre ulaska u poziciju.
    /// </summary>
    public interface IRiskValidator
    {
        /// <summary>
        /// Procena da li je signal dozvoljen s obzirom na keš, limte i fee.
        /// </summary>
        RiskCheckResult EvaluateEntry(
            TradeSignal signal,
            CashState cash,
            RiskLimits limits,
            CommissionSchedule fees,
            decimal perSymbolBudgetUsd
        );
    }
}