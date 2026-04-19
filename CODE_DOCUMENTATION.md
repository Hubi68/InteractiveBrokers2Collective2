# Code Documentation: IB-Collective2 Sync

This document provides a detailed technical overview of the C# classes and methods in the `IBCollective2Sync` project.

## File Structure

| File | Classes |
|---|---|
| `Program.cs` | `Program` |
| `IBClient.cs` | `IBClient`, `IBWrapper` |
| `Collective2Client.cs` | `Collective2Client` |
| `Configuration.cs` | `Configuration` |
| `SyncLogic.cs` | `SyncLogic` |
| `Models.cs` | `Position`, `PositionChangedEventArgs`, `TradeExecutedEventArgs`, `C2PositionsResponse`, `C2PositionDTO`, `C2SymbolDTO` |
| `FileLogger.cs` | `FileLogger` |

---

## 1. Class: `Program`

The main entry point and orchestrator. Manages lifecycle, event subscriptions, and sync coordination.

### Methods

- **`Main`**: Initializes config, logger, and clients in order, then starts the maintenance monitor, connects to IB, performs the initial sync, and enters the event loop. The maintenance monitor is started **after** the logger is initialized.
- **`ConnectWithRetry`**: Uses Polly exponential backoff (`_config.MaxRetryAttempts`) for robust IB connection.
- **`SyncPortfolio`**: Full portfolio reconciliation. Fetches IB and C2 positions concurrently. Aborts if either returns `null` (connection failure) or if the market is closed.
- **`SyncSpecificPosition`**: Single-symbol sync. Acquires the per-symbol lock, re-fetches the latest IB position (including `SecType`) via `GetCachedPositionFull`, compares with C2, and delegates to `ExecutePositionSync` if a difference exceeds the threshold.
- **`ExecutePositionSync`**: Calls `SyncLogic.DetermineAction` to decide the C2 action, submits it, and optionally verifies the result after `PostTradeCheckDelaySeconds`.
- **`OnPositionChanged` / `OnTradeExecuted`**: Event handlers wired to `IBClient`. Debounce briefly, then call `SyncSpecificPosition`.
- **`MonitorMaintenanceWindow`**: Background loop that disconnects from IB at 00:00 EST and reconnects at 02:00 EST.
- **`IsWeekendMarketClosed`**: Returns `true` during Futures market close window (Friday 18:00 – Sunday 17:00 EST).

---

## 2. Class: `IBClient`

Wrapper around the Interactive Brokers `EClientSocket`. Manages the TWS/Gateway connection and position cache.

### Key Design

- Positions are cached in a `ConcurrentDictionary<string, Position>` keyed by C2 symbol.
- `RefreshPositionsAsync` calls `reqPositions()` and awaits the `positionEnd` callback via `TaskCompletionSource`. A sweep phase detects closed positions (symbols present in cache but absent from the refresh response).
- All 4 event invocations (`OnPositionChanged`, `OnTradeExecuted`, `OnConnectionLost`) are wrapped in `FireAndForget` — exceptions inside event handlers are logged rather than silently dropped.

### Methods

- **`ConnectAsync`**: Establishes the socket and starts the reader thread.
- **`GetPositionsAsync`**: Triggers a full position refresh via `reqPositions`, awaits completion, returns the cached list (or `null` on timeout).
- **`GetCachedPosition`**: Returns the current cached quantity for a symbol (double).
- **`GetCachedPositionFull`**: Returns the full `Position?` object (includes `SecType`) — used by `SyncSpecificPosition`.
- **`FireAndForget`**: Private helper. Attaches a `ContinueWith(OnlyOnFaulted)` continuation that logs any exception escaping an event handler without blocking the IB reader thread.

---

## 3. Class: `IBWrapper`

Thin `EWrapper` implementation. Forwards IB callbacks to `IBClient` and filters error codes:
- Codes `1100`, `1102`, `2110` → trigger `OnConnectionClosed`
- Codes `< 2000` or `>= 10000` → logged as errors; others as debug

---

## 4. Class: `Collective2Client`

HTTP client for the Collective2 V4 API (`api4-general.collective2.com`).

### Resilience

Uses a composed Polly policy: `_resilientPolicy = Policy.WrapAsync(_circuitBreakerPolicy, _retryPolicy)`

- **Circuit breaker** (outer): opens after 5 failures, 1-minute break. Logs state transitions (OPEN / HALF-OPEN / CLOSED).
- **Retry** (inner): exponential backoff, `maxRetryAttempts` from config.

This means retries are invisible to the circuit breaker — only fully exhausted retry sequences count as failures.

### Methods

- **`GetPositionsWithRetryAsync(CancellationToken)`**: Calls `/Strategies/GetStrategyOpenPositions`. Returns `null` on failure or open circuit.
- **`SubmitSignalWithRetryAsync(symbol, action, quantity, secType, CancellationToken)`**: Posts to `/Strategies/NewStrategyOrder`. Maps `secType` to C2's string format:

  | IB SecType | C2 symbolType |
  |---|---|
  | `FUT` | `future` |
  | `STK` | `stock` |
  | `OPT` | `option` |
  | `CASH` | `forex` |
  | other | `future` (default) |

---

## 5. Class: `SyncLogic`

Pure static helper. Contains the action-selection logic extracted from `ExecutePositionSync`.

### Method: `DetermineAction(ibQuantity, c2Quantity, minimumThreshold)`

Returns `(string action, double quantity)?` — `null` if no action is needed.

| Condition | Action |
|---|---|
| IB flat, C2 long | `STC` (sell to close) |
| IB flat, C2 short | `BTC` (buy to close) |
| IB > C2, C2 short | `BTC` |
| IB > C2, C2 flat/long | `BTO` |
| IB < C2, C2 long | `STC` |
| IB < C2, C2 flat/short | `STO` |

Covered by `SyncActionTests.cs` (8 cases).

---

## 6. Class: `Configuration`

Manages settings loaded from `appsettings.json` (next to the executable).

### Method: `GetC2Symbol(Contract)`

Translates an IB `Contract` to a C2 symbol string.

- For futures (`SecType == "FUT"`):
  1. If `SymbolMappings` contains the IB root symbol, substitute the mapped root into `LocalSymbol` (e.g. `MGC` + `"@QMGC"` + `MGCG6` → `@QMGCG6`).
  2. Otherwise use `LocalSymbol` directly (or `Symbol + LastTradeDateOrContractMonth` if `LocalSymbol` is empty).
  3. Hardcoded fallback: symbols starting with `MGC` are rewritten to `QMGC…`.
  4. Prepend `@` if not already present.
- For all other security types: return `contract.Symbol` as-is.

Covered by `ConfigurationTests.cs` (6 cases).

---

## 7. Class: `FileLogger`

Custom thread-safe file logger. Writes to `./Logs/IBCollective2Sync_YYYY-MM-DD.log`.

- **Daily rotation**: new file each day; old file is closed and a new `StreamWriter` opened.
- **Auto-flush**: `StreamWriter` has `AutoFlush = true`; a 5-second timer also checks for date rollover.
- **Retention**: logs older than 30 days are deleted on startup.
- **Levels**: `Debug`, `Info`, `Warn`, `Error` (with optional `Exception`).
- Log line format: `[yyyy-MM-dd HH:mm:ss.fff] [LEVEL] [ThreadId] message`
