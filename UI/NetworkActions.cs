using Microsoft.Extensions.Logging;
using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using HyperVManagerTray.Services;

namespace HyperVManagerTray.UI;

/// <summary>
/// The app's live network commands — re-check, override, repair, and "add the current network as a
/// bridged rule" — shared by the tray menu and the Settings window (issue #34).
///
/// <para><b>Why this exists.</b> Issue #34 splits these four across two surfaces: Re-check and Override
/// stay in the tray as quick commands AND appear in Settings (which must be the complete superset),
/// while Repair and Add-current-network move to Settings only. Every one of them therefore has either
/// two call sites or a new one — and each carries hard-won behaviour that must not be re-derived at the
/// new site: the Wi-Fi rejection and MAC de-duplication in the add-rule flow, the "we genuinely don't
/// know" arm of the re-check, the never-silently-no-op override. Lifting the bodies here verbatim moves
/// them without rewriting them.</para>
///
/// <para><b>UI thread.</b> Every method is called from a menu command or a button click and awaits back
/// onto the UI thread (it shows dialogs and prompts). The genuinely blocking parts — NIC enumeration,
/// the config write — are explicitly offloaded, as they were before.</para>
/// </summary>
internal sealed class NetworkActions
{
    private readonly ConfigManager  _config;
    private readonly NetworkMonitor _monitor;
    private readonly HyperVManager  _hyperV;
    // Tray balloon (title, message, isError) — owned by App, which holds the TaskbarIcon. Lets the
    // manual network actions answer with the same non-blocking channel a failed apply uses (issue #37)
    // rather than a modal dialog.
    private readonly Action<string, string, bool> _notify;

    private const string AppName = AppInfo.Name;

    public NetworkActions(ConfigManager config, NetworkMonitor monitor, HyperVManager hyperV,
                          Action<string, string, bool> notify)
    {
        _config  = config;
        _monitor = monitor;
        _hyperV  = hyperV;
        _notify  = notify;
    }

    /// <summary>
    /// "Re-check network now" — re-evaluates the rules and ALWAYS answers with the result (issue #37,
    /// recommendation 5). This command previously fired <c>ForceEvaluateAsync</c> and returned, giving
    /// the user no way to tell whether it had run, matched, or failed.
    /// </summary>
    public async Task ReCheckNetworkAsync()
    {
        try
        {
            var result = await _monitor.ForceEvaluateAsync();
            if (result is null)
            {
                // Busy/disposed/threw — we genuinely don't know the outcome, so say that rather than
                // report a re-check that may never have run.
                _notify($"{AppName} — network", "Could not re-check the network right now — see switcher.log.", true);
                return;
            }

            var message = NetworkStatusUi.ReCheckMessage(
                result.RuleName, result.VirtualSwitch, result.ApplyStatus,
                result.HostAdapterName, result.FailedVms);
            _notify($"{AppName} — network", message, NetworkStatusUi.IsFailure(result.ApplyStatus));
        }
        catch (Exception ex)
        {
            UiActivityLog.Logger.LogWarning(ex, "Re-check network failed");
            _notify($"{AppName} — network", $"Re-check failed: {ex.Message}", true);
        }
    }

    /// <summary>
    /// "Override VM switch" — forces one VM onto one switch and confirms what it did, including the
    /// override's transience (issue #37, recommendation 5). Previously this silently no-opped when the
    /// VM wasn't in config and never confirmed anything in any case.
    /// </summary>
    public async Task OverrideSwitchAsync(string vmName, string switchName)
    {
        try
        {
            var outcome = await _monitor.ManualOverrideAsync(vmName, switchName);
            var (message, isError) = outcome switch
            {
                NetworkMonitor.OverrideOutcome.Applied =>
                    (NetworkStatusUi.OverrideAppliedMessage(vmName, switchName), false),
                NetworkMonitor.OverrideOutcome.NotConfigured =>
                    (NetworkStatusUi.OverrideNotConfiguredMessage(vmName), true),
                _ =>
                    (NetworkStatusUi.OverrideFailedMessage(vmName, switchName), true),
            };
            _notify($"{AppName} — {vmName}", message, isError);
        }
        catch (Exception ex)
        {
            UiActivityLog.Logger.LogWarning(ex, "Override switch failed for {Vm}", vmName);
            _notify($"{AppName} — {vmName}", $"Override failed: {ex.Message}", true);
        }
    }

    /// <summary>
    /// Manual escape hatch for the "host offline but VM online" failure: collapses any duplicate
    /// host vNIC on each configured bridged switch back to one (see
    /// <see cref="HyperVManager.RepairHostVNicAsync"/>) and reports the outcome.
    ///
    /// <para>Lives in Settings → Maintenance as of issue #34 (it is a recovery tool, not a quick
    /// command). The recorded trade-off: the automatic pass runs only once, ~15 s after startup
    /// (<c>App.HealSwitchOrphansOnStartupAsync</c>), so a mid-session dock cycle now needs Settings
    /// opened — which works fine while host networking is down, since all of this UI is local.</para>
    /// </summary>
    public async Task RepairHostNetworkingAsync()
    {
        try
        {
            var switches = _config.Current.RuleSwitches.ToList();

            if (switches.Count == 0)
            {
                NativeMethods.Info("No bridged switches are configured — nothing to repair.", AppName);
                return;
            }

            var repaired = new List<string>();
            bool anyError = false;
            foreach (var sw in switches)
            {
                var state = await _hyperV.RepairHostVNicAsync(sw).ConfigureAwait(true);
                if (state is HyperVManager.HostVNicState.Repaired or HyperVManager.HostVNicState.Reshared)
                    repaired.Add(sw);
                else if (state == HyperVManager.HostVNicState.Error)
                    anyError = true;
            }

            if (repaired.Count > 0)
                NativeMethods.Info(
                    $"Repaired host networking on: {string.Join(", ", repaired)}.\n\n" +
                    "A duplicate host network adapter was collapsed back to one — your wired connection should return within a few seconds.",
                    AppName);
            else if (anyError)
                NativeMethods.Warn("Could not repair host networking. See the log file for details.", AppName);
            else
                NativeMethods.Info("Host networking looks healthy — nothing to repair.", AppName);
        }
        catch (Exception ex)
        {
            NativeMethods.Warn($"Repair failed:\n{ex.Message}", AppName);
        }
    }

    /// <summary>
    /// "Add current network" — captures the LIVE host network (adapter description, MAC, IPv4 subnet) and
    /// writes it as a bridged rule, then re-checks so the new rule's effect is reported rather than
    /// assumed.
    ///
    /// <para>Lives in Settings → Network as of issue #34. The live-capture argument that originally put
    /// it in the tray was about CAPABILITY (auto-detected MAC/CIDR, Wi-Fi rejection, de-duplication), not
    /// about the tray as a location: the same code runs identically from Settings, and adding a rule is a
    /// configuration act performed a handful of times, not a quick command. It also sits where it is now
    /// most useful — beside "Add rule", which was until now the strictly worse path (a blank rule with a
    /// hand-typed MAC, the field most likely to be mistyped).</para>
    /// </summary>
    public async Task AddCurrentAsBridgedAsync()
    {
        // GetCurrentNetworkInfo enumerates all NICs (GetAllNetworkInterfaces + GetIPProperties) and can
        // block for hundreds of ms; this runs from a UI command, so offload it to the thread pool to keep
        // the UI responsive (issue #29, finding 3).
        var info = await Task.Run(AdapterMatcher.GetCurrentNetworkInfo);
        if (info is null)
        {
            NativeMethods.Warn("No active network adapter with an IPv4 address was found.", AppName);
            return;
        }

        // A Wi-Fi adapter surfaces as Msvm_WiFiPort, which the switch-binding path never targets, so a
        // rule bound to it could never take effect (issue #29, finding 5). Reject it up front with an
        // explanation rather than silently saving a rule that will never bridge.
        if (info.IsWireless)
        {
            NativeMethods.Warn(
                $"\"{info.AdapterDescription}\" is a Wi-Fi adapter.\n\n" +
                "Bridging a Hyper-V switch onto a wireless adapter isn't supported — the switch can only " +
                "bind to a wired (Ethernet) adapter, such as a USB-Ethernet dock. No rule was added.",
                AppName);
            return;
        }

        var normNew   = AdapterMatcher.NormalizeMac(info.Mac);
        var duplicate = _config.Current.Rules.FirstOrDefault(r =>
            r.Conditions.AdapterMac is not null &&
            AdapterMatcher.NormalizeMac(r.Conditions.AdapterMac) == normNew);
        if (duplicate is not null)
        {
            // Now that this flow lives in the rules editor, the existing rule is right there to edit —
            // so point at it rather than at config.json (which is no longer the only way to change it).
            NativeMethods.Info(
                $"This adapter is already covered by rule \"{duplicate.Name}\".\n\nEdit that rule above to update it.",
                AppName);
            return;
        }

        var fallbackSwitch = _config.Current.Fallback.VirtualSwitch;
        var bridgedSwitch  = _config.Current.Rules
            .Select(r => r.VirtualSwitch)
            .Where(s => s != fallbackSwitch)
            .OrderBy(s => s.Contains("bridge", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault() ?? "Bridged";

        // Ask for a memorable name ("Home", "Office", "Coffee shop …") instead of silently using
        // the raw adapter description (e.g. "Intel(R) Wi-Fi 6 AX201 160MHz") as the rule name —
        // that says nothing about WHERE the network is. Pre-filled with the adapter description as
        // a convenient starting point; Cancel here aborts the whole "add rule" flow.
        var defaultName = info.AdapterDescription.Length > 40 ? info.AdapterDescription[..40].TrimEnd() : info.AdapterDescription;
        var name = await TextPromptWindow.ShowAsync(
            "Add Current Network",
            $"Name this network (adapter: {info.AdapterDescription}):",
            defaultName);
        if (name is null) return;

        if (!NativeMethods.Confirm(
                $"Add the following rule?\n\n" +
                $"  Name    :  {name}\n" +
                $"  Adapter :  {info.AdapterDescription}\n" +
                $"  MAC     :  {info.Mac}\n" +
                $"  Network :  {info.IpCidr}\n" +
                $"  Switch  :  {bridgedSwitch}",
                "Add Current Network"))
            return;

        var rule = new NetworkRule
        {
            Name          = name,
            Priority      = _config.Current.Rules.Count > 0 ? _config.Current.Rules.Max(r => r.Priority) + 10 : 10,
            Conditions    = new RuleConditions { AdapterMac = info.Mac, IpCidr = info.IpCidr },
            VirtualSwitch = bridgedSwitch,
            TargetVms     = _config.Current.Fallback.TargetVms.ToList(),
        };

        try
        {
            await Task.Run(() => _config.AddBridgedRule(rule)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            NativeMethods.Error($"Failed to save rule:\n{ex.Message}", AppName);
            return;
        }

        // Apply the new rule AND report whether it actually took effect — a rule that saves fine but
        // fails to bind would otherwise look like a success (issue #37).
        await ReCheckNetworkAsync();
    }
}
