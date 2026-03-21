using Dapper;
using Serilog;

namespace Denis.TradingEngine.Data.Repositories;

public sealed class BrokerOrderRepository
{
    private readonly IDbConnectionFactory _factory;
    private readonly ILogger _log;

    public BrokerOrderRepository(IDbConnectionFactory factory, ILogger? log = null)
    {
        _factory = factory;
        _log = log ?? Log.ForContext<BrokerOrderRepository>();
    }

    private static string NormSide(string side)
        => string.IsNullOrWhiteSpace(side) ? side : side.Trim().ToLowerInvariant();

    private static string NormOrderType(string orderType)
    {
        if (string.IsNullOrWhiteSpace(orderType)) return "limit";
        var s = orderType.Trim().ToLowerInvariant();
        s = s.Replace(' ', '_').Replace('-', '_');
        while (s.Contains("__", StringComparison.Ordinal)) s = s.Replace("__", "_", StringComparison.Ordinal);

        if (s is "stp") return "stop";
        if (s is "mkt") return "market";
        return s;
    }

    private static string NormStatus(string status, bool forCrypto = false)
    {
        if (string.IsNullOrWhiteSpace(status)) return status;
        var s = status.Trim().ToLowerInvariant();

        // IMPORTANT (reference): broker_orders.status CHECK in db.sql uses DASH forms
        // for these statuses ("cancel-requested", "place-timeout", ...).
        // Preserve canonical DASH values here to avoid CHECK constraint failures when
        // repository normalization runs before DB writes.
        if (s is "cancel-requested" or "cancel_requested")
            return "cancel-requested";
        if (s is "place-timeout" or "place_timeout")
            return "place-timeout";
        if (s is "place-error" or "place_error")
            return "place-error";
        if (s is "place-rolled-back" or "place_rolled_back")
            return "place-rolled-back";

        if (forCrypto)
        {
            // Reserved for future crypto-specific status normalization rules.
        }

        // Za ostale statuse (IBKR i default), zameni '-' i ' ' sa '_'
        s = s.Replace(' ', '_').Replace('-', '_');
        while (s.Contains("__", StringComparison.Ordinal)) s = s.Replace("__", "_", StringComparison.Ordinal);
        return s;
    }

    private static bool IsDowngradeFromFilled(string? currentStatus, string nextStatus)
        => string.Equals(currentStatus, "filled", StringComparison.OrdinalIgnoreCase)
           && string.Equals(nextStatus, "partially_filled", StringComparison.OrdinalIgnoreCase);

    public async Task InsertSubmittedAsync(
        string id,
        string symbol,
        string side,
        decimal qty,
        string orderType,
        decimal? limitPrice,
        decimal? stopPrice,
        DateTime createdUtc,
        decimal? submitBid,
        decimal? submitAsk,
        decimal? submitSpread,
        string? exchange = null,
        CancellationToken ct = default)
    {
        const string sql = @"
INSERT INTO broker_orders
(id, broker_order_id, symbol, side, qty, order_type, limit_price, stop_price, status,
 created_utc, sent_utc, filled_utc, canceled_utc, expired_utc, last_msg,
 submit_bid, submit_ask, submit_spread, exchange)
VALUES
(@id, NULL, @symbol, @side, @qty, @order_type, @limit_price, @stop_price, 'submitted',
 @created_utc, NULL, NULL, NULL, NULL, NULL,
 @submit_bid, @submit_ask, @submit_spread, @exchange)
ON CONFLICT (id) DO NOTHING;";

        var ot = NormOrderType(orderType);

        await using var conn = await _factory.CreateOpenConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(sql, new
        {
            id,
            symbol,
            side = NormSide(side),
            qty,
            order_type = ot,
            limit_price = limitPrice,
            stop_price = stopPrice,
            created_utc = createdUtc,
            submit_bid = submitBid,
            submit_ask = submitAsk,
            submit_spread = submitSpread,
            exchange = exchange
        });

        _log.Information(
            "[DB] broker_orders submitted id={Id} sym={Sym} side={Side} qty={Qty} type={Type} lim={Lim} stop={Stop} affected={Aff}",
            id, symbol, NormSide(side), qty, ot, limitPrice, stopPrice, affected);

        if (affected == 0)
            _log.Warning("[DB] broker_orders INSERT ignored by ON CONFLICT (id exists) id={Id}", id);
    }

    public async Task MarkSentAsync(string id, string brokerOrderId, DateTime sentUtc, CancellationToken ct = default)
    {
        const string sql = @"
UPDATE broker_orders
SET broker_order_id = @BrokerOrderId,
    status = 'sent',
    sent_utc = @SentUtc
WHERE id = @Id;";

        await using var conn = await _factory.CreateOpenConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(sql, new { Id = id, BrokerOrderId = brokerOrderId, SentUtc = sentUtc });

        if (affected == 0)
            _log.Warning("[DB] broker_orders sent UPDATE affected=0 (missing row?) id={Id} brokerId={Bid}", id, brokerOrderId);
        else
            _log.Information("[DB] broker_orders sent {Id} brokerId={Bid}", id, brokerOrderId);
    }

    public async Task UpdateStatusAsync(string id, string status, string? lastMsg = null, bool forCrypto = false, CancellationToken ct = default)
    {
        var st = NormStatus(status, forCrypto);
        var nowUtc = DateTime.UtcNow;

        const string sql = @"
UPDATE broker_orders
SET status = @Status,
    last_msg = COALESCE(@LastMsg, last_msg),

    filled_utc = CASE
        WHEN @Status = 'filled' AND filled_utc IS NULL THEN @NowUtc
        ELSE filled_utc
    END,

    canceled_utc = CASE
        WHEN @Status = 'canceled' AND canceled_utc IS NULL THEN @NowUtc
        ELSE canceled_utc
    END,

    expired_utc = CASE
        WHEN @Status = 'expired' AND expired_utc IS NULL THEN @NowUtc
        ELSE expired_utc
    END
WHERE id = @Id;";

        await using var conn = await _factory.CreateOpenConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(sql, new { Id = id, Status = st, LastMsg = lastMsg, NowUtc = nowUtc });

        if (affected == 0)
            _log.Warning("[DB] broker_orders status UPDATE affected=0 id={Id} -> {Status}", id, st);
        else
            _log.Information("[DB] broker_orders status {Id} -> {Status}", id, st);
    }

    public async Task UpdateStatusAsyncCryptoGuardFilledAsync(string id, string status, string? lastMsg = null, CancellationToken ct = default)
    {
        // 2026-02-20: crypto-only guard for Bitfinex restart/reconcile flow.
        // Reason: avoid filled -> partially_filled downgrade caused by tiny
        // quantity precision deltas from REST trade-hist sync.
        var st = NormStatus(status, forCrypto: true);
        var nowUtc = DateTime.UtcNow;
        await using var conn = await _factory.CreateOpenConnectionAsync(ct);

        if (string.Equals(st, "partially_filled", StringComparison.OrdinalIgnoreCase))
        {
            const string statusSql = @"SELECT status FROM broker_orders WHERE id = @Id LIMIT 1;";
            var current = await conn.QuerySingleOrDefaultAsync<string?>(statusSql, new { Id = id });
            if (IsDowngradeFromFilled(current, st))
            {
                _log.Information("[DB] broker_orders status ignored (filled is terminal) id={Id} -> {Status}", id, st);
                return;
            }
        }

        const string sql = @"
UPDATE broker_orders
SET status = @Status,
    last_msg = COALESCE(@LastMsg, last_msg),

    filled_utc = CASE
        WHEN @Status = 'filled' AND filled_utc IS NULL THEN @NowUtc
        ELSE filled_utc
    END,

    canceled_utc = CASE
        WHEN @Status = 'canceled' AND canceled_utc IS NULL THEN @NowUtc
        ELSE canceled_utc
    END,

    expired_utc = CASE
        WHEN @Status = 'expired' AND expired_utc IS NULL THEN @NowUtc
        ELSE expired_utc
    END
WHERE id = @Id;";

        var affected = await conn.ExecuteAsync(sql, new { Id = id, Status = st, LastMsg = lastMsg, NowUtc = nowUtc });

        if (affected == 0)
            _log.Warning("[DB] broker_orders status UPDATE affected=0 id={Id} -> {Status}", id, st);
        else
            _log.Information("[DB] broker_orders status {Id} -> {Status}", id, st);
    }

    public async Task MarkCanceledAsync(string id, DateTime canceledUtc, string? reason = null, CancellationToken ct = default)
    {
        const string sql = @"
UPDATE broker_orders
SET status = 'canceled',
    canceled_utc = @CanceledUtc,
    last_msg = COALESCE(@Reason, last_msg)
WHERE id = @Id;";

        await using var conn = await _factory.CreateOpenConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(sql, new { Id = id, CanceledUtc = canceledUtc, Reason = reason });

        if (affected == 0)
            _log.Warning("[DB] broker_orders canceled UPDATE affected=0 id={Id}", id);
        else
            _log.Information("[DB] broker_orders canceled {Id} reason={Reason}", id, reason);
    }

    public async Task MarkExpiredAsync(string id, DateTime expiredUtc, CancellationToken ct = default)
    {
        const string sql = @"
UPDATE broker_orders
SET status = 'expired',
    expired_utc = @ExpiredUtc
WHERE id = @Id;";

        await using var conn = await _factory.CreateOpenConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(sql, new { Id = id, ExpiredUtc = expiredUtc });

        if (affected == 0)
            _log.Warning("[DB] broker_orders expired UPDATE affected=0 id={Id}", id);
        else
            _log.Information("[DB] broker_orders expired {Id}", id);
    }

    public async Task<IReadOnlyList<OpenBrokerOrder>> GetOpenOrdersAsync(CancellationToken ct = default)
    {
        const string sql = @"
SELECT
    id              AS Id,
    broker_order_id AS BrokerOrderId,
    symbol          AS Symbol,
    side            AS Side,
    qty             AS Qty,
    order_type      AS OrderType,
    limit_price     AS LimitPrice,
    stop_price      AS StopPrice,
    created_utc     AS CreatedUtc,
    exchange        AS Exchange
FROM broker_orders
WHERE broker_order_id IS NOT NULL
  AND status IN ('sent','partially_filled','cancel_requested');";

        await using var conn = await _factory.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<OpenBrokerOrder>(sql);
        return rows.AsList();
    }

    /// <summary>
    /// Vraća recoverable ordere za konkretan exchange.
    /// Uključuje i local "submitted" redove (bez broker_order_id), da se mapiranje dovrši posle restarta.
    /// </summary>
    public async Task<IReadOnlyList<OpenBrokerOrder>> GetRecoverableOrdersForExchangeAsync(string exchange, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(exchange))
            throw new ArgumentException("Exchange is required.", nameof(exchange));

        const string sql = @"
SELECT
    id              AS Id,
    broker_order_id AS BrokerOrderId,
    symbol          AS Symbol,
    side            AS Side,
    qty             AS Qty,
    order_type      AS OrderType,
    limit_price     AS LimitPrice,
    stop_price      AS StopPrice,
    created_utc     AS CreatedUtc,
    exchange        AS Exchange
FROM broker_orders
WHERE exchange = @Exchange
  AND status IN (
      'submitted',
      'sent',
      'partially_filled',
      'cancel-requested',
      'cancel_requested',
      'active'
  )
ORDER BY created_utc ASC;";

        await using var conn = await _factory.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<OpenBrokerOrder>(sql, new { Exchange = exchange.Trim() });
        return rows.AsList();
    }

    /// <summary>
    /// Vraća exit ordere sa statusom 'filled' koji su stvoreni u poslednjih 24h.
    /// Ovo omogućava da se execution details obrađuju i za ordere koji su već filled,
    /// ali još uvek čekaju execution details ili commission events.
    /// </summary>
    public async Task<IReadOnlyList<OpenBrokerOrder>> GetRecentFilledExitOrdersAsync(CancellationToken ct = default)
    {
        const string sql = @"
SELECT
    id              AS Id,
    broker_order_id AS BrokerOrderId,
    symbol          AS Symbol,
    side            AS Side,
    qty             AS Qty,
    order_type      AS OrderType,
    limit_price     AS LimitPrice,
    stop_price      AS StopPrice,
    created_utc     AS CreatedUtc,
    exchange        AS Exchange
FROM broker_orders
WHERE broker_order_id IS NOT NULL
  AND status = 'filled'
  AND id LIKE 'exit-%'
  AND created_utc >= NOW() - INTERVAL '24 hours';";

        await using var conn = await _factory.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<OpenBrokerOrder>(sql);
        return rows.AsList();
    }
    
    /// <summary>
    /// Vraća sve ordere (ne samo open) koji su poslati u poslednjih N sati.
    /// Koristi se za reconciliation i order status tracking.
    /// </summary>
    public async Task<IReadOnlyList<OpenBrokerOrder>> GetRecentOrdersAsync(int hoursBack = 24, CancellationToken ct = default)
    {
        const string sql = @"
SELECT
    id              AS Id,
    broker_order_id AS BrokerOrderId,
    symbol          AS Symbol,
    side            AS Side,
    qty             AS Qty,
    order_type      AS OrderType,
    limit_price     AS LimitPrice,
    stop_price      AS StopPrice,
    created_utc     AS CreatedUtc,
    exchange        AS Exchange
FROM broker_orders
WHERE broker_order_id IS NOT NULL
  AND created_utc >= NOW() - make_interval(hours => @Hours)
  AND status IN ('sent','partially_filled','filled','active','cancel_requested');";

        await using var conn = await _factory.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<OpenBrokerOrder>(sql, new { Hours = hoursBack });
        return rows.AsList();
    }

    /// <summary>
    /// Bitfinex reconciliation helper: vraća recent ordere sa statusom.
    /// Ovo je izolovan path i ne utiče na shared recovery tok.
    /// </summary>
    public async Task<IReadOnlyList<RecentBrokerOrderWithStatus>> GetRecentOrdersWithStatusForExchangeAsync(
        string exchange,
        int hoursBack = 24,
        CancellationToken ct = default)
    {
        const string sql = @"
SELECT
    id              AS Id,
    broker_order_id AS BrokerOrderId,
    symbol          AS Symbol,
    side            AS Side,
    status          AS Status,
    qty             AS Qty,
    order_type      AS OrderType,
    limit_price     AS LimitPrice,
    stop_price      AS StopPrice,
    created_utc     AS CreatedUtc,
    exchange        AS Exchange
FROM broker_orders
WHERE broker_order_id IS NOT NULL
  AND exchange = @Exchange
  AND created_utc >= NOW() - make_interval(hours => @Hours)
  AND status IN ('sent','partially_filled','filled','active','cancel_requested');";

        await using var conn = await _factory.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<RecentBrokerOrderWithStatus>(sql, new { Exchange = exchange, Hours = hoursBack });
        return rows.AsList();
    }
    
    
    /// <summary>
    /// Pronalazi submitted order bez broker_order_id koji odgovara (symbol, side, order_type).
    /// <param name="relaxQtyMatch">Ako true (samo Bitfinex OCO), qty se ne traži tačno – bira se red sa količinom najbližom exchange vrednosti. Default false = tačno qty.</param>
    /// </summary>
    public async Task<string?> FindSubmittedWithoutBrokerIdAsync(string exchange, string symbol, string side, decimal qty, string orderType, bool relaxQtyMatch = false, CancellationToken ct = default)
    {
        var sid = NormSide(side);
        var ot = NormOrderType(orderType);
        string sql;
        if (relaxQtyMatch)
        {
            sql = @"
SELECT id FROM broker_orders
WHERE exchange = @Exchange AND symbol = @Symbol AND side = @Side AND order_type = @OrderType
  AND (broker_order_id IS NULL OR broker_order_id = '')
  AND status = 'submitted'
ORDER BY ABS(qty - @Qty) ASC, created_utc DESC
LIMIT 1;";
        }
        else
        {
            sql = @"
SELECT id FROM broker_orders
WHERE exchange = @Exchange AND symbol = @Symbol AND side = @Side AND qty = @Qty AND order_type = @OrderType
  AND (broker_order_id IS NULL OR broker_order_id = '')
  AND status = 'submitted'
ORDER BY created_utc DESC
LIMIT 1;";
        }
        await using var conn = await _factory.CreateOpenConnectionAsync(ct);
        var id = await conn.QuerySingleOrDefaultAsync<string>(sql, new { Exchange = exchange, Symbol = symbol, Side = sid, Qty = qty, OrderType = ot });
        return id;
    }

    /// <summary>
    /// Pronalazi order u bazi po brokerOrderId.
    /// </summary>
    public async Task<OpenBrokerOrder?> GetByBrokerOrderIdAsync(string brokerOrderId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(brokerOrderId))
            return null;

        const string sql = @"
SELECT
    id              AS Id,
    broker_order_id AS BrokerOrderId,
    symbol          AS Symbol,
    side            AS Side,
    qty             AS Qty,
    order_type      AS OrderType,
    limit_price     AS LimitPrice,
    stop_price      AS StopPrice,
    created_utc     AS CreatedUtc,
    exchange        AS Exchange
FROM broker_orders
WHERE broker_order_id = @BrokerOrderId
LIMIT 1;";

        await using var conn = await _factory.CreateOpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<OpenBrokerOrder>(sql, new { BrokerOrderId = brokerOrderId });
        return row;
    }
    
    
    /// <summary>
    /// Ažurira status order-a direktno po brokerOrderId (bez potrebe da prvo tražimo order u bazi).
    /// </summary>
    public async Task UpdateStatusByBrokerOrderIdAsync(string brokerOrderId, string status, string? lastMsg = null, bool forCrypto = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(brokerOrderId))
            return;

        var st = NormStatus(status, forCrypto);
        var nowUtc = DateTime.UtcNow;

        const string sql = @"
UPDATE broker_orders
SET status = @Status,
    last_msg = COALESCE(@LastMsg, last_msg),

    filled_utc = CASE
        WHEN @Status = 'filled' AND filled_utc IS NULL THEN @NowUtc
        ELSE filled_utc
    END,

    canceled_utc = CASE
        WHEN @Status = 'canceled' AND canceled_utc IS NULL THEN @NowUtc
        ELSE canceled_utc
    END,

    expired_utc = CASE
        WHEN @Status = 'expired' AND expired_utc IS NULL THEN @NowUtc
        ELSE expired_utc
    END
WHERE broker_order_id = @BrokerOrderId;";

        await using var conn = await _factory.CreateOpenConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(sql, new { BrokerOrderId = brokerOrderId, Status = st, LastMsg = lastMsg, NowUtc = nowUtc });

        if (affected == 0)
            _log.Debug("[DB] broker_orders status UPDATE by brokerOrderId affected=0 brokerOrderId={BrokerOrderId} -> {Status}", brokerOrderId, st);
        else
            _log.Information("[DB] broker_orders status by brokerOrderId {BrokerOrderId} -> {Status}", brokerOrderId, st);
    }

    public async Task UpdateStatusByBrokerOrderIdAsyncCryptoGuardFilledAsync(string brokerOrderId, string status, string? lastMsg = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(brokerOrderId))
            return;

        // 2026-02-20: same guard as UpdateStatusAsyncCryptoGuardFilledAsync,
        // but for broker_order_id update path used by crypto reconciliation.
        var st = NormStatus(status, forCrypto: true);
        var nowUtc = DateTime.UtcNow;
        await using var conn = await _factory.CreateOpenConnectionAsync(ct);

        if (string.Equals(st, "partially_filled", StringComparison.OrdinalIgnoreCase))
        {
            const string statusSql = @"SELECT status FROM broker_orders WHERE broker_order_id = @BrokerOrderId LIMIT 1;";
            var current = await conn.QuerySingleOrDefaultAsync<string?>(statusSql, new { BrokerOrderId = brokerOrderId });
            if (IsDowngradeFromFilled(current, st))
            {
                _log.Information("[DB] broker_orders status by brokerOrderId ignored (filled is terminal) brokerOrderId={BrokerOrderId} -> {Status}", brokerOrderId, st);
                return;
            }
        }

        const string sql = @"
UPDATE broker_orders
SET status = @Status,
    last_msg = COALESCE(@LastMsg, last_msg),

    filled_utc = CASE
        WHEN @Status = 'filled' AND filled_utc IS NULL THEN @NowUtc
        ELSE filled_utc
    END,

    canceled_utc = CASE
        WHEN @Status = 'canceled' AND canceled_utc IS NULL THEN @NowUtc
        ELSE canceled_utc
    END,

    expired_utc = CASE
        WHEN @Status = 'expired' AND expired_utc IS NULL THEN @NowUtc
        ELSE expired_utc
    END
WHERE broker_order_id = @BrokerOrderId;";

        var affected = await conn.ExecuteAsync(sql, new { BrokerOrderId = brokerOrderId, Status = st, LastMsg = lastMsg, NowUtc = nowUtc });

        if (affected == 0)
            _log.Debug("[DB] broker_orders status UPDATE by brokerOrderId affected=0 brokerOrderId={BrokerOrderId} -> {Status}", brokerOrderId, st);
        else
            _log.Information("[DB] broker_orders status by brokerOrderId {BrokerOrderId} -> {Status}", brokerOrderId, st);
    }

    /// <summary>
    /// Vraća najveći numerički broker_order_id iz baze.
    /// Ignoriše non-numeričke ID-jeve (npr. GUID-ove za crypto exchange-e).
    /// </summary>
    public async Task<int> GetMaxBrokerOrderIdAsync(CancellationToken ct = default)
    {
        const string sql = @"
            SELECT MAX(CAST(broker_order_id AS INTEGER)) 
            FROM broker_orders 
            WHERE broker_order_id IS NOT NULL 
              AND broker_order_id ~ '^[0-9]+$';";

        await using var conn = await _factory.CreateOpenConnectionAsync(ct);
        var maxId = await conn.QuerySingleOrDefaultAsync<int?>(sql);
        
        // Ako je tabela prazna ili nema numeričkih ID-jeva, vraćamo 0.
        var result = maxId ?? 0;
        _log.Information("[DB] Max broker_order_id found: {MaxId}", result);
        return result;
    }

}

public sealed class OpenBrokerOrder
{
    public string Id { get; init; } = default!;
    public string? BrokerOrderId { get; init; }
    public string Symbol { get; init; } = default!;
    public string Side { get; init; } = default!;
    public decimal Qty { get; init; }

    public string? OrderType { get; init; }
    public decimal? LimitPrice { get; init; }
    public decimal? StopPrice { get; init; }

    public DateTime CreatedUtc { get; init; }
    public string? Exchange { get; init; }
}

public sealed class RecentBrokerOrderWithStatus
{
    public string Id { get; init; } = default!;
    public string? BrokerOrderId { get; init; }
    public string Symbol { get; init; } = default!;
    public string Side { get; init; } = default!;
    public string? Status { get; init; }
    public decimal Qty { get; init; }
    public string? OrderType { get; init; }
    public decimal? LimitPrice { get; init; }
    public decimal? StopPrice { get; init; }
    public DateTime CreatedUtc { get; init; }
    public string? Exchange { get; init; }
}

