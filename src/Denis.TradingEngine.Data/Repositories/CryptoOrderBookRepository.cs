#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Denis.TradingEngine.Data;
using Denis.TradingEngine.Core.Crypto;
using Serilog;

namespace Denis.TradingEngine.Data.Repositories;

/// <summary>
/// Repository za snimanje crypto orderbook podataka u bazu.
/// </summary>
public sealed class CryptoOrderBookRepository
{
    private readonly IDbConnectionFactory _factory;
    private readonly ILogger _log;

    public CryptoOrderBookRepository(IDbConnectionFactory factory, ILogger? log = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _log = log ?? Serilog.Log.ForContext<CryptoOrderBookRepository>();
    }

    /// <summary>
    /// Snima orderbook update u bazu.
    /// </summary>
    public async Task InsertAsync(
        OrderBookUpdate update,
        CancellationToken ct = default)
    {
        if (update == null) throw new ArgumentNullException(nameof(update));

        var exchange = update.Symbol.ExchangeId.ToString();
        var symbol = update.Symbol.PublicSymbol;
        var bids = SerializeLevels(update.Bids);
        var asks = SerializeLevels(update.Asks);
        
        var bidCount = update.Bids.Count;
        var askCount = update.Asks.Count;
        
        // Izračunaj spread i mid_price
        decimal? spread = null;
        decimal? midPrice = null;
        
        if (update.Bids.Count > 0 && update.Asks.Count > 0)
        {
            var bestBid = update.Bids[0].Price;
            var bestAsk = update.Asks[0].Price;
            spread = bestAsk - bestBid;
            midPrice = (bestBid + bestAsk) / 2m;
        }

        const string sql = @"
INSERT INTO crypto_orderbooks
(utc, exchange, symbol, bids, asks, spread, mid_price, bid_count, ask_count)
VALUES
(@Utc, @Exchange, @Symbol, @Bids::jsonb, @Asks::jsonb, @Spread, @MidPrice, @BidCount, @AskCount);";

        try
        {
            await using var conn = await _factory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
            await conn.ExecuteAsync(sql, new
            {
                Utc = update.TimestampUtc,
                Exchange = exchange,
                Symbol = symbol,
                Bids = bids,
                Asks = asks,
                Spread = spread,
                MidPrice = midPrice,
                BidCount = bidCount,
                AskCount = askCount
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[DB-CRYPTO-OB] insert failed for {Ex}:{Sym}", exchange, symbol);
        }
    }

    /// <summary>
    /// Bulk insert za performanse (kada imaš više orderbook update-a).
    /// Koristi ručno građenje SQL-a sa više VALUES za brže performanse.
    /// </summary>
    public async Task BatchInsertAsync(
        IEnumerable<OrderBookUpdate> updates,
        CancellationToken ct = default)
    {
        if (updates == null) return;

        var list = updates as IList<OrderBookUpdate> ?? new List<OrderBookUpdate>(updates);
        if (list.Count == 0) return;

        try
        {
            var sql = new System.Text.StringBuilder(512 + list.Count * 256);
            var parameters = new Dapper.DynamicParameters();

            sql.Append(@"
INSERT INTO crypto_orderbooks
(utc, exchange, symbol, bids, asks, spread, mid_price, bid_count, ask_count)
VALUES ");

            for (int i = 0; i < list.Count; i++)
            {
                var update = list[i];
                var exchange = update.Symbol.ExchangeId.ToString();
                var symbol = update.Symbol.PublicSymbol;
                var bids = SerializeLevels(update.Bids);
                var asks = SerializeLevels(update.Asks);
                
                var bidCount = update.Bids.Count;
                var askCount = update.Asks.Count;
                
                decimal? spread = null;
                decimal? midPrice = null;
                
                if (update.Bids.Count > 0 && update.Asks.Count > 0)
                {
                    var bestBid = update.Bids[0].Price;
                    var bestAsk = update.Asks[0].Price;
                    spread = bestAsk - bestBid;
                    midPrice = (bestBid + bestAsk) / 2m;
                }

                sql.Append($"(@Utc{i}, @Exchange{i}, @Symbol{i}, @Bids{i}::jsonb, @Asks{i}::jsonb, @Spread{i}, @MidPrice{i}, @BidCount{i}, @AskCount{i}),");

                parameters.Add($"Utc{i}", update.TimestampUtc);
                parameters.Add($"Exchange{i}", exchange);
                parameters.Add($"Symbol{i}", symbol);
                parameters.Add($"Bids{i}", bids);
                parameters.Add($"Asks{i}", asks);
                parameters.Add($"Spread{i}", spread);
                parameters.Add($"MidPrice{i}", midPrice);
                parameters.Add($"BidCount{i}", bidCount);
                parameters.Add($"AskCount{i}", askCount);
            }

            // Skloni zadnji zarez
            sql.Length--;

            await using var conn = await _factory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
            await conn.ExecuteAsync(sql.ToString(), parameters).ConfigureAwait(false);
            
            _log.Debug("[DB-CRYPTO-OB] bulk insert {Count} orderbooks", list.Count);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[DB-CRYPTO-OB] bulk insert failed (count={Count})", list.Count);
        }
    }

    private static string SerializeLevels(IReadOnlyList<(decimal Price, decimal Quantity)> levels)
    {
        var jsonArray = levels.Select(level => new { price = level.Price, size = level.Quantity });
        return JsonSerializer.Serialize(jsonArray);
    }
}

