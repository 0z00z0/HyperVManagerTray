namespace HyperVManagerTray.Helpers;

/// <summary>
/// Pure decisions and copy for the "which VMs does this app manage?" surface (issues #34 / #47) — the
/// sibling of <see cref="VmStateUi"/> (VM power) and <see cref="NetworkStatusUi"/> (the network path).
/// WMI-free and side-effect-free, so every decision below is unit-testable without a live Hyper-V host.
///
/// <para><b>Why this exists.</b> Adding and removing a managed VM is now offered on TWO surfaces — the
/// tray's "Manage VMs" list and Settings → Managed VMs — because the tray keeps the quick command while
/// Settings must be the complete superset (issue #34's decision). Two surfaces performing the same
/// destructive act must not drift into two different questions and two different reports, which is
/// exactly what happens when each one hand-writes its own strings. The wording lives here once.</para>
///
/// <para><b>The invariant this file exists to hold.</b> The messages below come in confirmed/unconfirmed
/// pairs, and the caller picks between them by RE-READING the config after the write — never by assuming
/// the write worked (issues #37 / #40: report what was confirmed, never what was attempted). A write
/// that lands but cannot be read back leaves <c>ConfigManager.Current</c> holding the previous config,
/// so the unconfirmed message is the honest answer and the caller must be able to say it.</para>
/// </summary>
public static class VmConfigUi
{
    /// <summary>
    /// The managed VM with this VM ID, or null if this app doesn't manage it — the single lookup behind
    /// "is this VM ours, and which adapter do we reconnect?". By ID alone: two VMs may share a name, and a
    /// name found the first of them.
    /// </summary>
    public static Models.VmTarget? FindManagedVm(IEnumerable<Models.VmTarget>? managedVms, string? vmId) =>
        string.IsNullOrWhiteSpace(vmId)
            ? null
            : managedVms?.FirstOrDefault(v => HostIdentity.Same(v.Id, vmId));

    /// <summary>
    /// The VMs on the host that this app does NOT manage — the set the "Manage VMs" list offers to add,
    /// and the set Settings' add-picker offers. Compared by VM ID, so a VM sharing its name with a managed
    /// one is still offered; ordered by name, then ID, so the menu doesn't reshuffle between opens.
    /// </summary>
    public static IReadOnlyList<HostVm> UnmanagedVms(
        IEnumerable<HostVm>? hostVms, IEnumerable<Models.VmTarget>? managedVms)
    {
        var managed = (managedVms ?? []).Where(v => !string.IsNullOrWhiteSpace(v.Id)).ToList();
        return
        [
            .. (hostVms ?? [])
                .Where(h => !string.IsNullOrWhiteSpace(h.Id))
                .Where(h => !managed.Any(m => HostIdentity.Same(m.Id, h.Id)))
                .DistinctBy(h => HostIdentity.Bare(h.Id), StringComparer.OrdinalIgnoreCase)
                .OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(h => h.Id, StringComparer.OrdinalIgnoreCase)
        ];
    }

    /// <summary>
    /// Which of a VM's synthetic adapters a NEW managed VM is seeded with — the single answer both
    /// add-a-VM surfaces use (the tray's "Manage VMs" list and Settings' add picker), so the two cannot
    /// seed different adapters. The first by name, then by ID, whatever order WMI returned them in; null
    /// when the VM has no adapter, which leaves "the VM's only adapter" to be decided when it has one.
    /// </summary>
    public static HostNic? SeedNic(IEnumerable<HostNic>? nics) =>
        (nics ?? [])
            .Where(n => !string.IsNullOrWhiteSpace(n.Id))
            .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(n => n.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    /// <summary>
    /// The switches an override may target: the fallback's plus every rule's, by ID. Shared by the tray's
    /// "Override VM switch" submenu and Settings → Network's override control so the two surfaces can't
    /// offer different sets. De-duplicated by ID and ordered by name; a switch not identified yet is left
    /// out, since nothing could be connected to it.
    /// </summary>
    public static IReadOnlyList<Models.SwitchRef> OverrideSwitches(
        Models.ISwitchTarget? fallback, IEnumerable<Models.ISwitchTarget>? rules)
    {
        var all = new List<Models.SwitchRef>();
        if (fallback is not null && !string.IsNullOrWhiteSpace(fallback.SwitchId))
            all.Add(new Models.SwitchRef(fallback.SwitchId, fallback.SwitchName));
        foreach (var r in rules ?? [])
            if (!string.IsNullOrWhiteSpace(r.SwitchId)) all.Add(new Models.SwitchRef(r.SwitchId, r.SwitchName));

        return
        [
            .. all.DistinctBy(s => HostIdentity.Bare(s.Id), StringComparer.OrdinalIgnoreCase)
                  .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                  .ThenBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
        ];
    }

    /// <summary>
    /// The ONE confirmation shown before a VM is un-managed (issues #37 / #40: exactly one dialog, then a
    /// verified outcome — not the four-dialog stack the rename flow used to raise).
    ///
    /// <para>It leads with what is actually at stake, because the word "remove" next to a VM name reads as
    /// "delete the virtual machine" and that is emphatically not what this does. The VM, its disks and its
    /// state are untouched; only this app's management of it ends.</para>
    /// </summary>
    public static string RemoveConfirmPrompt(string vmName) =>
        $"Stop managing {vmName}?\n\n" +
        $"{vmName} itself is not deleted or changed — this app simply stops reconnecting its network " +
        "adapter when the host network changes. You can start managing it again at any time.";

    /// <summary>Reported once the config has been re-read and the VM is confirmed GONE from it.</summary>
    public static string RemovedMessage(string vmName) =>
        $"{vmName} is no longer managed. Its network adapter is left exactly where it is now.";

    /// <summary>
    /// Reported when the removal did NOT verify: the config still lists the VM after the write. Says the
    /// state that is true rather than the action that was attempted — the whole point of issue #37.
    /// </summary>
    public static string RemoveNotConfirmedMessage(string vmName) =>
        $"{vmName} is still managed — the change could not be confirmed. Nothing else was altered; see ui.log.";

    /// <summary>Reported once the config has been re-read and the VM is confirmed PRESENT in it.</summary>
    public static string AddedMessage(string vmName) =>
        $"{vmName} is now managed. Check its network adapter in Settings → Managed VMs.";

    /// <summary>Reported when the addition did not verify — see <see cref="RemoveNotConfirmedMessage"/>.</summary>
    public static string AddNotConfirmedMessage(string vmName) =>
        $"{vmName} could not be added to the managed VMs — the change could not be confirmed; see ui.log.";

    /// <summary>The failure text when the config write itself threw (a locked file, a read-only folder).</summary>
    public static string WriteFailedMessage(string vmName, string error) =>
        $"Could not update the managed VMs for {vmName}: {error}";

    /// <summary>
    /// The dashboard's zero-VMs card (issue #38). Names the two SURFACES that can add a VM — the tray icon
    /// and Settings — and deliberately no menu path within either.
    ///
    /// <para><b>The rule this string broke once already.</b> Its own comment said it pointed at the tray
    /// icon rather than a menu item because "a signpost that names a menu path is a signpost that goes
    /// stale", citing issue #34 replacing the VM Power menu with "Manage VMs" — and the string underneath
    /// nonetheless read "Right-click the tray icon and use Manage VMs to add one". It then went stale in
    /// the predicted way: issue #47 added the Settings route, so naming only the tray became incomplete as
    /// well as path-bound. Surfaces are durable; the items inside them are what move
    /// (docs/STYLE.md: "a string can be false because the code moved").</para>
    /// </summary>
    public const string NoManagedVmsMessage =
        "No VMs are managed yet.\nAdd one from the tray icon, or in Settings.";
}
