using Microsoft.Extensions.Logging;
using HyperVManagerTray.Helpers;
using HyperVManagerTray.Services;

namespace HyperVManagerTray.UI;

/// <summary>
/// Adding and removing a managed VM, shared verbatim by the tray's "Manage VMs" list and by
/// Settings → Managed VMs (issues #34 / #47).
///
/// <para><b>Why one class for two surfaces.</b> Issue #34's decision keeps Add/Remove in the tray AND
/// requires Settings to be the complete superset, so the same destructive act is deliberately offered
/// twice. That is duplication of the AFFORDANCE, and it must not become duplication of the BEHAVIOUR:
/// two hand-written copies would inevitably drift into asking two different questions and making two
/// different claims about what happened. Both surfaces call this.</para>
///
/// <para><b>Consent and truthfulness.</b> Removal is destructive to config, so it raises EXACTLY ONE
/// confirmation — owned here rather than by the callers, which is what makes "exactly one" a property of
/// the code instead of a convention two call sites are each trusted to honour (the #40 pattern: four
/// dialogs collapsed to one + one verified outcome).
///
/// Both verbs then report a VERIFIED outcome, not an attempted one (#37). The verification is real, not
/// a formality: <see cref="ConfigManager.AddVmToConfig"/> / <see cref="ConfigManager.RemoveVmFromConfig"/>
/// write the file and then RE-READ it from disk, so <see cref="ConfigManager.Current"/> afterwards is
/// what the file actually says — and if the read-back failed it still holds the PREVIOUS config, so the
/// check below correctly reports the change as unconfirmed rather than claiming a success that never
/// reached the disk.</para>
/// </summary>
internal sealed class ManagedVmActions
{
    private readonly ConfigManager _config;
    // The tray balloon (title, message, isError) — the app's one non-blocking report channel. Used from
    // Settings too, deliberately: a second display vocabulary for the same outcome is the thing to avoid.
    private readonly Action<string, string, bool> _notify;

    private static string Title => $"{AppInfo.Name} — managed VMs";

    public ManagedVmActions(ConfigManager config, Action<string, string, bool> notify)
    {
        _config = config;
        _notify = notify;
    }

    /// <summary>True when the config currently lists <paramref name="vmName"/> — the checkmark state of
    /// the tray's "Manage VMs" list, and the post-write verification for both verbs below.</summary>
    public bool IsManaged(string vmName) =>
        _config.Current.VirtualMachines.Any(v => v.Name.Equals(vmName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Starts managing a VM. No confirmation: adding is non-destructive and trivially undone by the same
    /// list. <paramref name="nicName"/> is the VM's own adapter as the host reports it; blank is fine —
    /// <see cref="ConfigManager.AddVmToConfig"/> falls back to the Hyper-V default name.
    /// </summary>
    /// <returns>True only if the config was re-read and genuinely contains the VM afterwards.</returns>
    public async Task<bool> AddAsync(string vmName, string nicName)
    {
        UiActivityLog.Logger.LogInformation("Managed VMs: add '{Vm}'", vmName);
        try
        {
            await Task.Run(() => _config.AddVmToConfig(vmName, nicName)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            UiActivityLog.Logger.LogWarning(ex, "Adding '{Vm}' to the managed VMs failed", vmName);
            _notify(Title, VmConfigUi.WriteFailedMessage(vmName, ex.Message), true);
            return false;
        }

        var confirmed = IsManaged(vmName);
        _notify(Title,
            confirmed ? VmConfigUi.AddedMessage(vmName) : VmConfigUi.AddNotConfirmedMessage(vmName),
            !confirmed);
        return confirmed;
    }

    /// <summary>
    /// Stops managing a VM, after the single confirmation this method owns (see the class remarks).
    /// The VM itself is never touched — this only edits config.json.
    /// </summary>
    /// <returns>
    /// True only if the user confirmed AND the config was re-read and genuinely no longer contains the VM.
    /// False covers "the user said no" as well as "it did not work" — callers use it purely to decide
    /// whether to re-render, and both cases mean the same thing there.
    /// </returns>
    public async Task<bool> RemoveAsync(string vmName)
    {
        // The ONE confirmation. Deliberately before the log line: a cancelled dialog is not an action.
        if (!NativeMethods.Confirm(VmConfigUi.RemoveConfirmPrompt(vmName), Title)) return false;

        UiActivityLog.Logger.LogInformation("Managed VMs: remove '{Vm}' (confirmed)", vmName);
        try
        {
            await Task.Run(() => _config.RemoveVmFromConfig(vmName)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            UiActivityLog.Logger.LogWarning(ex, "Removing '{Vm}' from the managed VMs failed", vmName);
            _notify(Title, VmConfigUi.WriteFailedMessage(vmName, ex.Message), true);
            return false;
        }

        var confirmed = !IsManaged(vmName);
        _notify(Title,
            confirmed ? VmConfigUi.RemovedMessage(vmName) : VmConfigUi.RemoveNotConfirmedMessage(vmName),
            !confirmed);
        return confirmed;
    }
}
