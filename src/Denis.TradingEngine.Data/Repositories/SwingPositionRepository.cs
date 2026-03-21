using Dapper;
using Denis.TradingEngine.Core.Swing;
using Serilog;

namespace Denis.TradingEngine.Data.Repositories
{
    public sealed class SwingPositionRepository
    {
        private readonly IDbConnectionFactory _factory;
        private readonly ILogger _log;

        public SwingPositionRepository(IDbConnectionFactory factory, ILogger? log = null)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _log = log ?? Log.ForContext<SwingPositionRepository>();
        }

        public async Task UpsertOpenAsync(SwingPositionSnapshot snap, string? exchange = null, CancellationToken ct = default)
        {
            const string sql = @"
            insert into swing_positions
                (symbol, quantity, entry_price, opened_utc, strategy,
                 correlation_id, planned_holding_days, exit_policy,
                 is_open, closed_utc, exit_reason, auto_exit, exchange)
            values
                (@Symbol, @Quantity, @EntryPrice, @OpenedUtc, @Strategy,
                 @CorrelationId, @PlannedHoldingDays, @ExitPolicy,
                 true, null, null, false, @Exchange)
            on conflict (symbol, exchange) do update
            set quantity             = excluded.quantity,
                entry_price          = excluded.entry_price,
                opened_utc           = excluded.opened_utc,
                strategy             = excluded.strategy,
                correlation_id       = excluded.correlation_id,
                planned_holding_days = excluded.planned_holding_days,
                exit_policy          = excluded.exit_policy,
                is_open              = true,
                closed_utc           = null,
                exit_reason          = null,
                auto_exit            = false,
                exchange             = excluded.exchange;
            ";

            var row = new
            {
                Symbol = snap.Symbol,
                Quantity = snap.Quantity,
                EntryPrice = snap.EntryPrice,
                OpenedUtc = snap.OpenedUtc,
                Strategy = snap.Strategy,
                CorrelationId = snap.CorrelationId,
                PlannedHoldingDays = snap.PlannedHoldingDays,
                ExitPolicy = snap.ExitPolicy.ToString(),
                Exchange = exchange
            };

            await using var conn = await _factory.CreateOpenConnectionAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(sql, row, cancellationToken: ct));

            _log.Information(
                "[DB] swing_positions UPSERT-OPEN {Sym} qty={Qty} entry={Entry:F2} strat={Strat} corr={Corr}",
                snap.Symbol, snap.Quantity, snap.EntryPrice, snap.Strategy, snap.CorrelationId);
        }
        public async Task MarkClosedAsync(
    string symbol,
    string? exchange,
    DateTime closedUtc,
    SwingExitReason? exitReason,
    bool autoExit,
    CancellationToken ct = default)
        {
            const string sql = @"
                update  swing_positions
                set is_open     = false,
                    closed_utc  = @ClosedUtc,
                    exit_reason = @ExitReason,
                    auto_exit   = @AutoExit
                where symbol = @Symbol
                  and exchange = COALESCE(@Exchange, 'SMART')
                  and is_open = true;
                ";

            var row = new
            {
                Symbol = symbol,
                Exchange = exchange,
                ClosedUtc = closedUtc,
                ExitReason = (object?)exitReason?.ToString() ?? DBNull.Value,
                AutoExit = autoExit
            };

            await using var conn = await _factory.CreateOpenConnectionAsync(ct);
            var affected = await conn.ExecuteAsync(new CommandDefinition(sql, row, cancellationToken: ct));

            // Izmena: upozorenje kada UPDATE ne nađe red (npr. exchange Bitfinex vs BITFINEX u bazi)
            if (affected == 0)
                _log.Warning("[DB] swing_positions MARK-CLOSED no row updated: sym={Sym} exchange={Ex} (exchange mora tačno da odgovara redu u bazi)",
                    symbol, exchange ?? "SMART");

            _log.Information("[DB] swing_positions MARK-CLOSED {Sym} auto={Auto} reason={Reason} affected={Affected}",
                symbol, autoExit, exitReason?.ToString() ?? "n/a", affected);
        }

        public async Task UpdateOpenQuantityAsync(
            string symbol,
            string? exchange,
            decimal quantity,
            CancellationToken ct = default)
        {
            const string sql = @"
                update swing_positions
                set quantity = @Quantity
                where symbol = @Symbol
                  and exchange = COALESCE(@Exchange, 'SMART')
                  and is_open = true;
                ";

            var row = new
            {
                Symbol = symbol,
                Exchange = exchange,
                Quantity = quantity
            };

            await using var conn = await _factory.CreateOpenConnectionAsync(ct);
            var affected = await conn.ExecuteAsync(new CommandDefinition(sql, row, cancellationToken: ct));

            if (affected == 0)
            {
                _log.Warning(
                    "[DB] swing_positions UPDATE-OPEN-QTY no row updated: sym={Sym} exchange={Ex} qty={Qty}",
                    symbol, exchange ?? "SMART", quantity);
            }

            _log.Information(
                "[DB] swing_positions UPDATE-OPEN-QTY {Sym} qty={Qty} affected={Affected}",
                symbol, quantity, affected);
        }

        public async Task UpsertOpenExternalAsync(SwingPositionSnapshot snap, string? exchange = null, CancellationToken ct = default)
        {
            // Proveri da li pozicija već postoji (bilo open ili closed)
            var existing = await GetBySymbolAsync(snap.Symbol, exchange, ct);
            var finalEntryPrice = snap.EntryPrice;
            var isOurPosition = false;
            
            // Debug log za existing
            if (existing != null)
            {
                _log.Debug(
                    "[DB] swing_positions UPSERT-OPEN-EXT {Sym} existing found: IsOpen={IsOpen} EntryPrice={EntryPrice} Strategy={Strategy}",
                    snap.Symbol, existing.IsOpen, existing.EntryPrice?.ToString() ?? "null", existing.Strategy ?? "null");
            }
            else
            {
                _log.Warning("[DB] swing_positions UPSERT-OPEN-EXT {Sym} existing NOT found", snap.Symbol);
            }
            
            // Proveri da li je pozicija naša (ne external)
            if (existing != null)
            {
                isOurPosition = existing.Strategy != null && 
                                !existing.Strategy.Equals("External/IBKR", StringComparison.OrdinalIgnoreCase);
            }
            
            // Ako pozicija postoji i ima entry_price, proveri da li je naša
            if (existing != null && existing.EntryPrice.HasValue)
            {
                var existingPrice = existing.EntryPrice.Value;
                var brokerPrice = snap.EntryPrice;
                var priceDiff = Math.Abs(brokerPrice - existingPrice);
                var priceDiffPct = existingPrice > 0m ? (priceDiff / existingPrice) * 100m : 0m;
                
                // Ako je pozicija open i naša, sačuvaj naš entry_price
                // Ako je zatvorena, kreiraj novu poziciju sa broker cenom
                if (isOurPosition && existing.IsOpen)
                {
                    // Ako je naša pozicija i open, UVIJEK sačuvaj naš entry_price (ne override-uj)
                    finalEntryPrice = existingPrice;
                    
                    // Uvek loguj da je PRESERVED (čak i ako je razlika 0)
                    _log.Debug(
                        "[DB] swing_positions UPSERT-OPEN-EXT {Sym} qty={Qty} entry_price PRESERVED (ours={Our:F2} broker={Broker:F2} diff={Diff:F4} {Pct:F2}%) - OUR position (open)",
                        snap.Symbol, snap.Quantity, existingPrice, brokerPrice, priceDiff, priceDiffPct);
                }
                else if (isOurPosition && !existing.IsOpen)
                {
                    // Ako je naša pozicija ali zatvorena, kreiraj novu sa broker cenom
                    _log.Debug(
                        "[DB] swing_positions UPSERT-OPEN-EXT {Sym} qty={Qty} entry_price NEW (old closed had {Old:F2}, broker={Broker:F2}) - OUR position (was closed, reopening with broker price)",
                        snap.Symbol, snap.Quantity, existingPrice, brokerPrice);
                }
                else
                {
                    // Ako je external pozicija, koristi broker price (možda je broker ažurirao)
                    // Uvek loguj da je UPDATED (čak i ako je razlika 0)
                    _log.Debug(
                        "[DB] swing_positions UPSERT-OPEN-EXT {Sym} qty={Qty} entry_price UPDATED (old={Old:F2} broker={Broker:F2} diff={Diff:F4} {Pct:F2}%) - EXTERNAL position",
                        snap.Symbol, snap.Quantity, existingPrice, brokerPrice, priceDiff, priceDiffPct);
                }
            }
            else if (existing == null)
            {
                // Nova pozicija - prvi put se importuje
                _log.Debug(
                    "[DB] swing_positions UPSERT-OPEN-EXT {Sym} qty={Qty} entry_price NEW (broker={Broker:F2}) - no existing position",
                    snap.Symbol, snap.Quantity, snap.EntryPrice);
            }
            else if (existing != null && !existing.EntryPrice.HasValue)
            {
                // Pozicija postoji ali nema entry_price
                // Ako je naša pozicija, možda je zatvorena bez entry_price - kreiraj novu sa broker cenom
                if (isOurPosition)
                {
                    _log.Debug(
                        "[DB] swing_positions UPSERT-OPEN-EXT {Sym} qty={Qty} entry_price NEW (broker={Broker:F2}) - OUR position exists but has no entry_price (reopening with broker price)",
                        snap.Symbol, snap.Quantity, snap.EntryPrice);
                }
                else
                {
                    _log.Debug(
                        "[DB] swing_positions UPSERT-OPEN-EXT {Sym} qty={Qty} entry_price NEW (broker={Broker:F2}) - EXTERNAL position exists but has no entry_price",
                        snap.Symbol, snap.Quantity, snap.EntryPrice);
                }
            }
            
            const string sql = @"
            insert into swing_positions
                (symbol, quantity, entry_price, opened_utc, strategy,
                 correlation_id, planned_holding_days, exit_policy,
                 is_open, closed_utc, exit_reason, auto_exit, exchange)
            values
                (@Symbol, @Quantity, @EntryPrice, @OpenedUtc, @Strategy,
                 @CorrelationId, @PlannedHoldingDays, @ExitPolicy,
                 true, null, null, false, @Exchange)
            on conflict (symbol, exchange) do update
            set quantity             = excluded.quantity,
                -- ✅ Ne override-uj entry_price ako je pozicija već open I naša (ne external)
                -- BITNO: Uvek koristi postojeći entry_price ako je pozicija naša i open
                entry_price          = case 
                    when swing_positions.is_open 
                         and swing_positions.strategy is not null 
                         and swing_positions.strategy != 'External/IBKR' 
                    then swing_positions.entry_price  -- Sačuvaj postojeći (naš) entry_price
                    else excluded.entry_price          -- Koristi novi (broker) entry_price
                end,

                -- ✅ ne resetuj opened_utc/strategy/correlation_id ako je već open
                opened_utc           = case when swing_positions.is_open then swing_positions.opened_utc else excluded.opened_utc end,
                strategy             = case when swing_positions.is_open then swing_positions.strategy else excluded.strategy end,
                correlation_id       = case when swing_positions.is_open then swing_positions.correlation_id else excluded.correlation_id end,

                planned_holding_days = excluded.planned_holding_days,
                exit_policy          = excluded.exit_policy,
                is_open              = true,
                closed_utc           = null,
                exit_reason          = null,
                auto_exit            = false,
                exchange             = excluded.exchange;
            ";

            var row = new
            {
                Symbol = snap.Symbol,
                Quantity = snap.Quantity,
                EntryPrice = finalEntryPrice,
                OpenedUtc = snap.OpenedUtc,
                Strategy = snap.Strategy,
                CorrelationId = snap.CorrelationId,
                PlannedHoldingDays = snap.PlannedHoldingDays,
                ExitPolicy = snap.ExitPolicy.ToString(),
                Exchange = exchange  // External positions koriste SMART
            };

            await using var conn = await _factory.CreateOpenConnectionAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(sql, row, cancellationToken: ct));

            // Finalni log - samo ako nismo već logovali detalje gore
            if (existing == null || !existing.IsOpen || !existing.EntryPrice.HasValue)
            {
                _log.Debug("[DB] swing_positions UPSERT-OPEN-EXT {Sym} qty={Qty} entry={Entry:F2} - committed", snap.Symbol, snap.Quantity, finalEntryPrice);
            }
        }
        public async Task<SwingPositionDbRow?> GetOpenBySymbolAsync(string symbol, string? exchange = null, CancellationToken ct = default)
        {
            const string sql = @"
                select symbol, strategy, is_open, entry_price
                from swing_positions
                where symbol = @Symbol
                  and exchange = COALESCE(@Exchange, 'SMART')
                  and is_open = true
                limit 1;
            ";

            var param = new { Symbol = symbol, Exchange = exchange };

            await using var conn = await _factory.CreateOpenConnectionAsync(ct);
            var row = await conn.QuerySingleOrDefaultAsync<SwingPositionDbRow>(
                new CommandDefinition(sql, param, cancellationToken: ct));

            return row;
        }

        public async Task<SwingPositionDbRow?> GetBySymbolAsync(string symbol, string? exchange = null, CancellationToken ct = default)
        {
            const string sql = @"
                select symbol, strategy, is_open, entry_price
                from swing_positions
                where symbol = @Symbol
                  and exchange = COALESCE(@Exchange, 'SMART')
                limit 1;
            ";

            var param = new { Symbol = symbol, Exchange = exchange };

            await using var conn = await _factory.CreateOpenConnectionAsync(ct);
            var row = await conn.QuerySingleOrDefaultAsync<SwingPositionDbRow>(
                new CommandDefinition(sql, param, cancellationToken: ct));

            return row;
        }



    }



}

 public sealed class SwingPositionDbRow
{
    public string Symbol { get; set; } = string.Empty;
    public string? Strategy { get; set; }
    public bool IsOpen { get; set; }
    public decimal? EntryPrice { get; set; }
}
