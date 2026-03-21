using System;
using System.Collections.Generic;
using System.Globalization;
using Denis.TradingEngine.Core.Orders;
using Denis.TradingEngine.Core.Trading;
using IBApi;

namespace Denis.TradingEngine.Broker.IBKR.IBKRWrapper
{
    public class IbkrOrderManager
    {
        private readonly EClientSocket _client;
        private int _nextOrderId;
        public int NextOrderId => _nextOrderId;

        public IbkrOrderManager(EClientSocket client, int startingOrderId = 1)
        {
            _client = client;
            _nextOrderId = startingOrderId;
        }

        private int GetNextOrderId()
        {
            return _nextOrderId++;
        }

        public void UpdateNextOrderId(int id)
        {
            if (id > _nextOrderId)
                _nextOrderId = id;
        }

        public void Buy(string symbol, int quantity, double limitPrice = 0)
        {
            var contract = CreateStockContract(symbol);

            var order = new Order
            {
                Action = "BUY",
                TotalQuantity = quantity,
                OrderType = limitPrice > 0 ? "LMT" : "MKT",
                LmtPrice = limitPrice,
                Tif = "DAY" // može i GTC (Good-Till-Cancelled)
            };

            int orderId = GetNextOrderId();
            _client.placeOrder(orderId, contract, order);

            Console.WriteLine($"[ORDER] BUY {symbol} x{quantity} {(limitPrice > 0 ? $"@ {limitPrice}" : "(MKT)")} (id={orderId})");
        }

        public void Sell(string symbol, int quantity, double limitPrice = 0)
        {
            var contract = CreateStockContract(symbol);

            var order = new Order
            {
                Action = "SELL",
                TotalQuantity = quantity,
                OrderType = limitPrice > 0 ? "LMT" : "MKT",
                LmtPrice = limitPrice,
                Tif = "DAY"
            };

            int orderId = GetNextOrderId();
            _client.placeOrder(orderId, contract, order);

            Console.WriteLine($"[ORDER] SELL {symbol} x{quantity} {(limitPrice > 0 ? $"@ {limitPrice}" : "(MKT)")} (id={orderId})");
        }

        public void Cancel(int orderId)
        {
            // FIX: Pozovi novu verziju metode sa 'null' kao drugim argumentom
            _client.cancelOrder(orderId, null);
            Console.WriteLine($"[ORDER] CANCEL id={orderId}");
        }

        private static Contract CreateStockContract(string symbol)
        {
            return new Contract
            {
                Symbol = symbol,
                SecType = "STK",
                Currency = "USD",
                Exchange = "SMART",
                PrimaryExch = "NASDAQ"
            };
        }
        
        public int PlaceFromCore(OrderRequest req)
        {
            // mapiranje Symbol -> IB Contract (US akcije, SMART/NASDAQ)
            var c = new Contract
            {
                Symbol      = req.Symbol.Ticker,
                SecType     = "STK",
                Currency    = req.Symbol.Currency,   // očekujemo "USD"
                Exchange    = "SMART",
                PrimaryExch = "NASDAQ"
            };

            // mapiranje OrderRequest -> IB Order
            var o = new Order
            {
                Action         = req.Side == OrderSide.Buy ? "BUY" : "SELL",
                TotalQuantity  = req.Quantity,
                Tif            = req.Tif == TimeInForce.Day ? "DAY" : "GTC",
                OrderType      = req.Type == OrderType.Market ? "MKT" : "LMT",
                LmtPrice       = req.Type == OrderType.Limit ? (double)(req.LimitPrice ?? 0m) : 0.0,
                Transmit       = true
            };

            // dodeli i uvećaj _nextOrderId (tvoj postojeći brojač)
            var id = _nextOrderId++;
            _client.placeOrder(id, c, o);

            Console.WriteLine($"[ORDER] {o.Action} {c.Symbol} x{o.TotalQuantity} ({o.OrderType}) id={id} lmt={o.LmtPrice.ToString(CultureInfo.InvariantCulture)} tif={o.Tif}");
            return id;
        }
    }
}
