#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Serilog;

namespace Denis.TradingEngine.Data.Repositories;

/// <summary>
/// Repository za upis trade tikova u crypto_trades (batch). Broj tikova u prozoru za odluku ide iz in-memory providera.
/// </summary>
public sealed class CryptoTradesRepository
{
    private readonly IDbConnectionFactory _factory;
    private readonly ILogger _log;

    public CryptoTradesRepository(IDbConnectionFactory factory, ILogger? log = null)
    {
        _factory = factory ?? throw new System.ArgumentNullException(nameof(factory));
        _log = log ?? Log.ForContext<CryptoTradesRepository>();
    }

    /// <summary>
    /// Batch insert trade-ova. Poziva se iz batch writer-a (ne na svaki trade).
    /// </summary>
    public async Task InsertBatchAsync(IReadOnlyList<CryptoTradeRecord> records, CancellationToken ct = default)
    {
        if (records == null || records.Count == 0)
            return;

        try
        {
            const string sql = @"
INSERT INTO crypto_trades (utc, exchange, symbol, price, quantity, side, trade_id)
SELECT t.utc, t.exchange, t.symbol, t.price, t.quantity, t.side, t.trade_id
FROM unnest(
    @UtcArr::timestamptz[],
    @ExchangeArr::text[],
    @SymbolArr::text[],
    @PriceArr::numeric[],
    @QuantityArr::numeric[],
    @SideArr::text[],
    @TradeIdArr::bigint[]
) AS t(utc, exchange, symbol, price, quantity, side, trade_id)
ON CONFLICT DO NOTHING";
            var utcArr = new List<System.DateTime>(records.Count);
            var exchangeArr = new List<string>(records.Count);
            var symbolArr = new List<string>(records.Count);
            var priceArr = new List<decimal>(records.Count);
            var quantityArr = new List<decimal>(records.Count);
            var sideArr = new List<string>(records.Count);
            var tradeIdArr = new List<long?>(records.Count);

            foreach (var r in records)
            {
                utcArr.Add(r.Utc);
                exchangeArr.Add(r.Exchange);
                symbolArr.Add(r.Symbol);
                priceArr.Add(r.Price);
                quantityArr.Add(r.Quantity);
                sideArr.Add(r.Side);
                tradeIdArr.Add(r.TradeId);
            }

            await using var conn = await _factory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
            await conn.ExecuteAsync(sql, new
            {
                UtcArr = utcArr.ToArray(),
                ExchangeArr = exchangeArr.ToArray(),
                SymbolArr = symbolArr.ToArray(),
                PriceArr = priceArr.ToArray(),
                QuantityArr = quantityArr.ToArray(),
                SideArr = sideArr.ToArray(),
                TradeIdArr = tradeIdArr.ToArray()
            }).ConfigureAwait(false);

            _log.Debug("[DB-CRYPTO-TRADES] batch insert {Count} trades", records.Count);
        }
        catch (System.Exception ex)
        {
            _log.Warning(ex, "[DB-CRYPTO-TRADES] batch insert failed count={Count}", records.Count);
        }
    }

    public async Task<IReadOnlyList<CryptoTradeBucketRow>> GetRecentBucketsAsync(
        string exchange,
        DateTime sinceUtc,
        int barMinutes,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(exchange))
            return System.Array.Empty<CryptoTradeBucketRow>();

        if (barMinutes <= 0)
            barMinutes = 15;

        const string sql = @"
SELECT
    symbol AS ""Symbol"",
    date_bin((@BarMinutes || ' minutes')::interval, utc, TIMESTAMPTZ '2000-01-01') AS ""BucketUtc"",
    COUNT(*)::int AS ""TradeCount"",
    COALESCE(SUM(CASE WHEN side = 'buy' THEN quantity ELSE 0 END), 0) AS ""BuyQty"",
    COALESCE(SUM(CASE WHEN side = 'sell' THEN quantity ELSE 0 END), 0) AS ""SellQty""
FROM crypto_trades
WHERE exchange = @Exchange
  AND utc >= @SinceUtc
GROUP BY symbol, date_bin((@BarMinutes || ' minutes')::interval, utc, TIMESTAMPTZ '2000-01-01')
ORDER BY ""Symbol"", ""BucketUtc"";";

        try
        {
            await using var conn = await _factory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
            var rows = await conn.QueryAsync<CryptoTradeBucketRow>(sql, new
            {
                Exchange = exchange,
                SinceUtc = sinceUtc,
                BarMinutes = barMinutes
            }).ConfigureAwait(false);
            return rows.AsList();
        }
        catch (System.Exception ex)
        {
            _log.Warning(ex, "[DB-CRYPTO-TRADES] failed loading recent buckets exchange={Exchange}", exchange);
            return System.Array.Empty<CryptoTradeBucketRow>();
        }
    }
}

/// <summary>
/// Jedan red za insert u crypto_trades.
/// </summary>
public sealed record CryptoTradeRecord(
    System.DateTime Utc,
    string Exchange,
    string Symbol,
    decimal Price,
    decimal Quantity,
    string Side,
    long? TradeId = null
);

public sealed record CryptoTradeBucketRow(
    string Symbol,
    System.DateTime BucketUtc,
    int TradeCount,
    decimal BuyQty,
    decimal SellQty);
