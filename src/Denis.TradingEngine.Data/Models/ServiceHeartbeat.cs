#nullable enable
using System;

namespace Denis.TradingEngine.Data.Models
{
    public sealed class ServiceHeartbeat
    {
        public string ServiceName { get; init; } = "TradingEngine.App";
        public DateTime StartedAt { get; init; } = DateTime.UtcNow;
        public string? Host { get; init; }
        public string? Note { get; init; }
    }
}