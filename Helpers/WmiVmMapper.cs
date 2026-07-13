using System.Globalization;
using HyperVManagerTray.Models;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// Pure conversions from Hyper-V WMI (<c>root\virtualization\v2</c>) primitives to the app's
/// <see cref="VmStatus"/> model and progress messages. No WMI/System.Management dependency here so
/// the mapping (unit codes, MB/ms conversions, state names) can be unit-tested without a host.
///
/// Sources: <c>Msvm_ComputerSystem.EnabledState</c> / <c>Msvm_SummaryInformation</c> (ProcessorLoad,
/// MemoryUsage [MB], Uptime [ms], EnabledState, StatusDescriptions) and <c>Msvm_ConcreteJob</c>.
/// </summary>
public static class WmiVmMapper
{
    // CIM/Msvm EnabledState codes (see Msvm_ComputerSystem / Msvm_SummaryInformation).
    private const ushort Enabled   = 2;      // Running
    private const ushort Disabled  = 3;      // Off
    private const ushort Quiesce   = 9;      // user-initiated pause (see MapState)
    private const ushort Paused    = 32768;  // host critical-pause (e.g. low disk)
    private const ushort Suspended = 32769;  // Saved
    private const ushort Starting  = 32770;
    private const ushort Snapshotting = 32771;
    private const ushort Saving    = 32773;
    private const ushort Stopping  = 32774;
    private const ushort Pausing   = 32776;
    private const ushort Resuming  = 32777;

    /// <summary>
    /// Maps an <c>EnabledState</c> to the friendly string the UI expects
    /// (Running / Off / Paused / Saved / transient verbs), matching the old PowerShell VMState names.
    /// </summary>
    public static string MapState(ushort enabledState) => enabledState switch
    {
        Enabled      => "Running",
        Disabled     => "Off",
        // A user pause is driven by the CIM Quiesce request (9), and Hyper-V reports the paused VM
        // back as EnabledState 9; the vendor 32768 is the host's own critical-pause. Both must read
        // as "Paused" so the card shows the right label and the Resume/Save buttons (not "Unknown").
        Quiesce      => "Paused",
        Paused       => "Paused",
        Suspended    => "Saved",
        Starting     => "Starting",
        Snapshotting => "Snapshotting",
        Saving       => "Saving",
        Stopping     => "Stopping",
        Pausing      => "Pausing",
        Resuming     => "Resuming",
        _            => "Unknown",
    };

    /// <summary>Hyper-V reports assigned memory in MB; VmStatus stores bytes.</summary>
    public static long BytesFromMb(long megabytes) => megabytes <= 0 ? 0 : megabytes * 1048576L;

    /// <summary>
    /// Converts a WMI uptime in milliseconds to the <c>TimeSpan.ToString()</c> form that
    /// <see cref="UptimeFormatter"/> parses. Returns "" for 0 (not-running).
    /// </summary>
    public static string UptimeString(ulong uptimeMs) =>
        uptimeMs == 0 ? "" : TimeSpan.FromMilliseconds(uptimeMs).ToString();

    /// <summary>Assembles a <see cref="VmStatus"/> from summary-info primitives (units converted here).</summary>
    public static VmStatus BuildStatus(
        string name, ushort enabledState, int processorLoad, long memoryUsageMb, ulong uptimeMs,
        string? statusDescription, long memMaxBytes, string switchName) => new()
    {
        Name        = name,
        State       = MapState(enabledState),
        Switch      = switchName ?? "",
        StatusDesc  = statusDescription ?? "",
        Cpu         = Math.Clamp(processorLoad, 0, 100),
        MemAssigned = BytesFromMb(memoryUsageMb),
        MemMax      = memMaxBytes,
        Uptime      = UptimeString(uptimeMs),
    };

    // ── Power-operation progress text ────────────────────────────────────────────

    /// <summary>
    /// The message shown for a given operation phase. For a running job with a percent it produces
    /// e.g. "Saving (47%)…"; for failure it is the raw WMI <c>ErrorDescription</c> passed through.
    /// </summary>
    public static string ProgressMessage(VmOpKind kind, VmOpPhase phase, int? percent, string? error)
    {
        switch (phase)
        {
            case VmOpPhase.Requested:
                return $"Requesting {Verb(kind)}…";
            case VmOpPhase.Running:
                var gerund = Gerund(kind);
                return percent is int p ? $"{gerund} ({p}%)…" : $"{gerund}…";
            case VmOpPhase.Succeeded:
                return "";  // real state takes over
            case VmOpPhase.Failed:
                return string.IsNullOrWhiteSpace(error)
                    ? $"Failed to {Verb(kind)}"
                    : $"Failed: {error.Trim()}";
            default:
                return "";
        }
    }

    private static string Verb(VmOpKind k) => k switch
    {
        VmOpKind.Start    => "start",
        VmOpKind.Resume   => "resume",
        VmOpKind.Pause    => "pause",
        VmOpKind.Save     => "save",
        VmOpKind.Shutdown => "shut down",
        _                 => "update",
    };

    private static string Gerund(VmOpKind k) => k switch
    {
        VmOpKind.Start    => "Starting",
        VmOpKind.Resume   => "Resuming",
        VmOpKind.Pause    => "Pausing",
        VmOpKind.Save     => "Saving",
        VmOpKind.Shutdown => "Shutting down",
        _                 => "Working",
    };

    /// <summary>Parses a percent out of a WMI StatusDescriptions string like "Saving, 47 %" (fallback).</summary>
    public static int? PercentFromStatus(string? statusDescription)
    {
        if (string.IsNullOrEmpty(statusDescription)) return null;
        int pct = statusDescription.IndexOf('%');
        if (pct <= 0) return null;
        int i = pct - 1;
        while (i > 0 && (char.IsDigit(statusDescription[i - 1]) || statusDescription[i - 1] == ' ')) i--;
        var num = statusDescription[i..pct].Trim();
        return int.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>
    /// Parses the transient operation verb (e.g. "Restoring") out of a WMI StatusDescriptions string.
    /// This is the only place that verb exists: EnabledState 32770 ("Starting" per <see cref="MapState"/>)
    /// is reported for both a cold boot and a resume-from-Saved on this host, so StatusDescriptions is
    /// the sole signal that distinguishes them (issue #13).
    ///
    /// The verb always travels with a percentage during the transition (confirmed live: the percent is
    /// present the whole time a resume-from-Saved runs), but the exact surrounding shape varies by
    /// host/version and by how <see cref="Services.VmService.ReadSummaries"/> joins a multi-element
    /// StatusDescriptions array. All of these must yield "Restoring":
    ///   • "Restoring, 72 %"                    (comma + spaces)
    ///   • "Restoring 72%"                       (no comma — the shape the earlier comma-only parse
    ///                                            returned null for, leaving the coarse "Starting")
    ///   • "Operating normally Restoring 72%"    (health element [0] joined before the operation verb)
    /// So the verb is anchored on the '%' — it is the word immediately before the percent number —
    /// rather than on a comma that isn't always present. Returns null when there is no percentage
    /// (e.g. a plain "Operating normally"), so callers fall back cleanly to the coarse
    /// EnabledState-derived state name.
    /// </summary>
    public static string? LeadingVerbFromStatus(string? statusDescription)
    {
        if (string.IsNullOrEmpty(statusDescription)) return null;

        int pct = statusDescription.IndexOf('%');
        if (pct <= 0) return null;   // no percentage → not a transient "verb N %" status

        // Walk left from '%' over its spaces, digits and an optional comma to the end of the verb word,
        // then over the verb's own letters to its start.
        int i = pct - 1;
        while (i >= 0 && (char.IsDigit(statusDescription[i]) || statusDescription[i] is ' ' or ',')) i--;
        int end = i + 1;
        while (i >= 0 && !char.IsWhiteSpace(statusDescription[i])) i--;

        var verb = statusDescription[(i + 1)..end].Trim();
        return verb.Length > 0 ? verb : null;
    }
}
