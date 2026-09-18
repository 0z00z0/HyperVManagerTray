using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace HyperVManagerTray.Models;

/// <summary>
/// The switch and VMs to use when no <see cref="NetworkRule"/> matches the current network
/// (typically a NAT switch such as the Hyper-V "Default Switch").
/// </summary>
public sealed class FallbackAction : ISwitchTarget
{
    /// <inheritdoc/>
    public string SwitchId { get; set; } = string.Empty;

    /// <inheritdoc/>
    public string SwitchName { get; set; } = string.Empty;

    /// <inheritdoc/>
    [JsonPropertyName("virtualSwitch")]
    public string? LegacyVirtualSwitch { get; set; }

    /// <inheritdoc/>
    public List<string> TargetVmIds { get; set; } = [];

    /// <inheritdoc/>
    [JsonPropertyName("targetVms")]
    public List<string>? LegacyTargetVms { get; set; }
}

/// <summary>
/// Remembers the original <c>FriendlyName</c> of a physical network adapter before the user first
/// renamed its description (issue #15), so the rename dialog's "Reset" can restore it.  Keyed by the
/// PnP <c>DeviceInstanceID</c> (stable across reboots and dock re-plugs).  Reset always *writes* the
/// saved value back — it never deletes the FriendlyName, which on a multi-instance device would let
/// the description fall back to the un-numbered base name and produce two identical descriptions
/// (investigation §5.4).
/// </summary>
public sealed class AdapterNameOverride
{
    /// <summary>PnP device instance id, e.g. <c>USB\VID_0BDA&amp;PID_8153\000002000000</c>.</summary>
    public string DeviceInstanceId { get; set; } = string.Empty;

    /// <summary>
    /// The name Reset restores: the device's FACTORY description, derived from the driver's
    /// <c>DeviceDesc</c> — which a rename never touches — rather than from whatever
    /// <c>FriendlyName</c> happened to be on disk at first-rename time (issue #33). Falls back to that
    /// FriendlyName only when no factory description can be derived; empty when there is nothing to
    /// restore. Records written before #33 are repaired in place on the next rename-flow run.
    /// </summary>
    public string OriginalFriendlyName { get; set; } = string.Empty;

    /// <summary>
    /// True when there is nothing to restore, so Reset is not offered (deleting is disallowed — see
    /// the type summary).  Only reachable on the #33 fallback path: whenever a factory description IS
    /// derivable it is restorable, so this stays false even for a device that carried no explicit
    /// FriendlyName of its own.
    /// </summary>
    public bool OriginalWasAbsent { get; set; }

    /// <summary>Adapter MAC (colon-separated) at rename time — a human-friendly cross-check only.</summary>
    public string Mac { get; set; } = string.Empty;

    /// <summary>Date of the first rename (<c>yyyy-MM-dd</c>).</summary>
    public string RenamedOn { get; set; } = string.Empty;

    /// <summary>The name the app last applied — lets a future driver-update revert be detected.</summary>
    public string CurrentFriendlyName { get; set; } = string.Empty;
}

/// <summary>
/// Root configuration object deserialised from <c>config.json</c>: the managed VMs,
/// the priority-ordered match rules, and the fallback action.
/// </summary>
public sealed class AppConfig
{
    /// <summary>VMs this app manages, identified by VM ID.</summary>
    public List<VmTarget> VirtualMachines { get; set; } = [];

    /// <summary>Network-to-switch rules, evaluated in ascending <see cref="NetworkRule.Priority"/> order.</summary>
    public List<NetworkRule> Rules { get; set; } = [];

    /// <summary>Action applied when no rule matches the current host network.</summary>
    public FallbackAction Fallback { get; set; } = new();

    /// <summary>
    /// Saved original adapter descriptions, one per adapter the user has renamed (issue #15).
    /// Used to power the rename dialog's "Reset".  Empty for a fresh install.
    /// </summary>
    public List<AdapterNameOverride> AdapterNames { get; set; } = [];

    /// <summary>
    /// Minimum severity for the file logs — the single live gate (issue #22) shared by
    /// <c>switcher.log</c>, <c>vm-power.log</c> and <c>ui.log</c> alike.  One of <c>Trace</c>,
    /// <c>Debug</c>, <c>Information</c>, <c>Warning</c>, <c>Error</c>, <c>Critical</c>, <c>None</c>.
    /// Defaults to <c>Debug</c> so diagnostic detail (e.g. rule-evaluation decisions and WMI
    /// return codes) is captured.
    /// </summary>
    public LogLevel LogLevel { get; set; } = LogLevel.Debug;

    /// <summary>
    /// MQTT publishing (issue #75). Never null in a loaded config: <c>ConfigManager.Load</c> replaces a
    /// missing section, or one hand-edited to <c>"mqtt": null</c>, with inert defaults — so every
    /// consumer can read it without a null check and a config written before the section existed still
    /// loads.
    /// </summary>
    public MqttSection Mqtt { get; set; } = new();

    #region Settings window placement (issue #31)

    // The Settings window's last on-screen rect, in PHYSICAL SCREEN PIXELS (what AppWindow.Position /
    // AppWindow.Size report and consume — not DIPs), so it reopens where the user left it.
    //
    // Kept here in config.json rather than a second store: this app already has exactly one settings
    // file, and WinUIEx's PersistenceId is not an option — it persists through
    // Windows.Storage.ApplicationData, which an unpackaged app like this one does not have.
    //
    // All four are nullable and only honoured as a complete set (WindowPlacement.TryGetSavedRect):
    // absent (a fresh install, or a config written before this existed) means "no saved rect — use the
    // default centred on the cursor's monitor". They serialise through the same reflection-based
    // JsonSerializer as every other property here: camelCase on write (settingsWindowX, …),
    // case-insensitive on read, and omitted entirely while null (DefaultIgnoreCondition.WhenWritingNull).

    /// <summary>Left edge of the Settings window at last close (physical px); null = never saved.</summary>
    public int? SettingsWindowX { get; set; }

    /// <summary>Top edge of the Settings window at last close (physical px); null = never saved.</summary>
    public int? SettingsWindowY { get; set; }

    /// <summary>Width of the Settings window at last close (physical px); null = never saved.</summary>
    public int? SettingsWindowWidth { get; set; }

    /// <summary>Height of the Settings window at last close (physical px); null = never saved.</summary>
    public int? SettingsWindowHeight { get; set; }

    #endregion

    /// <summary>
    /// The distinct switches the rules identify — the set of bridged switches whose host vNICs may need
    /// repair. Used by both the startup self-heal and the tray "Repair host networking" action. A rule
    /// whose switch is not identified yet contributes nothing.
    /// </summary>
    public IEnumerable<SwitchRef> RuleSwitches => Rules
        .Where(r => !string.IsNullOrWhiteSpace(r.SwitchId))
        .Select(r => new SwitchRef(r.SwitchId, r.SwitchName))
        .DistinctBy(s => s.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>The managed VM with <paramref name="vmId"/>, or null. The one lookup every caller uses.</summary>
    public VmTarget? FindVm(string? vmId) =>
        string.IsNullOrWhiteSpace(vmId)
            ? null
            : VirtualMachines.FirstOrDefault(v => string.Equals(v.Id, vmId, StringComparison.OrdinalIgnoreCase));

    /// <summary>The managed VMs as references, in config order, the ones not identified yet left out.
    /// Derived, so never written to the file.</summary>
    [JsonIgnore]
    public IReadOnlyList<VmRef> IdentifiedVms =>
        [.. VirtualMachines.Where(v => !string.IsNullOrWhiteSpace(v.Id)).Select(v => v.Ref)];

    /// <summary><paramref name="vmIds"/> as references carrying each managed VM's label.</summary>
    public IReadOnlyList<VmRef> VmRefs(IEnumerable<string> vmIds) =>
        [.. vmIds.Select(id => FindVm(id)?.Ref ?? new VmRef(id, ""))];
}

/// <summary>A virtual switch as the app passes it around: the ID that identifies it, the name shown.</summary>
public sealed record SwitchRef(string Id, string Name)
{
    /// <summary>The name, or the ID when the name is blank, so a message never names nothing.</summary>
    public string Shown => string.IsNullOrWhiteSpace(Name) ? Id : Name;
}
