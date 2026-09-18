using HyperVManagerTray.Models;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// Whether a Hyper-V service may be stopped now (issue #114). Stopping either service with a VM still
/// running leaves that VM out of reach until the service is back, so a stop is refused while any VM on the
/// host is anything other than off or saved — managed or not, since an unmanaged VM is stranded just the
/// same. Pure: the VM states arrive as arguments, so every refusal is testable without a host.
/// </summary>
public static class ServiceStopGuard
{
    public enum Verdict
    {
        /// <summary>Nothing is running: stop without asking.</summary>
        Allowed,
        /// <summary>Nothing is running, but the stop takes other software with it and must be confirmed.</summary>
        ConfirmSideEffects,
        /// <summary>VMs are running or paused: refused, with an offer to save them first.</summary>
        OfferSaveFirst,
        /// <summary>A VM is mid-transition or unreadable: refused outright, nothing can be saved yet.</summary>
        RefusedBusy,
        /// <summary>VM states cannot be read (vmms is not delivering them): refused, since a running VM would be invisible.</summary>
        RefusedStatesUnknown,
    }

    /// <param name="Verdict">What the caller may do.</param>
    /// <param name="VmsToSave">VMs that are running or paused, in the order the host listed them.</param>
    /// <param name="BusyVms">VMs in a state that is neither stoppable nor saveable.</param>
    public sealed record Decision(Verdict Verdict, IReadOnlyList<string> VmsToSave, IReadOnlyList<string> BusyVms);

    /// <summary>
    /// Whether a stop of <paramref name="kind"/> may come from a source with nobody to ask — a network rule
    /// or an MQTT command. False for the Host Compute Service: stopping it also stops WSL 2, Windows Sandbox
    /// and Docker, so it is stopped only from the dashboard, after the person has been told so.
    /// </summary>
    public static bool MayStopUnattended(HyperVServiceKind kind) => kind != HyperVServiceKind.HostCompute;

    /// <summary>The refusal an unattended stop of the Host Compute Service gets.</summary>
    public static string UnattendedStopRefusedMessage(HyperVServiceKind kind) =>
        $"{HyperVServiceNames.DisplayName(kind)} was not stopped: it is stopped only from the dashboard.";

    /// <summary>
    /// Decides a stop of <paramref name="kind"/>.
    /// </summary>
    /// <param name="statuses">The last read of every VM on the host.</param>
    /// <param name="statesKnown">True only when <paramref name="statuses"/> came from a successful read
    /// while vmms was running. False means a running VM could be invisible, even with an empty list.</param>
    public static Decision Evaluate(
        HyperVServiceKind kind,
        IReadOnlyList<VmStatus>? statuses,
        bool statesKnown)
    {
        if (!statesKnown)
            return new Decision(Verdict.RefusedStatesUnknown, [], []);

        var toSave = new List<string>();
        var busy   = new List<string>();
        foreach (var status in statuses ?? [])
        {
            switch (VmStateUi.ClassifyShape(status.State))
            {
                case VmStateUi.Shape.Off:
                case VmStateUi.Shape.Saved:
                    break;
                case VmStateUi.Shape.Running:
                case VmStateUi.Shape.Paused:
                    toSave.Add(status.Name);
                    break;
                default:
                    // Transition or Unknown: it may be running, and a save request would be rejected.
                    busy.Add(status.Name);
                    break;
            }
        }

        if (busy.Count > 0)   return new Decision(Verdict.RefusedBusy, toSave, busy);
        if (toSave.Count > 0) return new Decision(Verdict.OfferSaveFirst, toSave, []);
        return new Decision(
            kind == HyperVServiceKind.HostCompute ? Verdict.ConfirmSideEffects : Verdict.Allowed, [], []);
    }

    // ── Wording ──────────────────────────────────────────────────────────────────

    /// <summary>What stopping the Host Compute Service takes with it. Part of every prompt for it.</summary>
    public const string HostComputeSideEffects =
        "Stopping it also stops WSL 2, Windows Sandbox and Docker, and anything running in them.";

    /// <summary>The confirmation for a stop with nothing running.</summary>
    public static string ConfirmStopPrompt(HyperVServiceKind kind) =>
        $"Stop {HyperVServiceNames.DisplayName(kind)}?\n\n{HostComputeSideEffects}";

    /// <summary>The refusal that offers to save the running VMs first.</summary>
    public static string SaveFirstPrompt(HyperVServiceKind kind, IReadOnlyList<string> vmsToSave)
    {
        var text = $"{HyperVServiceNames.DisplayName(kind)} cannot be stopped while these VMs are running: "
                   + $"{string.Join(", ", vmsToSave)}.\n\n"
                   + "Save them all first and then stop the service?";
        return kind == HyperVServiceKind.HostCompute ? $"{text}\n\n{HostComputeSideEffects}" : text;
    }

    /// <summary>The refusal when a VM is mid-transition.</summary>
    public static string BusyMessage(HyperVServiceKind kind, IReadOnlyList<string> busyVms) =>
        $"{HyperVServiceNames.DisplayName(kind)} was not stopped: {string.Join(", ", busyVms)} "
        + "is changing state. Try again once it has finished.";

    /// <summary>The refusal when VM states cannot be read.</summary>
    public static string StatesUnknownMessage(HyperVServiceKind kind) =>
        $"{HyperVServiceNames.DisplayName(kind)} was not stopped: the VMs' states cannot be read while "
        + $"{HyperVServiceNames.DisplayName(HyperVServiceKind.VirtualMachineManagement)} is not running, "
        + "so a running VM could not be seen. Start it first.";
}
