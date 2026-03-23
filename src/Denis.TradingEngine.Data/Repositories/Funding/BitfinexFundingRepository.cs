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
            batch.ShadowPlans.Count == 0 &&
            batch.ShadowActions.Count == 0 &&
            batch.Credits.Count == 0 &&
            batch.Loans.Count == 0 &&
            batch.Trades.Count == 0 &&
            batch.InterestEntries.Count == 0 &&
            batch.InterestAllocations.Count == 0 &&
            batch.CapitalEvents.Count == 0 &&
            batch.RuntimeHealth is null &&
            batch.ReconciliationLog is null)
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
            await UpsertShadowPlansCoreAsync(conn, tx, batch.ShadowPlans, ct).ConfigureAwait(false);
            await UpsertShadowActionsCoreAsync(conn, tx, batch.ShadowActions, ct).ConfigureAwait(false);
            await UpsertCreditsCoreAsync(conn, tx, batch.Credits, ct).ConfigureAwait(false);
            await UpsertLoansCoreAsync(conn, tx, batch.Loans, ct).ConfigureAwait(false);
            await UpsertTradesCoreAsync(conn, tx, batch.Trades, ct).ConfigureAwait(false);
            await UpsertInterestLedgerCoreAsync(conn, tx, batch.InterestEntries, ct).ConfigureAwait(false);
            await UpsertInterestAllocationsCoreAsync(conn, tx, batch.InterestAllocations, ct).ConfigureAwait(false);
            await UpsertCapitalEventsCoreAsync(conn, tx, batch.CapitalEvents, ct).ConfigureAwait(false);

            if (batch.RuntimeHealth is not null)
            {
                await InsertRuntimeHealthCoreAsync(conn, tx, batch.RuntimeHealth, ct).ConfigureAwait(false);
            }

            if (batch.ReconciliationLog is not null)
            {
                await InsertReconciliationLogCoreAsync(conn, tx, batch.ReconciliationLog, ct).ConfigureAwait(false);
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

    private static async Task UpsertCreditsCoreAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? transaction,
        IReadOnlyList<FundingCreditStateRecord> credits,
        CancellationToken ct)
    {
        if (credits.Count == 0)
            return;

        var sql = new StringBuilder(512 + credits.Count * 260);
        var parameters = new DynamicParameters();

        sql.Append(@"
INSERT INTO funding_credits
(exchange, credit_id, symbol, side, status, amount, original_amount, rate, period_days, created_utc, updated_utc, opened_utc, closed_utc, metadata)
VALUES ");

        for (int i = 0; i < credits.Count; i++)
        {
            var item = credits[i];
            sql.Append($"(@Exchange{i}, @CreditId{i}, @Symbol{i}, @Side{i}, @Status{i}, @Amount{i}, @OriginalAmount{i}, @Rate{i}, @PeriodDays{i}, @CreatedUtc{i}, @UpdatedUtc{i}, @OpenedUtc{i}, @ClosedUtc{i}, @Metadata{i}::jsonb),");
            parameters.Add($"Exchange{i}", item.Exchange);
            parameters.Add($"CreditId{i}", item.CreditId);
            parameters.Add($"Symbol{i}", item.Symbol);
            parameters.Add($"Side{i}", item.Side);
            parameters.Add($"Status{i}", item.Status);
            parameters.Add($"Amount{i}", item.Amount);
            parameters.Add($"OriginalAmount{i}", item.OriginalAmount);
            parameters.Add($"Rate{i}", item.Rate);
            parameters.Add($"PeriodDays{i}", item.PeriodDays);
            parameters.Add($"CreatedUtc{i}", item.CreatedUtc);
            parameters.Add($"UpdatedUtc{i}", item.UpdatedUtc);
            parameters.Add($"OpenedUtc{i}", item.OpenedUtc);
            parameters.Add($"ClosedUtc{i}", item.ClosedUtc);
            parameters.Add($"Metadata{i}", SerializeJson(item.Metadata));
        }

        sql.Length--;
        sql.Append(@"
ON CONFLICT (exchange, credit_id) DO UPDATE SET
    symbol = EXCLUDED.symbol,
    side = EXCLUDED.side,
    status = EXCLUDED.status,
    amount = EXCLUDED.amount,
    original_amount = EXCLUDED.original_amount,
    rate = EXCLUDED.rate,
    period_days = EXCLUDED.period_days,
    created_utc = COALESCE(EXCLUDED.created_utc, funding_credits.created_utc),
    updated_utc = COALESCE(EXCLUDED.updated_utc, funding_credits.updated_utc),
    opened_utc = COALESCE(EXCLUDED.opened_utc, funding_credits.opened_utc),
    closed_utc = COALESCE(EXCLUDED.closed_utc, funding_credits.closed_utc),
    metadata = EXCLUDED.metadata;");

        await conn.ExecuteAsync(new CommandDefinition(sql.ToString(), parameters, transaction, cancellationToken: ct)).ConfigureAwait(false);
    }

    private static async Task UpsertLoansCoreAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? transaction,
        IReadOnlyList<FundingLoanStateRecord> loans,
        CancellationToken ct)
    {
        if (loans.Count == 0)
            return;

        var sql = new StringBuilder(512 + loans.Count * 260);
        var parameters = new DynamicParameters();

        sql.Append(@"
INSERT INTO funding_loans
(exchange, loan_id, symbol, side, status, amount, original_amount, rate, period_days, created_utc, updated_utc, opened_utc, closed_utc, metadata)
VALUES ");

        for (int i = 0; i < loans.Count; i++)
        {
            var item = loans[i];
            sql.Append($"(@Exchange{i}, @LoanId{i}, @Symbol{i}, @Side{i}, @Status{i}, @Amount{i}, @OriginalAmount{i}, @Rate{i}, @PeriodDays{i}, @CreatedUtc{i}, @UpdatedUtc{i}, @OpenedUtc{i}, @ClosedUtc{i}, @Metadata{i}::jsonb),");
            parameters.Add($"Exchange{i}", item.Exchange);
            parameters.Add($"LoanId{i}", item.LoanId);
            parameters.Add($"Symbol{i}", item.Symbol);
            parameters.Add($"Side{i}", item.Side);
            parameters.Add($"Status{i}", item.Status);
            parameters.Add($"Amount{i}", item.Amount);
            parameters.Add($"OriginalAmount{i}", item.OriginalAmount);
            parameters.Add($"Rate{i}", item.Rate);
            parameters.Add($"PeriodDays{i}", item.PeriodDays);
            parameters.Add($"CreatedUtc{i}", item.CreatedUtc);
            parameters.Add($"UpdatedUtc{i}", item.UpdatedUtc);
            parameters.Add($"OpenedUtc{i}", item.OpenedUtc);
            parameters.Add($"ClosedUtc{i}", item.ClosedUtc);
            parameters.Add($"Metadata{i}", SerializeJson(item.Metadata));
        }

        sql.Length--;
        sql.Append(@"
ON CONFLICT (exchange, loan_id) DO UPDATE SET
    symbol = EXCLUDED.symbol,
    side = EXCLUDED.side,
    status = EXCLUDED.status,
    amount = EXCLUDED.amount,
    original_amount = EXCLUDED.original_amount,
    rate = EXCLUDED.rate,
    period_days = EXCLUDED.period_days,
    created_utc = COALESCE(EXCLUDED.created_utc, funding_loans.created_utc),
    updated_utc = COALESCE(EXCLUDED.updated_utc, funding_loans.updated_utc),
    opened_utc = COALESCE(EXCLUDED.opened_utc, funding_loans.opened_utc),
    closed_utc = COALESCE(EXCLUDED.closed_utc, funding_loans.closed_utc),
    metadata = EXCLUDED.metadata;");

        await conn.ExecuteAsync(new CommandDefinition(sql.ToString(), parameters, transaction, cancellationToken: ct)).ConfigureAwait(false);
    }

    private static async Task UpsertTradesCoreAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? transaction,
        IReadOnlyList<FundingTradeRecord> trades,
        CancellationToken ct)
    {
        if (trades.Count == 0)
            return;

        var sql = new StringBuilder(512 + trades.Count * 260);
        var parameters = new DynamicParameters();

        sql.Append(@"
INSERT INTO funding_trades
(utc, exchange, funding_trade_id, symbol, offer_id, credit_id, loan_id, amount, rate, period_days, maker, metadata)
VALUES ");

        for (int i = 0; i < trades.Count; i++)
        {
            var item = trades[i];
            sql.Append($"(@Utc{i}, @Exchange{i}, @FundingTradeId{i}, @Symbol{i}, @OfferId{i}, @CreditId{i}, @LoanId{i}, @Amount{i}, @Rate{i}, @PeriodDays{i}, @Maker{i}, @Metadata{i}::jsonb),");
            parameters.Add($"Utc{i}", item.Utc);
            parameters.Add($"Exchange{i}", item.Exchange);
            parameters.Add($"FundingTradeId{i}", item.FundingTradeId);
            parameters.Add($"Symbol{i}", item.Symbol);
            parameters.Add($"OfferId{i}", item.OfferId);
            parameters.Add($"CreditId{i}", item.CreditId);
            parameters.Add($"LoanId{i}", item.LoanId);
            parameters.Add($"Amount{i}", item.Amount);
            parameters.Add($"Rate{i}", item.Rate);
            parameters.Add($"PeriodDays{i}", item.PeriodDays);
            parameters.Add($"Maker{i}", item.Maker);
            parameters.Add($"Metadata{i}", SerializeJson(item.Metadata));
        }

        sql.Length--;
        sql.Append(@"
ON CONFLICT (exchange, funding_trade_id) DO UPDATE SET
    utc = EXCLUDED.utc,
    symbol = EXCLUDED.symbol,
    offer_id = EXCLUDED.offer_id,
    credit_id = EXCLUDED.credit_id,
    loan_id = EXCLUDED.loan_id,
    amount = EXCLUDED.amount,
    rate = EXCLUDED.rate,
    period_days = EXCLUDED.period_days,
    maker = EXCLUDED.maker,
    metadata = EXCLUDED.metadata;");

        await conn.ExecuteAsync(new CommandDefinition(sql.ToString(), parameters, transaction, cancellationToken: ct)).ConfigureAwait(false);
    }

    private static async Task UpsertInterestLedgerCoreAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? transaction,
        IReadOnlyList<FundingInterestLedgerRecord> entries,
        CancellationToken ct)
    {
        if (entries.Count == 0)
            return;

        var sql = new StringBuilder(512 + entries.Count * 320);
        var parameters = new DynamicParameters();

        sql.Append(@"
INSERT INTO funding_interest_ledger
(utc, exchange, ledger_id, currency, wallet_type, symbol, entry_type, credit_id, loan_id, funding_trade_id, raw_amount, balance_after, gross_interest, fee_amount, net_interest, description, metadata)
VALUES ");

        for (int i = 0; i < entries.Count; i++)
        {
            var item = entries[i];
            sql.Append($"(@Utc{i}, @Exchange{i}, @LedgerId{i}, @Currency{i}, @WalletType{i}, @Symbol{i}, @EntryType{i}, @CreditId{i}, @LoanId{i}, @FundingTradeId{i}, @RawAmount{i}, @BalanceAfter{i}, @GrossInterest{i}, @FeeAmount{i}, @NetInterest{i}, @Description{i}, @Metadata{i}::jsonb),");
            parameters.Add($"Utc{i}", item.Utc);
            parameters.Add($"Exchange{i}", item.Exchange);
            parameters.Add($"LedgerId{i}", item.LedgerId);
            parameters.Add($"Currency{i}", item.Currency);
            parameters.Add($"WalletType{i}", item.WalletType);
            parameters.Add($"Symbol{i}", item.Symbol);
            parameters.Add($"EntryType{i}", item.EntryType);
            parameters.Add($"CreditId{i}", item.CreditId);
            parameters.Add($"LoanId{i}", item.LoanId);
            parameters.Add($"FundingTradeId{i}", item.FundingTradeId);
            parameters.Add($"RawAmount{i}", item.RawAmount);
            parameters.Add($"BalanceAfter{i}", item.BalanceAfter);
            parameters.Add($"GrossInterest{i}", item.GrossInterest);
            parameters.Add($"FeeAmount{i}", item.FeeAmount);
            parameters.Add($"NetInterest{i}", item.NetInterest);
            parameters.Add($"Description{i}", item.Description);
            parameters.Add($"Metadata{i}", SerializeJson(item.Metadata));
        }

        sql.Length--;
        sql.Append(@"
ON CONFLICT (exchange, ledger_id) DO UPDATE SET
    utc = EXCLUDED.utc,
    currency = EXCLUDED.currency,
    wallet_type = EXCLUDED.wallet_type,
    symbol = EXCLUDED.symbol,
    entry_type = EXCLUDED.entry_type,
    credit_id = EXCLUDED.credit_id,
    loan_id = EXCLUDED.loan_id,
    funding_trade_id = EXCLUDED.funding_trade_id,
    raw_amount = EXCLUDED.raw_amount,
    balance_after = EXCLUDED.balance_after,
    gross_interest = EXCLUDED.gross_interest,
    fee_amount = EXCLUDED.fee_amount,
    net_interest = EXCLUDED.net_interest,
    description = EXCLUDED.description,
    metadata = EXCLUDED.metadata;");

        await conn.ExecuteAsync(new CommandDefinition(sql.ToString(), parameters, transaction, cancellationToken: ct)).ConfigureAwait(false);
    }

    private static async Task InsertReconciliationLogCoreAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? transaction,
        FundingReconciliationLogRecord reconciliation,
        CancellationToken ct)
    {
        const string sql = @"
INSERT INTO funding_reconciliation_log
(started_utc, completed_utc, exchange, symbol, mismatch_count, corrected_count, severity, summary, metadata)
VALUES
(@StartedUtc, @CompletedUtc, @Exchange, @Symbol, @MismatchCount, @CorrectedCount, @Severity, @Summary, @Metadata::jsonb);";

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            reconciliation.StartedUtc,
            reconciliation.CompletedUtc,
            reconciliation.Exchange,
            reconciliation.Symbol,
            reconciliation.MismatchCount,
            reconciliation.CorrectedCount,
            reconciliation.Severity,
            reconciliation.Summary,
            Metadata = SerializeJson(reconciliation.Metadata)
        }, transaction, cancellationToken: ct)).ConfigureAwait(false);
    }

    private static async Task UpsertInterestAllocationsCoreAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? transaction,
        IReadOnlyList<FundingInterestAllocationRecord> allocations,
        CancellationToken ct)
    {
        if (allocations.Count == 0)
            return;

        var sql = new StringBuilder(512 + allocations.Count * 360);
        var parameters = new DynamicParameters();

        sql.Append(@"
INSERT INTO funding_interest_allocations
(utc, exchange, allocation_key, ledger_id, currency, symbol, credit_id, loan_id, funding_trade_id, allocated_gross_interest, allocated_fee_amount, allocated_net_interest, allocation_fraction, allocation_method, confidence, metadata)
VALUES ");

        for (int i = 0; i < allocations.Count; i++)
        {
            var item = allocations[i];
            sql.Append($"(@Utc{i}, @Exchange{i}, @AllocationKey{i}, @LedgerId{i}, @Currency{i}, @Symbol{i}, @CreditId{i}, @LoanId{i}, @FundingTradeId{i}, @AllocatedGrossInterest{i}, @AllocatedFeeAmount{i}, @AllocatedNetInterest{i}, @AllocationFraction{i}, @AllocationMethod{i}, @Confidence{i}, @Metadata{i}::jsonb),");
            parameters.Add($"Utc{i}", item.Utc);
            parameters.Add($"Exchange{i}", item.Exchange);
            parameters.Add($"AllocationKey{i}", item.AllocationKey);
            parameters.Add($"LedgerId{i}", item.LedgerId);
            parameters.Add($"Currency{i}", item.Currency);
            parameters.Add($"Symbol{i}", item.Symbol);
            parameters.Add($"CreditId{i}", item.CreditId);
            parameters.Add($"LoanId{i}", item.LoanId);
            parameters.Add($"FundingTradeId{i}", item.FundingTradeId);
            parameters.Add($"AllocatedGrossInterest{i}", item.AllocatedGrossInterest);
            parameters.Add($"AllocatedFeeAmount{i}", item.AllocatedFeeAmount);
            parameters.Add($"AllocatedNetInterest{i}", item.AllocatedNetInterest);
            parameters.Add($"AllocationFraction{i}", item.AllocationFraction);
            parameters.Add($"AllocationMethod{i}", item.AllocationMethod);
            parameters.Add($"Confidence{i}", item.Confidence);
            parameters.Add($"Metadata{i}", SerializeJson(item.Metadata));
        }

        sql.Length--;
        sql.Append(@"
ON CONFLICT (exchange, allocation_key) DO UPDATE SET
    utc = EXCLUDED.utc,
    ledger_id = EXCLUDED.ledger_id,
    currency = EXCLUDED.currency,
    symbol = EXCLUDED.symbol,
    credit_id = EXCLUDED.credit_id,
    loan_id = EXCLUDED.loan_id,
    funding_trade_id = EXCLUDED.funding_trade_id,
    allocated_gross_interest = EXCLUDED.allocated_gross_interest,
    allocated_fee_amount = EXCLUDED.allocated_fee_amount,
    allocated_net_interest = EXCLUDED.allocated_net_interest,
    allocation_fraction = EXCLUDED.allocation_fraction,
    allocation_method = EXCLUDED.allocation_method,
    confidence = EXCLUDED.confidence,
    metadata = EXCLUDED.metadata;");

        await conn.ExecuteAsync(new CommandDefinition(sql.ToString(), parameters, transaction, cancellationToken: ct)).ConfigureAwait(false);
    }

    private static async Task UpsertCapitalEventsCoreAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? transaction,
        IReadOnlyList<FundingCapitalEventRecord> events,
        CancellationToken ct)
    {
        if (events.Count == 0)
            return;

        var sql = new StringBuilder(512 + events.Count * 360);
        var parameters = new DynamicParameters();

        sql.Append(@"
INSERT INTO funding_capital_events
(utc, exchange, event_key, symbol, currency, wallet_type, event_type, credit_id, loan_id, funding_trade_id, amount, source_type, description, metadata)
VALUES ");

        for (int i = 0; i < events.Count; i++)
        {
            var item = events[i];
            sql.Append($"(@Utc{i}, @Exchange{i}, @EventKey{i}, @Symbol{i}, @Currency{i}, @WalletType{i}, @EventType{i}, @CreditId{i}, @LoanId{i}, @FundingTradeId{i}, @Amount{i}, @SourceType{i}, @Description{i}, @Metadata{i}::jsonb),");
            parameters.Add($"Utc{i}", item.Utc);
            parameters.Add($"Exchange{i}", item.Exchange);
            parameters.Add($"EventKey{i}", item.EventKey);
            parameters.Add($"Symbol{i}", item.Symbol);
            parameters.Add($"Currency{i}", item.Currency);
            parameters.Add($"WalletType{i}", item.WalletType);
            parameters.Add($"EventType{i}", item.EventType);
            parameters.Add($"CreditId{i}", item.CreditId);
            parameters.Add($"LoanId{i}", item.LoanId);
            parameters.Add($"FundingTradeId{i}", item.FundingTradeId);
            parameters.Add($"Amount{i}", item.Amount);
            parameters.Add($"SourceType{i}", item.SourceType);
            parameters.Add($"Description{i}", item.Description);
            parameters.Add($"Metadata{i}", SerializeJson(item.Metadata));
        }

        sql.Length--;
        sql.Append(@"
ON CONFLICT (exchange, event_key) DO UPDATE SET
    utc = EXCLUDED.utc,
    symbol = EXCLUDED.symbol,
    currency = EXCLUDED.currency,
    wallet_type = EXCLUDED.wallet_type,
    event_type = EXCLUDED.event_type,
    credit_id = EXCLUDED.credit_id,
    loan_id = EXCLUDED.loan_id,
    funding_trade_id = EXCLUDED.funding_trade_id,
    amount = EXCLUDED.amount,
    source_type = EXCLUDED.source_type,
    description = EXCLUDED.description,
    metadata = EXCLUDED.metadata;");

        await conn.ExecuteAsync(new CommandDefinition(sql.ToString(), parameters, transaction, cancellationToken: ct)).ConfigureAwait(false);
    }

    private static async Task UpsertShadowPlansCoreAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? transaction,
        IReadOnlyList<FundingShadowPlanRecord> plans,
        CancellationToken ct)
    {
        if (plans.Count == 0)
            return;

        var sql = new StringBuilder(512 + plans.Count * 420);
        var parameters = new DynamicParameters();

        sql.Append(@"
INSERT INTO funding_shadow_plans
(utc, exchange, plan_key, symbol, currency, regime, bucket, available_balance, lendable_balance, allocation_amount, allocation_fraction, target_rate, target_period_days, max_wait_minutes, role, fallback_bucket, market_ask_rate, market_bid_rate, summary, metadata)
VALUES ");

        for (int i = 0; i < plans.Count; i++)
        {
            var item = plans[i];
            sql.Append($"(@Utc{i}, @Exchange{i}, @PlanKey{i}, @Symbol{i}, @Currency{i}, @Regime{i}, @Bucket{i}, @AvailableBalance{i}, @LendableBalance{i}, @AllocationAmount{i}, @AllocationFraction{i}, @TargetRate{i}, @TargetPeriodDays{i}, @MaxWaitMinutes{i}, @Role{i}, @FallbackBucket{i}, @MarketAskRate{i}, @MarketBidRate{i}, @Summary{i}, @Metadata{i}::jsonb),");
            parameters.Add($"Utc{i}", item.Utc);
            parameters.Add($"Exchange{i}", item.Exchange);
            parameters.Add($"PlanKey{i}", item.PlanKey);
            parameters.Add($"Symbol{i}", item.Symbol);
            parameters.Add($"Currency{i}", item.Currency);
            parameters.Add($"Regime{i}", item.Regime);
            parameters.Add($"Bucket{i}", item.Bucket);
            parameters.Add($"AvailableBalance{i}", item.AvailableBalance);
            parameters.Add($"LendableBalance{i}", item.LendableBalance);
            parameters.Add($"AllocationAmount{i}", item.AllocationAmount);
            parameters.Add($"AllocationFraction{i}", item.AllocationFraction);
            parameters.Add($"TargetRate{i}", item.TargetRate);
            parameters.Add($"TargetPeriodDays{i}", item.TargetPeriodDays);
            parameters.Add($"MaxWaitMinutes{i}", item.MaxWaitMinutes);
            parameters.Add($"Role{i}", item.Role);
            parameters.Add($"FallbackBucket{i}", item.FallbackBucket);
            parameters.Add($"MarketAskRate{i}", item.MarketAskRate);
            parameters.Add($"MarketBidRate{i}", item.MarketBidRate);
            parameters.Add($"Summary{i}", item.Summary);
            parameters.Add($"Metadata{i}", SerializeJson(item.Metadata));
        }

        sql.Length--;
        sql.Append(@"
ON CONFLICT (exchange, plan_key) DO UPDATE SET
    utc = EXCLUDED.utc,
    symbol = EXCLUDED.symbol,
    currency = EXCLUDED.currency,
    regime = EXCLUDED.regime,
    bucket = EXCLUDED.bucket,
    available_balance = EXCLUDED.available_balance,
    lendable_balance = EXCLUDED.lendable_balance,
    allocation_amount = EXCLUDED.allocation_amount,
    allocation_fraction = EXCLUDED.allocation_fraction,
    target_rate = EXCLUDED.target_rate,
    target_period_days = EXCLUDED.target_period_days,
    max_wait_minutes = EXCLUDED.max_wait_minutes,
    role = EXCLUDED.role,
    fallback_bucket = EXCLUDED.fallback_bucket,
    market_ask_rate = EXCLUDED.market_ask_rate,
    market_bid_rate = EXCLUDED.market_bid_rate,
    summary = EXCLUDED.summary,
    metadata = EXCLUDED.metadata;");

        await conn.ExecuteAsync(new CommandDefinition(sql.ToString(), parameters, transaction, cancellationToken: ct)).ConfigureAwait(false);
    }

    private static async Task UpsertShadowActionsCoreAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? transaction,
        IReadOnlyList<FundingShadowActionRecord> actions,
        CancellationToken ct)
    {
        if (actions.Count == 0)
            return;

        var sql = new StringBuilder(512 + actions.Count * 480);
        var parameters = new DynamicParameters();

        sql.Append(@"
INSERT INTO funding_shadow_actions
(utc, exchange, action_key, plan_key, symbol, currency, regime, bucket, action, is_actionable, available_balance, lendable_balance, allocation_amount, allocation_fraction, target_rate, fallback_rate, target_period_days, max_wait_minutes, decision_deadline_utc, role, fallback_bucket, active_offer_count, active_offer_id, active_offer_rate, active_offer_amount, active_offer_status, reason, summary, metadata)
VALUES ");

        for (int i = 0; i < actions.Count; i++)
        {
            var item = actions[i];
            sql.Append($"(@Utc{i}, @Exchange{i}, @ActionKey{i}, @PlanKey{i}, @Symbol{i}, @Currency{i}, @Regime{i}, @Bucket{i}, @Action{i}, @IsActionable{i}, @AvailableBalance{i}, @LendableBalance{i}, @AllocationAmount{i}, @AllocationFraction{i}, @TargetRate{i}, @FallbackRate{i}, @TargetPeriodDays{i}, @MaxWaitMinutes{i}, @DecisionDeadlineUtc{i}, @Role{i}, @FallbackBucket{i}, @ActiveOfferCount{i}, @ActiveOfferId{i}, @ActiveOfferRate{i}, @ActiveOfferAmount{i}, @ActiveOfferStatus{i}, @Reason{i}, @Summary{i}, @Metadata{i}::jsonb),");
            parameters.Add($"Utc{i}", item.Utc);
            parameters.Add($"Exchange{i}", item.Exchange);
            parameters.Add($"ActionKey{i}", item.ActionKey);
            parameters.Add($"PlanKey{i}", item.PlanKey);
            parameters.Add($"Symbol{i}", item.Symbol);
            parameters.Add($"Currency{i}", item.Currency);
            parameters.Add($"Regime{i}", item.Regime);
            parameters.Add($"Bucket{i}", item.Bucket);
            parameters.Add($"Action{i}", item.Action);
            parameters.Add($"IsActionable{i}", item.IsActionable);
            parameters.Add($"AvailableBalance{i}", item.AvailableBalance);
            parameters.Add($"LendableBalance{i}", item.LendableBalance);
            parameters.Add($"AllocationAmount{i}", item.AllocationAmount);
            parameters.Add($"AllocationFraction{i}", item.AllocationFraction);
            parameters.Add($"TargetRate{i}", item.TargetRate);
            parameters.Add($"FallbackRate{i}", item.FallbackRate);
            parameters.Add($"TargetPeriodDays{i}", item.TargetPeriodDays);
            parameters.Add($"MaxWaitMinutes{i}", item.MaxWaitMinutes);
            parameters.Add($"DecisionDeadlineUtc{i}", item.DecisionDeadlineUtc);
            parameters.Add($"Role{i}", item.Role);
            parameters.Add($"FallbackBucket{i}", item.FallbackBucket);
            parameters.Add($"ActiveOfferCount{i}", item.ActiveOfferCount);
            parameters.Add($"ActiveOfferId{i}", item.ActiveOfferId);
            parameters.Add($"ActiveOfferRate{i}", item.ActiveOfferRate);
            parameters.Add($"ActiveOfferAmount{i}", item.ActiveOfferAmount);
            parameters.Add($"ActiveOfferStatus{i}", item.ActiveOfferStatus);
            parameters.Add($"Reason{i}", item.Reason);
            parameters.Add($"Summary{i}", item.Summary);
            parameters.Add($"Metadata{i}", SerializeJson(item.Metadata));
        }

        sql.Length--;
        sql.Append(@"
ON CONFLICT (exchange, action_key) DO UPDATE SET
    utc = EXCLUDED.utc,
    plan_key = EXCLUDED.plan_key,
    symbol = EXCLUDED.symbol,
    currency = EXCLUDED.currency,
    regime = EXCLUDED.regime,
    bucket = EXCLUDED.bucket,
    action = EXCLUDED.action,
    is_actionable = EXCLUDED.is_actionable,
    available_balance = EXCLUDED.available_balance,
    lendable_balance = EXCLUDED.lendable_balance,
    allocation_amount = EXCLUDED.allocation_amount,
    allocation_fraction = EXCLUDED.allocation_fraction,
    target_rate = EXCLUDED.target_rate,
    fallback_rate = EXCLUDED.fallback_rate,
    target_period_days = EXCLUDED.target_period_days,
    max_wait_minutes = EXCLUDED.max_wait_minutes,
    decision_deadline_utc = EXCLUDED.decision_deadline_utc,
    role = EXCLUDED.role,
    fallback_bucket = EXCLUDED.fallback_bucket,
    active_offer_count = EXCLUDED.active_offer_count,
    active_offer_id = EXCLUDED.active_offer_id,
    active_offer_rate = EXCLUDED.active_offer_rate,
    active_offer_amount = EXCLUDED.active_offer_amount,
    active_offer_status = EXCLUDED.active_offer_status,
    reason = EXCLUDED.reason,
    summary = EXCLUDED.summary,
    metadata = EXCLUDED.metadata;");

        await conn.ExecuteAsync(new CommandDefinition(sql.ToString(), parameters, transaction, cancellationToken: ct)).ConfigureAwait(false);
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
    IReadOnlyList<FundingShadowPlanRecord> ShadowPlans,
    IReadOnlyList<FundingShadowActionRecord> ShadowActions,
    IReadOnlyList<FundingCreditStateRecord> Credits,
    IReadOnlyList<FundingLoanStateRecord> Loans,
    IReadOnlyList<FundingTradeRecord> Trades,
    IReadOnlyList<FundingInterestLedgerRecord> InterestEntries,
    IReadOnlyList<FundingInterestAllocationRecord> InterestAllocations,
    IReadOnlyList<FundingCapitalEventRecord> CapitalEvents,
    FundingRuntimeHealthRecord? RuntimeHealth,
    FundingReconciliationLogRecord? ReconciliationLog);

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

public sealed record FundingShadowPlanRecord(
    DateTime Utc,
    string Exchange,
    string PlanKey,
    string Symbol,
    string Currency,
    string Regime,
    string Bucket,
    decimal AvailableBalance,
    decimal LendableBalance,
    decimal AllocationAmount,
    decimal AllocationFraction,
    decimal? TargetRate,
    int? TargetPeriodDays,
    int? MaxWaitMinutes,
    string? Role,
    string? FallbackBucket,
    decimal? MarketAskRate,
    decimal? MarketBidRate,
    string? Summary,
    object? Metadata = null);

public sealed record FundingShadowActionRecord(
    DateTime Utc,
    string Exchange,
    string ActionKey,
    string PlanKey,
    string Symbol,
    string Currency,
    string Regime,
    string Bucket,
    string Action,
    bool IsActionable,
    decimal AvailableBalance,
    decimal LendableBalance,
    decimal AllocationAmount,
    decimal AllocationFraction,
    decimal? TargetRate,
    decimal? FallbackRate,
    int? TargetPeriodDays,
    int? MaxWaitMinutes,
    DateTime? DecisionDeadlineUtc,
    string? Role,
    string? FallbackBucket,
    int ActiveOfferCount,
    long? ActiveOfferId,
    decimal? ActiveOfferRate,
    decimal? ActiveOfferAmount,
    string? ActiveOfferStatus,
    string Reason,
    string? Summary,
    object? Metadata = null);

public sealed record FundingCreditStateRecord(
    string Exchange,
    long CreditId,
    string Symbol,
    string? Side,
    string Status,
    decimal Amount,
    decimal? OriginalAmount,
    decimal? Rate,
    int? PeriodDays,
    DateTime? CreatedUtc,
    DateTime? UpdatedUtc,
    DateTime? OpenedUtc,
    DateTime? ClosedUtc,
    object? Metadata = null);

public sealed record FundingLoanStateRecord(
    string Exchange,
    long LoanId,
    string Symbol,
    string? Side,
    string Status,
    decimal Amount,
    decimal? OriginalAmount,
    decimal? Rate,
    int? PeriodDays,
    DateTime? CreatedUtc,
    DateTime? UpdatedUtc,
    DateTime? OpenedUtc,
    DateTime? ClosedUtc,
    object? Metadata = null);

public sealed record FundingTradeRecord(
    DateTime Utc,
    string Exchange,
    long FundingTradeId,
    string Symbol,
    long? OfferId,
    long? CreditId,
    long? LoanId,
    decimal Amount,
    decimal? Rate,
    int? PeriodDays,
    bool? Maker,
    object? Metadata = null);

public sealed record FundingInterestLedgerRecord(
    DateTime Utc,
    string Exchange,
    long LedgerId,
    string Currency,
    string WalletType,
    string? Symbol,
    string EntryType,
    long? CreditId,
    long? LoanId,
    long? FundingTradeId,
    decimal RawAmount,
    decimal? BalanceAfter,
    decimal GrossInterest,
    decimal FeeAmount,
    decimal NetInterest,
    string? Description,
    object? Metadata = null);

public sealed record FundingInterestAllocationRecord(
    DateTime Utc,
    string Exchange,
    string AllocationKey,
    long LedgerId,
    string Currency,
    string? Symbol,
    long? CreditId,
    long? LoanId,
    long? FundingTradeId,
    decimal AllocatedGrossInterest,
    decimal AllocatedFeeAmount,
    decimal AllocatedNetInterest,
    decimal AllocationFraction,
    string AllocationMethod,
    string? Confidence,
    object? Metadata = null);

public sealed record FundingCapitalEventRecord(
    DateTime Utc,
    string Exchange,
    string EventKey,
    string? Symbol,
    string? Currency,
    string? WalletType,
    string EventType,
    long? CreditId,
    long? LoanId,
    long? FundingTradeId,
    decimal Amount,
    string SourceType,
    string? Description,
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

public sealed record FundingReconciliationLogRecord(
    DateTime StartedUtc,
    DateTime? CompletedUtc,
    string Exchange,
    string? Symbol,
    int MismatchCount,
    int CorrectedCount,
    string? Severity,
    string? Summary,
    object? Metadata = null);
