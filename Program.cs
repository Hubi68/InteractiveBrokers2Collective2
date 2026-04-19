using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Polly;

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
                var cachedPosition = _ibClient.GetCachedPositionFull(symbol);
                var newQuantity = cachedPosition?.Quantity ?? 0;
                var secType = cachedPosition?.SecType ?? "FUT";

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

                    await ExecutePositionSync(symbol, newQuantity, c2Quantity, secType);
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

        private static async Task ExecutePositionSync(string symbol, double ibQuantity, double c2Quantity, string secType)
        {
            var decision = SyncLogic.DetermineAction(ibQuantity, c2Quantity, _config.MinimumQuantityThreshold);
            if (decision == null)
            {
                _logger?.Debug($"No action needed for {symbol}: IB={ibQuantity}, C2={c2Quantity}");
                return;
            }

            var (action, quantity) = decision.Value;
            _logger?.Info($"Submitting C2 signal for {symbol}: {action} {quantity} (IB={ibQuantity}, C2={c2Quantity})");
            await _c2Client.SubmitSignalWithRetryAsync(symbol, action, quantity, secType, _cancellationTokenSource.Token);

            if (_config.PostTradeCheckDelaySeconds > 0)
            {
                _logger?.Info($"Waiting {_config.PostTradeCheckDelaySeconds}s for C2 execution verification on {symbol}...");
                await Task.Delay(_config.PostTradeCheckDelaySeconds * 1000, _cancellationTokenSource.Token);

                var freshC2Positions = await _c2Client.GetPositionsWithRetryAsync(_cancellationTokenSource.Token);
                if (freshC2Positions != null)
                {
                    var freshC2Qty = freshC2Positions.FirstOrDefault(p => p.Symbol == symbol)?.Quantity ?? 0;
                    if (Math.Abs(ibQuantity - freshC2Qty) < _config.MinimumQuantityThreshold)
                        _logger?.Info($"Verification Successful: {symbol} in sync (IB={ibQuantity}, C2={freshC2Qty}).");
                    else
                        _logger?.Warn($"Verification Warning: {symbol} may be pending. IB={ibQuantity}, C2={freshC2Qty}.");
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
}
