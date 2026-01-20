# IB-Collective2 Portfolio Sync Architecture

## Executive Summary

This application provides real-time, event-driven synchronization between Interactive Brokers (IB) trading accounts and Collective2 strategy portfolios. It monitors position changes in IB and automatically replicates them to Collective2 by submitting appropriate trading signals.

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────┐
│                        Main Program                          │
│  - Orchestrates lifecycle                                    │
│  - Manages event subscriptions                               │
│  - Coordinates sync operations                               │
└──────────────────┬──────────────────────┬────────────────────┘
                   │                      │
       ┌───────────▼──────────┐  ┌────────▼─────────────┐
       │     IBClient         │  │  Collective2Client   │
       │  - IB TWS/Gateway    │  │  - REST API client   │
       │  - Position tracking │  │  - Signal submission │
       │  - Event emission    │  │  - Retry logic       │
       └──────────┬───────────┘  └──────────────────────┘
                  │
       ┌──────────▼──────────┐
       │     IBWrapper        │
       │  - IB API callbacks  │
       │  - Event translation │
       └─────────────────────┘
```

## Core Components

### 1. Program (Main Orchestrator)

**Responsibilities:**
- Application lifecycle management
- Connection initialization and retry logic
- Event subscription and routing
- Synchronization coordination
- Graceful shutdown handling

**Key Features:**
- Exponential backoff retry for IB connection
- Semaphore-based sync operation serialization
- Periodic backup sync as failsafe
- Cancellation token support for clean shutdown

### 2. IBClient

**Purpose:** Interface with Interactive Brokers TWS or Gateway

**Key Capabilities:**
- Asynchronous connection management with timeout handling
- Real-time position monitoring via IB API subscriptions
- Execution report tracking
- Portfolio update streaming
- Connection loss detection and notification

**Event Model:**
- `OnPositionChanged`: Fires when position quantities change
- `OnTradeExecuted`: Fires when trades are executed
- `OnConnectionLost`: Fires on connection disruption

**Threading:**
- Uses dedicated reader thread for IB message processing
- Thread-safe position dictionary (ConcurrentDictionary)
- Connection semaphore prevents concurrent connection attempts

### 3. IBWrapper

**Purpose:** Implements IB API's EWrapper interface

**Functionality:**
- Translates IB callbacks into domain events
- Filters relevant position and execution updates
- Handles error codes and connection status
- Delegates to IBClient for business logic

**Error Handling:**
- Distinguishes between informational messages (codes 2000-9999) and errors
- Detects critical connection errors (1100, 1102, 2110)

### 4. Collective2Client

**Purpose:** Interface with Collective2 REST API

**Key Features:**
- HTTP-based REST API communication
- Polly-based retry policies with exponential backoff
- Circuit breaker pattern (opens after 5 failures, 1 minute duration)
- Rate limiting (10 concurrent requests, 100ms spacing)
- Automatic signal submission with market orders

**API Operations:**
- `GetPositionsWithRetryAsync()`: Retrieves current C2 positions
- `SubmitSignalWithRetryAsync()`: Submits BTO/STC signals

**Resilience:**
- Handles transient failures gracefully
- Circuit breaker prevents cascade failures
- Rate limiter prevents API throttling

### 5. Configuration

**Purpose:** Centralized configuration management

**Configurable Parameters:**
- IB connection settings (host, port)
- Collective2 credentials (API key, strategy ID)
- Sync intervals and delays
- Thresholds and timeouts
- Retry policies

**Features:**
- JSON-based configuration file support
- Environment variable fallback
- Runtime save capability
- Sensible defaults

### 6. FileLogger

**Purpose:** High-performance file-based logging

**Key Features:**
- Buffered writes (flushes every 5 seconds or 10KB)
- Daily log file rotation
- Automatic cleanup (30-day retention)
- Thread-safe operations
- Structured log format with timestamps, levels, and thread IDs
- Logs specific symbol translations for debugging (e.g., "MGC -> @QMGC")

**Log Levels:**
- DEBUG: Detailed diagnostic information
- INFO: General informational messages
- WARN: Warning conditions
- ERROR: Error conditions with stack traces

## Synchronization Strategy

### Event-Driven Architecture

The application uses an event-driven model for optimal responsiveness:

1. **Position Change Detection**
   - IB sends position updates via `updatePortfolio` callbacks
   - IBClient compares old vs. new position quantities
   - Changes trigger `OnPositionChanged` event
   - Debounced to prevent duplicate syncs (default: 500ms)

2. **Trade Execution Tracking**
   - IB sends execution reports via `execDetails` callbacks
   - System waits for position update (default: 1000ms delay)
   - Triggers targeted position sync

3. **Backup Periodic Sync**
   - Timer-based full portfolio comparison (default: 5 minutes)
   - Catches any missed events
   - Identifies orphaned C2 positions

### Sync Process Flow

```
Event Trigger (Position Change or Trade Execution)
    ↓
Debounce Delay (500ms for position, 1000ms for trade)
    ↓
Acquire Sync Semaphore (prevent concurrent syncs)
    ↓
Fetch Current C2 Position for Symbol
    ↓
Calculate Quantity Difference (IB - C2)
    ↓
Check if Difference > Threshold (0.01)
    ↓
Execute Position Sync:
    - If IB = 0: Submit STC to close C2
    - If Diff > 0: Submit BTO to increase C2
    - If Diff < 0: Submit STC to decrease C2
    ↓
Rate-Limited Signal Submission (100ms delay)
    ↓
Release Sync Semaphore
```

### Full Portfolio Sync

Periodic backup sync compares all positions:

```
Acquire Sync Semaphore
    ↓
Parallel Fetch: IB Positions + C2 Positions
    ↓
For Each IB Position:
    - Compare with C2 position
    - Queue signals for mismatches
    ↓
For Each C2-Only Position:
    - Queue STC signals (orphaned positions)
    ↓
Submit All Queued Signals with Rate Limiting
    ↓
Release Sync Semaphore
```

## Optimization Highlights

### Performance Improvements

1. **Async/Await Throughout**
   - All I/O operations are asynchronous
   - Prevents thread pool starvation
   - Improves scalability

2. **Efficient Position Tracking**
   - ConcurrentDictionary for thread-safe access
   - In-memory caching avoids redundant API calls
   - Parallel fetching of IB and C2 positions

3. **Buffered Logging**
   - Persistent `StreamWriter` avoids repeated file open/close operations
   - Buffered writes (flushes every 5 seconds or 10KB)
   - Automatic flushing based on size and time
   - Minimal performance impact with `DateTime.UtcNow` optimization

4. **Smart Event Handling**
   - Debouncing prevents redundant syncs
   - Targeted position updates vs. full syncs
   - Filters irrelevant position updates (< threshold)
   - Minimized IB API subscriptions (removed unused account updates)

5. **Memory Optimization**
   - Static JSON serialization options to reduce allocation overhead
   - Efficient object reuse in critical paths

### Reliability Enhancements

1. **Retry Policies**
   - Exponential backoff for IB connection
   - Polly-based retries for C2 API calls
   - Circuit breaker prevents cascade failures

2. **Connection Resilience**
   - Automatic reconnection on IB disconnects
   - Connection state tracking
   - Timeout protection on blocking operations

3. **Rate Limiting**
   - Prevents C2 API throttling
   - Semaphore-based concurrent request control
   - Configurable delays between signals

4. **Error Handling**
   - Try-catch blocks around critical operations
   - Graceful degradation on failures
   - Comprehensive error logging

5. **Resource Management**
   - Proper disposal of resources
   - Cancellation token support
   - Clean shutdown procedures

### Code Quality Improvements

1. **Separation of Concerns**
   - Clear component boundaries
   - Single responsibility principle
   - Minimal coupling between components

2. **Dependency Injection Ready**
   - Logger passed as constructor parameter
   - Configuration externalized
   - Easy to unit test

3. **LINQ Optimizations**
   - Efficient filtering with Where clauses
   - Avoid multiple enumerations
   - Dictionary lookups vs. repeated searches

4. **Modern C# Features**
   - Null-coalescing operators
   - Pattern matching
   - ValueTuple returns
   - Async event handlers (Func<Task>)

### 7. Symbol Translation

**Purpose:** Ensure correct symbol formats between IB and Collective2.

**Logic:**
- **Futures (@ Prefix):** Automatically adds `@` prefix to Collective2 futures symbols (e.g., `MES` -> `@MES`).
- **Local Symbol Usage:** Uses IB's `LocalSymbol` (which includes month/year codes) instead of the generic root symbol for futures.
- **Micro Gold Mapping:** Specifically maps IB's `MGC` to Collective2's `QMGC`.
- **Other Micro Contracts:** Handles standard mappings like `@MNQ`, `@MES`, `@MBT` automatically.

## Configuration Guide

### appsettings.json Example

```json
{
  "C2ApiKey": "YOUR_API_KEY",
  "C2StrategyId": "YOUR_STRATEGY_ID",
  "IbHost": "127.0.0.1",
  "IbPort": 7497,
  "BackupSyncIntervalMinutes": 5,
  "PositionChangeDebounceMs": 500,
  "TradeExecutionDelayMs": 1000,
  "MinimumQuantityThreshold": 0.01,
  "SignalSubmissionDelayMs": 100,
  "HttpTimeoutSeconds": 30,
  "MaxRetryAttempts": 3
}
```

### Key Parameters

| Parameter | Default | Description |
|-----------|---------|-------------|
| `BackupSyncIntervalMinutes` | 5 | Full portfolio sync frequency |
| `PositionChangeDebounceMs` | 500 | Delay after position change before syncing |
| `TradeExecutionDelayMs` | 1000 | Delay after trade before syncing |
| `MinimumQuantityThreshold` | 0.01 | Ignore position differences below this |
| `SignalSubmissionDelayMs` | 100 | Rate limiting between C2 signals |
| `HttpTimeoutSeconds` | 30 | HTTP request timeout |
| `MaxRetryAttempts` | 3 | Max retries for failed operations |

## Deployment Considerations

### Prerequisites

1. **Interactive Brokers Setup**
   - TWS or IB Gateway running
   - API connections enabled
   - Socket port configured (default: 7497 for TWS, 4001 for Gateway)
   - Read-only API permissions (if not placing orders)

2. **Collective2 Account**
   - Valid API key
   - Strategy ID for target strategy
   - API access enabled

3. **.NET Requirements**
   - .NET 6.0 or later
   - IB TWS API DLL (IBApi)
   - Polly NuGet package
   - System.Text.Json

### Running the Application

```bash
# Development
dotnet run

# Production (compiled)
dotnet IBCollective2Sync.dll

# Press 'q' to quit gracefully
```

### Monitoring

**Console Output:**
- Real-time position changes
- Trade executions
- Sync operations
- Error messages

**Log Files:**
- Location: `Documents/IBCollective2Sync/Logs/`
- Format: `IBCollective2Sync_YYYY-MM-DD.log`
- Rotation: Daily
- Retention: 30 days

### Error Recovery

**IB Connection Loss:**
- Automatic reconnection with exponential backoff
- Resumes position monitoring after reconnect
- Performs full sync to catch up

**C2 API Failures:**
- Retry with exponential backoff (3 attempts)
- Circuit breaker after 5 consecutive failures
- Automatic recovery when circuit closes

**Partial Sync Failures:**
- Individual position sync failures logged
- Other positions continue syncing
- Backup sync catches missed updates

## Security Considerations

1. **API Credentials**
   - Store in appsettings.json with restricted permissions
   - Consider environment variables for production
   - Never commit credentials to source control

2. **Network Security**
   - IB connection is local by default (127.0.0.1)
   - HTTPS for Collective2 API
   - No credentials transmitted in logs

3. **Access Control**
   - Run with minimal required permissions
   - Log files contain trading activity (protect appropriately)

## Troubleshooting

### Common Issues

**"Failed to connect to IB TWS/Gateway - timeout"**
- Verify TWS/Gateway is running
- Check API settings are enabled
- Confirm port number is correct
- Check firewall settings

**"C2 API circuit breaker is open"**
- Wait 1 minute for circuit to reset
- Check C2 API key is valid
- Verify strategy ID is correct
- Check C2 service status

**Positions not syncing**
- Check log files for errors
- Verify MinimumQuantityThreshold setting
- Ensure both platforms have positions
- Check network connectivity

**High CPU usage**
- Reduce BackupSyncIntervalMinutes
- Increase debounce delays
- Check for excessive position changes

## Future Enhancements

### Potential Improvements

1. **Multi-Strategy Support**
   - Sync multiple C2 strategies from single IB account
   - Strategy-specific configuration profiles

2. **Position Allocation Rules**
   - Percentage-based position sizing
   - Risk-based allocation
   - Position limits per symbol

3. **Advanced Order Types**
   - Limit orders instead of market
   - Stop-loss automation
   - Bracket orders

4. **Monitoring Dashboard**
   - Web-based real-time monitoring
   - Position drift visualization
   - Performance metrics

5. **Alerting**
   - Email/SMS notifications on errors
   - Position mismatch alerts
   - Performance reports

6. **Database Integration**
   - Historical position tracking
   - Audit trail
   - Analytics and reporting

## Conclusion

This application provides a robust, production-ready solution for synchronizing Interactive Brokers portfolios with Collective2 strategies. The event-driven architecture ensures minimal latency, while comprehensive error handling and retry logic provide reliability. The modular design allows for easy extension and customization to meet specific trading requirements.