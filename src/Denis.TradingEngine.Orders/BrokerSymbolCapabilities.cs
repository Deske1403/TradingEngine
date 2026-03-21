using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

    namespace Denis.TradingEngine.Orders
    {
        public sealed class BrokerSymbolCapabilities
        {
            public string Symbol { get; init; } = string.Empty;
            public bool SupportsFractional { get; init; }
            public decimal MinQty { get; init; } = 1m;
            public decimal StepSize { get; set; } = 1m;
        }
    }

