namespace HyperVManagerTray.Models;

/// <summary>A VM power operation the user (or a rule) requested.</summary>
public enum VmOpKind { Start, Resume, Pause, Save, Shutdown }

/// <summary>
/// Where a <see cref="VmOpKind"/> request originated — recorded in vm-power.log (issue #20).
///
/// <para><see cref="Tray"/> has had NO producer since issue #34 removed every power verb from the tray
/// menu (the dashboard is one click away, is state-aware, and can report an outcome — a native Win32 menu
/// can do none of that). It is kept rather than deleted for two reasons: existing vm-power.log files are
/// full of <c>origin=Tray</c> lines that a reader will want to interpret, and the members are only ever
/// rendered BY NAME into a log — never persisted, serialised or matched by ordinal — so a dead member
/// costs nothing, while renumbering the live ones to drop it would buy nothing.</para>
/// </summary>
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
