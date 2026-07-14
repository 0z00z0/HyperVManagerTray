using HyperVManagerTray.Models;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// Pure conversions from Hyper-V WMI (<c>root\virtualization\v2</c>) primitives to the app's
/// <see cref="VmStatus"/> model and progress messages. No WMI/System.Management dependency here so
/// the mapping (unit codes, MB/ms conversions, state names) can be unit-tested without a host.
///
/// Sources: <c>Msvm_ComputerSystem.EnabledState</c> / <c>Msvm_SummaryInformation</c> (ProcessorLoad,
/// MemoryUsage [MB], Uptime [ms]) and the VM's active <c>Msvm_ConcreteJob</c> (ElementName +
/// PercentComplete) — the latter is what Hyper-V Manager's Status column shows for a transition.
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

    // CIM-standard EnabledState codes. Msvm_ComputerSystem uses the Msvm vendor codes above, but
    // Msvm_SummaryInformation (and some hosts) report the CIM-standard values — a resume-from-Saved
    // was captured live with EnabledState 10 there. Map them too so a summary-sourced or
    // CIM-reporting host never falls through to "Unknown" (issue #13). Values 2/3/9 already coincide
    // with the vendor arms above (Enabled/Disabled/Quiesce), so only 4/6/10/11 need adding.
    private const ushort CimShuttingDown = 4;    // → Stopping
    private const ushort CimOffline      = 6;    // Enabled but Offline → Saved
    private const ushort CimStarting     = 10;
    private const ushort CimStopping     = 11;

    /// <summary>
    /// Maps an <c>EnabledState</c> to the friendly string the UI expects
    /// (Running / Off / Paused / Saved / transient verbs), matching the old PowerShell VMState names.
    /// Accepts both the Msvm vendor codes (Msvm_ComputerSystem) and the CIM-standard codes.
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
        // CIM-standard codes (see constants above).
        CimShuttingDown => "Stopping",
        CimOffline      => "Saved",
        CimStarting     => "Starting",
        CimStopping     => "Stopping",
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

    /// <summary>Assembles a <see cref="VmStatus"/> from summary-info primitives (units converted here).
    /// <paramref name="jobStatus"/> is the transient verb+percent from the VM's active
    /// <c>Msvm_ConcreteJob</c> (e.g. "Restoring (10%)"), or empty when no operation is in flight.</summary>
    public static VmStatus BuildStatus(
        string name, ushort enabledState, int processorLoad, long memoryUsageMb, ulong uptimeMs,
        long memMaxBytes, string switchName, string? jobStatus = "") => new()
    {
        Name        = name,
        State       = MapState(enabledState),
        Switch      = switchName ?? "",
        JobStatus   = jobStatus ?? "",
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

    // ── Transient status from the active Msvm_ConcreteJob ─────────────────────────

    /// <summary>
    /// A single snapshot of a VM's <c>Msvm_ConcreteJob</c> (or the embedded job in
    /// <c>Msvm_SummaryInformation.AsynchronousTasks</c>): the CIM <c>JobState</c>, the job's
    /// <c>ElementName</c> (the operation verb Hyper-V Manager shows, e.g. "Restoring"), and its
    /// <c>PercentComplete</c>. Kept WMI-free so the selection/formatting below is unit-testable.
    /// </summary>
    public readonly record struct JobSnapshot(ushort JobState, string? ElementName, int PercentComplete);

    // CIM_ConcreteJob.JobState: an operation is in progress only while Starting (3) or Running (4);
    // 7=Completed, 8/9/10=Terminated/Killed/Exception, etc. are finished and must be ignored.
    private const ushort JobStarting = 3;
    private const ushort JobRunning  = 4;

    /// <summary>
    /// The transient status string to mirror Hyper-V Manager's Status column, from the VM's first
    /// ACTIVE job — e.g. "Restoring (10%)" for a resume-from-Saved. This is the real signal issue #13
    /// needs: on a resume the coarse EnabledState reads "Starting" (same as a cold boot) while the
    /// live "Restoring (n%)" only exists on the active <c>Msvm_ConcreteJob</c>, not in
    /// StatusDescriptions (captured empty live). Returns null when no job is active (only JobState
    /// 3/4 count) or none has a usable ElementName, so the caller falls back to the coarse state.
    /// </summary>
    public static string? ActiveJobStatus(IEnumerable<JobSnapshot> jobs)
    {
        foreach (var job in jobs)
        {
            if (job.JobState is not (JobStarting or JobRunning)) continue;
            var verb = job.ElementName?.Trim();
            if (string.IsNullOrEmpty(verb)) continue;
            return $"{verb} ({Math.Clamp(job.PercentComplete, 0, 100)}%)";
        }
        return null;
    }
}
