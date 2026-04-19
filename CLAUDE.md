# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
dotnet build
dotnet run
dotnet publish -c Release
```

Press `q` in the console to gracefully shut down the running application.

## Configuration

The app reads `appsettings.json` from the output directory at startup. Key fields:

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
  "SymbolMappings": { "MGC": "@QMGC", "MYM": "@QMYM" },
  "MaxRetryAttempts": 3,
  "PostTradeCheckDelaySeconds": 60
}
```

IB ports: `7497` = paper trading, `7496` = live trading. TWS/Gateway must have "Enable ActiveX and Socket Clients" enabled.

## Architecture

The codebase is split across focused files in namespace `IBCollective2Sync`:

| File | Responsibility |
|---|---|
| `Program.cs` | Startup, event handlers, sync orchestration |
| `IBClient.cs` | IB TWS connection + `IBWrapper` (EWrapper impl) |
| `Collective2Client.cs` | C2 REST API client with resilience policies |
| `Configuration.cs` | Config loading + `GetC2Symbol` symbol translation |
| `SyncLogic.cs` | Pure static `DetermineAction` — action selection logic |
| `Models.cs` | `Position`, event args, C2 DTO types |
| `FileLogger.cs` | Daily-rotating file logger |

**Data flow:**
1. `IBClient` connects to IB TWS/Gateway via `IB.CSharpApi` (`EClientSocket`/`EWrapper`)
2. IB fires position/execution callbacks → `IBWrapper` delegates to `IBClient` internal handlers
3. `IBClient` raises `OnPositionChanged` / `OnTradeExecuted` events → `Program` handles them via `FireAndForget` (exceptions are logged, not silently dropped)
4. `Program` compares IB positions vs C2 positions; `SyncLogic.DetermineAction` selects the correct action (BTO/BTC/STO/STC); `Collective2Client` submits the signal

**Resilience in `Collective2Client`:**
- Polly retry (exponential backoff, `MaxRetryAttempts` from config) wraps a circuit breaker (opens after 5 failures, 1-minute break)
- Both HTTP methods accept `CancellationToken` — pressing `q` cancels in-flight requests immediately
- Rate limiter: `SemaphoreSlim(10,10)` with 100 ms spacing

**Sync safety rules (critical to preserve):**
- If IB or C2 position fetch returns `null`, sync is **aborted** — never send "close all" signals on connection failure
- Per-symbol `SemaphoreSlim` locks prevent race conditions between event-driven and backup syncs
- Inside the lock, `GetCachedPositionFull()` is always re-fetched to get the freshest IB quantity and SecType
- Weekend blackout: Friday 18:00 – Sunday 17:00 EST, all syncs are skipped
- TWS maintenance window: 00:00 – 02:00 EST, app disconnects and reconnects automatically

**C2 signal actions:**
- `BTO` (Buy to Open), `BTC` (Buy to Close), `STO` (Sell to Open), `STC` (Sell to Close)
- Action selection is in `SyncLogic.DetermineAction` (pure, unit-tested)
- Side sent to C2: starts with `B` → `"1"` (buy), otherwise `"2"` (sell)
- `symbolType` is derived from `SecType`: `FUT→"future"`, `STK→"stock"`, `OPT→"option"`, `CASH→"forex"`

## Tests

```bash
dotnet test
```

Test project: `IBCollective2Sync.Tests/` (xUnit, 14 tests)
- `ConfigurationTests.cs` — `GetC2Symbol` symbol mapping (6 cases)
- `SyncActionTests.cs` — `SyncLogic.DetermineAction` action selection (8 cases)
