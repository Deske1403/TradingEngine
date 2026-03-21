#nullable enable
using System;
using System.Collections.Generic;

namespace Denis.TradingEngine.App.Trading
{
    /// <summary>
    /// Podešavanja za trading koja dolaze iz appsettings.json
    /// (simboli, radno vreme, da li da koristimo estimirani fee)
    /// </summary>
    public sealed class TradingSettings
    {
        /// <summary>
        /// Lista simbola koje uopšte pratimo / trgujemo.
        /// Ovde možeš da staviš i capability po simbolu (fractional, min qty).
        /// </summary>
        public List<SymbolCapability> Symbols { get; init; } = new();

        /// <summary>
        /// Početak RTH prozora u UTC (npr. 14:30 za US)
        /// Ako je null -> ne proveravamo RTH.
        /// </summary>
        public TimeSpan? RthStartUtc { get; init; }  // npr. 14:30

        /// <summary>
        /// DST-safe pomeraj početka tradinga u odnosu na US equities open (09:30 ET).
        /// Primer: 01:30:14 => ne trguj prvih 90 min i 14 sekundi od open-a.
        /// Ako je null -> koristi legacy RthStartUtc ako je setovan.
        /// </summary>
        public TimeSpan? TradeStartOffsetFromOpenNy { get; init; }

        /// <summary>
        /// Kraj RTH prozora u UTC (npr. 21:00 za US)
        /// Ako je null -> ne proveravamo RTH.
        /// </summary>
        public TimeSpan? RthEndUtc { get; init; }    // npr. 21:00

        /// <summary>
        /// DST-safe lokalni kraj trading window-a u New York vremenu.
        /// Primer: 16:00:00 => do regularnog market close-a.
        /// Ako je null -> koristi legacy RthEndUtc ako je setovan.
        /// </summary>
        public TimeSpan? TradeEndLocalNy { get; init; }

        /// <summary>
        /// Da li orchestrator POSLE real fill-a da odmah skine
        /// onaj fiksni "EstimatedPerOrderUsd" iz CommissionSchedule.
        /// - u PAPER modu obično hoćemo = true
        /// - u REAL modu obično nećemo = false, jer IB će poslati pravu proviziju
        /// </summary>
        public bool UseEstimatedCommissionOnReal { get; init; } = false;
        public bool Enabled { get; init; } = true;
        
        /// <summary>
        /// Maksimalan broj trade-ova dnevno po simbolu.
        /// Ako je 0 ili negativno, nema limita.
        /// </summary>
        public int MaxTradesPerSymbol { get; init; } = 0;
        
        /// <summary>
        /// Maksimalan broj trade-ova dnevno ukupno (svi simboli zajedno).
        /// Ako je 0 ili negativno, nema limita.
        /// </summary>
        public int MaxTradesTotal { get; init; } = 0;

        /// <summary>
        /// Minimalni quality score za drugi trade slot i nadalje.
        /// Ako je null -> nema quality gate-a.
        /// </summary>
        public decimal? MinSignalPriorityScoreAfterFirstTrade { get; init; }

        /// <summary>
        /// Minimalni quality score za poslednji dnevni trade slot.
        /// Ako je null -> koristi MinSignalPriorityScoreAfterFirstTrade.
        /// </summary>
        public decimal? MinSignalPriorityScoreForLastTradeSlot { get; init; }
        
        /// <summary>
        /// Minimum količine za entry order (IBKR only).
        /// Ako je količina manja od ove vrednosti, order se blokira (orchestrator korak 8.5).
        /// Config: Trading.MinQuantity (default 3).
        /// </summary>
        public int MinQuantity { get; init; } = 0;

        /// <summary>
        /// Da li strategija koristi MID cenu za limit entry umesto ASK (IBKR only).
        /// true = suggestedLimit = mid (px), false = suggestedLimit = Ask.
        /// Config: Trading.UseMidPrice (default false). Prosleđuje se u PullbackInUptrendStrategy.
        /// </summary>
        public bool UseMidPrice { get; init; } = false;

        /// <summary>
        /// Ako je true, uz standardni RTH TP/SL dodaje se i treci exit nalog:
        /// STOP LIMIT sa OutsideRth=true (best-effort zastita van RTH).
        /// Ako je false, radi kao do sada (samo TP LIMIT + SL STOP u RTH).
        /// Config: Trading.EnableStopLimitOutsideRth (default false).
        /// </summary>
        public bool EnableStopLimitOutsideRth { get; init; } = false;

    }

    /// <summary>
    /// Per-symbol capability – da znaš da li broker prima frakcije i koliki je minimum.
    /// </summary>
    public sealed class SymbolCapability
    {
        public string Symbol { get; init; } = "";
        public bool SupportsFractional { get; init; } = false;
        public decimal MinQty { get; init; } = 1m;
        public decimal StepSize { get; set; } = 1m;
    }

    public class AtrState
    {
        public decimal? Prev;
        public Queue<decimal> Tr = new();
        public decimal? Atr;
    }
    // runtime stanje po simbolu (za vreme i trailing)
    public sealed class PositionRuntimeState
    {
        public DateTime EntryUtc { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal BestPrice { get; set; }

        // NOVO: da znamo da li je pozicija importovana iz IBKR-a
        public bool IsExternal { get; set; }
        
        // Hold-time model: regime i symbol baseline (zamrznuti pri entry-ju)
        public string? RegimeAtEntry { get; set; }  // LOW / NORMAL / HIGH
        public string? SymbolBaseline { get; set; } // slow / normal / fast
        public decimal? AtrAtEntry { get; set; }    // ATR vrednost pri entry-ju (za regime određivanje)
        
        // Trailing state tracking
        public bool TrailingArmed { get; set; }      // da li je trailing aktiviran
        public DateTime? LastTrailUpdateUtc { get; set; }  // kada je poslednji put ažuriran trail stop
        public decimal? LastTrailStop { get; set; }  // poslednja vrednost trail stop-a
        
        // Multi-day swing: gap protection
        public decimal? LastClosePrice { get; set; }  // poslednja cena pre zatvaranja (za gap detection)
        public DateTime? LastCloseUtc { get; set; }    // kada je poslednji put zatvoreno (za gap detection)
        public bool GapExitExecuted { get; set; }      // da li je gap exit već izvršen (da ne duplira)
    }

}
