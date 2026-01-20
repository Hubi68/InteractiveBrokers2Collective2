# Code Documentation: IB-Collective2 Sync

This document provides a detailed technical overview of the C# classes and methods within `ib_c2_optimized.cs`.

## 1. Class: `Program`
The main entry point and orchestrator of the application. It manages the lifecycle, synchronization loops, and signals between IB and C2.

### Methods
-   **`Main`**: Initializes clients, connects to IB, starts the maintenance monitor, performs the initial sync, and enters the event loop.
-   **`ConnectWithRetry`**: Connecting to IB can be flaky; this method uses `Polly` to implement exponential backoff retry logic for robust connection.
-   **`SyncPortfolio`**: The core logic for full portfolio reconciliation.
    -   Fetches positions from IB and C2 concurrently.
    -   **Safety Check**: Aborts if either position list is `null` (connection/API failure).
    -   **Blackout Check**: Skips if `IsWeekendMarketClosed()` returns true.
    -   Compares positions and submits BTO/STC/STO signals to C2 to match IB.
-   **`OnPositionChanged` / `OnTradeExecuted`**: Event handlers triggered by `IBClient` when real-time updates occur. They trigger `SyncSpecificPosition`.
-   **`SyncSpecificPosition`**: Optimized sync for a single symbol. Reduces API load by checking only the affected instrument.
    -   Includes weekend blackout check.
-   **`IsWeekendMarketClosed`**: Helper that checks if the current time (Eastern Time) falls within the Futures market close window (Friday 6pm - Sunday 5pm).
-   **`MonitorMaintenanceWindow`**: Background task that purposefully disconnects from IB during the daily TWS maintenance window (1:30 AM - 2:00 AM) and reconnects afterwards.

## 2. Class: `IBClient`
Wrapper around the Interactive Brokers `EClientSocket`. Manages the low-level TWS API connection.

### Methods
-   **`ConnectAsync`**: Establishes the socket connection and starts the reader thread.
-   **`StartPositionMonitoringAsync`**: Subscribes to `reqPositions` and `reqExecutions` for real-time streaming updates.
-   **`GetPositionsAsync`**: Fetches the current list of open positions.
    -   **Returns**: `List<Position>?`
    -   **Safety**: Returns `null` (not empty list) if the request times out or is disconnected.
-   **`OnPosition` / `OnExecution`**: Internal callbacks invoked by `IBWrapper`. They normalize the data and fire events to `Program`.

## 3. Class: `Collective2Client`
HTTP client for the Collective2 API (V4).

### Methods
-   **`GetPositionsWithRetryAsync`**: Retrieves open positions for the strategy.
    -   **Returns**: `List<Position>?`
    -   **Safety**: Returns `null` on HTTP error or exception.
-   **`SubmitSignalWithRetryAsync`**: Sends trade signals (BTO, STC, STO, BTC) to C2. Use `Polly` for retries on transient network errors.

## 4. Class: `Configuration`
Manages application settings loaded from `appsettings.json`.

### Method: `GetC2Symbol`
Handles the translation of IB symbols to Collective2 symbols.
-   **Logic**:
    -   Checks `SymbolMappings` dictionary first (e.g., `MGC` -> `@QMGC`).
    -   Falls back to constructing C2 symbols based on contract month (e.g., `@ESH6`).
    -   Ensures standard Futures formatting (prepending `@` if missing).

## 5. Class: `FileLogger`
A custom thread-safe file logger.

### Key Features
-   **Daily Rotation**: Creates a new file each day (`IBCollective2Sync_yyyy-MM-dd.log`).
-   **Append Mode**: Appends to the existing file of the day, ensuring persistence across application restarts.
-   **Cleanup**: Automatically deletes logs older than 30 days.
