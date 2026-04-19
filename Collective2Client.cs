using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Polly;
using Polly.CircuitBreaker;

namespace IBCollective2Sync
{
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

                var c2SymbolType = secType switch
                {
                    "STK" => "stock",
                    "OPT" => "option",
                    "FUT" => "future",
                    "CASH" => "forex",
                    _ => "future"
                };

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
                            symbolType = c2SymbolType
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
}
