using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.Scheduler;

namespace OstoraServerRestart;

public sealed class PluginConfig
{
    public bool Enabled { get; set; } = true;
    public float ChangeMapDelaySeconds { get; set; } = 30.0f;
    public bool IgnoreBots { get; set; } = true;
    public bool IgnorePlayersForScheduledChangeMap { get; set; } = true;
    public string ScheduledChangeMapTime { get; set; } = "04:00";
    public string WorkshopMapId { get; set; } = "";
}

[PluginMetadata(Id = "ostora.serverrestart", Version = "1.2.0", Name = "Ostora Server Restart", Author = "Zenjibad", Description = "Changes the map after the last player disconnects or at a scheduled time", Website = "https://ostora.xyz")]
public sealed class OstoraServerRestartPlugin(ISwiftlyCore core) : BasePlugin(core)
{
    private const string ConfigFileName = "config.jsonc";
    private const string ConfigSection = "OstoraServerRestart";

    private IOptionsMonitor<PluginConfig> _configMonitor = null!;
    private CancellationTokenSource? _emptyServerChangeMapCts;
    private CancellationTokenSource? _scheduledCountdownCts;
    private TimeSpan? _scheduledChangeMapTime;
    private string _currentMap = "";
    private IDisposable? _configChangeListener;
    private DateTime? _lastScheduledChangeMapDate;
    private float _emptyServerTimerRemaining;

    public override void Load(bool hotReload)
    {
        LoadConfiguration();

        ParseScheduledTime();

        Core.Event.OnClientDisconnected += OnClientDisconnected;
        Core.Event.OnClientConnected += OnClientConnected;
        Core.Event.OnMapLoad += OnMapLoad;

        Core.Logger.LogInformation("Ostora Server Restart loaded. Delay: {Delay}s, IgnoreBots: {IgnoreBots}, ScheduledTime: {ScheduledTime}, IgnorePlayers: {IgnorePlayers}",
            _configMonitor.CurrentValue.ChangeMapDelaySeconds, _configMonitor.CurrentValue.IgnoreBots,
            _scheduledChangeMapTime.HasValue ? _scheduledChangeMapTime.Value.ToString(@"hh\:mm") : "disabled",
            _configMonitor.CurrentValue.IgnorePlayersForScheduledChangeMap);
    }

    public override void Unload()
    {
        Core.Event.OnClientDisconnected -= OnClientDisconnected;
        Core.Event.OnClientConnected -= OnClientConnected;
        Core.Event.OnMapLoad -= OnMapLoad;

        _configChangeListener?.Dispose();

        CancelEmptyServerChangeMapTimer();
        CancelScheduledCountdown();
    }

    private void LoadConfiguration()
    {
        Core.Configuration
            .InitializeJsonWithModel<PluginConfig>(ConfigFileName, ConfigSection)
            .Configure(cfg => cfg.AddJsonFile(
                Core.Configuration.GetConfigPath(ConfigFileName),
                optional: false,
                reloadOnChange: true));

        ServiceCollection services = new();
        services.AddSwiftly(Core)
            .AddOptionsWithValidateOnStart<PluginConfig>()
            .BindConfiguration(ConfigSection);

        var provider = services.BuildServiceProvider();
        _configMonitor = provider.GetRequiredService<IOptionsMonitor<PluginConfig>>();

        // Listen for config changes
        _configChangeListener = _configMonitor.OnChange((newConfig, name) =>
        {
            Core.Logger.LogInformation("Config reloaded. New values: Enabled={Enabled}, ChangeMapDelaySeconds={Delay}, ScheduledChangeMapTime={Time}",
                newConfig.Enabled,
                newConfig.ChangeMapDelaySeconds,
                newConfig.ScheduledChangeMapTime);
            ParseScheduledTime();
        });
    }

    private void ParseScheduledTime()
    {
        CancelScheduledCountdown();
        _scheduledChangeMapTime = null;

        if (string.IsNullOrWhiteSpace(_configMonitor.CurrentValue.ScheduledChangeMapTime))
            return;

        var parts = _configMonitor.CurrentValue.ScheduledChangeMapTime.Split(':');
        if (parts.Length == 2 && int.TryParse(parts[0], out int hours) && int.TryParse(parts[1], out int minutes))
        {
            if (hours is >= 0 and < 24 && minutes is >= 0 and < 60)
            {
                _scheduledChangeMapTime = new TimeSpan(hours, minutes, 0);
                SchedulePreciseCountdown();
            }
            else
            {
                Core.Logger.LogWarning("Invalid scheduled change map time '{Time}': hours must be 0-23 and minutes 0-59.", _configMonitor.CurrentValue.ScheduledChangeMapTime);
            }
        }
        else
        {
            Core.Logger.LogWarning("Invalid scheduled change map time format '{Time}'. Expected 'HH:mm' (e.g. '04:30').", _configMonitor.CurrentValue.ScheduledChangeMapTime);
        }
    }

    private void SchedulePreciseCountdown(bool skipDueToPlayers = false)
    {
        if (!_scheduledChangeMapTime.HasValue || !_configMonitor.CurrentValue.Enabled)
            return;

        var now = DateTime.Now;
        var today = now.Date;
        var targetTimeToday = today + _scheduledChangeMapTime.Value;
        var timeUntilTarget = targetTimeToday - now;

        // If target is in the past, schedule for tomorrow
        if (timeUntilTarget.TotalSeconds <= 0)
        {
            targetTimeToday = today.AddDays(1) + _scheduledChangeMapTime.Value;
            timeUntilTarget = targetTimeToday - now;
        }

        // Calculate when to start countdown (60 seconds before target)
        var countdownStartTime = targetTimeToday.AddSeconds(-60);
        var timeUntilCountdown = countdownStartTime - now;

        Core.Logger.LogInformation("Scheduled map change at {Target}. Countdown will start in {Seconds}s ({TimeUntilTarget}s until target)",
            targetTimeToday.ToString("HH:mm:ss"),
            timeUntilCountdown.TotalSeconds,
            timeUntilTarget.TotalSeconds);

        // If countdown should start in the past (already passed), start it now
        if (timeUntilCountdown.TotalSeconds <= 0)
        {
            if (timeUntilTarget.TotalSeconds > 0)
            {
                // We're in the countdown window
                if (skipDueToPlayers)
                {
                    // Players are online — skip this window and schedule for tomorrow
                    targetTimeToday = today.AddDays(1) + _scheduledChangeMapTime.Value;
                    timeUntilCountdown = targetTimeToday.AddSeconds(-60) - now;
                    _scheduledCountdownCts = Core.Scheduler.DelayBySeconds((float)timeUntilCountdown.TotalSeconds, () =>
                    {
                        StartScheduledCountdown(60.0f);
                    });
                }
                else
                {
                    StartScheduledCountdown((float)timeUntilTarget.TotalSeconds);
                }
            }
            else
            {
                // Target already passed, skip to tomorrow
                targetTimeToday = today.AddDays(1) + _scheduledChangeMapTime.Value;
                timeUntilCountdown = targetTimeToday.AddSeconds(-60) - now;
                _scheduledCountdownCts = Core.Scheduler.DelayBySeconds((float)timeUntilCountdown.TotalSeconds, () =>
                {
                    StartScheduledCountdown(60.0f);
                });
            }
        }
        else
        {
            // Schedule countdown to start at the right time
            _scheduledCountdownCts = Core.Scheduler.DelayBySeconds((float)timeUntilCountdown.TotalSeconds, () =>
            {
                StartScheduledCountdown(60.0f);
            });
        }
    }

    private void OnClientConnected(IOnClientConnectedEvent @event)
    {
        CancelEmptyServerChangeMapTimer();

        // If a player joins during the scheduled countdown and the config says
        // not to ignore players, cancel the scheduled map change and re-schedule
        // for the next day.
        if (!_configMonitor.CurrentValue.IgnorePlayersForScheduledChangeMap &&
            _scheduledCountdownCts != null &&
            !_scheduledCountdownCts.IsCancellationRequested)
        {
            Core.Logger.LogInformation("Player connected during scheduled countdown. Cancelling scheduled map change (IgnorePlayersForScheduledChangeMap=false).");
            CancelScheduledCountdown();
            SchedulePreciseCountdown();
        }
    }

    private void OnMapLoad(IOnMapLoadEvent @event)
    {
        _currentMap = @event.MapName;
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        if (!_configMonitor.CurrentValue.Enabled)
            return;

        Core.Scheduler.NextTick(() =>
        {
            if (!HasRealPlayers())
            {
                StartEmptyServerChangeMapTimer();
            }
        });
    }

    private bool HasRealPlayers()
    {
        if (_configMonitor.CurrentValue.IgnoreBots)
        {
            foreach (var player in Core.PlayerManager.GetAllValidPlayers())
            {
                if (!player.IsFakeClient)
                    return true;
            }
            return false;
        }

        return Core.PlayerManager.PlayerCount > 0;
    }

    private void StartEmptyServerChangeMapTimer()
    {
        CancelEmptyServerChangeMapTimer();

        float delay = _configMonitor.CurrentValue.ChangeMapDelaySeconds;
        _emptyServerTimerRemaining = delay;

        Core.Logger.LogInformation(Core.Localizer["changemap.scheduled_log", delay.ToString("F0")]);
        Core.PlayerManager.SendChat(Core.Localizer["changemap.scheduled_chat", delay.ToString("F0")]);

        _emptyServerChangeMapCts = Core.Scheduler.AddTimer(ctx =>
        {
            _emptyServerTimerRemaining -= 1.0f;

            // Announce at specific intervals
            if (_emptyServerTimerRemaining <= 0)
            {
                Core.Logger.LogInformation(Core.Localizer["changemap.now_log"]);
                Core.PlayerManager.SendChat(Core.Localizer["changemap.now_chat"]);
                ChangeMap();
                return TimerStep.Stop();
            }

            // Announce at 60, 30, 10, 5, 3, 2, 1 seconds
            if (_emptyServerTimerRemaining == 60 || _emptyServerTimerRemaining == 30 ||
                _emptyServerTimerRemaining == 10 || _emptyServerTimerRemaining == 5 ||
                _emptyServerTimerRemaining == 3 || _emptyServerTimerRemaining == 2 ||
                _emptyServerTimerRemaining == 1)
            {
                Core.PlayerManager.SendChat(Core.Localizer["changemap.countdown_chat", _emptyServerTimerRemaining.ToString("F0")]);
            }

            return TimerStep.WaitForSeconds(1.0f);
        });
    }

    private void CancelEmptyServerChangeMapTimer()
    {
        if (_emptyServerChangeMapCts != null)
        {
            _emptyServerChangeMapCts.Cancel();
            _emptyServerChangeMapCts = null;
        }
    }

    private void CancelScheduledCountdown()
    {
        if (_scheduledCountdownCts != null)
        {
            _scheduledCountdownCts.Cancel();
            _scheduledCountdownCts = null;
        }
    }

    private void StartScheduledCountdown(float remainingSeconds)
    {
        CancelScheduledCountdown();

        // If players are already online and config says not to ignore them,
        // skip the countdown entirely and re-schedule for the next day.
        if (!_configMonitor.CurrentValue.IgnorePlayersForScheduledChangeMap && HasRealPlayers())
        {
            Core.Logger.LogInformation(Core.Localizer["scheduled_changemap_players_online_log", DateTime.Now.ToString("HH:mm")]);
            SchedulePreciseCountdown(skipDueToPlayers: true);
            return;
        }

        int remainingInt = (int)Math.Ceiling(remainingSeconds);
        Core.Logger.LogInformation("Starting scheduled map change countdown: {Seconds}s remaining", remainingInt);
        Core.Scheduler.NextTick(() =>
        {
            Core.PlayerManager.SendChat(Core.Localizer["scheduled_changemap_countdown_chat", remainingInt.ToString()]);
        });

        _scheduledCountdownCts = Core.Scheduler.AddTimer(ctx =>
        {
            remainingInt -= 1;

            if (remainingInt <= 0)
            {
                _lastScheduledChangeMapDate = DateTime.Now.Date;

                // Final check: if players have joined during the countdown and config
                // says not to ignore them, skip the change and re-schedule.
                if (!_configMonitor.CurrentValue.IgnorePlayersForScheduledChangeMap && HasRealPlayers())
                {
                    Core.Logger.LogInformation(Core.Localizer["scheduled_changemap_players_online_log", DateTime.Now.ToString("HH:mm")]);
                    SchedulePreciseCountdown(skipDueToPlayers: true);
                    return TimerStep.Stop();
                }

                Core.Logger.LogInformation(Core.Localizer["scheduled_changemap_log", DateTime.Now.ToString("HH:mm")]);
                Core.Scheduler.NextTick(() =>
                {
                    Core.PlayerManager.SendChat(Core.Localizer["scheduled_changemap_chat"]);
                });
                ChangeMap();
                SchedulePreciseCountdown();
                return TimerStep.Stop();
            }

            // Announce at 30, 10, 5, 3, 2, 1 seconds
            if (remainingInt == 30 || remainingInt == 10 ||
                remainingInt == 5 || remainingInt == 3 ||
                remainingInt == 2 || remainingInt == 1)
            {
                Core.Scheduler.NextTick(() =>
                {
                    Core.PlayerManager.SendChat(Core.Localizer["scheduled_changemap_countdown_chat", remainingInt.ToString()]);
                });
            }

            return TimerStep.WaitForSeconds(1.0f);
        });
    }

    private void ChangeMap()
    {
        var config = _configMonitor.CurrentValue;
        if (!string.IsNullOrEmpty(config.WorkshopMapId))
        {
            Core.Logger.LogInformation("Changing to workshop map {WorkshopMapId}", config.WorkshopMapId);
            Core.Engine.ExecuteCommand($"host_workshop_map {config.WorkshopMapId}");
            return;
        }

        string map = _currentMap;
        if (string.IsNullOrEmpty(map))
            map = Core.Engine.GlobalVars.MapName;

        if (!string.IsNullOrEmpty(map))
        {
            Core.Logger.LogInformation("Changing map to {Map}", map);
            Core.Engine.ExecuteCommand($"changelevel {map}");
        }
        else
        {
            Core.Logger.LogWarning("Could not determine current map name. Falling back to quit.");
            Core.Engine.ExecuteCommand("quit");
        }
    }
}
