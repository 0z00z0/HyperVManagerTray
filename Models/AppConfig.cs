using Microsoft.Extensions.Logging;

namespace HyperVManagerTray.Models;

/// <summary>
/// The switch and VMs to use when no <see cref="NetworkRule"/> matches the current network
/// (typically a NAT switch such as the Hyper-V "Default Switch").
/// </summary>
public sealed class FallbackAction
{
    /// <summary>Hyper-V virtual switch to connect the target VMs to when no rule matches.</summary>
    public string VirtualSwitch { get; set; } = "Default Switch";

    /// <summary>Names of the VMs to reconnect to <see cref="VirtualSwitch"/>.</summary>
    public List<string> TargetVms { get; set; } = [];
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

    /// <summary>The FriendlyName the device had before the first rename (empty when it had none).</summary>
    public string OriginalFriendlyName { get; set; } = string.Empty;

    /// <summary>
    /// True when the device had no explicit FriendlyName before the first rename.  In that case Reset
    /// is not offered (there is nothing to restore, and deleting is disallowed — see the type summary).
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
    /// <summary>VMs this app manages, keyed by Hyper-V VM name.</summary>
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
    /// Minimum severity written to <c>switcher.log</c>.  One of <c>Trace</c>, <c>Debug</c>,
    /// <c>Information</c>, <c>Warning</c>, <c>Error</c>, <c>Critical</c>, <c>None</c>.
    /// Defaults to <c>Debug</c> so diagnostic detail (e.g. the PowerShell worker echo) is captured.
    /// </summary>
    public LogLevel LogLevel { get; set; } = LogLevel.Debug;

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
    /// The distinct, non-empty virtual-switch names referenced by the rules — the set of
    /// bridged switches whose host vNICs may need repair.  Used by both the startup self-heal
    /// and the tray "Repair host networking" action.
    /// </summary>
    public IEnumerable<string> RuleSwitches => Rules
        .Select(r => r.VirtualSwitch)
        .Where(s => !string.IsNullOrWhiteSpace(s))
        .Distinct(StringComparer.OrdinalIgnoreCase);
}
