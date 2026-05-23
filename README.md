# Ostora Server Restart

A [SwiftlyS2](https://github.com/swiftly-solution/swiftlys2) plugin for Counter-Strike 2 that automatically changes the map (reloading the server) when the last player disconnects, or at a scheduled time of day.

## Features

- **Empty server map change** — Changes the map (reloading the server) after all real players disconnect, with a configurable delay
- **Scheduled daily map change** — Change the map at a specific time every day, 24-hour format (e.g. `04:30` for 4:30 AM, `16:00` for 4:00 PM)
- **Bot/HLTV awareness** — Optionally ignore bots and HLTV when counting players
- **Ignore players for scheduled map change** — Option to force scheduled map change even if players are online
- **Workshop map support** — Optionally use a workshop map ID to change to a workshop map instead of reloading the current map
- **Config file** — JSON config with hot-reload support (changes apply without plugin reload)
- **Translations** — Localized log messages (English, French, German, Arabic)

## Installation

1. Build the plugin:
   ```bash
   dotnet publish -c Release
   ```
2. Copy the contents of `build/publish/OstoraServerRestart/` to your SwiftlyS2 plugins directory on the server.
3. Start the server — the config file will be auto-generated on first load.

## Configuration

The config file is located at `configs/OstoraServerRestart/config.jsonc` (relative to the plugin directory) and is auto-generated on first run.

```jsonc
{
  "OstoraServerRestart": {
    // Enable or disable the plugin
    "Enabled": true,

    // Delay in seconds before changing the map after the last player disconnects
    "ChangeMapDelaySeconds": 30.0,

    // If true, bots and HLTV are not counted as players
    // (map only changes when all HUMAN players leave)
    "IgnoreBots": true,

    // If true, the scheduled map change will fire even if players are online
    // If false, the scheduled map change is skipped when players are connected
    "IgnorePlayersForScheduledChangeMap": true,

    // Time of day for a daily map change in 24-hour HH:mm format
    // Examples: "04:30" = 4:30 AM, "16:00" = 4:00 PM, "23:59" = 11:59 PM
    // Set to "" to disable scheduled map changes
    "ScheduledChangeMapTime": "04:00",

    // If set to a workshop file ID (e.g. "3043842750"), the server changes to
    // that workshop map instead of reloading the current map.
    // Leave empty to use the default changelevel behavior.
    "WorkshopMapId": ""
  }
}
```

### Config Options

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Enabled` | `bool` | `true` | Enable or disable the plugin |
| `ChangeMapDelaySeconds` | `float` | `30.0` | Seconds to wait after the last player leaves before changing the map |
| `IgnoreBots` | `bool` | `true` | If `true`, bots and HLTV don't count as players for the empty-server check |
| `IgnorePlayersForScheduledChangeMap` | `bool` | `true` | If `true`, the scheduled map change fires even when players are online |
| `ScheduledChangeMapTime` | `string` | `"04:00"` | Daily map change time in **24-hour** `HH:mm` format (e.g. `"04:00"` = 4:00 AM, `"16:00"` = 4:00 PM). Empty string disables scheduled map change |
| `WorkshopMapId` | `string` | `""` | Workshop file ID (e.g. `"3043842750"`). When set, uses `host_workshop_map` instead of `changelevel`. Leave empty for default behavior |

> **Note:** The config file supports hot-reload. Changes are picked up automatically without reloading the plugin.

## How It Works

### Empty Server Map Change

1. When a player disconnects, the plugin checks if any real players remain
2. If no real players are left (bots/HLTV ignored if `IgnoreBots` is `true`), a timer starts
3. If a new player connects before the timer fires, the timer is cancelled
4. When the timer expires, the server reloads the current map (or changes to the workshop map if `WorkshopMapId` is set)

### Scheduled Map Change

1. When `ScheduledChangeMapTime` is set (e.g. `"04:00"`), the plugin calculates the time until the target and starts a precise countdown
2. 60 seconds before the target time, a 60-second countdown begins:
   - If `IgnorePlayersForScheduledChangeMap` is `true` → countdown runs regardless of players
   - If `false` → checks for real players; if any are online, reschedules for the next day
3. If a player connects during the 60-second countdown (and `IgnorePlayersForScheduledChangeMap` is `false`), the countdown is cancelled and rescheduled for the next day
4. When the countdown reaches 0, a final player check is done — if players are online and `IgnorePlayersForScheduledChangeMap` is `false`, it reschedules instead of changing
5. The map changes to the current map (or workshop map if `WorkshopMapId` is set)

## Translations

Translation files are located in `resources/translations/`. To add a new language, create a file named `<language_code>.jsonc` (e.g. `es.jsonc` for Spanish).

### Available Languages

| Language | Code | File |
|----------|------|------|
| English | `en` | `en.jsonc` |
| French | `fr` | `fr.jsonc` |
| German | `de` | `de.jsonc` |
| Arabic | `ar` | `ar.jsonc` |

### Translation Keys

| Key | English Default |
|-----|-----------------|
| `changemap.scheduled_log` | No real players remaining. Map will change in {0} seconds. |
| `changemap.now_log` | Changing map now... |
| `scheduled_changemap_log` | Scheduled map change triggered at {0}. Changing map... |
| `scheduled_changemap_players_online_log` | Scheduled map change time reached at {0}, but players are online. Skipping map change. |

## Building

**Prerequisites:** .NET 10.0 SDK

```bash
dotnet publish -c Release
```

The published plugin is output to `build/publish/OstoraServerRestart/`.

## License

This plugin is licensed under the GNU GPLv3 License, consistent with SwiftlyS2.
