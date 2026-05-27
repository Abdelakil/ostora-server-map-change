# Memory

Persistent context across Claude Code sessions for the Ostora Server Restart project.

## Recent work

### 2026-05-27: Fixed infinite recursion crash (commit c2ec74d)

**Problem**: Server restarted at 04:00 despite players being connected, even with `IgnorePlayersForScheduledChangeMap=false`.

**Root cause**: Mutual recursion between `StartScheduledCountdown` and `SchedulePreciseCountdown`. When the `DelayBySeconds` callback fired at 03:59:00 (60s before the 04:00 target) and players were online, the player check in `StartScheduledCountdown` called `SchedulePreciseCountdown()`, which was still in the countdown window and called `StartScheduledCountdown()` back — infinitely. StackOverflowException crashed the plugin. During recovery reload, `HasRealPlayers()` returned false for already-connected players (event handlers weren't registered when they connected), so the countdown started and the server restarted.

**Fix**: Added `bool skipDueToPlayers = false` parameter to `SchedulePreciseCountdown()`. When true and inside the countdown window, it schedules for the next day instead of calling `StartScheduledCountdown`. Updated both player-check call sites in `StartScheduledCountdown` (initial check and final-tick check) to pass `skipDueToPlayers: true`.

**Evidence from log** (`log/2026-05-27.log`):
- 03:47:01 — Last player connect events, countdown rescheduled with `~718s until target`
- 03:47:01 to 03:59:46 — 13 minutes of silence (plugin crashed)
- 03:59:46 — Config reload triggers countdown start with 14s remaining (players invisible after recovery)
- 04:00:17 — Server shuts down (`HSR_QUIT`)

## Key observations

- **`HasRealPlayers()`** depends on `Core.PlayerManager.GetAllValidPlayers()`. When the plugin is freshly loaded, this may not include players who connected before the plugin's event handlers were registered. This is why the countdown started after crash recovery.
- **Config hot-reload** triggers `ParseScheduledTime()` which calls `CancelScheduledCountdown()` then `SchedulePreciseCountdown()`. During mass-plugin-reload events, this can fire many times in rapid succession.
- **`IgnoreBots`** at the time of the bug: `IgnoreBots: true`, `IgnorePlayersForScheduledChangeMap: false`. With `IgnoreBots: true`, all connected "players" being fake clients would cause `HasRealPlayers()` to return false.
- **The `_scheduledCountdownCts` `DelayBySeconds` approach** creates a window where any config change cancels and recalculates. Multiple config reloads at the countdown boundary can chain-start the countdown.

## Things to watch for

- Any change to `SchedulePreciseCountdown` or `StartScheduledCountdown` must ensure they cannot call each other in a loop. The `skipDueToPlayers` guard must be preserved.
- `Core.PlayerManager.GetAllValidPlayers()` behavior on plugin reload — if it changes, `HasRealPlayers()` may start returning true after reloads, which could interact with the recursion guard.
- Config hot-reload storms during plugin initialization can trigger the countdown path multiple times per second.
