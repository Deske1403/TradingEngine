#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Denis.TradingEngine.App.Trading.EodSkim
{
    /// <summary>
    /// Runtime context/adapters koje TradingOrchestrator prosleđuje EOD skim coordinatoru.
    /// V1: namerno delegat-based da minimizuje coupling.
    /// </summary>
    public sealed class IbkrEodSkimContext
    {
        public DateTime UtcNow { get; init; }
        public DateTime MarketCloseUtc { get; init; }
        public bool IsRealMode { get; init; }
        public bool IsIbkrMode { get; init; }
        public decimal EstimatedPerOrderFeeUsd { get; init; }
        public decimal EstimatedSlippageBufferUsd { get; init; }

        public IReadOnlyList<IbkrEodSkimPositionCandidate> OpenPositions { get; init; } =
            Array.Empty<IbkrEodSkimPositionCandidate>();

        public Func<string, DateTime, IbkrEodSkimQuoteLookupResult>? TryGetQuote { get; init; }

        public Func<string, DateTime, string, CancellationToken, Task>? CancelAllExitsForSymbolAsync { get; init; }

        public Func<IbkrEodSkimPlaceRequest, CancellationToken, Task<IbkrEodSkimPlaceResult>>? PlaceSkimExitAsync { get; init; }
    }
}
