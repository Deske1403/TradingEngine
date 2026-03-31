using Denis.TradingEngine.Core.Trading;
using Denis.TradingEngine.Logging;
using IBApi;
using IBApi.protobuf;
using Serilog;
using CommissionAndFeesReport = IBApi.CommissionAndFeesReport;
using Contract = IBApi.Contract;
using Order = IBApi.Order;
using OrderState = IBApi.OrderState;

namespace Denis.TradingEngine.Broker.IBKR.IBKRWrapper
{

    public class IbkrDefaultWrapper : EWrapper
    {


        // Minimalno-robustan wrapper:
        // - Sve metode interfejsa postoje (nema bacanja NotImplementedException)
        // - Glavne metode daju log (nextValidId, currentTime, error, tickPrice/tickSize/tickString, marketDataType, managedAccounts)
        // - Explicitne overload varijante (sa decimal, ProtoBuf itd.) ili prosleđuju na postojeće,
        //   ili su no-op (bez rušenja)

        // === KORISNI LOGOVI / GLAVNE METODE ===

       
        private static readonly ILogger Log = AppLog.ForContext<IbkrDefaultWrapper>();

        public event Action<int, int, double>? TickPriceArrived;   // (reqId, field, price)
        public event Action<int, int, int>? TickSizeArrived;    // (reqId, field, size)
        public event Action<int, int, string>? TickStringArrived;  // (reqId, field, value)
        public event Action? ConnectionClosed;
        public EClientSocket? Client { get; set; }

        public EClientSocket ClientSocket
            => Client ?? throw new InvalidOperationException("ClientSocket is not initialized. Set IbkrDefaultWrapper.Client before use.");

        public Dictionary<int, Contract>? Subscriptions { get; set; }

        public event Action<int>? NextValidIdReceived;
        public event Action<DateTimeOffset>? CurrentTimeReceived;
        public event Action<int, int, string>? ErrorReceived;
        public event Action<MarketQuote> QuoteUpdated;
        public event Action<int, string, string, string, string>? AccountSummaryArrived;
        public event Action<int>? AccountSummaryEnd;

        // na vrhu klase (pored QuoteUpdated eventa)
        public event Action<int, string, int, decimal?, string?>? OrderStatusUpdated;
        public event Action<int, decimal>? CommissionReported;

        private readonly Dictionary<int, (double bid, int bidSz, double ask, int askSz)> _bbo = new();
    
    



        private readonly Dictionary<int, Symbol> _symbolByReqId = new();
        public void nextValidId(int orderId)
        {
            Console.WriteLine($"[OK] nextValidId: {orderId}");
            NextValidIdReceived?.Invoke(orderId);   // <-- mora postojati
        }

        public void currentTime(long time)
        {
            var dto = DateTimeOffset.FromUnixTimeSeconds(time);
            Log.Information($"[SERVER TIME] {dto:O}");
            CurrentTimeReceived?.Invoke(dto);       // <-- mora postojati
        }

        public void managedAccounts(string accountsList) => Console.WriteLine($"[ACCOUNTS] {accountsList}");


        public void connectAck()
        {
            Console.WriteLine("[INFO] connectAck (socket connected)");
            Log.Information("[IBKR] connectAck received");
        }

        // Glavni error (bez timestampa)
        public void error(int id, int errorCode, string errorMsg, string advancedOrderRejectJson)
        {
            if (errorCode is 2104 or 2106 or 2158)
            {
                Console.WriteLine($"[IB-INFO] {errorCode}: {errorMsg}");
                return;
            }

            // isti fallback kao što smo dodali - samo dopuni s 10089
            if ((errorCode == 10168 || errorCode == 10089)
                && Client != null && Subscriptions != null
                && Subscriptions.TryGetValue(id, out var c))
            {
                Console.WriteLine("[INFO] No live MD. Switching to DELAYED (3) + retry");
                Client.reqMarketDataType(3);
                Client.reqMktData(id, c, "", false, false, null);
                return;
            }

            Console.WriteLine($"[IB-ERROR] id={id} code={errorCode} msg={errorMsg}");
            Log.Error("[IB-ERROR] id={Id} code={Code} msg={Msg}", id, errorCode, errorMsg);
            ErrorReceived?.Invoke(id, errorCode, errorMsg);
        }

        // === OSTALO – NE RUŠI, NEGO NO-OP / PROSLEĐIVANJE ===

        // Overload sa vremenom – mapiramo na prethodni
        void EWrapper.error(int id, long errorTime, int errorCode, string errorMsg, string advancedOrderRejectJson)
        {
            error(id, errorCode, errorMsg, advancedOrderRejectJson);
        }
        
        void EWrapper.error(Exception e)
        {
            Console.WriteLine($"[IB-EX] {e.GetType().Name}: {e.Message}");
            Log.Error(e, "[IB-EX] {Type}: {Msg}", e.GetType().Name, e.Message);
        }

        void EWrapper.error(string str)
        {
            Console.WriteLine($"[IB-ERRSTR] {str}");
            Log.Error("[IB-ERRSTR] {Msg}", str);
        }

   
        // tickSize (decimal) → tvoja int-varijanta (za log)
        //ima gresku za small ili large value !!!    prpveri vovo!!!!!!
        //public void OnTickSize(int tickerId, int field, decimal size) { /* tvoj kod */ }
        //void EWrapper.tickSize(int tickerId, int field, decimal size) => OnTickSize(tickerId, field, size);

        // orderStatus (decimal) → double varijanta
        void EWrapper.orderStatus(int orderId, string status, decimal filled, decimal remaining, double avgFillPrice,
                                  long permId, int parentId, double lastFillPrice, int clientId, string whyHeld, double mktCapPrice)
            => orderStatus(orderId, status, (double)filled, (double)remaining, avgFillPrice, (int)permId, parentId, lastFillPrice, clientId, whyHeld, mktCapPrice);

        // realTimeBar (decimal volume/WAP) → double log
        public void realtimeBar(int reqId, long time, double open, double high, double low, double close, long volume, double wap, int count)
            => Console.WriteLine($"[RTBAR] id={reqId} t={time} O={open} H={high} L={low} C={close} V={volume} WAP={wap} n={count}");
        void EWrapper.realtimeBar(int reqId, long date, double open, double high, double low, double close, decimal volume, decimal WAP, int count)
            => realtimeBar(reqId, date, open, high, low, close, (long)volume, (double)WAP, count);

        // currentTimeInMillis → currentTime
        void EWrapper.currentTimeInMillis(long timeInMillis) => currentTime(timeInMillis / 1000);

        // accountDownloadEnd – koristan mali log
        void EWrapper.accountDownloadEnd(string account) => Console.WriteLine($"[ACCOUNT-DL-END] {account}");

#region empty impl

        public void bondContractDetails(int reqId, IBApi.ContractDetails contractDetails) { }
        public void deltaNeutralValidation(int reqId, IBApi.DeltaNeutralContract deltaNeutralContract) { }
        public void displayGroupList(int reqId, string groups) { }
        public void displayGroupUpdated(int reqId, string contractInfo) { }
        public void fundamentalData(int reqId, string data) { }
        public void headTimestamp(int reqId, string headTimestamp) { }
        public void historicalData(int reqId, Bar bar) { }
        public void historicalDataEnd(int reqId, string start, string end) { }
        public void historicalDataUpdate(int reqId, Bar bar) { }
        public void historicalNews(int reqId, string time, string providerCode, string articleId, string headline) { }
        public void historicalNewsEnd(int reqId, bool hasMore) { }
        public void historicalTicks(int reqId, IBApi.HistoricalTick[] ticks, bool done) { }
        public void historicalTicksBidAsk(int reqId, IBApi.HistoricalTickBidAsk[] ticks, bool done) { }
        public void historicalTicksLast(int reqId, IBApi.HistoricalTickLast[] ticks, bool done) { }
        public void marketRule(int marketRuleId, IBApi.PriceIncrement[] priceIncrements) { }
        public void mktDepthExchanges(DepthMktDataDescription[] descs) { }
        public void newsArticle(int requestId, int articleType, string articleText) { }
        public void newsProviders(IBApi.NewsProvider[] newsProviders) { }
        public void pnl(int reqId, double dailyPnL, double unrealizedPnL, double realizedPnL) { }
        public void pnlSingle(int reqId, int pos, double dailyPnL, double unrealizedPnL, double realizedPnL, double value) { }
        public void positionMulti(int requestId, string account, string modelCode, IBApi.Contract contract, double pos, double avgCost) { }
        public void positionMultiEnd(int requestId) { }
        public void receiveFA(int faDataType, string xml) { }
        public void scannerData(int reqId, int rank, IBApi.ContractDetails contractDetails, string distance, string benchmark, string projection, string legsStr) { }
        public void scannerDataEnd(int reqId) { }
        public void scannerParameters(string xml) { }
        public void securityDefinitionOptionParameter(int reqId, string exchange, int underlyingConId, string tradingClass, string multiplier, HashSet<string> expirations, HashSet<double> strikes) { }
        public void securityDefinitionOptionParameterEnd(int reqId) { }
        public void smartComponents(int reqId, Dictionary<int, KeyValuePair<string, char>> theMap) { }
        public void softDollarTiers(int reqId, IBApi.SoftDollarTier[] tiers) { }
        public void tickEFP(int tickerId, int tickType, double basisPoints, string formattedBasisPoints, double impliedFuture, int holdDays, string futureLastTradeDate, double dividendImpact, double dividendsToLastTradeDate) { }
        public void tickGeneric(int tickerId, int field, double value) { }
        public void tickNews(int tickerId, long timeStamp, string providerCode, string articleId, string headline, string extraData) { }
        public void tickOptionComputation(int tickerId, int field, int tickAttrib, double impliedVol, double delta, double optPrice, double pvDividend, double gamma, double vega, double theta, double undPrice) { }
        public void tickSnapshotEnd(int reqId) { }
        public void updateAccountTime(string timeStamp) { }
        public void updateAccountValue(string key, string val, string currency, string accountName) { }
        public void updateNewsBulletin(int msgId, int msgType, string message, string origExchange) { }
        public void updatePortfolio(IBApi.Contract contract, double position, double marketPrice, double marketValue, double averageCost, double unrealizedPNL, double realizedPNL, string accountName) { }
        public void verifyAndAuthCompleted(bool isSuccessful, string errorText) { }
        public void verifyAndAuthMessageAPI(string apiData, string xyzChallenge) { }
        public void verifyMessageAPI(string apiData) { }
        public void wshMetaData(int reqId, string dataJson) { }
        public void wshEventData(int reqId, string dataJson) { }
        public void historicalSchedule(int reqId, string startDateTime, string endDateTime, string timeZone, HistoricalSchedule sessions) { }
        public void userInfo(int reqId, string whiteBrandingId) { }
        #endregion

#region not implented
        void EWrapper.updatePortfolio(IBApi.Contract contract, decimal position, double marketPrice, double marketValue, double averageCost, double unrealizedPNL, double realizedPNL, string accountName)
        {
            throw new NotImplementedException();
        }

        void EWrapper.commissionAndFeesReport(IBApi.CommissionAndFeesReport commissionAndFeesReport)
        {
            try
            {
                // FIX: Emituj CommissionReportArrived event koji RealIbkrClient sluša
                CommissionReportArrived?.Invoke(new CommissionAndFeesReport
                {
                    ExecId = commissionAndFeesReport.ExecId,
                    CommissionAndFees = commissionAndFeesReport.CommissionAndFees,
                    Currency = commissionAndFeesReport.Currency
                });
                
                Console.WriteLine($"[COMMISSION] execId={commissionAndFeesReport.ExecId} fee={commissionAndFeesReport.CommissionAndFees} {commissionAndFeesReport.Currency}");
            }
            catch { /* swallow */ }
        }

        void EWrapper.updateMktDepth(int tickerId, int position, int operation, int side, double price, decimal size)
      => updateMktDepth(tickerId, position, operation, side, price, size);

        void EWrapper.updateMktDepthL2(int tickerId, int position, string marketMaker, int operation, int side, double price, decimal size, bool isSmartDepth)
            => updateMktDepthL2(tickerId, position, marketMaker, operation, side, price, size, isSmartDepth);


        void EWrapper.verifyCompleted(bool isSuccessful, string errorText)
        {
            throw new NotImplementedException();
        }

        void EWrapper.positionMulti(int requestId, string account, string modelCode, IBApi.Contract contract, decimal pos, double avgCost)
        {
            throw new NotImplementedException();
        }

        void EWrapper.accountUpdateMulti(int requestId, string account, string modelCode, string key, string value, string currency)
        {
            throw new NotImplementedException();
        }

        void EWrapper.accountUpdateMultiEnd(int requestId)
        {
            throw new NotImplementedException();
        }

        void EWrapper.familyCodes(IBApi.FamilyCode[] familyCodes)
        {
            throw new NotImplementedException();
        }

        void EWrapper.symbolSamples(int reqId, IBApi.ContractDescription[] contractDescriptions)
        {
            throw new NotImplementedException();
        }

        void EWrapper.tickReqParams(int tickerId, double minTick, string bboExchange, int snapshotPermissions)
        {
            Console.WriteLine($"[TICK-REQ-PARAMS] id={tickerId} minTick={minTick} bboExchange={bboExchange} snapshotPermissions={snapshotPermissions}");
        }

        void EWrapper.histogramData(int reqId, HistogramEntry[] data)
        {
            throw new NotImplementedException();
        }

        void EWrapper.rerouteMktDataReq(int reqId, int conId, string exchange)
        {
            throw new NotImplementedException();
        }

        void EWrapper.rerouteMktDepthReq(int reqId, int conId, string exchange)
        {
            throw new NotImplementedException();
        }

        void EWrapper.pnlSingle(int reqId, decimal pos, double dailyPnL, double unrealizedPnL, double realizedPnL, double value)
        {
            throw new NotImplementedException();
        }

        void EWrapper.tickByTickAllLast(int reqId, int tickType, long time, double price, decimal size, IBApi.TickAttribLast tickAttribLast, string exchange, string specialConditions)
        {
            throw new NotImplementedException();
        }

        void EWrapper.tickByTickBidAsk(int reqId, long time, double bidPrice, double askPrice, decimal bidSize, decimal askSize, IBApi.TickAttribBidAsk tickAttribBidAsk)
        {
            throw new NotImplementedException();
        }

        void EWrapper.orderBound(long permId, int clientId, int orderId)
        {
            try
            {
                Log.Information("[ORDER-BOUND] permId={PermId} clientId={ClientId} orderId={OrderId}",
                    permId, clientId, orderId);
            }
            catch
            {
                // swallow – wrapper must never throw
            }
        }

        void EWrapper.completedOrder(IBApi.Contract contract, IBApi.Order order, IBApi.OrderState orderState)
        {
            try
            {
                Log.Information(
                    "[COMPLETED-ORDER] sym={Sym} action={Action} qty={Qty} type={Type} lmt={Lmt} aux={Aux} ref={Ref} oca={Oca} tif={Tif} outsideRth={OutsideRth} parentId={ParentId} transmit={Transmit} openClose={OpenClose} status={Status}",
                    contract?.Symbol,
                    order?.Action,
                    order?.TotalQuantity,
                    order?.OrderType,
                    order?.LmtPrice,
                    order?.AuxPrice,
                    order?.OrderRef,
                    order?.OcaGroup,
                    order?.Tif,
                    order?.OutsideRth,
                    order?.ParentId,
                    order?.Transmit,
                    order?.OpenClose,
                    orderState?.Status);
            }
            catch
            {
                // swallow – wrapper must never throw
            }
        }

        void EWrapper.completedOrdersEnd()
        {
            try
            {
                Log.Information("[COMPLETED-ORDERS-END]");
            }
            catch
            {
                // swallow – wrapper must never throw
            }
        }

        void EWrapper.replaceFAEnd(int reqId, string text)
        {
            throw new NotImplementedException();
        }

        void EWrapper.historicalSchedule(int reqId, string startDateTime, string endDateTime, string timeZone, IBApi.HistoricalSession[] sessions)
        {
            throw new NotImplementedException();
        }
        #endregion
        
#region protobuf methods
        void EWrapper.orderStatusProtoBuf(OrderStatus orderStatusProto)
        {
            Console.WriteLine($"[ORDER] id={orderStatusProto.OrderId} status={orderStatusProto.Status} filled={orderStatusProto.Filled} rem={orderStatusProto.Remaining} avg={orderStatusProto.AvgFillPrice}");
        }

        void EWrapper.openOrderProtoBuf(OpenOrder openOrderProto)
        {
            Console.WriteLine($"[OPEN-ORDER] id={openOrderProto.OrderId} symbol={openOrderProto.Contract.Symbol} qty={openOrderProto.Order.TotalQuantity} type={openOrderProto.Order.OrderType} price={openOrderProto.Order.LmtPrice}");
        }

        void EWrapper.openOrdersEndProtoBuf(OpenOrdersEnd openOrdersEndProto)
        {
            Console.WriteLine("[OPEN-ORDERS-END]");
        }

        void EWrapper.errorProtoBuf(ErrorMessage errorMessageProto)
        {
            Console.WriteLine($"[IB-ERROR] id={errorMessageProto.Id} code={errorMessageProto.ErrorCode} msg={errorMessageProto.ErrorMsg}");
        }

        void EWrapper.execDetailsProtoBuf(ExecutionDetails executionDetailsProto)
        {
            Console.WriteLine($"[EXEC-DETAILS] id={executionDetailsProto.ReqId} symbol={executionDetailsProto.Contract.Symbol} qty={executionDetailsProto.Execution.Shares} price={executionDetailsProto.Execution.Price}");
        }

        void EWrapper.execDetailsEndProtoBuf(ExecutionDetailsEnd executionDetailsEndProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.completedOrderProtoBuf(CompletedOrder completedOrderProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.completedOrdersEndProtoBuf(CompletedOrdersEnd completedOrdersEndProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.orderBoundProtoBuf(OrderBound orderBoundProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.contractDataProtoBuf(ContractData contractDataProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.bondContractDataProtoBuf(ContractData contractDataProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.contractDataEndProtoBuf(ContractDataEnd contractDataEndProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.tickPriceProtoBuf(TickPrice tickPriceProto)
        {
           // Console.WriteLine($"*************************[tickPrice] id={tickPriceProto.ReqId} Size={tickPriceProto.Size} price={tickPriceProto.Price}");
        }

        void EWrapper.tickSizeProtoBuf(TickSize tickSizeProto)
        {
          //  Console.WriteLine($"**********************[tickSize] id={tickSizeProto.ReqId} Size={tickSizeProto.Size} size={tickSizeProto.Size}");
        }

        void EWrapper.tickOptionComputationProtoBuf(TickOptionComputation tickOptionComputationProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.tickGenericProtoBuf(TickGeneric tickGenericProto)
        {
            Console.WriteLine($"[tickGeneric] id={tickGenericProto.ReqId} val={tickGenericProto.Value}");
        }

        void EWrapper.tickStringProtoBuf(TickString tickStringProto)
        {
            Console.WriteLine($"[tickString] id={tickStringProto.ReqId} val={tickStringProto.Value}");
        }

        void EWrapper.tickSnapshotEndProtoBuf(TickSnapshotEnd tickSnapshotEndProto)
        {
            Console.WriteLine($"[TICK-SNAPSHOT-END] id={tickSnapshotEndProto.ReqId}");
        }

        void EWrapper.updateMarketDepthProtoBuf(MarketDepth marketDepthProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.updateMarketDepthL2ProtoBuf(MarketDepthL2 marketDepthL2Proto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.marketDataTypeProtoBuf(MarketDataType marketDataTypeProto)
        {
            Console.WriteLine($"[MKT-DATA-TYPE] reqId={marketDataTypeProto.ReqId} type={marketDataTypeProto.MarketDataType_} (1=Live,3=Delayed,4=DelayedFrozen)");
        }

        void EWrapper.tickReqParamsProtoBuf(TickReqParams tickReqParamsProto)
        {
            Console.WriteLine($"[TICK-REQ-PARAMS] id={tickReqParamsProto.ReqId} minTick={tickReqParamsProto.MinTick} bboExchange={tickReqParamsProto.BboExchange} snapshotPermissions={tickReqParamsProto.SnapshotPermissions}");
        }

        void EWrapper.updateAccountValueProtoBuf(AccountValue accountValueProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.updatePortfolioProtoBuf(PortfolioValue portfolioValueProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.updateAccountTimeProtoBuf(AccountUpdateTime accountUpdateTimeProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.accountDataEndProtoBuf(AccountDataEnd accountDataEndProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.managedAccountsProtoBuf(ManagedAccounts managedAccountsProto)
        {
            Console.WriteLine($"[ACCOUNTS] {managedAccountsProto.AccountsList}");
        }

        void EWrapper.positionProtoBuf(Position positionProto)
        {
            Console.WriteLine($"[POSITION] account={positionProto.Account} symbol={positionProto.Contract.Symbol} qty={positionProto.Position_}");
        }

        void EWrapper.positionEndProtoBuf(PositionEnd positionEndProto)
        {
            Console.WriteLine($"[POSITION-END]");
        }

        void EWrapper.accountSummaryProtoBuf(AccountSummary accountSummaryProto)
        {
            Console.WriteLine($"[ACCOUNT-SUMMARY] reqId={accountSummaryProto.ReqId} tag={accountSummaryProto.Tag} value={accountSummaryProto.Value} cur={accountSummaryProto.Currency} account={accountSummaryProto.Account}");
        }

        void EWrapper.accountSummaryEndProtoBuf(AccountSummaryEnd accountSummaryEndProto)
        {
            Console.WriteLine($"[ACCOUNT-SUMMARY-END] reqId={accountSummaryEndProto.ReqId}");
        }

        void EWrapper.positionMultiProtoBuf(PositionMulti positionMultiProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.positionMultiEndProtoBuf(PositionMultiEnd positionMultiEndProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.accountUpdateMultiProtoBuf(AccountUpdateMulti accountUpdateMultiProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.accountUpdateMultiEndProtoBuf(AccountUpdateMultiEnd accountUpdateMultiEndProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.historicalDataProtoBuf(HistoricalData historicalDataProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.historicalDataUpdateProtoBuf(HistoricalDataUpdate historicalDataUpdateProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.historicalDataEndProtoBuf(HistoricalDataEnd historicalDataEndProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.realTimeBarTickProtoBuf(RealTimeBarTick realTimeBarTickProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.headTimestampProtoBuf(HeadTimestamp headTimestampProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.histogramDataProtoBuf(HistogramData histogramDataProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.historicalTicksProtoBuf(HistoricalTicks historicalTicksProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.historicalTicksBidAskProtoBuf(HistoricalTicksBidAsk historicalTicksBidAskProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.historicalTicksLastProtoBuf(HistoricalTicksLast historicalTicksLastProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.tickByTickDataProtoBuf(TickByTickData tickByTickDataProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.updateNewsBulletinProtoBuf(NewsBulletin newsBulletinProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.newsArticleProtoBuf(NewsArticle newsArticleProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.newsProvidersProtoBuf(NewsProviders newsProvidersProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.historicalNewsProtoBuf(HistoricalNews historicalNewsProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.historicalNewsEndProtoBuf(HistoricalNewsEnd historicalNewsEndProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.wshMetaDataProtoBuf(WshMetaData wshMetaDataProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.wshEventDataProtoBuf(IBApi.protobuf.WshEventData wshEventDataProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.tickNewsProtoBuf(TickNews tickNewsProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.scannerParametersProtoBuf(ScannerParameters scannerParametersProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.scannerDataProtoBuf(ScannerData scannerDataProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.fundamentalsDataProtoBuf(FundamentalsData fundamentalsDataProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.pnlProtoBuf(PnL pnlProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.pnlSingleProtoBuf(PnLSingle pnlSingleProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.receiveFAProtoBuf(ReceiveFA receiveFAProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.replaceFAEndProtoBuf(ReplaceFAEnd replaceFAEndProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.commissionAndFeesReportProtoBuf(IBApi.protobuf.CommissionAndFeesReport commissionAndFeesReportProto)
        {
            Console.WriteLine($"[COMMISSION] {commissionAndFeesReportProto.CommissionAndFees}");
        }

        void EWrapper.historicalScheduleProtoBuf(HistoricalSchedule historicalScheduleProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.rerouteMarketDataRequestProtoBuf(RerouteMarketDataRequest rerouteMarketDataRequestProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.rerouteMarketDepthRequestProtoBuf(RerouteMarketDepthRequest rerouteMarketDepthRequestProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.secDefOptParameterProtoBuf(SecDefOptParameter secDefOptParameterProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.secDefOptParameterEndProtoBuf(SecDefOptParameterEnd secDefOptParameterEndProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.softDollarTiersProtoBuf(SoftDollarTiers softDollarTiersProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.familyCodesProtoBuf(FamilyCodes familyCodesProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.symbolSamplesProtoBuf(SymbolSamples symbolSamplesProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.smartComponentsProtoBuf(SmartComponents smartComponentsProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.marketRuleProtoBuf(MarketRule marketRuleProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.userInfoProtoBuf(UserInfo userInfoProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.nextValidIdProtoBuf(NextValidId nextValidIdProto)
        {
            Console.WriteLine($"[OK] nextValidId: {nextValidIdProto.OrderId}");
        }

        void EWrapper.currentTimeProtoBuf(CurrentTime currentTimeProto)
        {
            Console.WriteLine($"[SERVER TIME] {DateTimeOffset.FromUnixTimeSeconds(currentTimeProto.CurrentTime_).UtcDateTime:O}");
        }

        void EWrapper.currentTimeInMillisProtoBuf(CurrentTimeInMillis currentTimeInMillisProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.verifyMessageApiProtoBuf(VerifyMessageApi verifyMessageApiProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.verifyCompletedProtoBuf(VerifyCompleted verifyCompletedProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.displayGroupListProtoBuf(DisplayGroupList displayGroupListProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.displayGroupUpdatedProtoBuf(DisplayGroupUpdated displayGroupUpdatedProto)
        {
            throw new NotImplementedException();
        }

        void EWrapper.marketDepthExchangesProtoBuf(MarketDepthExchanges marketDepthExchangesProto)
        {
            throw new NotImplementedException();
        }
#endregion

        private static string FieldName(int field) => field switch
        {
            0 => "BID",
            1 => "BID",              // stari/novi enum layouti umeju da se razlikuju; bitno je dole mapiranje
            2 => "ASK",
            4 => "LAST",
            6 => "HIGH",
            7 => "LOW",
            9 => "CLOSE",
            66 => "LAST",
            67 => "ASK",
            68 => "BID",
            69 => "LAST_SIZE",
            70 => "ASK_SIZE",
            71 => "BID_SIZE",
            72 => "HIGH",
            73 => "LOW",
            75 => "CLOSE",
            76 => "OPEN",
            77 => "LOW_13W",
            78 => "HI_13W",
            79 => "LOW_26W",
            80 => "HI_26W",
            81 => "LOW_52W",
            82 => "HI_52W",
            83 => "AVG_VOLUME",
            84 => "OPEN_INT",
            88 => "RT_VOLUME_TS",
            _ => $"F{field}"
        };

        private string SymbolOf(int tickerId)
        {
            if (Subscriptions != null && Subscriptions.TryGetValue(tickerId, out var c))
                return c.Symbol ?? $"ID{tickerId}";
            return $"ID{tickerId}";
        }

        public void marketDataType(int reqId, int marketDataType)
        {
            Console.WriteLine($"[MKT-DATA-TYPE] {SymbolOf(reqId)} type={marketDataType} (1=Live,3=Delayed,4=DelayedFrozen)");
        }
        
        public void tickString(int tickerId, int tickType, string value)
        {
            TickStringArrived?.Invoke(tickerId, tickType, value);
            var name = FieldName(tickType);
            Console.WriteLine($"[STR  ] {SymbolOf(tickerId)} {name}={value}");
        }

        public void tickByTickAllLast(int reqId, int tickType, long time, double price, int size, IBApi.TickAttribLast tickAttribLast, string exchange, string specialConditions)
        {
            var ts = DateTimeOffset.FromUnixTimeSeconds(time).ToLocalTime();
            Console.WriteLine($"[TBT-LAST] {SymbolOf(reqId)} {ts:HH:mm:ss} px={price} sz={size} ex={exchange}");
        }

        public void tickByTickBidAsk( int reqId, long time, double bidPrice, double askPrice, int bidSize, int askSize, IBApi.TickAttribBidAsk tickAttribBidAsk)
        {
            var ts = DateTimeOffset.FromUnixTimeSeconds(time).ToLocalTime();
            Console.WriteLine($"[TBT-BA] {SymbolOf(reqId)} {ts:HH:mm:ss} {bidPrice}x{bidSize} | {askPrice}x{askSize}");
        }

        public void tickByTickMidPoint(int reqId, long time, double midPoint)
        {
            var ts = DateTimeOffset.FromUnixTimeSeconds(time).ToLocalTime();
            Console.WriteLine($"[TBT-MID] {SymbolOf(reqId)} {ts:HH:mm:ss} mid={midPoint}");
        }
        public void updateMktDepth(int tickerId, int position, int operation, int side, double price, decimal size)
        {
            // operation: 0=insert, 1=update, 2=remove ; side: 0=bid, 1=ask
            Console.WriteLine($"[L2] id={tickerId} pos={position} op={operation} side={(side == 0 ? "BID" : "ASK")} px={price} sz={size}");
        }

        public void updateMktDepthL2(int tickerId, int position, string marketMaker, int operation, int side, double price, decimal size, bool isSmartDepth)
        {
            Console.WriteLine($"[L2] id={tickerId} pos={position} mm={marketMaker} op={operation} side={(side == 0 ? "BID" : "ASK")} px={price} sz={size} smart={isSmartDepth}");
        }
        public void tickSize(int tickerId, int field, decimal size)
        {
            int intSize;

            if (size <= 0)
            {
                intSize = 0;
            }
            else if (size >= int.MaxValue)
            {
                intSize = int.MaxValue;
            }
            else
            {
                intSize = decimal.ToInt32(size);
            }

            TickSizeArrived?.Invoke(tickerId, field, intSize);

            if (!_bbo.TryGetValue(tickerId, out var b))
                b = (0, 0, 0, 0);

            switch (field)
            {
                case 69: b.bidSz = intSize; break; // BID_SIZE
                case 70: b.askSz = intSize; break; // ASK_SIZE
                case 71: /* LAST_SIZE */ break;
            }

            _bbo[tickerId] = b;
            PrintBbo(tickerId);
        }

        private void PrintBbo(int id)
        {
            var (bid, bidSz, ask, askSz) = _bbo[id];
            if (bid <= 0 || ask <= 0) return;

            var mid = (bid + ask) / 2.0;
            var spread = ask - bid;
            var spreadBp = mid > 0 ? (spread / mid) * 10000.0 : 0.0;
            var totSz = bidSz + askSz;
            var imb = totSz > 0 ? (bidSz - askSz) / (double)totSz : 0.0;

            Console.WriteLine($"[BBO] id={id}  {bid:0.####} x {bidSz}  |  {ask:0.####} x {askSz}  mid={mid:0.####}  spread={spread:0.####} ({spreadBp:0.#} bp)  imb={imb:P0}");
        }
        
        // === NOVO: mape reqId -> Symbol i poslednje vrednosti po simbolu ===

        private readonly Dictionary<string, (decimal? bid, decimal? ask, decimal? last)> _lastByTicker = new(StringComparer.OrdinalIgnoreCase);

        // === NOVO: poziv iz Session-a kada se napravi pretplata ===
        public void RegisterSubscription(int reqId, Symbol symbol) => _symbolByReqId[reqId] = symbol;
        public void UnregisterSubscription(int reqId) => _symbolByReqId.Remove(reqId);

        // ... Tvoj postojeći kod (connectAck, error, itd) ostaje 

        // === Bitno: u tickPrice/ tickSize već dobijaš feed.
        // Dodaj emitovanje QuoteUpdated gde ima smisla (na svaku promenu BID/ASK/LAST).
        public void tickPrice(int tickerId, int field, double price, TickAttrib attribs)
        {
            // 1) Prosledi sirovi tick dalje (za MarketDataFeedIbkr)
            try
            {
                TickPriceArrived?.Invoke(tickerId, field, price);
            }
            catch
            {
                // swallow da wrapper ne puca zbog event handlera
            }

            // 2) Legacy pipeline → QuoteUpdated event
            try
            {
                if (!_symbolByReqId.TryGetValue(tickerId, out var sym))
                    return;

                var key = sym.Ticker;

                // _lastByTicker je Dictionary<string, (decimal? bid, decimal? ask, decimal? last)>
                var st = _lastByTicker.TryGetValue(key, out var s)
                    ? s
                    : (bid: (decimal?)null, ask: (decimal?)null, last: (decimal?)null);

                switch (field)
                {
                    case (int)TickType.BID:
                        st.bid = (decimal)price;
                        break;

                    case (int)TickType.ASK:
                        st.ask = (decimal)price;
                        break;

                    case (int)TickType.LAST:
                        st.last = (decimal)price;
                        break;

                    default:
                        // ostali price tickovi nam ne trebaju za ovaj QuoteUpdated
                        return;
                }

                _lastByTicker[key] = st;

                var quote = new MarketQuote(
                    Symbol: sym,
                    Bid: st.bid,
                    Ask: st.ask,
                    Last: st.last,
                    BidSize: null,              // ovde za sada nemamo size
                    AskSize: null,              // ovde za sada nemamo size
                    TimestampUtc: DateTime.UtcNow
                );

                QuoteUpdated?.Invoke(quote);
            }
            catch
            {
                // ne ruši wrapper zbog jedne greške u eventu
            }
        }
        private static int PackAttribs(TickAttrib a)
        {
            // simple bit mask: 1=CanAutoExecute, 2=PastLimit, 4=PreOpen
            int mask = 0;
            if (a.CanAutoExecute) mask |= 1;
            if (a.PastLimit) mask |= 2;
            if (a.PreOpen) mask |= 4;
            return mask;
        }


        public event Action<int, Contract, Order, OrderState?>? OpenOrderArrived;
        public event Action? OpenOrdersEnd;

        public void openOrder(int orderId, Contract contract, Order order, OrderState orderState)
        {
            try
            {
                // Some IB API versions do not expose AvgFillPrice on OrderState.
                // Also "filled" is not reliable here; we'll emit 0 and let orderStatus/execDetails update real fills.
                var status = orderState?.Status ?? "Submitted";
                int filled = 0;
                decimal? avg = null;

                Log.Information(
                    "[OPEN-ORDER] id={OrderId} sym={Sym} action={Action} qty={Qty} type={Type} lmt={Lmt} aux={Aux} ref={Ref} oca={Oca} tif={Tif} outsideRth={OutsideRth} parentId={ParentId} transmit={Transmit} openClose={OpenClose} status={Status}",
                    orderId,
                    contract?.Symbol,
                    order?.Action,
                    order?.TotalQuantity,
                    order?.OrderType,
                    order?.LmtPrice,
                    order?.AuxPrice,
                    order?.OrderRef,
                    order?.OcaGroup,
                    order?.Tif,
                    order?.OutsideRth,
                    order?.ParentId,
                    order?.Transmit,
                    order?.OpenClose,
                    status);

                OrderStatusUpdated?.Invoke(orderId, status, filled, avg, null);

                // NOVO: prosledi ceo open order (sa OrderRef, qty, status)
                OpenOrderArrived?.Invoke(orderId, contract, order, orderState);
            }
            catch
            {
                // swallow – wrapper must never throw
            }
        }



        public void openOrderEnd()
        {
            try
            {
                OpenOrdersEnd?.Invoke();
            }
            catch
            {
                // swallow – wrapper must never throw
            }
        }


        // u EWrapper.commissionAndFeesReport() (noviji IB API tip)
        public void commissionAndFeesReport(CommissionAndFeesReport report)
        {
            try
            {
                // report.ExecId, report.CommissionAndFees (double)
                // orderId IB ne šalje direktno; često ga dobiješ iz execDetails ranije, ali za sada emituj sa -1:
                CommissionReported?.Invoke(-1, (decimal)report.CommissionAndFees);
                
                // FIX: Takođe emituj CommissionReportArrived event koji RealIbkrClient sluša
                CommissionReportArrived?.Invoke(new CommissionAndFeesReport
                {
                    ExecId = report.ExecId,
                    CommissionAndFees = report.CommissionAndFees,
                    Currency = report.Currency
                });
                
                Console.WriteLine($"[COMMISSION] execId={report.ExecId} fee={report.CommissionAndFees} {report.Currency}");
            }
            catch { /* swallow */ }
        }

        public event Action<int, int, string, int, decimal?, string?>? OrderStatusWithPermId;
        public void orderStatus(
            int orderId,
            string status,
            double filled,
            double remaining,
            double avgFillPrice,
            int permId,
            int parentId,
            double lastFillPrice,
            int clientId,
            string whyHeld,
            double mktCapPrice)
        {
            try
            {
                var avg = avgFillPrice > 0 ? (decimal?)avgFillPrice : null;

                // 1) TVOJ STARI EVENT — ostaje
                OrderStatusUpdated?.Invoke(orderId, status, (int)filled, avg, whyHeld);

                // 2) NOVO — event koji uključuje permId
                OrderStatusWithPermId?.Invoke(orderId, permId, status, (int)filled, avg, whyHeld);

                Log.Information(
                    "[ORDER-STATUS] id={OrderId} permId={PermId} parentId={ParentId} clientId={ClientId} status={Status} filled={Filled} remaining={Remaining} avg={Avg} lastFill={LastFill} whyHeld={WhyHeld} mktCapPrice={MktCapPrice}",
                    orderId,
                    permId,
                    parentId,
                    clientId,
                    status,
                    filled,
                    remaining,
                    avgFillPrice,
                    lastFillPrice,
                    string.IsNullOrWhiteSpace(whyHeld) ? "n/a" : whyHeld,
                    mktCapPrice);
            }
            catch { }
        }


        // ======================
        //  POZICIJE (IBKR)
        // ======================

        private readonly object _posLock = new();
        private readonly List<(string Account, string Symbol, decimal Qty, double AvgCost)> _positions = new();

        // event-i koje koristi IbkrPositionsProvider
        // account, symbol, qty, avgCost
        public event Action<string, string, decimal, decimal>? PositionReceived;
        public event Action? PositionsEnd;

        // ZAJEDNIČKI HELPER za oba overloada
        private void HandlePosition(string account, Contract contract, decimal pos, double avgCost)
        {
            if (contract == null || string.IsNullOrWhiteSpace(contract.Symbol))
                return;

            if (pos == 0)
                return; // preskoči prazne pozicije

            lock (_posLock)
            {
                _positions.Add((account, contract.Symbol, pos, avgCost));
            }

            Console.WriteLine($"[POS] {account} {contract.Symbol} qty={pos} avgCost={avgCost:F2}");

            try
            {
                PositionReceived?.Invoke(
                    account,
                    contract.Symbol,
                    pos,
                    (decimal)avgCost
                );
            }
            catch
            {
                // ne ruši wrapper zbog subskrajbera
            }
        }

        // IBApi.EWrapper potpis sa double
        public void position(string account, Contract contract, double pos, double avgCost)
        {
            HandlePosition(account, contract, (decimal)pos, avgCost);
        }

        // IBApi.EWrapper potpis sa decimal
        public void position(string account, Contract contract, decimal pos, double avgCost)
        {
            HandlePosition(account, contract, pos, avgCost);
        }

        public void positionEnd()
        {
            Console.WriteLine("[POS] --- END ---");

            lock (_posLock)
            {
                foreach (var p in _positions)
                    Console.WriteLine($"[POS] FINAL {p.Account} {p.Symbol} qty={p.Qty} avgCost={p.AvgCost:F2}");

                _positions.Clear();
            }

            try
            {
                PositionsEnd?.Invoke();
            }
            catch
            {
                // ništa, samo da ne srušimo wrapper
            }
        }


        public event Action<int, string, double>? ExecutionFilled;

        public void execDetails(int reqId, Contract contract, IBApi.Execution execution)
        {
            // prosledi svima koje zanima
            try
            {
                ExecutionArrived?.Invoke(reqId, contract, execution);
            }
            catch { /* nemoj da srušiš wrapper zbog subskrajbera */ }

            // ako želiš i log:
            Console.WriteLine($"[EXEC] req={reqId} {contract.Symbol} {execution.Side} {execution.Shares} @ {execution.Price} execId={execution.ExecId}");
        }

        // === EXEC / COMMISSION EVENTI ===
        public event Action<int, Contract, IBApi.Execution>? ExecutionArrived;
        public event Action<CommissionAndFeesReport>? CommissionReportArrived;
        public event Action<int>? ExecutionRequestFinished;
        public void commissionReport(IBApi.CommissionAndFeesReport commissionReport)
        {
            try
            {
                // novi event – da order service može da snimi pravi fee
                CommissionReportArrived?.Invoke(new CommissionAndFeesReport
                {
                    ExecId = commissionReport.ExecId,
                    CommissionAndFees = (double)commissionReport.CommissionAndFees,
                    Currency = commissionReport.Currency
                });
            }
            catch { }

            Console.WriteLine($"[COMMISSION] execId={commissionReport.ExecId} fee={commissionReport.CommissionAndFees} {commissionReport.Currency}");
        }

        public void connectionClosed()
        {
            try { ConnectionClosed?.Invoke(); } catch { /* swallow */ }
            Console.WriteLine("Konekcija je zatvorena.");
            Log.Warning("[IBKR] connectionClosed received from wrapper");
        }
        public void execDetailsEnd(int reqId)
        {
            try
            {
                ExecutionRequestFinished?.Invoke(reqId);
            }
            catch { }

            Console.WriteLine($"[EXEC-END] req={reqId}");
        }
        public void accountSummary(int reqId, string account, string tag, string value, string currency)
        {
            // čisto da vidiš u logu
            Console.WriteLine($"[IB-ACC-SUM] reqId={reqId} acc={account} tag={tag} val={value} cur={currency}");

            AccountSummaryArrived?.Invoke(reqId, account, tag, value, currency);
        }
        public void accountSummaryEnd(int reqId)
        {
            Console.WriteLine($"[IB-ACC-SUM-END] reqId={reqId}");
            AccountSummaryEnd?.Invoke(reqId);
        }
        public  void contractDetails(int reqId, IBApi.ContractDetails details)
        {
            Console.WriteLine("============== CONTRACT DETAILS ==============");
            Console.WriteLine($"reqId:       {reqId}");
            Console.WriteLine($"Symbol:      {details.Contract.Symbol}");
            Console.WriteLine($"LocalSymbol: {details.Contract.LocalSymbol}");
            Console.WriteLine($"SecType:     {details.Contract.SecType}");
            Console.WriteLine($"ConId:       {details.Contract.ConId}");
            Console.WriteLine($"Exchange:    {details.Contract.Exchange}");
            Console.WriteLine($"PrimaryExch: {details.Contract.PrimaryExch}");
            Console.WriteLine($"Currency:    {details.Contract.Currency}");
            Console.WriteLine($"Long Name:   {details.LongName}");
            Console.WriteLine("==============================================");
        }

        public  void contractDetailsEnd(int reqId)
        {
            Console.WriteLine($"[DEBUG] contractDetailsEnd reqId={reqId}");
        }


    }
}
