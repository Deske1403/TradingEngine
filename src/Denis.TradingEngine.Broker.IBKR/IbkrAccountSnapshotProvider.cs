#nullable enable
using System.Globalization;
using Denis.TradingEngine.Broker.IBKR.IBKRWrapper;
using Serilog;

namespace Denis.TradingEngine.Broker.IBKR
{
    /// <summary>
    /// Pulluje IBKR Account Summary i vraća najbitnije: NetLiq, TotalCash, AvailableFunds, BuyingPower.
    /// Minimalno: samo čitanje + snapshot (bez reconcile).
    /// </summary>
    public sealed class IbkrAccountSnapshotProvider : IDisposable
    {
        private readonly IbkrDefaultWrapper _w;
        private readonly ILogger _log = Log.ForContext<IbkrAccountSnapshotProvider>();

        private readonly object _sync = new();

        private int _nextReqId = 9000;
        private int? _activeReqId;

        private Snapshot _last = new();
        private TaskCompletionSource<Snapshot>? _tcs;

        public IbkrAccountSnapshotProvider(IbkrDefaultWrapper wrapper)
        {
            _w = wrapper ?? throw new ArgumentNullException(nameof(wrapper));

            _w.AccountSummaryArrived += OnAccountSummary;
            _w.AccountSummaryEnd += OnAccountSummaryEnd;
        }

        public async Task<Snapshot?> GetOnceAsync(TimeSpan timeout, CancellationToken ct)
        {
            TaskCompletionSource<Snapshot> tcs;
            int reqId;

            lock (_sync)
            {
                if (_activeReqId is not null)
                    return null; // već u toku

                reqId = Interlocked.Increment(ref _nextReqId);
                _activeReqId = reqId;

                _last = new Snapshot { TimestampUtc = DateTime.UtcNow };

                tcs = new TaskCompletionSource<Snapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
                _tcs = tcs;
            }

            try
            {
                var client = _w.ClientSocket;
                if (client is null)
                {
                    _log.Warning("[IB-ACCT] wrapper.ClientSocket is null");
                    return null;
                }

                // Tagovi koje IBKR tipično vraća:
                // NetLiquidation, TotalCashValue, AvailableFunds, BuyingPower
                client.reqAccountSummary(
                         reqId,
                         group: "All",
                         tags: "NetLiquidation,TotalCashValue,SettledCash,AvailableFunds,BuyingPower,EquityWithLoanValue"); 

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);

                return await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _log.Warning("[IB-ACCT] snapshot timeout");
                return null;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[IB-ACCT] snapshot failed");
                return null;
            }
            finally
            {
                lock (_sync)
                {
                    if (_activeReqId == reqId)
                    {
                        _activeReqId = null;
                        _tcs = null;
                    }
                }

                try
                {
                    _w.ClientSocket?.cancelAccountSummary(reqId);
                }
                catch
                {
                    // ignore
                }
            }
        }

        private void OnAccountSummary(int reqId, string account, string tag, string value, string currency)
        {
            _log.Information("[IB-ACCT-RAW] req={Req} tag={Tag} val={Val} cur={Cur}", reqId, tag, value, currency);

            lock (_sync)
            {
                if (_activeReqId != reqId) return;

                var next = _last;

                if (next.Account is null)
                    next = next with { Account = account };

                var cur = (currency ?? string.Empty).Trim();

                if (!TryDec(value, out var dec))
                    return;

                var isUsd = string.Equals(cur, "USD", StringComparison.OrdinalIgnoreCase);

                // “base currency” uzimamo kao prvu valutu koja nije USD (kod tebe EUR)
                var baseCur = next.BaseCurrency;
                if (baseCur is null && !isUsd && !string.IsNullOrWhiteSpace(cur))
                {
                    baseCur = cur;
                    next = next with { BaseCurrency = baseCur };
                }

                // Upisujemo:
                // - USD polja kad currency == USD
                // - Base polja kad currency == BaseCurrency
                bool isBase = !isUsd && baseCur is not null && string.Equals(cur, baseCur, StringComparison.OrdinalIgnoreCase);

                switch (tag)
                {
                    case "NetLiquidation":
                        if (isUsd) next = next with { NetLiquidationUsd = dec };
                        else if (isBase) next = next with { NetLiquidationBase = dec };
                        break;

                    case "TotalCashValue":
                        if (isUsd) next = next with { TotalCashValueUsd = dec };
                        else if (isBase) next = next with { TotalCashValueBase = dec };
                        break;

                    case "AvailableFunds":
                        if (isUsd) next = next with { AvailableFundsUsd = dec };
                        else if (isBase) next = next with { AvailableFundsBase = dec };
                        break;

                    case "BuyingPower":
                        if (isUsd) next = next with { BuyingPowerUsd = dec };
                        else if (isBase) next = next with { BuyingPowerBase = dec };
                        break;

                    case "SettledCash":
                        if (isUsd) next = next with { SettledCashUsd = dec };
                        else if (isBase) next = next with { SettledCashBase = dec };
                        break;

                    case "EquityWithLoanValue":
                        if (isUsd) next = next with { EquityWithLoanUsd = dec };
                        else if (isBase) next = next with { EquityWithLoanBase = dec };
                        break;
                }

                _last = next;
            }
        }
        private void OnAccountSummaryEnd(int reqId)
        {
            TaskCompletionSource<Snapshot>? tcs;
            Snapshot snap;

            lock (_sync)
            {
                if (_activeReqId != reqId) return;

                snap = _last with { TimestampUtc = DateTime.UtcNow };
                tcs = _tcs;

                _activeReqId = null;
                _tcs = null;
            }

            tcs?.TrySetResult(snap);
        }

        private static bool TryDec(string s, out decimal v) =>
            decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out v);

        public void Dispose()
        {
            _w.AccountSummaryArrived -= OnAccountSummary;
            _w.AccountSummaryEnd -= OnAccountSummaryEnd;
        }

        public sealed record Snapshot
        {
            public string? Account { get; init; }

            public string? BaseCurrency { get; init; }            // npr. EUR
            public decimal? NetLiquidationBase { get; init; }
            public decimal? TotalCashValueBase { get; init; }
            public decimal? AvailableFundsBase { get; init; }
            public decimal? BuyingPowerBase { get; init; }

            public decimal? NetLiquidationUsd { get; init; }
            public decimal? TotalCashValueUsd { get; init; }
            public decimal? AvailableFundsUsd { get; init; }
            public decimal? BuyingPowerUsd { get; init; }
            public DateTime TimestampUtc { get; init; }
            public decimal? SettledCashBase { get; init; }
            public decimal? SettledCashUsd { get; init; }
            public decimal? EquityWithLoanBase { get; init; }
            public decimal? EquityWithLoanUsd { get; init; }
        }
    }
}