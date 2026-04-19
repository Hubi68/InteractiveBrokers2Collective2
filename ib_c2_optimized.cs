using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Linq;
using System.IO;
using IBApi;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;

using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace IBCollective2Sync
{
    public class Program
    {
        private static IBClient? _ibClient;
        private static Collective2Client? _c2Client;
        private static Timer? _syncTimer;
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _symbolSemaphores = new ConcurrentDictionary<string, SemaphoreSlim>();
        private static FileLogger? _logger;
        private static readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private static Configuration? _config;
        private static bool _isInMaintenanceMode = false;

        public static async Task Main(string[] args)
        {
            Console.WriteLine("IB-Collective2 Portfolio Sync Starting...");

            try
            {
                _config = Configuration.Load();
                _logger = new FileLogger("IBCollective2Sync");



                _ibClient = new IBClient(_logger, _config);
                _c2Client = new Collective2Client(_config.C2ApiKey, _config.C2StrategyId, _logger, _config.MaxRetryAttempts);

                _ibClient.OnPositionChanged += OnPositionChanged;
                _ibClient.OnTradeExecuted += OnTradeExecuted;
                _ibClient.OnConnectionLost += OnConnectionLost;

                // Start maintenance monitor AFTER logger and clients are initialized
                _ = MonitorMaintenanceWindow();

                await ConnectWithRetry();

                Console.WriteLine("Performing initial portfolio sync...");
                await SyncPortfolio();

                _syncTimer = new Timer(
                    async _ => await BackupSync(), 
                    null, 
                    TimeSpan.FromMinutes(_config.BackupSyncIntervalMinutes), 
                    TimeSpan.FromMinutes(_config.BackupSyncIntervalMinutes)
                );

                Console.WriteLine("Event-driven sync active. Press 'q' to quit.");
                
                await WaitForQuitCommand();
            }
            catch (Exception ex)
            {
                _logger.Error($"Fatal error: {ex.Message}", ex);
                Console.WriteLine($"Fatal error: {ex.Message}");
            }
            finally
            {
                await Cleanup();
            }
        }

        private static async Task ConnectWithRetry()
        {
            var retryPolicy = Policy
                .Handle<Exception>()
                .WaitAndRetryAsync(
                    _config.MaxRetryAttempts,
                    retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                    onRetry: (exception, timeSpan, retryCount, context) =>
                    {
                        _logger.Warn($"Connection attempt {retryCount} failed. Retrying in {timeSpan.TotalSeconds} seconds...");
                    });

            await retryPolicy.ExecuteAsync(async () =>
            {
                await _ibClient.ConnectAsync(_config.IbHost, _config.IbPort, _config.IbClientId);
                await _ibClient.StartPositionMonitoringAsync();
            });
        }

        private static async Task WaitForQuitCommand()
        {
            await Task.Run(() =>
            {
                while (!_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    if (Console.KeyAvailable && Console.ReadKey(true).KeyChar == 'q')
                    {
                        _cancellationTokenSource.Cancel();
                        break;
                    }
                    Thread.Sleep(100);
                }
            }, _cancellationTokenSource.Token);
        }

        private static async Task Cleanup()
        {
            _logger?.Info("Shutting down...");

            _cancellationTokenSource.Cancel();
            _syncTimer?.Dispose();

            if (_ibClient != null)
            {
                await _ibClient.DisconnectAsync();
            }

            _logger?.Info("Shutdown complete");
            _logger?.Dispose();
        }

        private static async Task MonitorMaintenanceWindow()
        {
            _logger.Info("Starting TWS Maintenance Window Monitor (00:00 - 02:00 EST)");
            
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try 
                {
                    var easternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                    var utcNow = DateTime.UtcNow;
                    var easternTime = TimeZoneInfo.ConvertTimeFromUtc(utcNow, easternZone).TimeOfDay;
                    
                    // DEBUG LOGGING to verify logic
                    if (utcNow.Second == 0 && utcNow.Minute % 5 == 0) // Log every 5 mins
                    {
                         string msg = $"Maintenance Monitor: UTC={utcNow:HH:mm:ss}, EST={easternTime}, InMaintenance={_isInMaintenanceMode}";
                         Console.WriteLine(msg);
                         _logger.Debug(msg);
                    }

                    var start = new TimeSpan(0, 0, 0); // 00:00 AM (Midnight)
                    var end = new TimeSpan(2, 0, 0);    // 02:00 AM

                    if (easternTime >= start && easternTime < end)
                    {
                        if (!_isInMaintenanceMode)
                        {
                            _isInMaintenanceMode = true;
                            _logger.Info("Entering TWS maintenance window (00:00 - 02:00 EST). Disconnecting...");
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Entering TWS maintenance window. Disconnecting...");
                            if (_ibClient != null) await _ibClient.DisconnectAsync();
                        }
                    }
                    else
                    {
                        if (_isInMaintenanceMode)
                        {
                            _isInMaintenanceMode = false;
                            _logger.Info("Exiting TWS maintenance window. Reconnecting...");
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Exiting TWS maintenance window. Reconnecting...");
                            await ConnectWithRetry();
                            // Force a sync upon reconnection
                            await SyncPortfolio();
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error in maintenance monitor: {ex.Message}", ex);
                }

                await Task.Delay(TimeSpan.FromSeconds(30), _cancellationTokenSource.Token);
            }
        }

        private static async Task OnConnectionLost()
        {
            if (_isInMaintenanceMode) return;

            _logger.Error("IB connection lost. Attempting to reconnect...");
            
            try
            {
                await Task.Delay(5000, _cancellationTokenSource.Token);
                await ConnectWithRetry();
                _logger.Info("Successfully reconnected to IB");
            }
            catch (OperationCanceledException)
            {
                _logger.Info("Reconnection cancelled during shutdown");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to reconnect: {ex.Message}", ex);
            }
        }

        private static async Task OnPositionChanged(PositionChangedEventArgs e)
        {
            var message = $"Position changed: {e.Symbol} = {e.NewQuantity} (was {e.OldQuantity})";
            _logger.Info(message);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
            
            await Task.Delay(_config.PositionChangeDebounceMs, _cancellationTokenSource.Token);
            
            await SyncSpecificPosition(e.Symbol, 0);
        }

        private static async Task OnTradeExecuted(TradeExecutedEventArgs e)
        {
            var message = $"Trade executed: {e.Action} {e.Quantity} {e.Symbol} @ {e.Price:F2}";
            _logger.Info(message);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
            
            await Task.Delay(_config.TradeExecutionDelayMs, _cancellationTokenSource.Token);

            // Fetch fresh position from cache after delay to ensure we have the latest update
            // We pass 0 as dummy because SyncSpecificPosition now fetches fresh data internally
            await SyncSpecificPosition(e.Symbol, 0); 
        }

        private static SemaphoreSlim GetSymbolLock(string symbol)
        {
            return _symbolSemaphores.GetOrAdd(symbol, _ => new SemaphoreSlim(1, 1));
        }

        private static async Task BackupSync()
        {
            if (_isInMaintenanceMode) return;

            if (IsWeekendMarketClosed())
            {
                // Log only once per hour to avoid spam? Or just Debug.
                // For now, Info is fine as it's a distinct event "skipping sync".
                _logger.Info("Skipping backup sync: Markets closed (Weekend Fri 18:00 - Sun 17:00 EST).");
                return;
            }

            if (_cancellationTokenSource.Token.IsCancellationRequested)
                return;

            _logger.Info("Running scheduled backup sync");
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Running backup sync...");
            
            try
            {
                await SyncPortfolio();
            }
            catch (Exception ex)
            {
                _logger.Error($"Backup sync failed: {ex.Message}", ex);
            }
        }

        private static async Task SyncSpecificPosition(string symbol, double dummyQuantity)
        {
            if (IsWeekendMarketClosed())
            {
                 _logger.Debug($"Skipping position update for {symbol}: Markets closed.");
                 return;
            }

            var positionLock = GetSymbolLock(symbol);
            await positionLock.WaitAsync(_cancellationTokenSource.Token);
            try
            {
                _logger.Debug($"Starting sync for specific position: {symbol}");

                // CRITICAL FIX: Always fetch the latest cached position from IB client inside the lock.
                // This prevents using stale data if multiple events were queued.
                var newQuantity = _ibClient.GetCachedPosition(symbol);
                
                var c2Positions = await _c2Client.GetPositionsWithRetryAsync(_cancellationTokenSource.Token);
                var c2Position = c2Positions?.FirstOrDefault(p => p.Symbol == symbol);
                var c2Quantity = c2Position?.Quantity ?? 0;

                var quantityDiff = newQuantity - c2Quantity;

                _logger.Debug($"Position comparison for {symbol}: IB={newQuantity}, C2={c2Quantity}, Diff={quantityDiff}");

                if (Math.Abs(quantityDiff) > _config.MinimumQuantityThreshold)
                {
                    var syncMessage = $"Syncing {symbol}: IB={newQuantity}, C2={c2Quantity}, Diff={quantityDiff}";
                    _logger.Info(syncMessage);
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {syncMessage}");
                    
                    await ExecutePositionSync(symbol, newQuantity, c2Quantity, quantityDiff);
                }
                else
                {
                    var inSyncMessage = $"Position {symbol} already in sync: {newQuantity}";
                    _logger.Debug(inSyncMessage);
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {inSyncMessage}");
                }
            }
            catch (OperationCanceledException)
            {
                _logger.Info($"Position sync cancelled for {symbol}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Error syncing position {symbol}: {ex.Message}", ex);
                Console.WriteLine($"Error syncing position {symbol}: {ex.Message}");
            }
            finally
            {
                positionLock.Release();
            }
        }

        private static async Task ExecutePositionSync(string symbol, double ibQuantity, double c2Quantity, double quantityDiff)
        {
            if (Math.Abs(ibQuantity) < _config.MinimumQuantityThreshold)
            {
                if (Math.Abs(c2Quantity) > _config.MinimumQuantityThreshold)
                {
                    // If C2 is Short (negative), Buy to Close (BTC). If Long (positive), Sell to Close (STC).
                    string action = c2Quantity < 0 ? "BTC" : "STC";
                    _logger.Info($"Closing C2 position: {action} {Math.Abs(c2Quantity)} {symbol}");
                    await _c2Client.SubmitSignalWithRetryAsync(symbol, action, Math.Abs(c2Quantity), cancellationToken: _cancellationTokenSource.Token);
                }
            }
            else if (quantityDiff > 0)
            {
                // Buying
                // If C2 is Short (<0), Buy to Close (BTC).
                // If C2 is Flat or Long (>=0), Buy to Open (BTO).
                string action = c2Quantity < 0 ? "BTC" : "BTO";
                _logger.Info($"Increasing C2 position: {action} {Math.Abs(quantityDiff)} {symbol}");
                await _c2Client.SubmitSignalWithRetryAsync(symbol, action, Math.Abs(quantityDiff), cancellationToken: _cancellationTokenSource.Token);
            }
            else
            {
                // Selling
                // If C2 is Long (>0), Sell to Close (STC).
                // If C2 is Flat or Short (<=0), Sell to Open (STO).
                string action = c2Quantity > 0 ? "STC" : "STO";
                string logAction = c2Quantity > 0 ? "Reducing C2 Long" : "Increasing C2 Short";

                _logger.Info($"{logAction}: {action} {Math.Abs(quantityDiff)} {symbol}");
                await _c2Client.SubmitSignalWithRetryAsync(symbol, action, Math.Abs(quantityDiff), cancellationToken: _cancellationTokenSource.Token);
            }

            // Post-Trade Verification Delay
            if (_config.PostTradeCheckDelaySeconds > 0)
            {
                _logger.Info($"Waiting {_config.PostTradeCheckDelaySeconds} seconds for C2 execution verification on {symbol}...");
                await Task.Delay(_config.PostTradeCheckDelaySeconds * 1000, _cancellationTokenSource.Token);

                // Re-fetch C2 positions to verify Sync
                var freshC2Positions = await _c2Client.GetPositionsWithRetryAsync(_cancellationTokenSource.Token);
                if (freshC2Positions != null)
                {
                    var freshC2Qty = freshC2Positions.FirstOrDefault(p => p.Symbol == symbol)?.Quantity ?? 0;
                    if (Math.Abs(ibQuantity - freshC2Qty) < _config.MinimumQuantityThreshold)
                    {
                         _logger.Info($"Verification Successful: {symbol} is in sync (IB={ibQuantity}, C2={freshC2Qty}).");
                    }
                    else
                    {
                         _logger.Warn($"Verification Warning: {symbol} execution might be pending or failed. IB={ibQuantity}, C2={freshC2Qty}.");
                    }
                }
            }
        }

        private static async Task SyncPortfolio()
        {
            if (_isInMaintenanceMode) return;

            if (IsWeekendMarketClosed())
            {
                _logger.Info("Skipping portfolio sync: Markets closed (Weekend Fri 18:00 - Sun 17:00 EST).");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Market closed (Weekend). Sync skipped.");
                return;
            }
            
            // NOTE: We do NOT take a global lock here anymore.
            // We allow the sync to inspect all positions and then lock per-symbol if action is needed.
            
            try
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Starting full portfolio sync...");

                var ibPositionsTask = _ibClient.GetPositionsAsync();
                var c2PositionsTask = _c2Client.GetPositionsWithRetryAsync(_cancellationTokenSource.Token);

                await Task.WhenAll(ibPositionsTask, c2PositionsTask);

                var ibPositions = await ibPositionsTask;
                var c2Positions = await c2PositionsTask;

                if (ibPositions == null || c2Positions == null)
                {
                    _logger.Error($"Aborting sync: Failed to retrieve positions. IB success: {ibPositions != null}, C2 success: {c2Positions != null}");
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Sync aborted due to connection/API failure.");
                    return;
                }

                await SyncPositions(ibPositions, c2Positions);

                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Full sync completed.");
            }
            catch (OperationCanceledException)
            {
                _logger.Info("Portfolio sync cancelled");
            }
            catch (Exception ex)
            {
                _logger.Error($"Portfolio sync error: {ex.Message}", ex);
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Sync error: {ex.Message}");
            }
        }

        private static async Task SyncPositions(List<Position> ibPositions, List<Position> c2Positions)
        {
            var c2PositionDict = c2Positions.ToDictionary(p => p.Symbol, p => p);
            _logger.Debug($"Starting position comparison: {ibPositions.Count} IB positions vs {c2Positions.Count} C2 positions");

            var symbolsToSync = new HashSet<string>();

            // 1. Identify IB positions that differ from C2
            foreach (var ibPosition in ibPositions.Where(p => Math.Abs(p.Quantity) >= _config.MinimumQuantityThreshold))
            {
                var c2Quantity = c2PositionDict.GetValueOrDefault(ibPosition.Symbol)?.Quantity ?? 0;
                var quantityDiff = ibPosition.Quantity - c2Quantity;

                if (Math.Abs(quantityDiff) > _config.MinimumQuantityThreshold)
                {
                    symbolsToSync.Add(ibPosition.Symbol);
                }

                c2PositionDict.Remove(ibPosition.Symbol);
            }

            // 2. Identify Orphans (C2 has position, IB does not/is zero)
            foreach (var c2Position in c2PositionDict.Values.Where(p => Math.Abs(p.Quantity) >= _config.MinimumQuantityThreshold))
            {
                symbolsToSync.Add(c2Position.Symbol);
            }

            // 3. Delegate to robust sync
            foreach (var symbol in symbolsToSync)
            {
                // We pass 0 because SyncSpecificPosition will re-fetch the authoritative IB position inside the lock
                await SyncSpecificPosition(symbol, 0);
            }
        }

        private static bool IsWeekendMarketClosed()
        {
            try
            {
                var easternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                var easternTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, easternZone);
                
                // Weekend Close Window: Friday 18:00 (6 PM) to Sunday 17:00 (5 PM)
                
                if (easternTime.DayOfWeek == DayOfWeek.Friday && easternTime.Hour >= 18)
                    return true;
                
                if (easternTime.DayOfWeek == DayOfWeek.Saturday)
                    return true;
                
                if (easternTime.DayOfWeek == DayOfWeek.Sunday && easternTime.Hour < 17)
                    return true;

                return false;
            }
            catch (Exception ex)
            {
                _logger.Error($"Error checking weekend market close: {ex.Message}");
                // Default to Open (safe fail) or Closed? 
                // Failing Open allows sync, but might error if exchange is actually closed.
                // Given "don't sync", maybe fail closed? But "Futures" implies standard market hours.
                // Let's return false (allow sync) so we don't block valid times on TZ error.
                return false;
            }
        }
    }

    public class Configuration
    {
        public string C2ApiKey { get; set; } = "YOUR_C2_API_KEY";
        public string C2StrategyId { get; set; } = "YOUR_STRATEGY_ID";
        public string IbHost { get; set; } = "127.0.0.1";
        public int IbPort { get; set; } = 7497;
        public int IbClientId { get; set; } = 987;
        public int BackupSyncIntervalMinutes { get; set; } = 5;
        public int PositionChangeDebounceMs { get; set; } = 500;
        public int TradeExecutionDelayMs { get; set; } = 1000;
        public double MinimumQuantityThreshold { get; set; } = 0.01;
        public int SignalSubmissionDelayMs { get; set; } = 100;
        public int PostTradeCheckDelaySeconds { get; set; } = 60;
        public int HttpTimeoutSeconds { get; set; } = 30;
        public int MaxRetryAttempts { get; set; } = 3;

        public Dictionary<string, string> SymbolMappings { get; set; } = new Dictionary<string, string>();

        public string GetC2Symbol(Contract contract)
        {
            if (contract.SecType == "FUT")
            {
                // Check for configurable mapping first (based on root symbol)
                // Assuming contract.Symbol is the root (e.g. MGC, ES)
                if (SymbolMappings.TryGetValue(contract.Symbol, out var mappedRoot))
                {
                   // Try to reconstruct symbol with mapped root
                   // IB LocalSymbol: MGCG6
                   // We want to replace MGC with QMGC (if mapped)
                   // Or simply prefix if that is the strategy.
                   
                   // Robust approach: If LocalSymbol starts with IB root, replace it with C2 root.
                   if (!string.IsNullOrEmpty(contract.LocalSymbol) && contract.LocalSymbol.StartsWith(contract.Symbol))
                   {
                       return mappedRoot + contract.LocalSymbol.Substring(contract.Symbol.Length);
                   }
                   
                   // Fallback logic if LocalSymbol doesn't match expected pattern
                   return mappedRoot + contract.LastTradeDateOrContractMonth;
                }

                // Default logic if no mapping found
                var symbol = !string.IsNullOrEmpty(contract.LocalSymbol) 
                    ? contract.LocalSymbol 
                    : $"{contract.Symbol}{contract.LastTradeDateOrContractMonth}"; 

                // Special Mappings (Hardcoded legacy fallback or remove if fully config driven)
                // Special Mappings (Hardcoded legacy fallback or remove if fully config driven)
                // Keeping Micro Gold hardcode just in case config is missing, but config takes precedence above.
                if (symbol.StartsWith("MGC"))
                {
                    symbol = "QMGC" + symbol.Substring(3);
                }

                // FIX: Ensure 2-digit years for C2 compatibility (e.g., MESH6 -> MESH26)
                // Matches a letter followed by a single digit at the end of the string.
                // Replace with Letter + "2" + Digit.
                // usage of ${1} creates unambiguous reference to group 1
                // DISABLED: This logic breaks MBT (Micro Bitcoin) which expects @MBTG6 (1-digit year)
                // symbol = Regex.Replace(symbol, @"([A-Z])([0-9])$", "${1}2$2");

                if (!symbol.StartsWith("@"))
                    return "@" + symbol;
                
                return symbol;
            }
            
            return contract.Symbol;
        }

        public static Configuration Load()
        {
            var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            
            if (File.Exists(configPath))
            {
                try
                {
                    var json = File.ReadAllText(configPath);
                    return JsonSerializer.Deserialize<Configuration>(json, new JsonSerializerOptions 
                    { 
                        PropertyNameCaseInsensitive = true 
                    }) ?? new Configuration();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: Could not load config file: {ex.Message}. Using defaults.");
                }
            }
            
            return new Configuration();
        }

        public void Save()
        {
            var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
            File.WriteAllText(configPath, json);
        }
    }

    public class Position
    {
        public string Symbol { get; set; } = string.Empty;
        public double Quantity { get; set; }
        public double AvgCost { get; set; }
        public string SecType { get; set; } = string.Empty;
        public DateTime LastUpdated { get; set; } = DateTime.Now;
    }

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
                            OnPositionChanged?.Invoke(new PositionChangedEventArgs
                            {
                                Symbol = symbol,
                                OldQuantity = removedPos.Quantity,
                                NewQuantity = 0,
                                Timestamp = DateTime.Now
                            });
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
                OnPositionChanged?.Invoke(new PositionChangedEventArgs
                {
                    Symbol = c2Symbol,
                    OldQuantity = oldPosition,
                    NewQuantity = position
                });
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
            
            OnTradeExecuted?.Invoke(new TradeExecutedEventArgs
            {
                Symbol = c2Symbol,
                Action = execution.Side,
                Quantity = execution.Shares,
                Price = execution.Price,
                NewPosition = currentPosition
            });
        }



        internal void OnConnectionClosed()
        {
            _isConnected = false;
            _logger.Error("IB connection closed");
            OnConnectionLost?.Invoke();
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

    public class Collective2Client
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly string _strategyId;
        private readonly FileLogger _logger;
        private const string BaseUrl = "https://api4-general.collective2.com";
        private readonly IAsyncPolicy<HttpResponseMessage> _retryPolicy;
        private readonly IAsyncPolicy<HttpResponseMessage> _circuitBreakerPolicy;
        private readonly IAsyncPolicy<HttpResponseMessage> _resilientPolicy;
        private readonly SemaphoreSlim _rateLimiter;
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions 
        { 
            PropertyNameCaseInsensitive = true 
        };

        public Collective2Client(string apiKey, string strategyId, FileLogger logger, int maxRetryAttempts = 3)
        {
            _apiKey = apiKey;
            _strategyId = strategyId;
            _logger = logger;
            
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            
            _retryPolicy = Policy
                .HandleResult<HttpResponseMessage>(r => !r.IsSuccessStatusCode && r.StatusCode != System.Net.HttpStatusCode.BadRequest)
                .Or<TaskCanceledException>()
                .Or<HttpRequestException>()
                .WaitAndRetryAsync(
                    maxRetryAttempts,
                    retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                    onRetry: (outcome, timespan, retryCount, context) =>
                    {
                        var reason = outcome.Result?.StatusCode.ToString() ?? outcome.Exception?.Message ?? "Unknown";
                        _logger.Warn($"C2 API retry {retryCount} after {timespan.TotalSeconds}s. Reason: {reason}");
                    });

            _circuitBreakerPolicy = Policy
                .HandleResult<HttpResponseMessage>(r => !r.IsSuccessStatusCode && r.StatusCode != System.Net.HttpStatusCode.BadRequest)
                .Or<TaskCanceledException>()
                .Or<HttpRequestException>()
                .CircuitBreakerAsync(
                    handledEventsAllowedBeforeBreaking: 5,
                    durationOfBreak: TimeSpan.FromMinutes(1),
                    onBreak: (outcome, breakDelay) =>
                    {
                        _logger.Error($"C2 API circuit breaker OPEN for {breakDelay.TotalSeconds}s. Last reason: {outcome.Result?.StatusCode.ToString() ?? outcome.Exception?.Message}");
                    },
                    onReset: () => _logger.Info("C2 API circuit breaker CLOSED - resuming requests"),
                    onHalfOpen: () => _logger.Info("C2 API circuit breaker HALF-OPEN - testing connection"));

            // Wrap: retry sits inside the circuit breaker
            _resilientPolicy = Policy.WrapAsync(_circuitBreakerPolicy, _retryPolicy);

            _rateLimiter = new SemaphoreSlim(10, 10);

            _logger.Info($"Collective2Client initialized for strategy: {strategyId}");
        }

        public async Task<List<Position>?> GetPositionsWithRetryAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await _rateLimiter.WaitAsync(cancellationToken);

                _logger.Debug("Requesting positions from Collective2 V4 API");
                // V4 Endpoint: /Strategies/GetStrategyOpenPositions
                // Parameter: StrategyIds (plural, array)
                var url = $"{BaseUrl}/Strategies/GetStrategyOpenPositions?StrategyIds={_strategyId}";

                var response = await _resilientPolicy.ExecuteAsync(async ct =>
                    await _httpClient.GetAsync(url, ct), cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    var data = JsonSerializer.Deserialize<C2PositionsResponse>(responseContent, _jsonOptions);

                    var positions = data?.Results?.Select(p => new Position
                    {
                        Symbol = p.C2Symbol?.FullSymbol ?? "Unknown",
                        Quantity = p.Quantity,
                        SecType = p.C2Symbol?.SymbolType ?? "Unknown",
                        LastUpdated = DateTime.Now
                    }).ToList() ?? new List<Position>();

                    _logger.Debug($"Retrieved {positions.Count} positions from Collective2");
                    return positions;
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.Error($"Failed to get C2 positions. Status: {response.StatusCode}, Content: {errorContent}");
                    return null;
                }
            }
            catch (BrokenCircuitException)
            {
                _logger.Error("C2 API circuit breaker is open. Returning null.");
                return null;
            }
            catch (Exception ex)
            {
                _logger.Error($"Error getting C2 positions: {ex.Message}", ex);
                return null;
            }
            finally
            {
                _ = Task.Delay(100).ContinueWith(_ => _rateLimiter.Release());
            }
        }

        public async Task<bool> SubmitSignalWithRetryAsync(string symbol, string action, double quantity, string secType = "FUT", CancellationToken cancellationToken = default)
        {
            try
            {
                await _rateLimiter.WaitAsync(cancellationToken);

                _logger.Info($"Submitting C2 signal: {action} {quantity} {symbol}");

                // Mapping to C2 V4 Enum values:
                // Side: 1=Buy, 2=Sell
                // OrderType: 1=Market
                // Correct logic for BTO/BTC/BUY -> 1, STO/STC/SELL -> 2
                var actionUpper = action.ToUpper();
                var sideStr = (actionUpper.Contains("BUY") || actionUpper.StartsWith("B")) ? "1" : "2";

                var requestBody = new
                {
                    order = new
                    {
                        strategyId = long.Parse(_strategyId),
                        side = sideStr,
                        orderQuantity = quantity,
                        orderType = "1", // Market
                        tif = "0", // Day
                        c2Symbol = new
                        {
                            fullSymbol = symbol,
                            symbolType = "future" // Assuming futures based on context
                        }
                    }
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                _logger.Debug($"C2 Signal payload: {json}");

                var response = await _resilientPolicy.ExecuteAsync(async ct =>
                    await _httpClient.PostAsync($"{BaseUrl}/Strategies/NewStrategyOrder", content, ct), cancellationToken);

                var responseText = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    _logger.Info($"C2 Signal submitted successfully: {action} {quantity} {symbol}");
                    Console.WriteLine($"C2 Signal submitted: {action} {quantity} {symbol}");
                    return true;
                }
                else
                {
                    _logger.Error($"C2 Signal failed - Status: {response.StatusCode}, Response: {responseText}");
                    Console.WriteLine($"C2 Signal failed: {responseText}");
                    return false;
                }
            }
            catch (BrokenCircuitException)
            {
                _logger.Error("C2 API circuit breaker is open. Signal not submitted.");
                return false;
            }
            catch (Exception ex)
            {
                _logger.Error($"Error submitting C2 signal: {ex.Message}", ex);
                return false;
            }
            finally
            {
                _ = Task.Delay(100).ContinueWith(_ => _rateLimiter.Release());
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
            _rateLimiter?.Dispose();
        }
    }

    public class C2PositionsResponse
    {
        [JsonPropertyName("results")]
        public List<C2PositionDTO> Results { get; set; }
    }

    public class C2PositionDTO
    {
        [JsonPropertyName("quantity")]
        public double Quantity { get; set; }

        [JsonPropertyName("c2Symbol")]
        public C2SymbolDTO C2Symbol { get; set; }
    }

    public class C2SymbolDTO
    {
        [JsonPropertyName("fullSymbol")]
        public string FullSymbol { get; set; }

        [JsonPropertyName("symbolType")]
        public string SymbolType { get; set; }
    }

    public class PositionChangedEventArgs : EventArgs
    {
        public string Symbol { get; set; } = string.Empty;
        public double OldQuantity { get; set; }
        public double NewQuantity { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    public class TradeExecutedEventArgs : EventArgs
    {
        public string Symbol { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public double Quantity { get; set; }
        public double Price { get; set; }
        public double NewPosition { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    public class FileLogger : IDisposable
    {
        private readonly string _logDirectory;
        private readonly string _applicationName;
        private readonly object _logLock = new object();
        private readonly Timer _flushTimer;
        private StreamWriter _logWriter;
        private string _currentLogFile;
        private bool _disposed = false;

        public string LogFilePath => _currentLogFile;

        public FileLogger(string applicationName)
        {
            _applicationName = applicationName;
            _logDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Logs");
            Directory.CreateDirectory(_logDirectory);
            
            UpdateLogFilePath();
            CleanupOldLogs();
            
            _flushTimer = new Timer(_ => FlushBuffer(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            
            WriteLog("INFO", "=== Logger initialized ===");
        }

        private void UpdateLogFilePath()
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var newLogFile = Path.Combine(_logDirectory, $"{_applicationName}_{today}.log");

            if (_currentLogFile != newLogFile)
            {
                lock (_logLock)
                {
                    try 
                    {
                        _logWriter?.Flush();
                        _logWriter?.Dispose();
                        _currentLogFile = newLogFile;
                        _logWriter = new StreamWriter(new FileStream(_currentLogFile, FileMode.Append, FileAccess.Write, FileShare.Read))
                        {
                            AutoFlush = true
                        };
                    }
                    catch (IOException ex)
                    {
                        Console.WriteLine($"LOGGER ERROR: Could not open log file {newLogFile}: {ex.Message}");
                        // Fallback to a temp file or just ignore - validation can rely on Console.
                         _currentLogFile = Path.Combine(Path.GetTempPath(), $"{_applicationName}_{Guid.NewGuid()}.log");
                         try {
                            _logWriter = new StreamWriter(new FileStream(_currentLogFile, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
                         } catch { /* Give up on file logging */ }
                    }
                }
            }
        }

        private void CleanupOldLogs()
        {
            try
            {
                var cutoffDate = DateTime.Now.AddDays(-30);
                var files = Directory.GetFiles(_logDirectory, "*.log");
                
                foreach (var file in files)
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        if (fileInfo.CreationTime < cutoffDate)
                        {
                            File.Delete(file);
                        }
                    }
                    catch
                    {
                        // Ignore individual file deletion errors
                    }
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        private void WriteLog(string level, string message, Exception exception = null)
        {
            if (_disposed) return;

            try
            {
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                var logEntry = $"[{timestamp}] [{level.PadRight(5)}] [{Thread.CurrentThread.ManagedThreadId:D3}] {message}";
                
                if (exception != null)
                {
                    logEntry += $"\nException: {exception}";
                }
                
                lock (_logLock)
                {
                    // Check if day changed
                    if (DateTime.Now.Date != new FileInfo(_currentLogFile).CreationTime.Date) 
                    {
                         UpdateLogFilePath();
                    }

                    _logWriter.WriteLine(logEntry);
                }
            }
            catch
            {
                // Fail silently
            }
        }

        private void FlushBuffer()
        {
            if (_disposed) return;

            lock (_logLock)
            {
                try
                {
                     // Writer is AutoFlush=true, but we can force it or handle rotation checks here if needed.
                     // For now, just ensuring file path is correct is handled in WriteLog or here.
                     // Let's rely on WriteLog for rotation check to be more real-time, 
                     // or do it here to avoid checking every write.
                     
                     var today = DateTime.Now.ToString("yyyy-MM-dd");
                     if (!_currentLogFile.Contains(today))
                     {
                         UpdateLogFilePath();
                     }
                }
                catch
                {
                    // Ignore
                }
            }
        }

        public void Debug(string message) => WriteLog("DEBUG", message);
        public void Info(string message) => WriteLog("INFO", message);
        public void Warn(string message) => WriteLog("WARN", message);
        public void Error(string message, Exception exception = null) => WriteLog("ERROR", message, exception);

        public void Dispose()
        {
            if (_disposed) return;
            
            _disposed = true;
            _flushTimer?.Dispose();
            
            lock (_logLock)
            {
                _logWriter?.Flush();
                _logWriter?.Dispose();
            }
        }
    }
}