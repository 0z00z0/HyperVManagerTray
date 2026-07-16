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
    // Serialises SaveAndReload so overlapping saves (the rules editor commits each control edit via a
    // fire-and-forget Task.Run, so rapid edits can overlap) queue instead of colliding — otherwise a
    // second File.WriteAllText can hit the prior write's FileShare handle (spurious "could not save"),
    // or a stale snapshot can finish last and silently lose an update. A plain lock (not SemaphoreSlim)
    // so a ConfigReloaded subscriber that re-enters on the same thread can't self-deadlock.
    private readonly object _saveLock = new();

    /// <summary>Raised after the config is reloaded (file change or rule addition). NOT raised when a
    /// reload FAILED — see <see cref="OnDebounceElapsed"/>: nothing was reloaded, so telling subscribers
    /// otherwise would hand them stale data dressed as fresh (issue #39).</summary>
    public event EventHandler<AppConfig>? ConfigReloaded;

    /// <summary>
    /// Raised (at most once per broken save — see <see cref="_failureAnnounced"/>) when an unsolicited
    /// reload could not parse config.json, so the app is still running on the previous settings and the
    /// user's edit did NOT take effect (issue #39). The subscriber is expected to say so out loud.
    /// Startup is deliberately NOT routed through this event — no one is subscribed yet when the
    /// constructor runs; check <see cref="LastLoad"/> instead.
    /// </summary>
    public event EventHandler<ConfigLoadOutcome>? ConfigLoadFailed;

    /// <summary>The most recently loaded configuration. After a FAILED load this still holds the
    /// previous, successfully-loaded config — never a half-parsed or empty one.</summary>
    public AppConfig Current => _config;

    /// <summary>
    /// The outcome of the most recent <see cref="Load"/>. Exists mainly so startup can detect a corrupt
    /// config.json (which the constructor's own Load hits before any <see cref="ConfigLoadFailed"/>
    /// subscriber can exist) and surface it rather than silently starting on an empty default.
    /// </summary>
    public ConfigLoadOutcome LastLoad { get; private set; } = ConfigLoadOutcome.Success(0, 0);

    // True once a failed load has been announced, so a debounce storm (editors save repeatedly) or a
    // long-broken file doesn't balloon on every tick. Cleared by the next successful load, so a NEW
    // breakage always announces itself — the same say-it-once/re-arm discipline App applies to a failed
    // switch apply (_lastNotifiedFailure).
    private bool _failureAnnounced;

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

    /// <summary>
    /// Reads and deserialises config.json, ordering rules by priority, and REPORTS what happened
    /// (issue #39). Errors are still logged rather than thrown — a broken hand-edit must not take the
    /// tray app down — but they are no longer swallowed: the returned <see cref="ConfigLoadOutcome"/>
    /// (also stored on <see cref="LastLoad"/>) lets every caller distinguish "your edit is now live"
    /// from "your edit was rejected and the old settings are still running", which is the whole point.
    /// On failure <see cref="Current"/> is left untouched — the last good config stays live.
    /// </summary>
    public ConfigLoadOutcome Load()
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
            _logger.LogInformation("Config loaded from {Path} ({RuleCount} rules, {VmCount} VMs)",
                _configPath, _config.Rules.Count, _config.VirtualMachines.Count);
            return LastLoad = ConfigLoadOutcome.Success(_config.Rules.Count, _config.VirtualMachines.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load config from {Path} — keeping the previously loaded config", _configPath);
            return LastLoad = ConfigLoadOutcome.Failure(ex.Message);
        }
    }

    private void OnDebounceElapsed(object? _)
    {
        _logger.LogInformation("Config file changed — reloading");
        var outcome = Load();

        if (!outcome.Succeeded)
        {
            // Nothing was reloaded. Raising ConfigReloaded here would hand NetworkMonitor the stale
            // config as if it were the user's new intent (and trigger a switch re-evaluation off it),
            // and would tell an open Settings window to re-render the old values as freshly read —
            // the exact "surface claims a state the app hasn't confirmed" defect #37 fixed elsewhere.
            if (_failureAnnounced) return;   // same broken file, still broken — say it once
            _failureAnnounced = true;
            try { ConfigLoadFailed?.Invoke(this, outcome); }
            catch (Exception ex) { _logger.LogError(ex, "A ConfigLoadFailed subscriber threw an exception"); }
            return;
        }

        _failureAnnounced = false;   // parsed again — re-arm for the next breakage
        try { ConfigReloaded?.Invoke(this, _config); }
        catch (Exception ex) { _logger.LogError(ex, "A ConfigReloaded subscriber threw an exception"); }
    }

    /// <summary>
    /// Appends a new rule to config.json, saves it, and triggers an immediate reload.
    /// The FileSystemWatcher is paused during the write to avoid a redundant debounced reload.
    /// </summary>
    public void AddBridgedRule(NetworkRule rule) => SaveAndReload(
        With(rules: [.. _config.Rules, rule]),
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
            With(vms: [.. _config.VirtualMachines, newVm]),
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
            With(vms: [.. _config.VirtualMachines.Where(v =>
                !v.Name.Equals(name, StringComparison.OrdinalIgnoreCase))]),
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
            With(logLevel: level),
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
            With(vms:
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
            ]),
            $"Bridge-lost action for VM '{vmName}' set to {normalizedAction ?? "none"} ({normalizedDelay}s) and saved to {_configPath}",
            $"Failed to save bridge-lost action for VM '{vmName}'");
    }

    /// <summary>
    /// Updates which of a managed VM's network adapters the app reconnects
    /// (<see cref="VmTarget.NicName"/>) — issue #41. This was previously reachable ONLY by hand-editing
    /// config.json: a VM with a renamed or second synthetic adapter silently never reconnected, and the
    /// file was the only fix. Blank restores the Hyper-V default ("Network Adapter") rather than
    /// persisting an empty name that would match no adapter at all. No-op when the VM is absent or the
    /// value is already stored.
    /// </summary>
    /// <remarks>
    /// Follows the same discipline as <see cref="SetVmBridgeLostAction"/>: the live <see cref="VmTarget"/>
    /// is never mutated — a fresh copy carrying the new NIC name (and every other field verbatim, so a
    /// hand-edited bridge-lost action can't be dropped by a NIC edit) replaces the target in a new list,
    /// swapped in only via <see cref="Load"/> after a successful write.
    /// </remarks>
    public void SetVmNicName(string vmName, string? nicName)
    {
        var vm = _config.VirtualMachines.FirstOrDefault(v =>
            v.Name.Equals(vmName, StringComparison.OrdinalIgnoreCase));
        if (vm is null)
        {
            _logger.LogInformation("SetVmNicName: '{Name}' not found in config — skipping.", vmName);
            return;
        }

        var normalized = SettingsOptions.NormalizeNicName(nicName);
        if (string.Equals(vm.NicName, normalized, StringComparison.Ordinal))
        {
            _logger.LogInformation("SetVmNicName: '{Name}' already uses '{Nic}' — skipping.", vmName, normalized);
            return;
        }

        SaveAndReload(
            With(vms:
            [
                .. _config.VirtualMachines.Select(v =>
                    v.Name.Equals(vmName, StringComparison.OrdinalIgnoreCase)
                        ? new VmTarget
                          {
                              Name                     = v.Name,
                              NicName                  = normalized,
                              OnBridgeLostAction       = v.OnBridgeLostAction,
                              OnBridgeLostDelaySeconds = v.OnBridgeLostDelaySeconds,
                          }
                        : v)
            ]),
            $"NIC name for VM '{vmName}' set to '{normalized}' and saved to {_configPath}",
            $"Failed to save the NIC name for VM '{vmName}'");
    }

    /// <summary>
    /// Replaces the entire rules list (issue #23 — the Network editor). Each rule is sanitised through a
    /// fresh copy before it is written (name/switch trimmed, priority clamped, a valid MAC canonicalised,
    /// CIDR/MAC blanks→null, an INVALID MAC/CIDR dropped to null, target-VM list cleaned) so a malformed
    /// hand-edit can't be persisted verbatim. Follows
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
            With(rules: cleaned),
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
            With(fallback: new FallbackAction { VirtualSwitch = sw, TargetVms = targets }),
            $"Fallback switch set to '{sw}' ({targets.Count} target VM(s)) and saved to {_configPath}",
            "Failed to save fallback switch");
    }

    /// <summary>
    /// Returns a sanitised deep copy of a rule (see <see cref="SaveRules"/>). This is the persistence
    /// boundary that makes the round-trip guarantee true: a MAC or CIDR that is NOT well-formed is
    /// dropped to <c>null</c> ("don't match on it") rather than persisted verbatim, so a malformed
    /// hand-edited value can't survive an unrelated edit-and-save (the UI already blocks committing an
    /// invalid value, but this enforces it even for a value that reached here another way). A valid MAC
    /// is canonicalised; blanks become null.
    /// </summary>
    internal static NetworkRule CleanRule(NetworkRule r) => new()
    {
        Name          = r.Name?.Trim() ?? string.Empty,
        Priority      = SettingsOptions.NormalizePriority(r.Priority),
        VirtualSwitch = r.VirtualSwitch?.Trim() ?? string.Empty,
        TargetVms     = SettingsOptions.CleanVmList(r.TargetVms ?? []),
        AutoStart     = r.AutoStart,
        Conditions    = new RuleConditions
        {
            // IsValidMac/IsValidCidr treat null/blank as valid ("don't match"); an invalid, non-blank
            // value fails the guard and is dropped to null rather than written back malformed.
            AdapterMac = SettingsOptions.IsValidMac(r.Conditions?.AdapterMac)
                             ? SettingsOptions.CanonicalizeMac(r.Conditions?.AdapterMac)
                             : null,
            IpCidr     = SettingsOptions.IsValidCidr(r.Conditions?.IpCidr)
                             ? SettingsOptions.BlankToNull(r.Conditions?.IpCidr)
                             : null,
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
            With(adapterNames: [.. others, entry]),
            $"Adapter-name record for '{entry.DeviceInstanceId}' saved to {_configPath}",
            $"Failed to save adapter-name record for '{entry.DeviceInstanceId}'");
    }

    /// <summary>
    /// Persists the Settings window's last on-screen rect (physical px) to config.json (issue #31), so
    /// it reopens where the user left it.  A no-op when the rect is unchanged, so opening and closing
    /// Settings without moving it doesn't rewrite the file.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT raise <see cref="ConfigReloaded"/> (<c>raiseReloaded: false</c>): a window
    /// rect has no bearing on any subscriber, and the only two subscribers would both misbehave — the
    /// <see cref="NetworkMonitor"/> would schedule a pointless network re-evaluation (which can move a
    /// VM's switch) every time Settings is closed, and an open Settings window would rebuild all of its
    /// sections in response to its own write.  Throws on a write failure like the other mutators; the
    /// caller (a <c>Closed</c> handler) is expected to swallow it — a lost rect is cosmetic.
    /// </remarks>
    public void SaveSettingsWindowRect(WindowRect rect)
    {
        if (_config.SettingsWindowX      == rect.X     && _config.SettingsWindowY      == rect.Y &&
            _config.SettingsWindowWidth  == rect.Width && _config.SettingsWindowHeight == rect.Height)
        {
            _logger.LogDebug("SaveSettingsWindowRect: unchanged — skipping.");
            return;
        }

        SaveAndReload(
            With(settingsWindowRect: rect),
            $"Settings window rect {rect.Width}×{rect.Height} at ({rect.X},{rect.Y}) saved to {_configPath}",
            "Failed to save the Settings window rect",
            raiseReloaded: false);
    }

    /// <summary>
    /// Serialises <paramref name="updated"/> to config.json, reloads, and (unless
    /// <paramref name="raiseReloaded"/> is false) raises <see cref="ConfigReloaded"/> — with the
    /// watcher paused so the write itself doesn't trigger a second, debounced reload.  On failure the
    /// error is logged and rethrown so the caller can surface it.  Shared by every config-mutating
    /// method.
    /// </summary>
    /// <param name="successMessage">Message logged on success (already fully formatted).</param>
    /// <param name="failureMessage">Message logged if the write throws.</param>
    /// <param name="raiseReloaded">
    /// False to persist and refresh <see cref="Current"/> without notifying subscribers — for a write
    /// no subscriber cares about (see <see cref="SaveSettingsWindowRect"/>).  Defaults to true so every
    /// existing caller keeps its current behaviour.
    /// </param>
    private void SaveAndReload(AppConfig updated, string successMessage, string failureMessage,
                               bool raiseReloaded = true)
    {
        // Serialise the whole write+reload so concurrent saves queue rather than clobbering each other.
        lock (_saveLock)
        {
            _watcher.EnableRaisingEvents = false;
            try
            {
                File.WriteAllText(_configPath, JsonSerializer.Serialize(updated, WriteOptions));
                _logger.LogInformation("{Message}", successMessage);

                // We just serialised this object ourselves, so a failed read-back is a bug (or the file
                // was clobbered between the write and the read) — either way _config still holds the
                // PREVIOUS config, so announcing a reload would publish stale state as new (issue #39).
                if (!Load().Succeeded)
                {
                    _logger.LogError("Config was written to {Path} but could not be re-read — subscribers "
                                     + "were NOT notified and the previously loaded config is still live.",
                                     _configPath);
                    return;
                }

                if (raiseReloaded) ConfigReloaded?.Invoke(this, _config);
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
    }

    /// <summary>
    /// Builds a copy of <see cref="Current"/> with only the named fields replaced (each defaults to the
    /// current value). The config mutators funnel through this instead of hand-writing a 5-field
    /// <see cref="AppConfig"/> initialiser each — a hand-written copy that silently drops a field (a
    /// real drift risk as fields are added) can't happen when every unspecified field falls through to
    /// the live value.
    /// </summary>
    private AppConfig With(
        List<VmTarget>? vms = null,
        List<NetworkRule>? rules = null,
        FallbackAction? fallback = null,
        List<AdapterNameOverride>? adapterNames = null,
        LogLevel? logLevel = null,
        WindowRect? settingsWindowRect = null) => new()
        {
            VirtualMachines = vms          ?? _config.VirtualMachines,
            Rules           = rules        ?? _config.Rules,
            Fallback        = fallback     ?? _config.Fallback,
            AdapterNames    = adapterNames ?? _config.AdapterNames,
            LogLevel        = logLevel     ?? _config.LogLevel,

            // The Settings window rect (issue #31) MUST be carried through here like every other field:
            // this method builds the object that is serialised over config.json wholesale, so a field it
            // omits is not "left alone" — it is written back as null and permanently lost. Without these
            // four lines, changing the log level (or any rule) would silently erase the saved rect.
            SettingsWindowX      = settingsWindowRect?.X      ?? _config.SettingsWindowX,
            SettingsWindowY      = settingsWindowRect?.Y      ?? _config.SettingsWindowY,
            SettingsWindowWidth  = settingsWindowRect?.Width  ?? _config.SettingsWindowWidth,
            SettingsWindowHeight = settingsWindowRect?.Height ?? _config.SettingsWindowHeight,
        };

    /// <summary>Returns the expected config.json path: next to the executable.</summary>
    public static string GetConfigPath() =>
        Path.Combine(AppContext.BaseDirectory, "config.json");

    /// <summary>
    /// Writes <see cref="DefaultConfig.Json"/> to <paramref name="configPath"/> if no file is there, and
    /// returns whether it created one (issue #38). Startup previously answered a missing config with an
    /// error box and an immediate exit — in an app that ships a full rules editor and tray onboarding
    /// flows, i.e. it refused to start into the very UI that would have fixed the problem. It can
    /// trivially heal itself instead, so it does.
    ///
    /// <para>Called BEFORE the <see cref="ConfigManager"/> (and hence its FileSystemWatcher) exists, so
    /// this write can't trigger a reload, and no <see cref="ConfigReloaded"/> can fire off it — a
    /// startup-path write must never kick <see cref="NetworkMonitor"/> into moving a VM's switch.</para>
    ///
    /// <para>Never throws: if the directory is read-only, the app still starts and the subsequent
    /// <see cref="Load"/> reports the missing file through <see cref="LastLoad"/>, which startup
    /// balloons. A cosmetic self-heal is not worth a fatal.</para>
    /// </summary>
    /// <returns>True if a default config was created; false if one already existed or creation failed.</returns>
    public static bool CreateDefaultIfMissing(string configPath, ILogger? logger = null)
    {
        try
        {
            if (File.Exists(configPath)) return false;

            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            File.WriteAllText(configPath, DefaultConfig.Json);
            logger?.LogInformation("No config.json at {Path} — created a default one", configPath);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Could not create a default config at {Path}", configPath);
            return false;
        }
    }

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
