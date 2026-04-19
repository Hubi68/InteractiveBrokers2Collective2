# IB-Collective2 Portfolio Sync

A high-performance C# application that synchronizes an Interactive Brokers (IB) portfolio with a Collective2 (C2) strategy in real-time. It mirrors positions from IB to C2, ensuring your strategy followers match your actual trading account.

## Features

-   **Real-Time Synchronization**: Monitors IB positions via TWS/Gateway API and executes updates to Collective2 immediately upon change detection.
-   **Event-Driven**: Uses IB's `reqExecutions` and `reqPositions` for low-latency updates, rather than polling.
-   **Smart Symbol Mapping**: Automatically maps IB Future symbols (e.g., `MGC`) to C2 symbols (e.g., `@QMGC`), with fallback logic for contract months.
-   **Robust Safety Mechanisms**:
    -   **Sync Safety**: Aborts synchronization immediately if connection to IB or C2 fails, preventing erroneous "liquidate all" signals.
    -   **Weekend Blackout**: Automatically disables syncing during Futures market close (Friday 6:00 PM EST to Sunday 5:00 PM EST) to prevent errors.
    -   **Double-Check Verification**: Compares quantities before submitting orders to minimize unnecessary signals.
-   **Resilient API Client**: Polly-based retry with exponential backoff and a circuit breaker (opens after 5 failures) for the Collective2 API.
-   **Multi-Asset Symbol Type**: Automatically maps IB `SecType` to the correct Collective2 `symbolType` (`FUT→future`, `STK→stock`, `OPT→option`, `CASH→forex`).
-   **Daily Logging**: Rotates log files daily (`IBCollective2Sync_YYYY-MM-DD.log`) for clean and manageable audit trails.

## Prerequisites

1.  **Interactive Brokers TWS or Gateway**: Must be running and configured to accept API connections.
    -   Enable "Enable ActiveX and Socket Clients".
    -   Default Port: `7497` (Paper) or `7496` (Live).
2.  **Collective2 Account**: You need an API Key and a Strategy ID.
3.  **.NET 8.0 SDK** (or compatible runtime).

## Configuration

Configure the application via `appsettings.json`:

```json
{
  "C2ApiKey": "YOUR_C2_API_KEY",
  "C2StrategyId": "YOUR_STRATEGY_ID",
  "IbHost": "127.0.0.1",
  "IbPort": 7497,
  "IbClientId": 987,
  "BackupSyncIntervalMinutes": 5,
  "PositionChangeDebounceMs": 500,
  "TradeExecutionDelayMs": 1000,
  "MinimumQuantityThreshold": 0.01,
  "SignalSubmissionDelayMs": 100,
  "MaxRetryAttempts": 3,
  "PostTradeCheckDelaySeconds": 60,
  "SymbolMappings": {
     "MGC": "@QMGC",
     "MYM": "@QMYM"
  }
}
```

### Key Parameters
| Parameter | Default | Description |
| :--- | :--- | :--- |
| `C2ApiKey` | — | Your Collective2 API Token. |
| `C2StrategyId` | — | The ID of the strategy you are signaling. |
| `IbHost` / `IbPort` | `127.0.0.1` / `7497` | Connection details for TWS/Gateway. |
| `BackupSyncIntervalMinutes` | `5` | How often to perform a full reconciliation (safeguard). |
| `MaxRetryAttempts` | `3` | Max retries for IB connection and C2 API calls. |
| `PostTradeCheckDelaySeconds` | `60` | Seconds to wait after a signal before verifying C2 position. Set `0` to disable. |
| `SymbolMappings` | `{}` | Custom rules to map IB root symbols to C2 symbols. |

## Usage

1.  Start IB Gateway or TWS.
2.  Run the application:
    ```bash
    dotnet run
    ```
3.  The application will:
    -   Connect to IB.
    -   Perform an initial full sync.
    -   Enter real-time monitoring mode.
4.  **Stopping**: Press `q` in the console to gracefully shut down.

## Safety & Blackout Windows
-   **Connection Loss**: If IB connection is lost, it attempts to reconnect automatically.
-   **Weekend Blackout**: From **Friday 18:00 EST** to **Sunday 17:00 EST**, the application will **pause** all sync activities to respect Futures market closure.
