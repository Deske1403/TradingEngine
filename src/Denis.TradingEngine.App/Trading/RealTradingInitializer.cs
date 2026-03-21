#nullable enable
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Denis.TradingEngine.Broker.IBKR;
using Denis.TradingEngine.Broker.IBKR.IBKRWrapper;
using Denis.TradingEngine.Core.Interfaces;
using Denis.TradingEngine.Data;
using Denis.TradingEngine.Data.Repositories;
using Denis.TradingEngine.Logging;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace Denis.TradingEngine.App.Trading
{
    public sealed class RealTradingInitResult
    {
        public decimal StartingCashUsd { get; init; }
        public IExternalPositionsProvider ExternalPositions { get; init; }

        public RealTradingInitResult(decimal startingCashUsd, IExternalPositionsProvider externalPositions)
        {
            StartingCashUsd = startingCashUsd;
            ExternalPositions = externalPositions;
        }
    }

    public static class RealTradingInitializer
    {
        private static readonly ILogger Log = AppLog.ForContext<RealTradingInitResult>();

        public static async Task<RealTradingInitResult> InitializeAsync(
           IConfiguration cfg,
           IbkrDefaultWrapper wrapper,
           RealIbkrClient realIbClient,
           IbkrFxRateProvider fx,
           decimal perSymbolBudgetUsd,
           IDbConnectionFactory? dbFactory = null,
           CancellationToken ct = default)
        {
            var accountId = cfg["Ibkr:AccountId"] ?? "U21507612";
            decimal startingCashUsd = 0m;

            try
            {
                using var acctSnap = new IbkrAccountSnapshotProvider(wrapper);
                var snap = await acctSnap.GetOnceAsync(TimeSpan.FromSeconds(5), ct);

                if (snap is not null)
                {
                    Log.Information(
                        "[IB-ACCT] acc={Acc} base={BaseCur} " +
                        "NetLiqBase={NetLiqB:F2} EwlBase={EwlB:F2} CashBase={CashB:F2} SettledBase={SetB:F2} AvailBase={AvailB:F2} BPBase={BPB:F2} | " +
                        "NetLiqUsd={NetLiqU:F2} EwlUsd={EwlU:F2} CashUsd={CashU:F2} SettledUsd={SetU:F2} AvailUsd={AvailU:F2} BPUsd={BPU:F2}",
                        snap.Account ?? "n/a",
                        snap.BaseCurrency ?? "n/a",

                        snap.NetLiquidationBase ?? 0m,
                        snap.EquityWithLoanBase ?? 0m,
                        snap.TotalCashValueBase ?? 0m,
                        snap.SettledCashBase ?? 0m,
                        snap.AvailableFundsBase ?? 0m,
                        snap.BuyingPowerBase ?? 0m,

                        snap.NetLiquidationUsd ?? 0m,
                        snap.EquityWithLoanUsd ?? 0m,
                        snap.TotalCashValueUsd ?? 0m,
                        snap.SettledCashUsd ?? 0m,
                        snap.AvailableFundsUsd ?? 0m,
                        snap.BuyingPowerUsd ?? 0m
                    );

                    static decimal PickSafe(decimal? settled, decimal? avail, out string source)
                    {
                        var s = settled.GetValueOrDefault();
                        var a = avail.GetValueOrDefault();

                        if (s > 0m && a > 0m)
                        {
                            source = "min(SettledCash,AvailableFunds)";
                            return Math.Min(s, a);
                        }

                        if (s > 0m)
                        {
                            source = "SettledCash";
                            return s;
                        }

                        if (a > 0m)
                        {
                            source = "AvailableFunds";
                            return a;
                        }

                        source = "none";
                        return 0m;
                    }

                    // 1) USD direktno, ako IB vraća
                    {
                        var picked = PickSafe(snap.SettledCashUsd, snap.AvailableFundsUsd, out var src);
                        if (picked > 0m)
                        {
                            startingCashUsd = Math.Round(picked, 2);
                            Log.Information("[IB-CASH] using {Src}Usd => {Cash:F2} USD", src, startingCashUsd);
                        }
                    }

                    // 2) Base (npr EUR) konvertuj u USD, ali opet SAFE (min settled/avail)
                    if (startingCashUsd <= 0m)
                    {
                        var baseCur = (snap.BaseCurrency ?? string.Empty).Trim();
                        var settledBase = snap.SettledCashBase;
                        var availBase = snap.AvailableFundsBase;

                        var pickedBase = PickSafe(settledBase, availBase, out var srcBase);
                        if (!string.IsNullOrWhiteSpace(baseCur) && pickedBase > 0m)
                        {
                            var rate = await fx.GetFxRateAsync(baseCur, "USD", TimeSpan.FromSeconds(5), ct);
                            if (rate is { } r && r > 0m)
                            {
                                startingCashUsd = Math.Round(pickedBase * r, 2);
                                Log.Information(
                                    "[IB-CASH] using {Src}Base converted {BaseCur}->USD rate={Rate} => {CashUsd} USD",
                                    srcBase,
                                    baseCur,
                                    r.ToString("F6", CultureInfo.InvariantCulture),
                                    startingCashUsd.ToString("F2", CultureInfo.InvariantCulture)
                                );
                            }
                            else
                            {
                                Log.Warning("[IB-CASH][WARN] FX rate not available for {BaseCur}->USD", baseCur);
                            }
                        }
                    }

                    // 3) Fallback (manje poželjno): AvailableFundsUsd pa TotalCashValueUsd
                    // (ako iz nekog razloga SettledCash nije dostupan)
                    if (startingCashUsd <= 0m && snap.AvailableFundsUsd is { } afUsd && afUsd > 0m)
                    {
                        startingCashUsd = Math.Round(afUsd, 2);
                        Log.Information("[IB-CASH] fallback using AvailableFundsUsd={Cash:F2} USD", startingCashUsd);
                    }
                    else if (startingCashUsd <= 0m && snap.TotalCashValueUsd is { } tcUsd && tcUsd > 0m)
                    {
                        startingCashUsd = Math.Round(tcUsd, 2);
                        Log.Warning("[IB-CASH] fallback using TotalCashValueUsd={Cash:F2} USD", startingCashUsd);
                    }
                }
                else
                {
                    Log.Warning("[IB-CASH][WARN] Account snapshot not available (timeout)");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[IB-CASH][WARN] Snapshot read failed");
            }

            // 4) Poslednji fallback: RealIbkrClient (ovo je AvailableFunds u USD, nije settled)
            if (startingCashUsd <= 0m)
            {
                try
                {
                    var ibCash = await realIbClient.GetAvailableFundsUsdAsync(accountId, ct);
                    Log.Information("[IB-CASH] RealIbkrClient fallback available={Cash} USD",
                        ibCash?.ToString("F2", CultureInfo.InvariantCulture) ?? "n/a");

                    if (ibCash is { } v && v > 0m)
                        startingCashUsd = Math.Round(v, 2);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[IB-CASH][WARN] RealIbkrClient fallback failed");
                }
            }

            if (startingCashUsd <= 0m)
            {
                startingCashUsd = perSymbolBudgetUsd;
                Log.Warning("[IB-CASH] using PerSymbolBudgetUSD fallback as starting cash = {Cash:F2} USD", startingCashUsd);
            }
            else
            {
                Log.Information("[IB-CASH] starting cash resolved (SAFE) = {Cash:F2} USD", startingCashUsd);
            }

            // FIX: Učitaj max broker_order_id iz baze i postavi minimum dozvoljeni ID
            // Osigurava da ne koristimo ID-jeve koji su već korišćeni nakon restarta
            if (dbFactory is not null)
            {
                try
                {
                    var orderRepo = new BrokerOrderRepository(dbFactory, Log);
                    var maxDbId = await orderRepo.GetMaxBrokerOrderIdAsync(ct);
                    
                    if (maxDbId > 0)
                    {
                        // Postavi minimum na maxDbId + 1 da sledeći order bude siguran
                        var minOrderId = maxDbId + 1;
                        realIbClient.SetMinOrderId(minOrderId);
                        Log.Information("[INIT] Found max BrokerOrderId in DB: {MaxId}, set minimum to {MinId}", maxDbId, minOrderId);
                    }
                    else
                    {
                        Log.Information("[INIT] No existing broker_order_id found in DB, using default minimum");
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[INIT][WARN] Failed to load max BrokerOrderId from DB - continuing with default");
                }
            }
            else
            {
                Log.Debug("[INIT] DB factory not provided, skipping max BrokerOrderId check");
            }

            IExternalPositionsProvider extPositions = new IbkrPositionsProvider(wrapper);
            return new RealTradingInitResult(startingCashUsd, extPositions);
        }
    }
}