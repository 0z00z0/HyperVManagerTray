namespace HyperVManagerTray.Models;

/// <summary>A VM power operation the user (or a rule) requested.</summary>
public enum VmOpKind { Start, Resume, Pause, Save, Shutdown }

/// <summary>Where a <see cref="VmOpKind"/> request originated — recorded in vm-power.log (issue #20).</summary>
public enum VmOpOrigin { Tray, Dashboard, Auto }

/// <summary>Lifecycle phase of a <see cref="VmOpKind"/>.</summary>
public enum VmOpPhase { Requested, Running, Succeeded, Failed }

/// <summary>
/// Progress of an in-flight VM power operation, surfaced from the WMI job so the dashboard can show
/// "Requesting start…" immediately, then live progress ("Saving (47%)…"), then success or the exact
/// failure text (e.g. "not enough memory").
/// </summary>
/// <param name="Percent">0–100 when the WMI job reports it; null otherwise.</param>
/// <param name="Message">
/// Human-readable status/failure text for the UI (e.g. "Requesting start…", "Saving (47%)…",
/// "Failed: not enough memory") — built by <see cref="Helpers.WmiVmMapper.ProgressMessage"/>.
/// </param>
public readonly record struct VmOperationProgress(
    string VmName, VmOpKind Kind, VmOpPhase Phase, int? Percent, string? Message);
