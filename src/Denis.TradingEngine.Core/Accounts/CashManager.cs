#nullable enable
using System;
using System.Threading.Tasks;
using Serilog;

namespace Denis.TradingEngine.Core.Accounts
{
    public sealed class CashManager : IAccountCashService
    {
        private readonly object _sync = new();

        private decimal _free;
        private decimal _reserved;
        private decimal _settling;
        private decimal _inPositions;

        private DateTime _lastRollDateUtc;
        private readonly ILogger _log = Log.ForContext<CashManager>();

        public event Action<CashState>? CashStateChanged;

        public CashManager(decimal initialFreeUsd)
        {
            if (initialFreeUsd < 0m)
                throw new ArgumentOutOfRangeException(nameof(initialFreeUsd));

            _free = Round2(initialFreeUsd);
            _reserved = 0m;
            _settling = 0m;
            _inPositions = 0m;

            _lastRollDateUtc = DateTime.UtcNow.Date;

            Publish();
        }

        public Task<CashState> GetCashStateAsync()
        {
            lock (_sync)
            {
                return Task.FromResult(CaptureSnapshot_NoLock());
            }
        }

        public void Reserve(decimal amountUsd, DateTime utcNow)
        {
            if (amountUsd <= 0m) return;
            amountUsd = Round2(amountUsd);

            DailyRoll(utcNow);

            CashState snap;
            lock (_sync)
            {
                if (_free < amountUsd)
                {
                    throw new InvalidOperationException(
                        $"Insufficient free cash to reserve. Need={amountUsd:F2}, Free={_free:F2}");
                }

                _free = Round2(_free - amountUsd);
                _reserved = Round2(_reserved + amountUsd);

                _log.Information("[CASH] Reserve {Amt:F2} -> Free={Free:F2} Reserved={Res:F2}", amountUsd, _free, _reserved);
                snap = CaptureSnapshot_NoLock();
            }

            Raise(snap);
        }

        public void Unreserve(decimal amountUsd)
        {
            if (amountUsd <= 0m) return;
            amountUsd = Round2(amountUsd);

            CashState snap;
            lock (_sync)
            {
                var delta = Math.Min(_reserved, amountUsd);

                _reserved = Round2(_reserved - delta);
                _free = Round2(_free + delta);

                _log.Information("[CASH] Unreserve {Amt:F2} -> Free={Free:F2} Reserved={Res:F2}", delta, _free, _reserved);
                snap = CaptureSnapshot_NoLock();
            }

            Raise(snap);
        }

        public void OnBuyFilled(decimal costUsd, DateTime utcNow)
        {
            if (costUsd <= 0m) return;
            costUsd = Round2(costUsd);

            DailyRoll(utcNow);

            CashState snap;
            lock (_sync)
            {
                var fromReserved = Math.Min(_reserved, costUsd);
                _reserved = Round2(_reserved - fromReserved);

                var fromFree = costUsd - fromReserved;
                if (fromFree > 0m)
                {
                    if (_free < fromFree)
                    {
                        _log.Error(
                            "[CASH][INCONSISTENT] BUY filled cost={Cost:F2} fromFree={FromFree:F2} but Free={Free:F2}. Clamping Free to 0",
                            costUsd, fromFree, _free);

                        _free = 0m;
                    }
                    else
                    {
                        _free = Round2(_free - fromFree);
                    }
                }

                _inPositions = Round2(_inPositions + costUsd);

                _log.Information("[CASH] BUY filled cost={Cost:F2} -> Free={Free:F2} Reserved={Res:F2} InPos={InPos:F2}",
                    costUsd, _free, _reserved, _inPositions);

                snap = CaptureSnapshot_NoLock();
            }

            Raise(snap);
        }

        public void OnSellProceeds(decimal proceedsUsd, DateTime utcNow)
        {
            if (proceedsUsd <= 0m) return;
            proceedsUsd = Round2(proceedsUsd);

            DailyRoll(utcNow);

            CashState snap;
            lock (_sync)
            {
                _settling = Round2(_settling + proceedsUsd);

                var nextInPos = _inPositions - proceedsUsd;
                _inPositions = Round2(nextInPos > 0m ? nextInPos : 0m);

                _log.Information("[CASH] SELL proceeds +{Proc:F2} -> InPos={InPos:F2} Settling={Set:F2}",
                    proceedsUsd, _inPositions, _settling);

                snap = CaptureSnapshot_NoLock();
            }

            Raise(snap);
        }

        public void MarkSettling(decimal amountUsd)
        {
            if (amountUsd <= 0m) return;
            amountUsd = Round2(amountUsd);

            CashState snap;
            lock (_sync)
            {
                _settling = Round2(_settling + amountUsd);
                _log.Information("[CASH] MarkSettling +{Amt:F2} -> Settling={Set:F2}", amountUsd, _settling);

                snap = CaptureSnapshot_NoLock();
            }

            Raise(snap);
        }

        public void MarkFree(decimal amountUsd)
        {
            if (amountUsd <= 0m) return;
            amountUsd = Round2(amountUsd);

            CashState snap;
            lock (_sync)
            {
                _free = Round2(_free + amountUsd);
                _log.Information("[CASH] MarkFree +{Amt:F2} -> Free={Free:F2}", amountUsd, _free);

                snap = CaptureSnapshot_NoLock();
            }

            Raise(snap);
        }

        public void DailyRoll(DateTime utcNow)
        {
            var today = utcNow.Date;

            CashState snap = default!;
            var changed = false;

            lock (_sync)
            {
                if (today == _lastRollDateUtc)
                    return;

                if (_settling > 0m)
                {
                    _free = Round2(_free + _settling);
                    _log.Information("[CASH] DailyRoll: move Settling->Free amount={Amt:F2} Free={Free:F2}",
                        _settling, _free);
                    _settling = 0m;
                }

                _lastRollDateUtc = today;

                snap = CaptureSnapshot_NoLock();
                changed = true;
            }

            if (changed)
                Raise(snap);
        }

        public void OnCommissionPaid(decimal feeUsd)
        {
            if (feeUsd <= 0m) return;
            feeUsd = Round2(feeUsd);

            CashState snap;
            lock (_sync)
            {
                var freeAfter = _free - feeUsd;
                _free = freeAfter > 0m ? Round2(freeAfter) : 0m;

                _log.Information("[CASH] Fee paid {Fee:F2} -> Free={Free:F2} Reserved={Res:F2}",
                    feeUsd, _free, _reserved);

                snap = CaptureSnapshot_NoLock();
            }

            Raise(snap);
        }

        private static decimal Round2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

        private CashState CaptureSnapshot_NoLock()
        {
            return new CashState(
                Free: Round2(_free),
                Settling: Round2(_settling),
                InPositions: Round2(_inPositions),
                Reserved: Round2(_reserved)
            );
        }

        private void Raise(CashState snapshot)
        {
            CashStateChanged?.Invoke(snapshot);
        }

        private void Publish()
        {
            CashState snap;
            lock (_sync)
            {
                snap = CaptureSnapshot_NoLock();
            }

            Raise(snap);
        }
    }
}
