#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Denis.TradingEngine.Data.Models;
using Serilog;

namespace Denis.TradingEngine.Data.Repositories
{
    /// <summary>
    /// Unified read repository for trend analysis data.
    /// Sources:
    /// - market_ticks (IBKR)
    /// - crypto_trades (Crypto)
    /// </summary>
    public sealed class TrendMarketDataRepository
    {
        private readonly IDbConnectionFactory _factory;
        private readonly ILogger _log;

        public TrendMarketDataRepository(IDbConnectionFactory factory, ILogger? log = null)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _log = log ?? Log.ForContext<TrendMarketDataRepository>();
        }

        public Task<IReadOnlyList<TrendMarketDataPoint>> GetIbkrMarketTicksByWindowAsync(
            string exchange,
            string symbol,
            TimeSpan window,
            DateTime? asOfUtc = null,
            CancellationToken ct = default)
        {
            if (window <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(window), "Window must be > 0.");

            var endUtc = asOfUtc ?? DateTime.UtcNow;
            var startUtc = endUtc - window;
            return GetIbkrMarketTicksByRangeAsync(exchange, symbol, startUtc, endUtc, ct);
        }

        public async Task<IReadOnlyList<TrendMarketDataPoint>> GetIbkrMarketTicksByRangeAsync(
            string exchange,
            string symbol,
            DateTime fromUtc,
            DateTime toUtc,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(exchange))
                throw new ArgumentException("Exchange is required.", nameof(exchange));
            if (string.IsNullOrWhiteSpace(symbol))
                throw new ArgumentException("Symbol is required.", nameof(symbol));
            if (toUtc <= fromUtc)
                throw new ArgumentException("toUtc must be greater than fromUtc.");

            const string sql = @"
SELECT
    utc       AS Utc,
    exchange  AS Exchange,
    symbol    AS Symbol,
    bid       AS Bid,
    ask       AS Ask,
    bid_size  AS BidSize,
    ask_size  AS AskSize,
    CASE
        WHEN bid IS NOT NULL AND ask IS NOT NULL THEN (bid + ask) / 2
        WHEN bid IS NOT NULL THEN bid
        WHEN ask IS NOT NULL THEN ask
        ELSE NULL
    END       AS Price
FROM market_ticks
WHERE exchange = @Exchange
  AND symbol = @Symbol
  AND utc >= @FromUtc
  AND utc <= @ToUtc
ORDER BY utc ASC;";

            try
            {
                await using var conn = await _factory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
                var rows = await conn.QueryAsync<IbkrTickRow>(sql, new
                {
                    Exchange = exchange.Trim(),
                    Symbol = symbol.Trim(),
                    FromUtc = fromUtc,
                    ToUtc = toUtc
                }).ConfigureAwait(false);

                var result = new List<TrendMarketDataPoint>();
                foreach (var row in rows)
                {
                    result.Add(new TrendMarketDataPoint
                    {
                        Source = TrendMarketDataSource.IbkrMarketTick,
                        Utc = row.Utc,
                        Exchange = row.Exchange,
                        Symbol = row.Symbol,
                        Bid = row.Bid,
                        Ask = row.Ask,
                        BidSize = row.BidSize,
                        AskSize = row.AskSize,
                        Price = row.Price
                    });
                }

                return result;
            }
            catch (Exception ex)
            {
                _log.Warning(ex,
                    "[DB-TREND] Failed to read IBKR market_ticks ex={Ex} sym={Sym} from={From:o} to={To:o}",
                    exchange, symbol, fromUtc, toUtc);
                return Array.Empty<TrendMarketDataPoint>();
            }
        }

        public Task<IReadOnlyList<TrendMarketDataPoint>> GetCryptoTradesByWindowAsync(
            string exchange,
            string symbol,
            TimeSpan window,
            DateTime? asOfUtc = null,
            CancellationToken ct = default)
        {
            if (window <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(window), "Window must be > 0.");

            var endUtc = asOfUtc ?? DateTime.UtcNow;
            var startUtc = endUtc - window;
            return GetCryptoTradesByRangeAsync(exchange, symbol, startUtc, endUtc, ct);
        }

        public async Task<IReadOnlyList<TrendMarketDataPoint>> GetCryptoTradesByRangeAsync(
            string exchange,
            string symbol,
            DateTime fromUtc,
            DateTime toUtc,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(exchange))
                throw new ArgumentException("Exchange is required.", nameof(exchange));
            if (string.IsNullOrWhiteSpace(symbol))
                throw new ArgumentException("Symbol is required.", nameof(symbol));
            if (toUtc <= fromUtc)
                throw new ArgumentException("toUtc must be greater than fromUtc.");

            const string sql = @"
WITH filtered AS (
    SELECT
        id,
        utc,
        exchange,
        symbol,
        price,
        quantity,
        side,
        trade_id
    FROM crypto_trades
    WHERE exchange = @Exchange
      AND symbol = @Symbol
      AND utc >= @FromUtc
      AND utc <= @ToUtc
),
ranked AS (
    SELECT
        utc      AS Utc,
        exchange AS Exchange,
        symbol   AS Symbol,
        price    AS Price,
        quantity AS Quantity,
        side     AS Side,
        trade_id AS TradeId,
        ROW_NUMBER() OVER (
            PARTITION BY trade_id
            ORDER BY utc DESC, id DESC
        ) AS rn
    FROM filtered
)
SELECT
    Utc,
    Exchange,
    Symbol,
    Price,
    Quantity,
    Side,
    TradeId
FROM ranked
WHERE TradeId IS NULL OR rn = 1
ORDER BY Utc ASC;";

            try
            {
                await using var conn = await _factory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
                var rows = await conn.QueryAsync<CryptoTradeRow>(sql, new
                {
                    Exchange = exchange.Trim(),
                    Symbol = symbol.Trim(),
                    FromUtc = fromUtc,
                    ToUtc = toUtc
                }).ConfigureAwait(false);

                var result = new List<TrendMarketDataPoint>();
                foreach (var row in rows)
                {
                    result.Add(new TrendMarketDataPoint
                    {
                        Source = TrendMarketDataSource.CryptoTrade,
                        Utc = row.Utc,
                        Exchange = row.Exchange,
                        Symbol = row.Symbol,
                        Price = row.Price,
                        Quantity = row.Quantity,
                        Side = row.Side,
                        TradeId = row.TradeId
                    });
                }

                return result;
            }
            catch (Exception ex)
            {
                _log.Warning(ex,
                    "[DB-TREND] Failed to read crypto_trades ex={Ex} sym={Sym} from={From:o} to={To:o}",
                    exchange, symbol, fromUtc, toUtc);
                return Array.Empty<TrendMarketDataPoint>();
            }
        }

        private sealed class IbkrTickRow
        {
            public DateTime Utc { get; init; }
            public string Exchange { get; init; } = string.Empty;
            public string Symbol { get; init; } = string.Empty;
            public decimal? Bid { get; init; }
            public decimal? Ask { get; init; }
            public decimal? BidSize { get; init; }
            public decimal? AskSize { get; init; }
            public decimal? Price { get; init; }
        }

        private sealed class CryptoTradeRow
        {
            public DateTime Utc { get; init; }
            public string Exchange { get; init; } = string.Empty;
            public string Symbol { get; init; } = string.Empty;
            public decimal Price { get; init; }
            public decimal Quantity { get; init; }
            public string Side { get; init; } = string.Empty;
            public long? TradeId { get; init; }
        }
    }
}
