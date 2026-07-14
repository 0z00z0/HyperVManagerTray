using Microsoft.UI.Dispatching;
using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using HyperVManagerTray.Services;

namespace HyperVManagerTray.UI;

/// <summary>
/// Orchestrates the "rename network adapter" flow (issue #15) end to end: resolve the PnP device
/// deterministically, show the rename/reset dialog (<see cref="RenameAdapterWindow"/>), and — only on
/// explicit consent — perform the device-mutating FriendlyName write and optional device restart.
///
/// <para>Extracted verbatim from <c>TrayMenu</c> when the rename entry point moved out of the tray
/// context menu into the Settings window (issue #18).  The safe, consent-gated write path
/// (<see cref="AdapterRenamer"/>) is unchanged; this class just makes the orchestration reusable and
/// keeps <c>TrayMenu</c> focused on quick actions.  Every user-facing message box goes through
/// <see cref="NativeMethods"/> (safe from any thread); the one post-restart success/warning toast is
/// marshalled back to the UI thread via the captured <see cref="DispatcherQueue"/>.</para>
/// </summary>
internal sealed class AdapterRenameFlow
{
    private const string AppName = AppInfo.Name;

    private readonly ConfigManager _config;
    private readonly DispatcherQueue _ui;

    public AdapterRenameFlow(ConfigManager config, DispatcherQueue ui)
    {
        _config = config;
        _ui     = ui;
    }

    /// <summary>
    /// Rename flow for one adapter: resolve its PnP device deterministically (abort on a 0/&gt;1
    /// match), show the rename/reset dialog, then — only on explicit consent — write the new
    /// FriendlyName and offer a device restart.  The saved original is captured before the first
    /// write so Reset can restore it.
    /// </summary>
    public async Task RunAsync(PhysicalAdapterInfo adapter)
    {
        AdapterNameRules.DeviceResolution resolution;
        try   { resolution = await Task.Run(() => AdapterRenamer.ResolveDevice(adapter.InterfaceGuid)); }
        catch (Exception ex)
        {
            NativeMethods.Error($"Could not identify the device for this adapter:\n{ex.Message}", AppName);
            return;
        }

        if (!resolution.Success || resolution.DeviceInstanceId is null)
        {
            // 0 or >1 devices resolved — never guess which dock; abort with no changes.
            NativeMethods.Error(
                $"Could not safely identify the device behind \"{adapter.Description}\".\n\n" +
                $"{resolution.Error}\n\nNo changes were made.",
                AppName);
            return;
        }

        var deviceInstanceId = resolution.DeviceInstanceId;

        var existing = _config.Current.AdapterNames.FirstOrDefault(o =>
            o.DeviceInstanceId.Equals(deviceInstanceId, StringComparison.OrdinalIgnoreCase));

        // Reset is offered only when we have a real original to restore (never delete — §5.4).
        bool canReset = existing is not null
                        && !existing.OriginalWasAbsent
                        && !string.IsNullOrEmpty(existing.OriginalFriendlyName);
        string? savedOriginal = canReset ? existing!.OriginalFriendlyName : null;

        var others = AdapterMatcher.GetPhysicalAdapters()
            .Where(p => !p.InterfaceGuid.Equals(adapter.InterfaceGuid, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Description)
            .ToList();

        var result = await RenameAdapterWindow.ShowAsync(adapter.Description, others, savedOriginal, canReset);
        if (result is null) return;

        if (result.Choice == RenameDialogChoice.Reset)
            await ResetAdapterNameAsync(adapter, deviceInstanceId, existing!);
        else
            await ApplyRenameAsync(adapter, deviceInstanceId, result.NewName!, existing);
    }

    private async Task ApplyRenameAsync(
        PhysicalAdapterInfo adapter, string deviceInstanceId, string newName, AdapterNameOverride? existing)
    {
        if (!NativeMethods.Confirm(
                "Rename this network adapter's description?\n\n" +
                $"  From :  {adapter.Description}\n" +
                $"  To   :  {newName}\n\n" +
                "This changes the description everywhere Windows shows it. To appear everywhere the " +
                "adapter may need to be disabled/enabled or the PC restarted, which will briefly drop " +
                "this adapter's network connection.",
                AppName))
            return;

        try
        {
            // Persist the true original BEFORE the first write so Reset can always restore it (§5.4).
            if (existing is null)
            {
                var (present, original) = await Task.Run(() => AdapterRenamer.ReadFriendlyName(deviceInstanceId));
                var entry = new AdapterNameOverride
                {
                    DeviceInstanceId     = deviceInstanceId,
                    OriginalFriendlyName = present ? (original ?? string.Empty) : string.Empty,
                    OriginalWasAbsent    = !present,
                    Mac                  = adapter.Mac,
                    RenamedOn            = DateTime.Now.ToString("yyyy-MM-dd"),
                    CurrentFriendlyName  = newName,
                };
                await Task.Run(() => _config.UpsertAdapterName(entry));
            }
            else
            {
                existing.CurrentFriendlyName = newName;   // keep the original; update last-applied only
                await Task.Run(() => _config.UpsertAdapterName(existing));
            }

            // ★ DEVICE-MUTATING WRITE ★ (SetupAPI, parameterized — no shell). Consent captured above.
            await Task.Run(() => AdapterRenamer.WriteFriendlyName(deviceInstanceId, newName));
        }
        catch (Exception ex)
        {
            NativeMethods.Error($"The rename could not be completed:\n{ex.Message}", AppName);
            return;
        }

        OfferDeviceRestart(deviceInstanceId, newName);
    }

    private async Task ResetAdapterNameAsync(
        PhysicalAdapterInfo adapter, string deviceInstanceId, AdapterNameOverride existing)
    {
        if (existing.OriginalWasAbsent || string.IsNullOrEmpty(existing.OriginalFriendlyName))
        {
            // Defensive: Reset should be disabled in this case. Never delete — it could leave two
            // identically-named adapters (§5.4).
            NativeMethods.Info(
                "This adapter had no custom description originally, so there is nothing to restore. " +
                "The app won't delete the name automatically.",
                AppName);
            return;
        }

        if (!NativeMethods.Confirm(
                "Restore this adapter's original description?\n\n" +
                $"  Current  :  {adapter.Description}\n" +
                $"  Restore  :  {existing.OriginalFriendlyName}\n\n" +
                "The adapter may need a restart or reboot to update everywhere, briefly dropping its connection.",
                AppName))
            return;

        try
        {
            existing.CurrentFriendlyName = existing.OriginalFriendlyName;
            await Task.Run(() => _config.UpsertAdapterName(existing));

            // ★ DEVICE-MUTATING WRITE ★ Restore the saved value (never a delete).
            await Task.Run(() => AdapterRenamer.WriteFriendlyName(deviceInstanceId, existing.OriginalFriendlyName));
        }
        catch (Exception ex)
        {
            NativeMethods.Error($"Could not restore the original name:\n{ex.Message}", AppName);
            return;
        }

        OfferDeviceRestart(deviceInstanceId, existing.OriginalFriendlyName);
    }

    /// <summary>
    /// After a successful FriendlyName write, offers to disable/enable the adapter so the change
    /// propagates immediately (warning about the brief link drop), or defer it to a later reboot.
    /// </summary>
    private void OfferDeviceRestart(string deviceInstanceId, string appliedName)
    {
        bool restart = NativeMethods.Confirm(
            $"The adapter description was set to \"{appliedName}\".\n\n" +
            "Some places may still show the old name until the adapter is restarted. " +
            "Restart (disable + enable) this adapter now?\n\n" +
            "Your network connection on this adapter will drop for a few seconds. " +
            "Choose No to apply it later — a PC restart will also pick it up.",
            AppName);

        if (!restart)
        {
            NativeMethods.Info(
                "The new name is saved. Restart the adapter or reboot to see it everywhere.", AppName);
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                // ★ DEVICE-MUTATING ★ Disable + enable to force NDIS to re-read the description.
                AdapterRenamer.RestartDevice(deviceInstanceId);

                // Confirm the name is still on disk after the cycle before claiming success — a PnP
                // re-enumeration could, in principle, have regenerated it (issue #15: never report a
                // success we haven't verified).
                var (present, current) = AdapterRenamer.ReadFriendlyName(deviceInstanceId);
                if (AdapterNameRules.FriendlyNameApplied(present, current, appliedName))
                    _ui.TryEnqueue(() => NativeMethods.Info(
                        "Adapter restarted. The new name should now appear everywhere.", AppName));
                else
                    _ui.TryEnqueue(() => NativeMethods.Warn(
                        "The adapter was restarted, but the name on disk is now " +
                        (present ? $"\"{current}\"" : "absent") +
                        $" instead of \"{appliedName}\" — Windows may have reset it. Try renaming again " +
                        "or reboot to re-apply.", AppName));
            }
            catch (Exception ex)
            {
                _ui.TryEnqueue(() => NativeMethods.Warn(
                    "The name was saved, but the adapter could not be restarted automatically:\n" +
                    $"{ex.Message}\n\nRestart the adapter manually or reboot to apply it.", AppName));
            }
        });
    }
}
