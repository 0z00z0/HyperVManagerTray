using System.Text.Json;
using System.Text.Json.Serialization;
using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using Microsoft.Extensions.Logging;

namespace HyperVManagerTray.Services;

/// <summary>
/// Loads <c>config.json</c>, exposes the current <see cref="AppConfig"/>, and watches the file
/// for changes (debounced) so edits take effect without a restart.  Rules are kept sorted by
/// ascending priority after every load.
/// </summary>
public sealed class ConfigManager : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented          = true,
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters             = { new JsonStringEnumConverter() }
    };

    private readonly string _configPath;
    // Non-generic ILogger so App can route ConfigManager's save/reload lines to the "ui" category →
    // ui.log (issue #21). The category is chosen by App at construction, not by the type parameter.
    private readonly ILogger _logger;
    // Live log-level gate (issue #22). Kept in sync with the loaded config.LogLevel on every Load so a
    // level change — via UpdateLogLevel (Settings) or a manual config.json edit — applies with no restart.
    private readonly LogLevelSwitch? _levelSwitch;
    private readonly FileSystemWatcher _watcher;
    private readonly System.Threading.Timer _debounceTimer;
    private AppConfig _config = new();

    /// <summary>Raised after the config is reloaded (file change or rule addition).</summary>
    public event EventHandler<AppConfig>? ConfigReloaded;

    /// <summary>The most recently loaded configuration.</summary>
    public AppConfig Current => _config;

    public ConfigManager(string configPath, ILogger logger, LogLevelSwitch? levelSwitch = null)
    {
        _configPath  = configPath;
        _logger      = logger;
        _levelSwitch = levelSwitch;
        _debounceTimer = new System.Threading.Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);

        _watcher = new FileSystemWatcher(Path.GetDirectoryName(configPath)!, Path.GetFileName(configPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _watcher.Changed += (_, _) => _debounceTimer.Change(500, Timeout.Infinite);

        Load();
    }

    /// <summary>Reads and deserialises config.json, ordering rules by priority. Errors are logged, not thrown.</summary>
    public void Load()
    {
        try
        {
            var json = File.ReadAllText(_configPath);
            var loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            loaded.Rules = [.. loaded.Rules.OrderBy(r => r.Priority)];
            _config = loaded;
            // Apply the loaded verbosity to the live gate immediately (issue #22) — this is the single
            // place every path (startup, UpdateLogLevel's SaveAndReload, a manual file edit) flows through.
            if (_levelSwitch is not null) _levelSwitch.MinimumLevel = loaded.LogLevel;
            _logger.LogInformation("Config loaded from {Path} ({RuleCount} rules)", _configPath, _config.Rules.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load config from {Path}", _configPath);
        }
    }

    private void OnDebounceElapsed(object? _)
    {
        _logger.LogInformation("Config file changed — reloading");
        Load();
        try { ConfigReloaded?.Invoke(this, _config); }
        catch (Exception ex) { _logger.LogError(ex, "A ConfigReloaded subscriber threw an exception"); }
    }

    /// <summary>
    /// Appends a new rule to config.json, saves it, and triggers an immediate reload.
    /// The FileSystemWatcher is paused during the write to avoid a redundant debounced reload.
    /// </summary>
    public void AddBridgedRule(NetworkRule rule) => SaveAndReload(
        new AppConfig
        {
            VirtualMachines = _config.VirtualMachines,
            Rules           = [.. _config.Rules, rule],
            Fallback        = _config.Fallback,
            AdapterNames    = _config.AdapterNames,
            LogLevel        = _config.LogLevel,
        },
        $"Rule '{rule.Name}' added and saved to {_configPath}",
        $"Failed to save new rule '{rule.Name}'");

    /// <summary>
    /// Appends a new <see cref="VmTarget"/> to config.json and reloads.
    /// Does nothing if a VM with the same name is already present.
    /// </summary>
    public void AddVmToConfig(string name, string nicName)
    {
        if (_config.VirtualMachines.Any(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogInformation("AddVmToConfig: '{Name}' is already in config — skipping.", name);
            return;
        }

        var newVm = new VmTarget
        {
            Name    = name,
            NicName = string.IsNullOrWhiteSpace(nicName) ? "Network Adapter" : nicName,
        };

        SaveAndReload(
            new AppConfig
            {
                VirtualMachines = [.. _config.VirtualMachines, newVm],
                Rules           = _config.Rules,
                Fallback        = _config.Fallback,
                AdapterNames    = _config.AdapterNames,
                LogLevel        = _config.LogLevel,
            },
            $"VM '{name}' added and saved to {_configPath}",
            $"Failed to save new VM '{name}'");
    }

    /// <summary>
    /// Removes the named VM from config.json and reloads.
    /// Does nothing if no VM with that name exists.
    /// </summary>
    public void RemoveVmFromConfig(string name)
    {
        if (!_config.VirtualMachines.Any(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogInformation("RemoveVmFromConfig: '{Name}' not found in config — skipping.", name);
            return;
        }

        SaveAndReload(
            new AppConfig
            {
                VirtualMachines = [.. _config.VirtualMachines.Where(v =>
                    !v.Name.Equals(name, StringComparison.OrdinalIgnoreCase))],
                Rules           = _config.Rules,
                Fallback        = _config.Fallback,
                AdapterNames    = _config.AdapterNames,
                LogLevel        = _config.LogLevel,
            },
            $"VM '{name}' removed and saved to {_configPath}",
            $"Failed to remove VM '{name}'");
    }

    /// <summary>
    /// Persists a new <see cref="AppConfig.LogLevel"/> to config.json and reloads (issue #18 —
    /// surfaced in the Settings window; previously config.json-only).  A no-op when the level is
    /// unchanged, so opening Settings and closing it doesn't needlessly rewrite the file.
    /// The reload runs through <see cref="Load"/>, which applies the new level to the live
    /// <see cref="LogLevelSwitch"/> immediately — all logs pick it up with no restart (issue #22).
    /// </summary>
    public void UpdateLogLevel(LogLevel level)
    {
        if (_config.LogLevel == level)
        {
            _logger.LogInformation("UpdateLogLevel: already {Level} — skipping.", level);
            return;
        }

        SaveAndReload(
            new AppConfig
            {
                VirtualMachines = _config.VirtualMachines,
                Rules           = _config.Rules,
                Fallback        = _config.Fallback,
                AdapterNames    = _config.AdapterNames,
                LogLevel        = level,
            },
            $"Log level set to {level} and saved to {_configPath}",
            $"Failed to save log level {level}");
    }

    /// <summary>
    /// Updates the "when the bridged network is lost" action and delay for a managed VM (issue #18 —
    /// surfaced in the Settings window; previously config.json-only).  <paramref name="action"/> is the
    /// canonical string (null = do nothing); <paramref name="delaySeconds"/> is clamped to a sane range.
    /// Does nothing if no VM with that name is present, or if the value is already what's stored.
    /// </summary>
    /// <remarks>
    /// The live <see cref="VmTarget"/> is NOT mutated: a fresh copy carrying the new values replaces the
    /// target in a new list which is only swapped in via <see cref="Load"/> after a successful write —
    /// so a failed save (e.g. an OneDrive/AV file lock) can't leave <c>_config</c> diverged from disk
    /// with a possibly-destructive action armed (the NetworkMonitor reads these values live).
    /// </remarks>
    public void SetVmBridgeLostAction(string vmName, string? action, int delaySeconds)
    {
        var vm = _config.VirtualMachines.FirstOrDefault(v =>
            v.Name.Equals(vmName, StringComparison.OrdinalIgnoreCase));
        if (vm is null)
        {
            _logger.LogInformation("SetVmBridgeLostAction: '{Name}' not found in config — skipping.", vmName);
            return;
        }

        // Honour the XML-doc's promise: canonicalise the action and clamp the delay before persisting.
        var normalizedAction = SettingsOptions.NormalizeBridgeLostAction(action);
        var normalizedDelay  = SettingsOptions.NormalizeDelaySeconds(delaySeconds);

        if (vm.OnBridgeLostAction == normalizedAction && vm.OnBridgeLostDelaySeconds == normalizedDelay)
        {
            _logger.LogInformation("SetVmBridgeLostAction: '{Name}' already {Action} ({Delay}s) — skipping.",
                vmName, normalizedAction ?? "none", normalizedDelay);
            return;
        }

        SaveAndReload(
            new AppConfig
            {
                VirtualMachines =
                [
                    .. _config.VirtualMachines.Select(v =>
                        v.Name.Equals(vmName, StringComparison.OrdinalIgnoreCase)
                            ? new VmTarget
                              {
                                  Name                     = v.Name,
                                  NicName                  = v.NicName,
                                  OnBridgeLostAction       = normalizedAction,
                                  OnBridgeLostDelaySeconds = normalizedDelay,
                              }
                            : v)
                ],
                Rules           = _config.Rules,
                Fallback        = _config.Fallback,
                AdapterNames    = _config.AdapterNames,
                LogLevel        = _config.LogLevel,
            },
            $"Bridge-lost action for VM '{vmName}' set to {normalizedAction ?? "none"} ({normalizedDelay}s) and saved to {_configPath}",
            $"Failed to save bridge-lost action for VM '{vmName}'");
    }

    /// <summary>
    /// Replaces the entire rules list (issue #23 — the Network editor). Each rule is sanitised through a
    /// fresh copy before it is written (name/switch trimmed, priority clamped, MAC canonicalised, CIDR/MAC
    /// blanks→null, target-VM list cleaned) so a malformed hand-edit can't be persisted verbatim. Follows
    /// the same build-fresh-then-swap discipline as the other mutators: the new list is only swapped in via
    /// <see cref="Load"/> after a successful write, and the live <see cref="AppConfig"/> is never mutated.
    /// A no-op when the cleaned list already equals what's stored.
    /// </summary>
    public void SaveRules(IReadOnlyList<NetworkRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var cleaned = rules.Select(CleanRule).ToList();

        if (RulesEqual(cleaned, _config.Rules))
        {
            _logger.LogInformation("SaveRules: rules unchanged — skipping.");
            return;
        }

        SaveAndReload(
            new AppConfig
            {
                VirtualMachines = _config.VirtualMachines,
                Rules           = cleaned,
                Fallback        = _config.Fallback,
                AdapterNames    = _config.AdapterNames,
                LogLevel        = _config.LogLevel,
            },
            $"Saved {cleaned.Count} network rule(s) to {_configPath}",
            "Failed to save network rules");
    }

    /// <summary>
    /// Updates the fallback switch and its target VMs (issue #23 — previously config.json-only). The
    /// switch is trimmed (blank keeps the current value rather than writing an empty switch that would
    /// break binding); the target-VM list is cleaned. No-op when nothing changed.
    /// </summary>
    public void SetFallback(string virtualSwitch, IEnumerable<string> targetVms)
    {
        var sw      = string.IsNullOrWhiteSpace(virtualSwitch) ? _config.Fallback.VirtualSwitch : virtualSwitch.Trim();
        var targets = SettingsOptions.CleanVmList(targetVms ?? []);

        if (sw.Equals(_config.Fallback.VirtualSwitch, StringComparison.Ordinal)
            && targets.SequenceEqual(_config.Fallback.TargetVms, StringComparer.Ordinal))
        {
            _logger.LogInformation("SetFallback: unchanged — skipping.");
            return;
        }

        SaveAndReload(
            new AppConfig
            {
                VirtualMachines = _config.VirtualMachines,
                Rules           = _config.Rules,
                Fallback        = new FallbackAction { VirtualSwitch = sw, TargetVms = targets },
                AdapterNames    = _config.AdapterNames,
                LogLevel        = _config.LogLevel,
            },
            $"Fallback switch set to '{sw}' ({targets.Count} target VM(s)) and saved to {_configPath}",
            "Failed to save fallback switch");
    }

    /// <summary>Returns a sanitised deep copy of a rule (see <see cref="SaveRules"/>).</summary>
    internal static NetworkRule CleanRule(NetworkRule r) => new()
    {
        Name          = r.Name?.Trim() ?? string.Empty,
        Priority      = SettingsOptions.NormalizePriority(r.Priority),
        VirtualSwitch = r.VirtualSwitch?.Trim() ?? string.Empty,
        TargetVms     = SettingsOptions.CleanVmList(r.TargetVms ?? []),
        AutoStart     = r.AutoStart,
        Conditions    = new RuleConditions
        {
            AdapterMac = SettingsOptions.CanonicalizeMac(r.Conditions?.AdapterMac),
            IpCidr     = SettingsOptions.BlankToNull(r.Conditions?.IpCidr),
        },
    };

    /// <summary>Structural equality of two rule lists — used to skip a redundant write.</summary>
    private static bool RulesEqual(IReadOnlyList<NetworkRule> a, IReadOnlyList<NetworkRule> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            var x = a[i];
            var y = b[i];
            if (!string.Equals(x.Name, y.Name, StringComparison.Ordinal)
                || x.Priority != y.Priority
                || !string.Equals(x.VirtualSwitch, y.VirtualSwitch, StringComparison.Ordinal)
                || x.AutoStart != y.AutoStart
                || !string.Equals(x.Conditions?.AdapterMac, y.Conditions?.AdapterMac, StringComparison.Ordinal)
                || !string.Equals(x.Conditions?.IpCidr, y.Conditions?.IpCidr, StringComparison.Ordinal)
                || !x.TargetVms.SequenceEqual(y.TargetVms, StringComparer.Ordinal))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Inserts or updates the saved-original-name record for a renamed adapter (issue #15), keyed by
    /// <see cref="AdapterNameOverride.DeviceInstanceId"/>, then saves and reloads.  Any existing record
    /// for the same device is replaced (so a re-rename updates <c>CurrentFriendlyName</c> without
    /// losing the true original).
    /// </summary>
    public void UpsertAdapterName(AdapterNameOverride entry)
    {
        var others = _config.AdapterNames
            .Where(a => !a.DeviceInstanceId.Equals(entry.DeviceInstanceId, StringComparison.OrdinalIgnoreCase));

        SaveAndReload(
            new AppConfig
            {
                VirtualMachines = _config.VirtualMachines,
                Rules           = _config.Rules,
                Fallback        = _config.Fallback,
                AdapterNames    = [.. others, entry],
                LogLevel        = _config.LogLevel,
            },
            $"Adapter-name record for '{entry.DeviceInstanceId}' saved to {_configPath}",
            $"Failed to save adapter-name record for '{entry.DeviceInstanceId}'");
    }

    /// <summary>
    /// Serialises <paramref name="updated"/> to config.json, reloads, and raises
    /// <see cref="ConfigReloaded"/> — with the watcher paused so the write itself doesn't trigger a
    /// second, debounced reload.  On failure the error is logged and rethrown so the caller can
    /// surface it.  Shared by every config-mutating method.
    /// </summary>
    /// <param name="successMessage">Message logged on success (already fully formatted).</param>
    /// <param name="failureMessage">Message logged if the write throws.</param>
    private void SaveAndReload(AppConfig updated, string successMessage, string failureMessage)
    {
        _watcher.EnableRaisingEvents = false;
        try
        {
            File.WriteAllText(_configPath, JsonSerializer.Serialize(updated, WriteOptions));
            _logger.LogInformation("{Message}", successMessage);

            Load();
            ConfigReloaded?.Invoke(this, _config);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Message}", failureMessage);
            throw;
        }
        finally
        {
            _watcher.EnableRaisingEvents = true;
        }
    }

    /// <summary>Returns the expected config.json path: next to the executable.</summary>
    public static string GetConfigPath() =>
        Path.Combine(AppContext.BaseDirectory, "config.json");

    /// <summary>
    /// Reads just the <see cref="AppConfig.LogLevel"/> from config.json, for use at startup before
    /// the logger (and hence a full <see cref="ConfigManager"/>) exists.  Falls back to
    /// <see cref="LogLevel.Debug"/> if the file is missing or unreadable.
    /// </summary>
    public static LogLevel ReadLogLevel(string configPath)
    {
        try
        {
            if (!File.Exists(configPath)) return LogLevel.Debug;
            var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(configPath), JsonOptions);
            return cfg?.LogLevel ?? LogLevel.Debug;
        }
        catch { return LogLevel.Debug; }
    }

    public void Dispose()
    {
        _debounceTimer.Dispose();
        _watcher.Dispose();
    }
}
