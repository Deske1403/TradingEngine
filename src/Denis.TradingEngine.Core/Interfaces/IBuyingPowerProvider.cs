using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Denis.TradingEngine.Core.Interfaces
{
    public interface IBuyingPowerProvider
    {
        Task<decimal> GetBuyingPowerUsdAsync(CancellationToken ct);
    }
}
