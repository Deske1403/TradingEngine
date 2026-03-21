#nullable enable
using System;
using Denis.TradingEngine.Core.Interfaces;
using Denis.TradingEngine.Core.Risk;
using Denis.TradingEngine.Core.Trading;
using CashState = Denis.TradingEngine.Core.Accounts.CashState;

namespace Denis.TradingEngine.Risk
{
    public sealed class FeeAwareRiskValidator : IRiskValidator
    {
        // trenutno ih ne koristimo u računu, ali ih čuvamo da bi strategija i risk bili "u tonu"
        private readonly decimal _targetProfitFraction;
        private readonly decimal _stopLossFraction;

        public FeeAwareRiskValidator(decimal targetProfitFraction = 0.01m, decimal stopLossFraction = 0.002m)
        {
            _targetProfitFraction = targetProfitFraction;
            _stopLossFraction = stopLossFraction;
        }

        public RiskCheckResult EvaluateEntry(TradeSignal signal, CashState cash,RiskLimits limits,CommissionSchedule fees,decimal perSymbolBudgetUsd)
        {
            var px = signal.SuggestedLimitPrice ?? 0m;
            if (px <= 0m)
                return new(false, 0m, "no price");

            // 1) max po simbolu iz konfiguracije (tvojih 1000 npr.)
            var symbolCap = perSymbolBudgetUsd;

            // 2) max po TREJDU iz limita (npr. 0.3 = 30% od free keša)
            // ako nisi dodao polje u RiskLimits, dole ću ti pokazati
            var maxPerTradeUsd = cash.Free * limits.MaxPerTradeFrac;

            // 3) stvarni cap je manji od ta dva
            var spendCap = Math.Min(symbolCap, maxPerTradeUsd);
            if (spendCap <= 0m)
                return new(false, 0m, "spendCap=0");

            // 4) skini fee
            var availableForShares = spendCap - fees.EstimatedPerOrderUsd;
            if (availableForShares <= 0m)
                return new(false, 0m, "not enough after fee");

            // 5) izračunaj količinu
            var qty = availableForShares / px;

            // malo obrezivanje da ne šaljemo 0.0000000001
            qty = Math.Round(qty, 6, MidpointRounding.ToZero);
            if (qty <= 0m)
                return new(false, 0m, "qty<=0");

            return new(true, qty, "");
        }
    }
}