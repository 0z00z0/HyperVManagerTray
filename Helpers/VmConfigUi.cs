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
    /// Which of a VM's synthetic adapters a NEW managed VM is seeded with — the single answer both
    /// add-a-VM surfaces use (the tray's "Manage VMs" list and Settings' add picker).
    ///
    /// <para><b>Why this is a shared function and not two one-liners.</b> It was two one-liners, and they
    /// disagreed. <c>VmService.ReadDiscovered</c> kept one NIC per VM by assigning into a dictionary as
    /// WMI rows arrived — so the LAST row won, in whatever order WMI happened to return them.
    /// <c>SettingsWindow</c> took <c>NicNamesFor(name).FirstOrDefault()</c>, and
    /// <c>HostInventory.ReadNicNames</c> sorts OrdinalIgnoreCase — so "Ethernet 2" beat "Network
    /// Adapter". The same act on two surfaces therefore persisted a different adapter, and whichever
    /// surface picked the non-primary NIC wrote a name <c>HyperVManager.FindSyntheticNic</c> never
    /// matches: <c>ApplySwitchAsync</c> then returns false on every pass and the VM is silently never
    /// reconnected — the exact failure issue #41 exists to fix. Issues #34/#47 built
    /// <c>ManagedVmActions</c> and this class specifically so the two surfaces could not drift; the NIC
    /// seed bypassed both. It doesn't now.</para>
    ///
    /// <para>Ordinal-ignore-case ordering, matching <c>HostInventory.ReadNicNames</c>: the choice among
    /// several adapters is arbitrary either way, so the property that matters is that it is the SAME
    /// arbitrary choice on both surfaces and stable between reads — not WMI's row order. An empty or
    /// all-blank list yields the Hyper-V default, which is also what
    /// <see cref="Services.ConfigManager.AddVmToConfig"/> falls back to, so the two agree by construction
    /// rather than by coincidence.</para>
    /// </summary>
    public static string SeedNicName(IEnumerable<string>? nicNames) =>
        (nicNames ?? [])
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()
        ?? SettingsOptions.DefaultNicName;

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
