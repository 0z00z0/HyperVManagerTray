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
/// <see cref="NativeMethods"/> (safe from any thread); the one post-restart outcome notification is
/// marshalled back to the UI thread via the captured <see cref="DispatcherQueue"/>.</para>
///
/// <para><b>One consent, one outcome (issue #40).</b> A completed rename used to cost up to four
/// stacked dialogs — the rename window, a From/To confirm, a restart prompt, then an outcome box. That
/// is consent theatre: by the second box the user is pattern-matching "yes, yes, yes", which is the
/// worst possible state for a device-mutating action that drops the link. The consent stack is now
/// exactly one dialog (<see cref="RenameAdapterWindow"/>, carrying the full consequence and the restart
/// checkbox) followed by exactly one outcome notification. The happy path shows no intermediate boxes
/// at all; the remaining <see cref="NativeMethods"/> calls here are hard-failure paths only.</para>
///
/// <para><b>What was collapsed was the consent, NOT the verification.</b> The write still re-reads the
/// value from disk and throws on a mismatch (<see cref="AdapterRenamer.WriteFriendlyName"/>), and the
/// post-restart path still re-reads it again before the outcome is decided. Because the outcome is now
/// the single thing the user reads, that verification matters more, not less — see
/// <see cref="ApplyDeviceRestart"/>.</para>
/// </summary>
internal sealed class AdapterRenameFlow
{
    private const string AppName = AppInfo.Name;

    private readonly ConfigManager _config;
    private readonly DispatcherQueue _ui;
    private readonly Func<IReadOnlyList<PhysicalAdapterInfo>?>? _knownAdapters;
    private readonly Action? _onRenamed;

    /// <param name="knownAdapters">
    /// The adapters the CALLER already enumerated, or null/empty for "I have none" (issue #50). Settings
    /// passes its cached <c>HostInventory</c> snapshot — the very list this flow's only call site built
    /// its own rename buttons from — so a rename click stops re-running the NIC sweep that produced it.
    ///
    /// <para><b>Why a delegate and not the list.</b> This flow is constructed in the Settings ctor, and
    /// the inventory arrives later, off a background thread. A list captured at construction would be
    /// permanently null; the delegate is read at click time, when the answer exists.</para>
    ///
    /// <para><b>Why it may return null, and why that is not a degraded corner.</b> Null means "nothing
    /// trustworthy in hand" — the host read never landed, OR a rename since made it stale (see
    /// <paramref name="onRenamed"/>) — and this flow answers it by enumerating live, exactly as it always
    /// did. The caller decides; this flow never assumes a cache is fresh because it is non-empty.</para>
    /// </param>
    /// <param name="onRenamed">
    /// Called on the UI thread after a FriendlyName write has been verified on disk, so the caller can
    /// re-read the host (issue #50). This is what keeps <paramref name="knownAdapters"/> honest: a rename
    /// changes the very DisplayName the snapshot holds, so a cache that outlived it would feed the NEXT
    /// rename's uniqueness check a name that no longer exists — and let a second adapter take a name the
    /// first one already has. That is issue #32's failure mode (a display name asserted from a stale
    /// read), and it is the one thing reusing the cache could reintroduce.
    /// </param>
    public AdapterRenameFlow(ConfigManager config, DispatcherQueue ui,
                             Func<IReadOnlyList<PhysicalAdapterInfo>?>? knownAdapters = null,
                             Action? onRenamed = null)
    {
        _config        = config;
        _ui            = ui;
        _knownAdapters = knownAdapters;
        _onRenamed     = onRenamed;
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

        // The comparands for the dialog's uniqueness check. Prefer the caller's list: this flow's only
        // call site builds its rename buttons FROM that list, so re-sweeping every NIC here re-derived
        // what the click already had in hand (issue #50). Fall back to a live sweep whenever it cannot
        // answer — never enumerated, or stale since a rename (see the ctor's onRenamed).
        var known = _knownAdapters?.Invoke();
        var all   = AdapterMatcher.CanRenameFromKnownAdapters(known, adapter.InterfaceGuid)
            ? known!
            // GetPhysicalAdapters enumerates all NICs and can block for hundreds of ms; the awaits above
            // resume on the UI thread, so run it on the thread pool to keep the UI responsive
            // (issue #29, finding 3 — mirrors SettingsWindow.LoadAdaptersAsync).
            : await Task.Run(AdapterMatcher.GetPhysicalAdapters);

        var others = AdapterMatcher.OtherAdapterDisplayNames(all, adapter.InterfaceGuid);

        var result = await RenameAdapterWindow.ShowAsync(adapter.DisplayName, others, savedOriginal, canReset);
        if (result is null)
        {
            UiActivityLog.Logger.LogInformation("Rename flow: dialog cancelled for '{Adapter}'", adapter.DisplayName);
            return;
        }

        // The dialog IS the consent (issue #40): clicking Rename/Reset consented to the write, and the
        // checkbox consented to the restart. Both are recorded here as the single consent event, then the
        // flow proceeds with no further prompting.
        if (result.Choice == RenameDialogChoice.Reset)
        {
            UiActivityLog.Logger.LogInformation(
                "Rename flow: consent — reset requested for '{Adapter}' (restart now: {Restart})",
                adapter.DisplayName, result.RestartNow);
            await ResetAdapterNameAsync(adapter, deviceInstanceId, existing!, result.RestartNow);
        }
        else
        {
            UiActivityLog.Logger.LogInformation(
                "Rename flow: consent — rename requested for '{Adapter}' → '{NewName}' (restart now: {Restart})",
                adapter.DisplayName, result.NewName, result.RestartNow);
            await ApplyRenameAsync(adapter, deviceInstanceId, result.NewName!, existing, factoryDescription, result.RestartNow);
        }
    }

    /// <param name="factoryDescription">
    /// The device's DeviceDesc-derived factory description (null when it could not be derived), read
    /// once by <see cref="RunAsync"/>. Ground truth for the saved original — see issue #33.
    /// </param>
    /// <param name="restartNow">The restart consent from the dialog's checkbox (issue #40).</param>
    private async Task ApplyRenameAsync(
        PhysicalAdapterInfo adapter, string deviceInstanceId, string newName, AdapterNameOverride? existing,
        string? factoryDescription, bool restartNow)
    {
        // No confirmation box here (issue #40): the dialog already showed the From/To, the system-wide
        // effect and the link drop, and the user clicked Rename against all of it. Re-asking the same
        // question is what trains the click-through this flow needs the user NOT to be doing.
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

            // ★ DEVICE-MUTATING WRITE ★ (parameterized registry set — no shell). Consent = the dialog's
            // Rename click. Throws unless the value is re-read from disk and matches (issue #15).
            await Task.Run(() => AdapterRenamer.WriteFriendlyName(deviceInstanceId, newName));
        }
        catch (Exception ex)
        {
            UiActivityLog.Logger.LogWarning("Rename flow: FriendlyName write failed for '{Adapter}'", adapter.DisplayName);
            NativeMethods.Error($"The rename could not be completed:\n{ex.Message}", AppName);
            return;
        }

        UiActivityLog.Logger.LogInformation("Rename flow: FriendlyName written and verified for '{Adapter}' → '{NewName}'", adapter.DisplayName, newName);
        ApplyDeviceRestart(deviceInstanceId, newName, restartNow);
    }

    /// <param name="restartNow">The restart consent from the dialog's checkbox (issue #40).</param>
    private async Task ResetAdapterNameAsync(
        PhysicalAdapterInfo adapter, string deviceInstanceId, AdapterNameOverride existing, bool restartNow)
    {
        if (existing.OriginalWasAbsent || string.IsNullOrEmpty(existing.OriginalFriendlyName))
        {
            // Defensive: Reset is disabled in the dialog in this case. Never delete — it could leave two
            // identically-named adapters (§5.4).
            UiActivityLog.Logger.LogWarning(
                "Rename flow: reset requested for '{Adapter}' with no original to restore — no changes made",
                adapter.DisplayName);
            NativeMethods.Info(
                "This adapter had no custom description originally, so there is nothing to restore. " +
                "The app won't delete the description automatically.",
                AppName);
            return;
        }

        // No confirmation box here (issue #40): the dialog's Reset button carries the original name in
        // its tooltip and sits beside the same warning the rename does. The click is the consent.
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
            NativeMethods.Error($"Could not restore the original description:\n{ex.Message}", AppName);
            return;
        }

        UiActivityLog.Logger.LogInformation("Rename flow: original description restored and verified for '{Adapter}' → '{NewName}'", adapter.DisplayName, existing.OriginalFriendlyName);
        ApplyDeviceRestart(deviceInstanceId, existing.OriginalFriendlyName, restartNow);
    }

    /// <summary>
    /// After a successful, on-disk-verified FriendlyName write, either restarts the device so the change
    /// propagates immediately or defers it — then shows <b>the one outcome notification</b> the user gets
    /// (issue #40). No prompt: <paramref name="restartNow"/> is the consent already captured by the
    /// dialog's checkbox, whose text carries the link-drop consequence.
    ///
    /// <para><b>The verification is the point (issue #15/#40).</b> Success is never reported for an
    /// attempt — only for a description re-read from disk AFTER the device cycle and found to match.
    /// The verdict and its wording live in <see cref="AdapterNameRules.DescribeRestartOutcome"/> so that
    /// rule is unit-testable without a device; this method only performs the I/O and shows the result.</para>
    /// </summary>
    private void ApplyDeviceRestart(string deviceInstanceId, string appliedName, bool restartNow)
    {
        // The adapter's display name on this host has just changed, and it changed for BOTH write paths —
        // this method is the one point they converge on, and it is reached only after WriteFriendlyName
        // re-read the value from disk and matched it. So this is the earliest moment the change is a
        // fact, and the only one worth telling the caller about. Announced before the restart branch
        // below, not inside it: the name is already on disk either way, and a caller that re-reads only
        // when the user opted into a restart would keep a stale list for the far more common deferred
        // choice. Fire-and-forget by contract (Settings kicks off a background host read) — a throw here
        // must not turn a completed rename into a failure report, and this method has an outcome to show.
        try   { _onRenamed?.Invoke(); }
        catch (Exception ex)
        {
            UiActivityLog.Logger.LogWarning(
                "Rename flow: the post-rename refresh callback threw for '{Name}': {Error}", appliedName, ex.Message);
        }

        if (!restartNow)
        {
            // Honest "saved", not "applied": the write is verified on disk (WriteFriendlyName throws
            // otherwise), but nothing has re-read it into NDIS yet.
            UiActivityLog.Logger.LogInformation("Rename flow: device restart declined in dialog — description '{Name}' saved, not yet live", appliedName);
            Show(AdapterNameRules.DescribeDeferredOutcome(appliedName));
            return;
        }

        UiActivityLog.Logger.LogInformation("Rename flow: restarting device to apply description '{Name}'", appliedName);
        _ = Task.Run(() =>
        {
            AdapterNameRules.RenameOutcome outcome;
            try
            {
                // ★ DEVICE-MUTATING ★ Disable + enable to force NDIS to re-read the description.
                AdapterRenamer.RestartDevice(deviceInstanceId);

                // Confirm the description is still on disk after the cycle before claiming success — a PnP
                // re-enumeration could, in principle, have regenerated it (issue #15: never report a
                // success we haven't verified).
                var (present, current) = AdapterDeviceRegistry.ReadFriendlyName(deviceInstanceId);
                outcome = AdapterNameRules.DescribeRestartOutcome(present, current, appliedName);
            }
            catch (Exception ex)
            {
                outcome = AdapterNameRules.DescribeRestartFailure(appliedName, ex.Message);
            }

            UiActivityLog.Logger.LogInformation(
                "Rename flow: verified outcome for description '{Name}' — {Outcome}", appliedName, outcome.Kind);

            _ui.TryEnqueue(() => Show(outcome));
        });
    }

    /// <summary>
    /// Shows the single outcome notification. The mismatch and restart-failure cases stay real warnings;
    /// the verified success and the user's own deferred choice are plain information. The message text is
    /// the outcome's own — this method never composes a claim of its own.
    /// </summary>
    private static void Show(AdapterNameRules.RenameOutcome outcome)
    {
        if (outcome.NeedsAttention) NativeMethods.Warn(outcome.Message, AppName);
        else                        NativeMethods.Info(outcome.Message, AppName);
    }
}
