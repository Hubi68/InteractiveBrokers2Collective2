using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBApi;

namespace IBCollective2Sync
{
    public class IBClient
    {
        private EClientSocket _clientSocket;
        private IBWrapper _wrapper;
        private readonly EReaderMonitorSignal _signal = new EReaderMonitorSignal();
        private readonly ConcurrentDictionary<string, Position> _positions = new ConcurrentDictionary<string, Position>();
        // Polling primitives
        private readonly SemaphoreSlim _refreshSemaphore = new SemaphoreSlim(1, 1);
        private TaskCompletionSource<bool>? _refreshTcs;
        private ConcurrentDictionary<string, byte>? _refreshSeenSymbols;

        private readonly FileLogger _logger;
        private readonly Configuration _config;
        private readonly SemaphoreSlim _connectionSemaphore = new SemaphoreSlim(1, 1);
        private bool _isConnected = false;
        private int _nextOrderId = 0;
        private CancellationTokenSource _readerCancellation;

        public event Func<PositionChangedEventArgs, Task> OnPositionChanged;
        public event Func<TradeExecutedEventArgs, Task> OnTradeExecuted;
        public event Func<Task> OnConnectionLost;

        public IBClient(FileLogger logger, Configuration config)
        {
            _logger = logger;
            _config = config;
            _wrapper = new IBWrapper(this, logger);
            _clientSocket = new EClientSocket(_wrapper, _signal);
        }

        private void FireAndForget(Task? task, string context)
        {
            if (task == null) return;
            _ = task.ContinueWith(t =>
            {
                if (t.IsFaulted)
                    _logger.Error($"Unhandled exception in event handler [{context}]: {t.Exception?.GetBaseException().Message}", t.Exception?.GetBaseException());
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        public async Task ConnectAsync(string host, int port, int clientId)
        {
            await _connectionSemaphore.WaitAsync();
            try
            {
                if (_isConnected)
                {
                    _logger.Info("Already connected to IB");
                    return;
                }

                _logger.Info($"Attempting to connect to IB TWS/Gateway at {host}:{port}");
                Console.WriteLine($"Connecting to IB TWS/Gateway at {host}:{port}...");

                Console.WriteLine($"Connecting to IB TWS/Gateway at {host}:{port} (Client ID: {clientId})...");

                // Clear state on new connection attempt
                _positions.Clear();

                _clientSocket.eConnect(host, port, clientId);

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

                while (!_clientSocket.IsConnected() && !cts.Token.IsCancellationRequested)
                {
                    await Task.Delay(100);
                }

                if (!_clientSocket.IsConnected())
                {
                    var errorMsg = "Failed to connect to IB TWS/Gateway - timeout";
                    _logger.Error(errorMsg);
                    throw new TimeoutException(errorMsg);
                }

                _isConnected = true;
                _logger.Info("Successfully connected to IB TWS/Gateway");
                Console.WriteLine("Connected to IB successfully.");

                var reader = new EReader(_clientSocket, _signal);
                reader.Start();

                _readerCancellation = new CancellationTokenSource();

                _ = Task.Run(() =>
                {
                    while (_clientSocket.IsConnected() && !_readerCancellation.Token.IsCancellationRequested)
                    {
                        reader.processMsgs();
                    }
                }, _readerCancellation.Token);
            }
            finally
            {
                _connectionSemaphore.Release();
            }
        }

        public async Task StartPositionMonitoringAsync()
        {
            _logger.Info("Starting real-time position monitoring");

            // We do NOT call reqPositions here anymore. We poll it on demand.
            _clientSocket.reqExecutions(_nextOrderId++, new ExecutionFilter());

            _logger.Info("Real-time monitoring subscriptions activated");
            Console.WriteLine("Started real-time position monitoring.");

            await Task.CompletedTask;
        }

        public async Task DisconnectAsync()
        {
            await _connectionSemaphore.WaitAsync();
            try
            {
                if (_isConnected)
                {
                    _logger.Info("Disconnecting from IB");
                    _readerCancellation?.Cancel();
                    _clientSocket?.eDisconnect();
                    _isConnected = false;
                }
            }
            finally
            {
                _connectionSemaphore.Release();
            }
        }

        public async Task<List<Position>?> GetPositionsAsync()
        {
            if (!_isConnected) return null;

            var success = await RefreshPositionsAsync();
            if (!success)
            {
                _logger.Warn("Failed to refresh positions from IB. Aborting GetPositionsAsync.");
                return null;
            }

            return _positions.Values.ToList();
        }

        private async Task<bool> RefreshPositionsAsync()
        {
            // Ensure only one refresh happens at a time
            await _refreshSemaphore.WaitAsync();
            try
            {
                _logger.Debug("Refreshing positions from IB...");

                _refreshTcs = new TaskCompletionSource<bool>();
                _refreshSeenSymbols = new ConcurrentDictionary<string, byte>();

                _clientSocket.reqPositions();

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var completedTask = await Task.WhenAny(_refreshTcs.Task, Task.Delay(-1, cts.Token));

                if (cts.Token.IsCancellationRequested)
                {
                    _logger.Warn("Timeout waiting for position refresh from IB");
                    return false;
                }

                // SWEEP PHASE: Remove positions that were NOT seen in this refresh cycle
                var allCachedSymbols = _positions.Keys.ToList();
                foreach (var symbol in allCachedSymbols)
                {
                    if (_refreshSeenSymbols != null && !_refreshSeenSymbols.ContainsKey(symbol))
                    {
                        // Symbol was in cache but not returned by IB -> It is closed.
                        if (_positions.TryRemove(symbol, out var removedPos))
                        {
                            _logger.Info($"Position closed (detected via refresh sweep): {symbol}");
                            FireAndForget(OnPositionChanged?.Invoke(new PositionChangedEventArgs
                            {
                                Symbol = symbol,
                                OldQuantity = removedPos.Quantity,
                                NewQuantity = 0,
                                Timestamp = DateTime.Now
                            }), $"OnPositionChanged/sweep/{symbol}");
                        }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Error refreshing positions: {ex.Message}", ex);
                return false;
            }
            finally
            {
                // Cleanup
                _refreshSeenSymbols = null;
                _refreshTcs = null;
                _refreshSemaphore.Release();
            }
        }

        public double GetCachedPosition(string symbol)
        {
            return _positions.GetValueOrDefault(symbol)?.Quantity ?? 0;
        }

        public Position? GetCachedPositionFull(string symbol)
        {
            return _positions.GetValueOrDefault(symbol);
        }

        internal void OnPosition(string account, Contract contract, double position, double avgCost)
        {
            var c2Symbol = _config.GetC2Symbol(contract);

            // Mark as seen if we are refreshing
            _refreshSeenSymbols?.TryAdd(c2Symbol, 1);

            var oldPosition = _positions.GetValueOrDefault(c2Symbol)?.Quantity ?? 0;

            _logger.Debug($"Position update received: {contract.Symbol} (Local: {contract.LocalSymbol}) -> {c2Symbol} = {position}");

            if (Math.Abs(position) > 0.01)
            {
                _positions[c2Symbol] = new Position
                {
                    Symbol = c2Symbol,
                    Quantity = position,
                    AvgCost = avgCost,
                    SecType = contract.SecType,
                    LastUpdated = DateTime.Now
                };
            }
            else
            {
                _positions.TryRemove(c2Symbol, out _);
                _logger.Debug($"Position closed and removed: {c2Symbol}");
            }

            if (Math.Abs(position - oldPosition) > 0.01)
            {
                _logger.Info($"Position change detected: {c2Symbol} {oldPosition} → {position}");
                FireAndForget(OnPositionChanged?.Invoke(new PositionChangedEventArgs
                {
                    Symbol = c2Symbol,
                    OldQuantity = oldPosition,
                    NewQuantity = position
                }), $"OnPositionChanged/{c2Symbol}");
            }
        }

        internal void OnPositionEnd()
        {
            _logger.Info("Position list received from IB");
            _refreshTcs?.TrySetResult(true);
        }

        internal void OnExecution(Contract contract, Execution execution)
        {
            var c2Symbol = _config.GetC2Symbol(contract);
            _logger.Info($"Execution received: {execution.Side} {execution.Shares} {c2Symbol} @ {execution.Price:F2}");

            var currentPosition = _positions.GetValueOrDefault(c2Symbol)?.Quantity ?? 0;

            FireAndForget(OnTradeExecuted?.Invoke(new TradeExecutedEventArgs
            {
                Symbol = c2Symbol,
                Action = execution.Side,
                Quantity = execution.Shares,
                Price = execution.Price,
                NewPosition = currentPosition
            }), $"OnTradeExecuted/{c2Symbol}");
        }

        internal void OnConnectionClosed()
        {
            _isConnected = false;
            _logger.Error("IB connection closed");
            FireAndForget(OnConnectionLost?.Invoke(), "OnConnectionLost");
        }

        internal void SetNextOrderId(int orderId)
        {
            _nextOrderId = orderId;
        }
    }

    public class IBWrapper : EWrapper
    {
        private readonly IBClient _client;
        private readonly FileLogger _logger;

        public IBWrapper(IBClient client, FileLogger logger)
        {
            _client = client;
            _logger = logger;
        }

        public void position(string account, Contract contract, double position, double avgCost)
        {
            _client.OnPosition(account, contract, position, avgCost);
        }

        public void positionEnd()
        {
            _client.OnPositionEnd();
        }

        public void error(Exception e)
        {
            _logger.Error($"IB Error: {e.Message}", e);
        }

        public void error(string str)
        {
            _logger.Error($"IB Error: {str}");
        }

        public void error(int id, int errorCode, string errorMsg)
        {
            if (errorCode == 1100 || errorCode == 1102 || errorCode == 2110)
            {
                _client.OnConnectionClosed();
            }

            if (errorCode < 2000 || errorCode >= 10000)
            {
                _logger.Error($"IB Error {errorCode}: {errorMsg}");
            }
            else
            {
                _logger.Debug($"IB Info {errorCode}: {errorMsg}");
            }
        }

        public void connectionClosed()
        {
            _client.OnConnectionClosed();
        }

        public void nextValidId(int orderId)
        {
            _client.SetNextOrderId(orderId);
        }

        public void execDetails(int reqId, Contract contract, Execution execution)
        {
            _client.OnExecution(contract, execution);
        }

        // Minimal EWrapper implementations
        public void tickPrice(int tickerId, int field, double price, TickAttrib attrib) { }
        public void tickSize(int tickerId, int field, int size) { }
        public void tickOptionComputation(int tickerId, int field, double impliedVol, double delta, double optPrice, double pvDividend, double gamma, double vega, double theta, double undPrice) { }
        public void tickGeneric(int tickerId, int tickType, double value) { }
        public void tickString(int tickerId, int tickType, string value) { }
        public void tickEFP(int tickerId, int tickType, double basisPoints, string formattedBasisPoints, double impliedFuture, int holdDays, string futureLastTradeDate, double dividendImpact, double dividendsToLastTradeDate) { }
        public void orderStatus(int orderId, string status, double filled, double remaining, double avgFillPrice, int permId, int parentId, double lastFillPrice, int clientId, string whyHeld, double mktCapPrice) { }
        public void openOrder(int orderId, Contract contract, Order order, OrderState orderState) { }
        public void openOrderEnd() { }
        public void updateAccountValue(string key, string val, string currency, string accountName) { }
        public void updateAccountTime(string timeStamp) { }
        public void accountDownloadEnd(string accountName) { }
        public void contractDetails(int reqId, ContractDetails contractDetails) { }
        public void bondContractDetails(int reqId, ContractDetails contractDetails) { }
        public void contractDetailsEnd(int reqId) { }
        public void execDetailsEnd(int reqId) { }
        public void updateMktDepth(int id, int position, int operation, int side, double price, int size) { }
        public void updateMktDepthL2(int id, int position, string marketMaker, int operation, int side, double price, int size) { }
        public void updateNewsBulletin(int msgId, int msgType, String newsMessage, String originExch) { }
        public void managedAccounts(string accountsList) { }
        public void receiveFA(int faData, string cxml) { }
        public void historicalData(int reqId, Bar bar) { }
        public void historicalDataEnd(int reqId, string startDateStr, string endDateStr) { }
        public void scannerParameters(string xml) { }
        public void scannerData(int reqId, int rank, ContractDetails contractDetails, string distance, string benchmark, string projection, string legsStr) { }
        public void scannerDataEnd(int reqId) { }
        public void realtimeBar(int reqId, long time, double open, double high, double low, double close, long volume, double wap, int count) { }
        public void currentTime(long time) { }
        public void fundamentalData(int reqId, string data) { }
        public void deltaNeutralValidation(int reqId, UnderComp deltaNeutralContract) { }
        public void tickSnapshotEnd(int reqId) { }
        public void marketDataType(int reqId, int marketDataType) { }
        public void commissionReport(CommissionReport commissionReport) { }
        public void positionMulti(int reqId, string account, string modelCode, Contract contract, double pos, double avgCost) { }
        public void positionMultiEnd(int reqId) { }
        public void accountUpdateMulti(int reqId, string account, string modelCode, string key, string value, string currency) { }
        public void accountUpdateMultiEnd(int reqId) { }
        public void securityDefinitionOptionParameter(int reqId, string exchange, int underlyingConId, string tradingClass, string multiplier, HashSet<string> expirations, HashSet<double> strikes) { }
        public void securityDefinitionOptionParameterEnd(int reqId) { }
        public void softDollarTiers(int reqId, SoftDollarTier[] tiers) { }
        public void familyCodes(FamilyCode[] familyCodes) { }
        public void symbolSamples(int reqId, ContractDescription[] contractDescriptions) { }
        public void mktDepthExchanges(DepthMktDataDescription[] depthMktDataDescriptions) { }
        public void tickNews(int tickerId, long timeStamp, string providerCode, string articleId, string headline, string extraData) { }
        public void smartComponents(int reqId, Dictionary<int, KeyValuePair<string, char>> theMap) { }
        public void tickReqParams(int tickerId, double minTick, string bboExchange, int snapshotPermissions) { }
        public void newsProviders(NewsProvider[] newsProviders) { }
        public void newsArticle(int requestId, int articleType, string articleText) { }
        public void historicalNews(int requestId, string time, string providerCode, string articleId, string headline) { }
        public void historicalNewsEnd(int requestId, bool hasMore) { }
        public void headTimestamp(int reqId, string headTimestamp) { }
        public void histogramData(int reqId, HistogramEntry[] data) { }
        public void historicalDataUpdate(int reqId, Bar bar) { }
        public void rerouteMktDataReq(int reqId, int conid, string exchange) { }
        public void rerouteMktDepthReq(int reqId, int conid, string exchange) { }
        public void marketRule(int marketRuleId, PriceIncrement[] priceIncrements) { }
        public void updatePortfolio(Contract contract, double position, double marketPrice, double marketValue, double averageCost, double unrealizedPNL, double realizedPNL, string accountName) { }
        public void pnl(int reqId, double dailyPnL, double unrealizedPnL, double realizedPnL) { }
        public void pnlSingle(int reqId, int pos, double dailyPnL, double unrealizedPnL, double realizedPnL, double value) { }
        public void historicalTicks(int reqId, HistoricalTick[] ticks, bool done) { }
        public void historicalTicksBidAsk(int reqId, HistoricalTickBidAsk[] ticks, bool done) { }
        public void historicalTicksLast(int reqId, HistoricalTickLast[] ticks, bool done) { }
        public void tickByTickAllLast(int reqId, int tickType, long time, double price, int size, TickAttrib tickAttrib, string exchange, string specialConditions) { }
        public void tickByTickBidAsk(int reqId, long time, double bidPrice, double askPrice, int bidSize, int askSize, TickAttrib tickAttrib) { }
        public void connectAck() { }
        public void displayGroupList(int reqId, string groups) { }
        public void displayGroupUpdated(int reqId, string contractInfo) { }
        public void verifyMessageAPI(string apiData) { }
        public void verifyCompleted(bool isSuccessful, string errorText) { }
        public void verifyAndAuthMessageAPI(string apiData, string xyzChallenge) { }
        public void verifyAndAuthCompleted(bool isSuccessful, string errorText) { }
        public void replaceFA(int reqId, int faDataType, string xml) { }
        public void wshMetaData(int reqId, string dataJson) { }
        public void wshEventData(int reqId, string dataJson) { }
        public void userInfo(int reqId, string whiteLabel) { }
        public void accountSummary(int reqId, string account, string tag, string value, string currency) { }
        public void accountSummaryEnd(int reqId) { }
        public void tickByTickMidPoint(int reqId, long time, double midPoint) { }
    }
}
