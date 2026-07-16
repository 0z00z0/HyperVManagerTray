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
    /// The managed VM with this name, or null if this app doesn't manage it — the single lookup behind
    /// "is this VM ours, and what is its NIC called?".
    ///
    /// <para><b>Case-insensitive, like every other name comparison on this surface.</b> Hyper-V VM names
    /// are case-insensitive, and the two surfaces that produce one disagree on casing by construction:
    /// the "Start managing a VM" prompt (issue #47) takes free text, so a user types <c>devbox</c>, while
    /// the "Add VM" picker (issue #41) sources <see cref="UnmanagedVms"/> from the host inventory and
    /// therefore carries Hyper-V's own <c>DevBox</c>. <c>NetworkMonitor</c> used to look these up with an
    /// ordinal <c>==</c> — the only such compare left in the app — so a config the app's OWN pickers
    /// created could fail to match, and since issue #37 escalated that miss from a log line to a
    /// permanent failure state, the result was a pinned red icon and a VM that was never reconnected.
    /// Centralising the lookup here is what stops a third caller re-deriving it a fourth way.</para>
    /// </summary>
    public static Models.VmTarget? FindManagedVm(IEnumerable<Models.VmTarget>? managedVms, string vmName) =>
        managedVms?.FirstOrDefault(v => string.Equals(v.Name, vmName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The VMs on the host that this app does NOT manage — the set the "Manage VMs" list offers to add,
    /// and the set Settings' add-picker suggests. Case-insensitive (Hyper-V VM names are), de-duplicated,
    /// and ordered by name so the menu doesn't reshuffle between opens.
    /// </summary>
    public static IReadOnlyList<string> UnmanagedVms(
        IEnumerable<string> hostVms, IEnumerable<string> managedVms)
    {
        var managed = new HashSet<string>(managedVms ?? [], StringComparer.OrdinalIgnoreCase);
        return
        [
            .. (hostVms ?? [])
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                .Where(n => !managed.Contains(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
        ];
    }

    /// <summary>
    /// The switches an override may target: the fallback plus every switch named by a rule. Shared by the
    /// tray's "Override VM switch" submenu and Settings → Network's override control so the two surfaces
    /// can't offer different sets. De-duplicated and ordered.
    /// </summary>
    public static IReadOnlyList<string> OverrideSwitchNames(
        string? fallbackSwitch, IEnumerable<string> ruleSwitches)
    {
        var all = new List<string>();
        if (!string.IsNullOrWhiteSpace(fallbackSwitch)) all.Add(fallbackSwitch.Trim());
        foreach (var s in ruleSwitches ?? [])
            if (!string.IsNullOrWhiteSpace(s)) all.Add(s.Trim());

        return [.. all.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase)];
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
}
