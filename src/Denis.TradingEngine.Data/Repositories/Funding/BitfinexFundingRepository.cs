#nullable enable

using System.Text;
using System.Text.Json;
using Dapper;
using Denis.TradingEngine.Data;
using Npgsql;
using Serilog;

namespace Denis.TradingEngine.Data.Repositories.Funding;

public sealed class BitfinexFundingRepository
{
    private readonly IDbConnectionFactory _factory;
    private readonly ILogger _log;

    public BitfinexFundingRepository(IDbConnectionFactory factory, ILogger? log = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _log = log ?? Log.ForContext<BitfinexFundingRepository>();
    }

    public async Task PersistCycleAsync(FundingPersistenceBatch batch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (batch.WalletSnapshots.Count == 0 &&
            batch.MarketSnapshots.Count == 0 &&
            batch.OfferActions.Count == 0 &&
            batch.Offers.Count == 0 &&
            batch.OfferEvents.Count == 0 &&
            batch.RuntimeHealth is null)
        {
            return;
        }

        try
        {
            await using var conn = await _factory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

            await InsertWalletSnapshotsCoreAsync(conn, tx, batch.WalletSnapshots, ct).ConfigureAwait(false);
            await InsertMarketSnapshotsCoreAsync(conn, tx, batch.MarketSnapshots, ct).ConfigureAwait(false);
            await InsertOfferActionsCoreAsync(conn, tx, batch.OfferActions, ct).ConfigureAwait(false);
            await UpsertOffersCoreAsync(conn, tx, batch.Offers, ct).ConfigureAwait(false);
            await InsertOfferEventsCoreAsync(conn, tx, batch.OfferEvents, ct).ConfigureAwait(false);

            if (batch.RuntimeHealth is not null)
            {
                await InsertRuntimeHealthCoreAsync(conn, tx, batch.RuntimeHealth, ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);

            _log.Debug(
                "[DB-FUND] persisted cycle wallet={WalletCount} market={MarketCount} actions={ActionCount} offers={OfferCount} events={EventCount} runtime={HasRuntime}",
                batch.WalletSnapshots.Count,
                batch.MarketSnapshots.Count,
                batch.OfferActions.Count,
                batch.Offers.Count,
                batch.OfferEvents.Count,
                batch.RuntimeHealth is not null);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[DB-FUND] cycle persist failed");
        }
    }

    public async Task PersistOfferEventsAsync(
        IReadOnlyList<FundingOfferEventRecord> offerEvents,
        IReadOnlyList<FundingOfferStateRecord> offerStates,
        CancellationToken ct = default)
    {
        if ((offerEvents?.Count ?? 0) == 0 && (offerStates?.Count ?? 0) == 0)
            return;

        try
        {
            await using var conn = await _factory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

            await UpsertOffersCoreAsync(conn, tx, offerStates ?? Array.Empty<FundingOfferStateRecord>(), ct).ConfigureAwait(false);
            await InsertOfferEventsCoreAsync(conn, tx, offerEvents ?? Array.Empty<FundingOfferEventRecord>(), ct).ConfigureAwait(false);

            await tx.CommitAsync(ct).ConfigureAwait(false);
            _log.Debug("[DB-FUND] persisted offer events events={EventCount} offers={OfferCount}", offerEvents?.Count ?? 0, offerStates?.Count ?? 0);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[DB-FUND] offer event persist failed");
        }
    }

    public async Task InsertWalletSnapshotsAsync(IReadOnlyList<FundingWalletSnapshotRecord> snapshots, CancellationToken ct = default)
    {
        if (snapshots == null || snapshots.Count == 0)
            return;

        try
        {
            await using var conn = await _factory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
            await InsertWalletSnapshotsCoreAsync(conn, transaction: null, snapshots, ct).ConfigureAwait(false);
            _log.Debug("[DB-FUND] inserted wallet snapshots count={Count}", snapshots.Count);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[DB-FUND] wallet snapshot insert failed count={Count}", snapshots.Count);
        }
    }

    private static async Task InsertWalletSnapshotsCoreAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? transaction,
        IReadOnlyList<FundingWalletSnapshotRecord> snapshots,
        CancellationToken ct)
    {
        if (snapshots.Count == 0)
            return;

        var sql = new StringBuilder(256 + snapshots.Count * 180);
        var parameters = new DynamicParameters();

        sql.Append(@"
INSERT INTO funding_wallet_snapshots
(utc, exchange, wallet_type, currency, total, available, reserved, source, metadata)
VALUES ");

        for (int i = 0; i < snapshots.Count; i++)
        {
            var snapshot = snapshots[i];
            sql.Append($"(@Utc{i}, @Exchange{i}, @WalletType{i}, @Currency{i}, @Total{i}, @Available{i}, @Reserved{i}, @Source{i}, @Metadata{i}::jsonb),");
            parameters.Add($"Utc{i}", snapshot.Utc);
            parameters.Add($"Exchange{i}", snapshot.Exchange);
            parameters.Add($"WalletType{i}", snapshot.WalletType);
            parameters.Add($"Currency{i}", snapshot.Currency);
            parameters.Add($"Total{i}", snapshot.Total);
            parameters.Add($"Available{i}", snapshot.Available);
            parameters.Add($"Reserved{i}", snapshot.Reserved);
            parameters.Add($"Source{i}", snapshot.Source);
            parameters.Add($"Metadata{i}", SerializeJson(snapshot.Metadata));
        }

        sql.Length--;

        await conn.ExecuteAsync(new CommandDefinition(sql.ToString(), parameters, transaction, cancellationToken: ct)).ConfigureAwait(false);
    }

    private static async Task InsertMarketSnapshotsCoreAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? transaction,
        IReadOnlyList<FundingMarketSnapshotRecord> snapshots,
        CancellationToken ct)
    {
        if (snapshots.Count == 0)
            return;

        var sql = new StringBuilder(256 + snapshots.Count * 220);
        var parameters = new DynamicParameters();

        sql.Append(@"
INSERT INTO funding_market_snapshots
(utc, exchange, symbol, frr, bid_rate, bid_period_days, bid_size, ask_rate, ask_period_days, ask_size, metadata)
VALUES ");

        for (int i = 0; i < snapshots.Count; i++)
        {
            var snapshot = snapshots[i];
            sql.Append($"(@Utc{i}, @Exchange{i}, @Symbol{i}, @Frr{i}, @BidRate{i}, @BidPeriodDays{i}, @BidSize{i}, @AskRate{i}, @AskPeriodDays{i}, @AskSize{i}, @Metadata{i}::jsonb),");
            parameters.Add($"Utc{i}", snapshot.Utc);
            parameters.Add($"Exchange{i}", snapshot.Exchange);
            parameters.Add($"Symbol{i}", snapshot.Symbol);
            parameters.Add($"Frr{i}", snapshot.Frr);
            parameters.Add($"BidRate{i}", snapshot.BidRate);
            parameters.Add($"BidPeriodDays{i}", snapshot.BidPeriodDays);
            parameters.Add($"BidSize{i}", snapshot.BidSize);
            parameters.Add($"AskRate{i}", snapshot.AskRate);
            parameters.Add($"AskPeriodDays{i}", snapshot.AskPeriodDays);
            parameters.Add($"AskSize{i}", snapshot.AskSize);
            parameters.Add($"Metadata{i}", SerializeJson(snapshot.Metadata));
        }

        sql.Length--;

        await conn.ExecuteAsync(new CommandDefinition(sql.ToString(), parameters, transaction, cancellationToken: ct)).ConfigureAwait(false);
    }

    private static async Task InsertOfferActionsCoreAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? transaction,
        IReadOnlyList<FundingOfferActionRecord> actions,
        CancellationToken ct)
    {
        if (actions.Count == 0)
            return;

        var sql = new StringBuilder(256 + actions.Count * 260);
        var parameters = new DynamicParameters();

        sql.Append(@"
INSERT INTO funding_offer_actions
(utc, exchange, symbol, action, dry_run, is_actionable, currency, wallet_type, available_balance, lendable_balance, amount, rate, period_days, reason, offer_id, correlation_id, metadata)
VALUES ");

        for (int i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            sql.Append($"(@Utc{i}, @Exchange{i}, @Symbol{i}, @Action{i}, @DryRun{i}, @IsActionable{i}, @Currency{i}, @WalletType{i}, @AvailableBalance{i}, @LendableBalance{i}, @Amount{i}, @Rate{i}, @PeriodDays{i}, @Reason{i}, @OfferId{i}, @CorrelationId{i}, @Metadata{i}::jsonb),");
            parameters.Add($"Utc{i}", action.Utc);
            parameters.Add($"Exchange{i}", action.Exchange);
            parameters.Add($"Symbol{i}", action.Symbol);
            parameters.Add($"Action{i}", action.Action);
            parameters.Add($"DryRun{i}", action.DryRun);
            parameters.Add($"IsActionable{i}", action.IsActionable);
            parameters.Add($"Currency{i}", action.Currency);
            parameters.Add($"WalletType{i}", action.WalletType);
            parameters.Add($"AvailableBalance{i}", action.AvailableBalance);
            parameters.Add($"LendableBalance{i}", action.LendableBalance);
            parameters.Add($"Amount{i}", action.Amount);
            parameters.Add($"Rate{i}", action.Rate);
            parameters.Add($"PeriodDays{i}", action.PeriodDays);
            parameters.Add($"Reason{i}", action.Reason);
            parameters.Add($"OfferId{i}", action.OfferId);
            parameters.Add($"CorrelationId{i}", action.CorrelationId);
            parameters.Add($"Metadata{i}", SerializeJson(action.Metadata));
        }

        sql.Length--;

        await conn.ExecuteAsync(new CommandDefinition(sql.ToString(), parameters, transaction, cancellationToken: ct)).ConfigureAwait(false);
    }

    private static async Task UpsertOffersCoreAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? transaction,
        IReadOnlyList<FundingOfferStateRecord> offers,
        CancellationToken ct)
    {
        if (offers.Count == 0)
            return;

        var sql = new StringBuilder(512 + offers.Count * 320);
        var parameters = new DynamicParameters();

        sql.Append(@"
INSERT INTO funding_offers
(exchange, offer_id, symbol, currency, wallet_type, offer_type, status, amount, original_amount, rate, rate_real, period_days, flags, notify, hidden, renew, is_active, created_utc, updated_utc, closed_utc, metadata)
VALUES ");

        for (int i = 0; i < offers.Count; i++)
        {
            var offer = offers[i];
            sql.Append($"(@Exchange{i}, @OfferId{i}, @Symbol{i}, @Currency{i}, @WalletType{i}, @OfferType{i}, @Status{i}, @Amount{i}, @OriginalAmount{i}, @Rate{i}, @RateReal{i}, @PeriodDays{i}, @Flags{i}, @Notify{i}, @Hidden{i}, @Renew{i}, @IsActive{i}, @CreatedUtc{i}, @UpdatedUtc{i}, @ClosedUtc{i}, @Metadata{i}::jsonb),");
            parameters.Add($"Exchange{i}", offer.Exchange);
            parameters.Add($"OfferId{i}", offer.OfferId);
            parameters.Add($"Symbol{i}", offer.Symbol);
            parameters.Add($"Currency{i}", offer.Currency);
            parameters.Add($"WalletType{i}", offer.WalletType);
            parameters.Add($"OfferType{i}", offer.OfferType);
            parameters.Add($"Status{i}", offer.Status);
            parameters.Add($"Amount{i}", offer.Amount);
            parameters.Add($"OriginalAmount{i}", offer.OriginalAmount);
            parameters.Add($"Rate{i}", offer.Rate);
            parameters.Add($"RateReal{i}", offer.RateReal);
            parameters.Add($"PeriodDays{i}", offer.PeriodDays);
            parameters.Add($"Flags{i}", offer.Flags);
            parameters.Add($"Notify{i}", offer.Notify);
            parameters.Add($"Hidden{i}", offer.Hidden);
            parameters.Add($"Renew{i}", offer.Renew);
            parameters.Add($"IsActive{i}", offer.IsActive);
            parameters.Add($"CreatedUtc{i}", offer.CreatedUtc);
            parameters.Add($"UpdatedUtc{i}", offer.UpdatedUtc);
            parameters.Add($"ClosedUtc{i}", offer.ClosedUtc);
            parameters.Add($"Metadata{i}", SerializeJson(offer.Metadata));
        }

        sql.Length--;
        sql.Append(@"
ON CONFLICT (exchange, offer_id) DO UPDATE SET
    symbol = EXCLUDED.symbol,
    currency = EXCLUDED.currency,
    wallet_type = EXCLUDED.wallet_type,
    offer_type = EXCLUDED.offer_type,
    status = EXCLUDED.status,
    amount = EXCLUDED.amount,
    original_amount = EXCLUDED.original_amount,
    rate = EXCLUDED.rate,
    rate_real = EXCLUDED.rate_real,
    period_days = EXCLUDED.period_days,
    flags = EXCLUDED.flags,
    notify = EXCLUDED.notify,
    hidden = EXCLUDED.hidden,
    renew = EXCLUDED.renew,
    is_active = EXCLUDED.is_active,
    created_utc = COALESCE(EXCLUDED.created_utc, funding_offers.created_utc),
    updated_utc = COALESCE(EXCLUDED.updated_utc, funding_offers.updated_utc),
    closed_utc = COALESCE(EXCLUDED.closed_utc, funding_offers.closed_utc),
    metadata = EXCLUDED.metadata;");

        await conn.ExecuteAsync(new CommandDefinition(sql.ToString(), parameters, transaction, cancellationToken: ct)).ConfigureAwait(false);
    }

    private static async Task InsertOfferEventsCoreAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? transaction,
        IReadOnlyList<FundingOfferEventRecord> events,
        CancellationToken ct)
    {
        if (events.Count == 0)
            return;

        var sql = new StringBuilder(256 + events.Count * 260);
        var parameters = new DynamicParameters();

        sql.Append(@"
INSERT INTO funding_offer_events
(utc, exchange, offer_id, symbol, event_type, status, amount, original_amount, rate, rate_real, period_days, message, metadata)
VALUES ");

        for (int i = 0; i < events.Count; i++)
        {
            var item = events[i];
            sql.Append($"(@Utc{i}, @Exchange{i}, @OfferId{i}, @Symbol{i}, @EventType{i}, @Status{i}, @Amount{i}, @OriginalAmount{i}, @Rate{i}, @RateReal{i}, @PeriodDays{i}, @Message{i}, @Metadata{i}::jsonb),");
            parameters.Add($"Utc{i}", item.Utc);
            parameters.Add($"Exchange{i}", item.Exchange);
            parameters.Add($"OfferId{i}", item.OfferId);
            parameters.Add($"Symbol{i}", item.Symbol);
            parameters.Add($"EventType{i}", item.EventType);
            parameters.Add($"Status{i}", item.Status);
            parameters.Add($"Amount{i}", item.Amount);
            parameters.Add($"OriginalAmount{i}", item.OriginalAmount);
            parameters.Add($"Rate{i}", item.Rate);
            parameters.Add($"RateReal{i}", item.RateReal);
            parameters.Add($"PeriodDays{i}", item.PeriodDays);
            parameters.Add($"Message{i}", item.Message);
            parameters.Add($"Metadata{i}", SerializeJson(item.Metadata));
        }

        sql.Length--;

        await conn.ExecuteAsync(new CommandDefinition(sql.ToString(), parameters, transaction, cancellationToken: ct)).ConfigureAwait(false);
    }

    private static async Task InsertRuntimeHealthCoreAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? transaction,
        FundingRuntimeHealthRecord runtimeHealth,
        CancellationToken ct)
    {
        const string sql = @"
INSERT INTO funding_runtime_health
(utc, exchange, ws_connected, ws_last_message_utc, rest_last_sync_utc, error_count, degraded_mode, self_disabled, metadata)
VALUES
(@Utc, @Exchange, @WsConnected, @WsLastMessageUtc, @RestLastSyncUtc, @ErrorCount, @DegradedMode, @SelfDisabled, @Metadata::jsonb);";

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            runtimeHealth.Utc,
            runtimeHealth.Exchange,
            runtimeHealth.WsConnected,
            runtimeHealth.WsLastMessageUtc,
            runtimeHealth.RestLastSyncUtc,
            runtimeHealth.ErrorCount,
            runtimeHealth.DegradedMode,
            runtimeHealth.SelfDisabled,
            Metadata = SerializeJson(runtimeHealth.Metadata)
        }, transaction, cancellationToken: ct)).ConfigureAwait(false);
    }

    private static string? SerializeJson(object? value)
    {
        if (value is null)
            return null;

        return JsonSerializer.Serialize(value);
    }
}

public sealed record FundingPersistenceBatch(
    IReadOnlyList<FundingWalletSnapshotRecord> WalletSnapshots,
    IReadOnlyList<FundingMarketSnapshotRecord> MarketSnapshots,
    IReadOnlyList<FundingOfferActionRecord> OfferActions,
    IReadOnlyList<FundingOfferStateRecord> Offers,
    IReadOnlyList<FundingOfferEventRecord> OfferEvents,
    FundingRuntimeHealthRecord? RuntimeHealth);

public sealed record FundingWalletSnapshotRecord(
    DateTime Utc,
    string Exchange,
    string WalletType,
    string Currency,
    decimal Total,
    decimal Available,
    decimal Reserved,
    string Source,
    object? Metadata = null);

public sealed record FundingMarketSnapshotRecord(
    DateTime Utc,
    string Exchange,
    string Symbol,
    decimal? Frr,
    decimal BidRate,
    int BidPeriodDays,
    decimal BidSize,
    decimal AskRate,
    int AskPeriodDays,
    decimal AskSize,
    object? Metadata = null);

public sealed record FundingOfferActionRecord(
    DateTime Utc,
    string Exchange,
    string Symbol,
    string Action,
    bool DryRun,
    bool IsActionable,
    string? Currency,
    string? WalletType,
    decimal? AvailableBalance,
    decimal? LendableBalance,
    decimal? Amount,
    decimal? Rate,
    int? PeriodDays,
    string? Reason,
    long? OfferId,
    string? CorrelationId,
    object? Metadata = null);

public sealed record FundingOfferStateRecord(
    string Exchange,
    long OfferId,
    string Symbol,
    string? Currency,
    string? WalletType,
    string? OfferType,
    string Status,
    decimal Amount,
    decimal? OriginalAmount,
    decimal Rate,
    decimal? RateReal,
    int PeriodDays,
    int Flags,
    bool Notify,
    bool Hidden,
    bool Renew,
    bool IsActive,
    DateTime? CreatedUtc,
    DateTime? UpdatedUtc,
    DateTime? ClosedUtc,
    object? Metadata = null);

public sealed record FundingOfferEventRecord(
    DateTime Utc,
    string Exchange,
    long OfferId,
    string Symbol,
    string EventType,
    string? Status,
    decimal? Amount,
    decimal? OriginalAmount,
    decimal? Rate,
    decimal? RateReal,
    int? PeriodDays,
    string? Message,
    object? Metadata = null);

public sealed record FundingRuntimeHealthRecord(
    DateTime Utc,
    string Exchange,
    bool WsConnected,
    DateTime? WsLastMessageUtc,
    DateTime? RestLastSyncUtc,
    int ErrorCount,
    bool DegradedMode,
    bool SelfDisabled,
    object? Metadata = null);
