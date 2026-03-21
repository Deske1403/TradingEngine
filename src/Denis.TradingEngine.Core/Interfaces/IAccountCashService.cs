#nullable enable
using System;
using System.Threading.Tasks;

namespace Denis.TradingEngine.Core.Accounts
{
    /// <summary>
    /// Servis za stanje gotovine sa T+1 modelom (Free / Settling / InPositions).
    /// </summary>
    public interface IAccountCashService
    {
        /// <summary> Trenutno stanje keša. </summary>
        Task<CashState> GetCashStateAsync();

        /// <summary> Rezerviši iznos pre slanja naloga. </summary>
        void Reserve(decimal amountUsd, DateTime utcNow);

        /// <summary> Otpusti rezervaciju (npr. kad se nalog otkaže). </summary>
        void Unreserve(decimal amountUsd);

        /// <summary> BUY je popunjen: iznos ide iz Free u InPositions. </summary>
        void OnBuyFilled(decimal costUsd, DateTime utcNow);

        /// <summary> SELL je popunjen: prihod ide u Settling (T+1). </summary>
        void OnSellProceeds(decimal proceedsUsd, DateTime utcNow);

        /// <summary>
        /// Broker je naplatio proviziju – skini odmah iz Free (ili koliko ima).
        /// </summary>
        void OnCommissionPaid(decimal feeUsd);

        /// <summary> Ručno obeleži da je nešto ušlo u Settling (ako treba). </summary>
        void MarkSettling(decimal amountUsd);

        /// <summary> Ručno dodaj u Free (ako treba). </summary>
        void MarkFree(decimal amountUsd);

        /// <summary> Dnevni roll: Settling -> Free kad dođe novi UTC dan. </summary>
        void DailyRoll(DateTime utcNow);

        /// <summary> Event kad se stanje promeni (opciono za UI/telemetriju). </summary>
        event Action<CashState>? CashStateChanged;
    }

    /// <summary>
    /// Snapshot stanja keša (bez Total – računamo po potrebi).
    /// </summary>
    public sealed record CashState(
        decimal Free,
        decimal Settling,
        decimal InPositions,
        decimal Reserved = 0m
    );
}