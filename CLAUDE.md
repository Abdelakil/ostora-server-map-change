# Ostora Server Restart

A SwiftlyS2 plugin for CS2 that changes the map when the last player disconnects or at a scheduled time.

## graphify

This project has a knowledge graph at `graphify-out/` with god nodes, community structure, and cross-file relationships.

Rules:
- For codebase questions, first run `graphify query "<question>"` when `graphify-out/graph.json` exists. Use `graphify path "<A>" "<B>"` for relationships and `graphify explain "<concept>"` for focused concepts. These return a scoped subgraph, usually much smaller than GRAPH_REPORT.md or raw grep output.
- If `graphify-out/wiki/index.md` exists, use it for broad navigation instead of raw source browsing.
- Read `graphify-out/GRAPH_REPORT.md` only for broad architecture review or when query/path/explain do not surface enough context.
- After modifying code, run `graphify update .` to keep the graph current (AST-only, no API cost).

## Build

```bash
dotnet build OstoraServerRestart.csproj
# Release:
dotnet publish -c Release
```

Requires .NET 10.0 SDK.

## Architecture

Single-file plugin: `src/OstoraServerRestartPlugin.cs` (~350 lines)

### Key components

| Component | Location | Purpose |
|-----------|----------|---------|
| `PluginConfig` | Lines 14-21 | Config model (hot-reloaded from JSON) |
| `OstoraServerRestartPlugin` | Lines 24+ | Plugin class inheriting `BasePlugin(ISwiftlyCore)` |
| `SchedulePreciseCountdown()` | Line 122 | Calculates time until scheduled change, starts or schedules countdown |
| `StartScheduledCountdown(float)` | Line 295 | Runs the 60-second countdown with tick announcements and player checks |
| `StartEmptyServerChangeMapTimer()` | Line 241 | Countdown when last player leaves |
| `OnClientConnected` | Line 190 | Cancels empty-server timer; cancels scheduled countdown if players online |
| `OnClientDisconnected` | Line 212 | Checks if server is empty after a disconnect |
| `HasRealPlayers()` | Line 226 | Returns true if any non-bot players are connected |
| `ChangeMap()` | Line 357 | Executes the actual map change |

### Control flow: Scheduled map change

1. `ParseScheduledTime()` → `SchedulePreciseCountdown()` (line 92)
2. If before countdown window (>60s until target): `DelayBySeconds` schedules callback for 60s before target
3. When callback fires → `StartScheduledCountdown(60f)` starts a per-second countdown
4. Each tick: announcement at 30/10/5/3/2/1s; final check at 0s → `ChangeMap()`
5. Player checks at countdown start AND final tick: if real players online and `IgnorePlayersForScheduledChangeMap=false`, skip and reschedule for next day

### Control flow: Empty server map change

1. `OnClientDisconnected` → if no real players → `StartEmptyServerChangeMapTimer()`
2. If player connects before timer fires → `OnClientConnected` → `CancelEmptyServerChangeMapTimer()`

## Critical bug fixed (commit c2ec74d)

**Infinite recursion** between `StartScheduledCountdown` and `SchedulePreciseCountdown`:

When the scheduled countdown window opened (60s before target) and real players were online with `IgnorePlayersForScheduledChangeMap=false`, `StartScheduledCountdown` detected players and called `SchedulePreciseCountdown()`, which immediately called `StartScheduledCountdown()` back since it was still in the countdown window. Neither method changed the conditions — `CancelScheduledCountdown()` only clears a token, irrelevant once a callback is executing. StackOverflow → plugin crash → recovery in stale state → restart despite players.

**Fix**: Added `skipDueToPlayers` parameter to `SchedulePreciseCountdown`. When true and in the countdown window, it schedules for tomorrow instead of re-entering `StartScheduledCountdown`.

## Config

- Location: `configs/OstoraServerRestart/config.jsonc` (auto-generated on first load)
- Supports hot-reload
- `IgnorePlayersForScheduledChangeMap: false` means scheduled restarts respect connected players

## Dependencies

- SwiftlyS2.CS2 (NuGet package)
- System.Data.SQLite.Core
