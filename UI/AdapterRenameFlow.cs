using Microsoft.Extensions.Logging;
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
        UiActivityLog.Logger.LogInformation("Rename flow: started for adapter '{Adapter}'", adapter.DisplayName);

        AdapterNameRules.DeviceResolution resolution;
        try   { resolution = await Task.Run(() => AdapterDeviceRegistry.ResolveDevice(adapter.InterfaceGuid)); }
        catch (Exception ex)
        {
            UiActivityLog.Logger.LogWarning("Rename flow: device resolution threw for '{Adapter}'", adapter.DisplayName);
            NativeMethods.Error($"Could not identify the device for this adapter:\n{ex.Message}", AppName);
            return;
        }

        if (!resolution.Success || resolution.DeviceInstanceId is null)
        {
            // 0 or >1 devices resolved — never guess which dock; abort with no changes.
            UiActivityLog.Logger.LogWarning("Rename flow: device could not be safely resolved for '{Adapter}' — aborted", adapter.DisplayName);
            NativeMethods.Error(
                $"Could not safely identify the device behind \"{adapter.DisplayName}\".\n\n" +
                $"{resolution.Error}\n\nNo changes were made.",
                AppName);
            return;
        }

        var deviceInstanceId = resolution.DeviceInstanceId;

        var existing = _config.Current.AdapterNames.FirstOrDefault(o =>
            o.DeviceInstanceId.Equals(deviceInstanceId, StringComparison.OrdinalIgnoreCase));

        // Ground truth for "the original" (issue #33): DeviceDesc is written by the driver's INF and is
        // never touched by a rename, unlike FriendlyName — which, after an uninstall/reinstall wiped the
        // config while the registry rename survived, holds a PREVIOUS rename's output. Read once here
        // (read-only, never throws) and reuse for both the repair below and the capture in
        // ApplyRenameAsync, so the flow does not hit the registry twice for the same value.
        var factoryDescription = await Task.Run(() => AdapterDeviceRegistry.ReadFactoryDescription(deviceInstanceId));

        // Repair a record whose stored "original" predates the #33 fix and may be a prior rename's
        // output. Only ever corrects a record whose factory description we could positively re-derive;
        // anything else is left completely untouched. See AdapterNameRules.RepairOriginal.
        if (existing is not null)
        {
            var repaired = AdapterNameRules.RepairOriginal(
                new AdapterNameRules.OriginalCapture(existing.OriginalFriendlyName, existing.OriginalWasAbsent),
                factoryDescription);

            if (repaired is not null)
            {
                UiActivityLog.Logger.LogInformation(
                    "Rename flow: repairing saved original for '{Adapter}' — '{Stored}' → '{Factory}' (issue #33)",
                    adapter.DisplayName, existing.OriginalFriendlyName, repaired.OriginalFriendlyName);

                existing.OriginalFriendlyName = repaired.OriginalFriendlyName;
                existing.OriginalWasAbsent    = repaired.OriginalWasAbsent;

                // Persist immediately: the correction must survive even if the user cancels the dialog.
                // Config-only — no device is touched here.
                try { await Task.Run(() => _config.UpsertAdapterName(existing)); }
                catch (Exception ex)
                {
                    // A failed repair must not block the rename — the in-memory record is already
                    // corrected, so Reset works this session and the repair retries next time.
                    UiActivityLog.Logger.LogWarning(
                        "Rename flow: could not persist the repaired original for '{Adapter}': {Error}",
                        adapter.DisplayName, ex.Message);
                }
            }
        }

        // Reset is offered only when we have a real original to restore (never delete — §5.4).
        bool canReset = existing is not null
                        && !existing.OriginalWasAbsent
                        && !string.IsNullOrEmpty(existing.OriginalFriendlyName);
        string? savedOriginal = canReset ? existing!.OriginalFriendlyName : null;

        // GetPhysicalAdapters enumerates all NICs and can block for hundreds of ms; the awaits above
        // resume on the UI thread, so run it on the thread pool to keep the UI responsive
        // (issue #29, finding 3 — mirrors SettingsWindow.LoadAdaptersAsync).
        // DisplayName, not Description (issue #32): the uniqueness check compares the candidate against
        // the OTHER adapters' names, and the name being written is a FriendlyName — so the comparands
        // must be the other adapters' FriendlyNames too, which is what DisplayName carries.
        var others = (await Task.Run(AdapterMatcher.GetPhysicalAdapters))
            .Where(p => !p.InterfaceGuid.Equals(adapter.InterfaceGuid, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.DisplayName)
            .ToList();

        var result = await RenameAdapterWindow.ShowAsync(adapter.DisplayName, others, savedOriginal, canReset);
        if (result is null)
        {
            UiActivityLog.Logger.LogInformation("Rename flow: dialog cancelled for '{Adapter}'", adapter.DisplayName);
            return;
        }

        if (result.Choice == RenameDialogChoice.Reset)
        {
            UiActivityLog.Logger.LogInformation("Rename flow: reset requested for '{Adapter}'", adapter.DisplayName);
            await ResetAdapterNameAsync(adapter, deviceInstanceId, existing!);
        }
        else
        {
            UiActivityLog.Logger.LogInformation("Rename flow: rename requested for '{Adapter}' → '{NewName}'", adapter.DisplayName, result.NewName);
            await ApplyRenameAsync(adapter, deviceInstanceId, result.NewName!, existing, factoryDescription);
        }
    }

    /// <param name="factoryDescription">
    /// The device's DeviceDesc-derived factory description (null when it could not be derived), read
    /// once by <see cref="RunAsync"/>. Ground truth for the saved original — see issue #33.
    /// </param>
    private async Task ApplyRenameAsync(
        PhysicalAdapterInfo adapter, string deviceInstanceId, string newName, AdapterNameOverride? existing,
        string? factoryDescription)
    {
        if (!NativeMethods.Confirm(
                "Rename this network adapter's description?\n\n" +
                $"  From :  {adapter.DisplayName}\n" +
                $"  To   :  {newName}\n\n" +
                "This changes the description everywhere Windows shows it. To appear everywhere the " +
                "adapter may need to be disabled/enabled or the PC restarted, which will briefly drop " +
                "this adapter's network connection.",
                AppName))
        {
            UiActivityLog.Logger.LogInformation("Rename flow: rename confirmation declined for '{Adapter}'", adapter.DisplayName);
            return;
        }

        try
        {
            // Persist the true original BEFORE the first write so Reset can always restore it (§5.4).
            if (existing is null)
            {
                // Prefer the factory description (issue #33): FriendlyName may already hold a previous
                // rename's output whenever the config record was lost but the registry rename survived,
                // and recording that would make Reset restore the name the user is escaping. The
                // FriendlyName read stays as the fallback for when DeviceDesc yields nothing usable.
                var (present, original) = await Task.Run(() => AdapterDeviceRegistry.ReadFriendlyName(deviceInstanceId));
                var capture = AdapterNameRules.CaptureOriginal(factoryDescription, present, original);
                var entry = new AdapterNameOverride
                {
                    DeviceInstanceId     = deviceInstanceId,
                    OriginalFriendlyName = capture.OriginalFriendlyName,
                    OriginalWasAbsent    = capture.OriginalWasAbsent,
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
            UiActivityLog.Logger.LogWarning("Rename flow: FriendlyName write failed for '{Adapter}'", adapter.DisplayName);
            NativeMethods.Error($"The rename could not be completed:\n{ex.Message}", AppName);
            return;
        }

        UiActivityLog.Logger.LogInformation("Rename flow: FriendlyName written for '{Adapter}' → '{NewName}'", adapter.DisplayName, newName);
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
                $"  Current  :  {adapter.DisplayName}\n" +
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
            UiActivityLog.Logger.LogWarning("Rename flow: reset write failed for '{Adapter}'", adapter.DisplayName);
            NativeMethods.Error($"Could not restore the original name:\n{ex.Message}", AppName);
            return;
        }

        UiActivityLog.Logger.LogInformation("Rename flow: original name restored for '{Adapter}' → '{NewName}'", adapter.DisplayName, existing.OriginalFriendlyName);
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
            UiActivityLog.Logger.LogInformation("Rename flow: device restart deferred (name '{Name}')", appliedName);
            NativeMethods.Info(
                "The new name is saved. Restart the adapter or reboot to see it everywhere.", AppName);
            return;
        }

        UiActivityLog.Logger.LogInformation("Rename flow: restarting device to apply name '{Name}'", appliedName);
        _ = Task.Run(() =>
        {
            try
            {
                // ★ DEVICE-MUTATING ★ Disable + enable to force NDIS to re-read the description.
                AdapterRenamer.RestartDevice(deviceInstanceId);

                // Confirm the name is still on disk after the cycle before claiming success — a PnP
                // re-enumeration could, in principle, have regenerated it (issue #15: never report a
                // success we haven't verified).
                var (present, current) = AdapterDeviceRegistry.ReadFriendlyName(deviceInstanceId);
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
