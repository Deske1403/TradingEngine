#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Denis.TradingEngine.Data;
using Serilog;

namespace Denis.TradingEngine.Data.Repositories;

/// <summary>
/// Repository za snimanje crypto snapshot podataka u bazu.
/// Snapshot može biti orderbook, ticker, ili trade snapshot.
/// </summary>
public sealed class CryptoSnapshotRepository
{
    private readonly IDbConnectionFactory _factory;
    private readonly ILogger _log;

    public CryptoSnapshotRepository(IDbConnectionFactory factory, ILogger? log = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _log = log ?? Serilog.Log.ForContext<CryptoSnapshotRepository>();
    }

    /// <summary>
    /// Snima snapshot u bazu.
    /// </summary>
    public async Task InsertAsync(
        DateTime utc,
        string exchange,
        string symbol,
        string snapshotType,
        object data,
        object? metadata = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(exchange))
            throw new ArgumentException("Exchange cannot be empty.", nameof(exchange));
        if (string.IsNullOrWhiteSpace(symbol))
            throw new ArgumentException("Symbol cannot be empty.", nameof(symbol));
        if (string.IsNullOrWhiteSpace(snapshotType))
            throw new ArgumentException("SnapshotType cannot be empty.", nameof(snapshotType));
        if (data == null)
            throw new ArgumentNullException(nameof(data));

        var dataJson = JsonSerializer.Serialize(data);
        var metadataJson = metadata != null ? JsonSerializer.Serialize(metadata) : null;

        const string sql = @"
INSERT INTO crypto_snapshots
(utc, exchange, symbol, snapshot_type, data, metadata)
VALUES
(@Utc, @Exchange, @Symbol, @SnapshotType, @Data::jsonb, @Metadata::jsonb);";

        try
        {
            await using var conn = await _factory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
            await conn.ExecuteAsync(sql, new
            {
                Utc = utc,
                Exchange = exchange,
                Symbol = symbol,
                SnapshotType = snapshotType,
                Data = dataJson,
                Metadata = metadataJson
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[DB-CRYPTO-SNAP] insert failed for {Ex}:{Sym} type={Type}", 
                exchange, symbol, snapshotType);
        }
    }

    /// <summary>
    /// Bulk insert za performanse.
    /// Koristi ručno građenje SQL-a sa više VALUES za brže performanse.
    /// </summary>
    public async Task BatchInsertAsync(
        IEnumerable<CryptoSnapshotRecord> records,
        CancellationToken ct = default)
    {
        if (records == null) return;

        var list = records as IList<CryptoSnapshotRecord> ?? new List<CryptoSnapshotRecord>(records);
        if (list.Count == 0) return;

        try
        {
            var sql = new StringBuilder(512 + list.Count * 256);
            var parameters = new Dapper.DynamicParameters();

            sql.Append(@"
INSERT INTO crypto_snapshots
(utc, exchange, symbol, snapshot_type, data, metadata)
VALUES ");

            for (int i = 0; i < list.Count; i++)
            {
                var record = list[i];
                var dataJson = JsonSerializer.Serialize(record.Data);
                var metadataJson = record.Metadata != null ? JsonSerializer.Serialize(record.Metadata) : null;

                sql.Append($"(@Utc{i}, @Exchange{i}, @Symbol{i}, @SnapshotType{i}, @Data{i}::jsonb, @Metadata{i}::jsonb),");

                parameters.Add($"Utc{i}", record.Utc);
                parameters.Add($"Exchange{i}", record.Exchange);
                parameters.Add($"Symbol{i}", record.Symbol);
                parameters.Add($"SnapshotType{i}", record.SnapshotType);
                parameters.Add($"Data{i}", dataJson);
                parameters.Add($"Metadata{i}", metadataJson);
            }

            // Skloni zadnji zarez
            sql.Length--;

            await using var conn = await _factory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
            await conn.ExecuteAsync(sql.ToString(), parameters).ConfigureAwait(false);
            
            _log.Debug("[DB-CRYPTO-SNAP] bulk insert {Count} snapshots", list.Count);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[DB-CRYPTO-SNAP] bulk insert failed (count={Count})", list.Count);
        }
    }
}

/// <summary>
/// Record za batch insertion crypto snapshot-a.
/// </summary>
public sealed record CryptoSnapshotRecord(
    DateTime Utc,
    string Exchange,
    string Symbol,
    string SnapshotType,
    object Data,
    object? Metadata = null
);

