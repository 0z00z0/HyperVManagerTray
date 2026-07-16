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
/// <para><b>One vocabulary, four commands (issue #51).</b> Lifting those bodies verbatim also carried
/// their report channels in unexamined, and they disagreed: Re-check and Override answered with a tray
/// balloon while Repair and Add-current-network answered with blocking modals — two of which then landed
/// side by side in one Settings row, so two adjacent buttons spoke two languages. All four now REPORT
/// through <see cref="_notify"/> and ASK through <see cref="NativeMethods"/>, per the rule in
/// <c>docs/DISPLAY-VOCABULARY.md</c>: <b>ask with a modal, tell with a balloon</b>. The `XamlRoot`
/// constraint that appeared to force the split does not exist here — <see cref="NativeMethods"/> is
/// parentless Win32 <c>MessageBoxW</c>, so both channels always worked from both surfaces, and the split
/// was never anything but an accident of the move. Read that file before adding a fifth command.</para>
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
    // rather than a modal dialog. This is the ONLY way any of the four commands below reports an
    // outcome (issue #51) — see docs/DISPLAY-VOCABULARY.md.
    private readonly Action<string, string, bool> _notify;

    private const string AppName = AppInfo.Name;

    // Every one of these commands is about the host network, so they share one balloon title. The
    // per-VM override overrides it with the VM's name, which is the more specific subject.
    private static string NetworkTitle => $"{AppName} — network";

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
                _notify($"{AppName} — network", NetworkStatusUi.ReCheckUnavailableMessage(), true);
                return;
            }

            // This command owns the report for the pass it just ran: ForceEvaluateAsync marks the result
            // UserInitiated, which stands the automatic failure balloon down (App.NotifyIfApplyFailed),
            // so a failing re-check answers once here instead of firing two toasts.
            _notify($"{AppName} — network",
                    NetworkStatusUi.ReCheckMessage(result),
                    NetworkStatusUi.IsFailure(result.ApplyStatus));
        }
        catch (Exception ex)
        {
            UiActivityLog.Logger.LogWarning(ex, "Re-check network failed");
            _notify($"{AppName} — network", NetworkStatusUi.ReCheckUnexpectedErrorMessage(ex.Message), true);
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
            // As with Re-check, this command owns the report: ManualOverrideAsync marks its result
            // UserInitiated so the automatic balloon stands down. It has to be this path — only here is
            // the NotConfigured outcome visible (no apply pass ever runs for it), and only here is it
            // known that the user asked for an override rather than a rule having fired.
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
            _notify($"{AppName} — {vmName}", NetworkStatusUi.OverrideUnexpectedErrorMessage(vmName, ex.Message), true);
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
                _notify(NetworkTitle, NetworkStatusUi.RepairNoSwitchesMessage(), false);
                return;
            }

            // Collect what happened to EACH switch and let NetworkStatusUi compose the report. This used
            // to be an if/else chain right here, which is how it came to say three untrue things — most
            // sharply, NoSwitch (the configured switch does not exist on the host) matched neither the
            // "repaired" nor the "error" arm and so fell into the else, reporting a clean bill of health
            // for a switch that was never found. Nothing here decides what to SAY any more; this loop only
            // reports what it saw, and the pure class is where the claims are made and tested.
            var outcomes = new List<NetworkStatusUi.RepairStepOn>();
            foreach (var sw in switches)
            {
                var state = await _hyperV.RepairHostVNicAsync(sw).ConfigureAwait(true);
                outcomes.Add(new NetworkStatusUi.RepairStepOn(sw, StepFor(state)));
            }

            var report = NetworkStatusUi.RepairReportFor(outcomes);
            _notify(NetworkTitle, report.Message, report.IsError);
        }
        catch (Exception ex)
        {
            UiActivityLog.Logger.LogWarning(ex, "Repair host networking failed");
            _notify(NetworkTitle, NetworkStatusUi.RepairUnexpectedErrorMessage(ex.Message), true);
        }
    }

    /// <summary>
    /// Maps the WMI mutator's per-switch outcome to the pure reporting enum. The only line in this class
    /// that knows both types, and deliberately total: a new <c>HostVNicState</c> lands in the
    /// <see cref="NetworkStatusUi.RepairStep.Failed"/> arm — "we do not know that this worked" — rather
    /// than defaulting into a success or a silent no-op, which is the same never-optimistic-by-default
    /// rule <see cref="NetworkStatusUi.IconFor"/> and <see cref="NetworkStatusUi.FromBindOutcome"/> hold.
    /// </summary>
    private static NetworkStatusUi.RepairStep StepFor(HyperVManager.HostVNicState state) => state switch
    {
        HyperVManager.HostVNicState.Ok       => NetworkStatusUi.RepairStep.Inspected,
        HyperVManager.HostVNicState.Repaired => NetworkStatusUi.RepairStep.Collapsed,
        HyperVManager.HostVNicState.Reshared => NetworkStatusUi.RepairStep.ShareRestored,
        HyperVManager.HostVNicState.NoSwitch => NetworkStatusUi.RepairStep.SwitchNotFound,
        _                                    => NetworkStatusUi.RepairStep.Failed,   // Error, and any future member
    };

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
    /// <para><b>Top-level try/catch, like its three siblings.</b> This is invoked from an
    /// <c>async void</c> handler, so an escaping exception becomes a stowed exception on the
    /// DispatcherQueue and tears down the whole tray process — taking the network monitor and any
    /// pending bridge-lost timers with it. It is not a theoretical risk here:
    /// <see cref="TextPromptWindow.ShowAsync"/> constructs a Window and does native monitor placement in
    /// its constructor, which is precisely the failure class <c>SafeInit</c> exists to contain.</para>
    public async Task AddCurrentAsBridgedAsync()
    {
        try
        {
            await AddCurrentAsBridgedCoreAsync();
        }
        catch (Exception ex)
        {
            UiActivityLog.Logger.LogWarning(ex, "Add current network failed");
            _notify(NetworkTitle, NetworkStatusUi.AddRuleUnexpectedErrorMessage(ex.Message), true);
        }
    }

    private async Task AddCurrentAsBridgedCoreAsync()
    {
        // GetCurrentNetworkInfo enumerates all NICs (GetAllNetworkInterfaces + GetIPProperties) and can
        // block for hundreds of ms; this runs from a UI command, so offload it to the thread pool to keep
        // the UI responsive (issue #29, finding 3).
        var info = await Task.Run(AdapterMatcher.GetCurrentNetworkInfo);
        if (info is null)
        {
            _notify(NetworkTitle, NetworkStatusUi.AddRuleNoAdapterMessage(), true);
            return;
        }

        // A Wi-Fi adapter surfaces as Msvm_WiFiPort, which the switch-binding path never targets, so a
        // rule bound to it could never take effect (issue #29, finding 5). Reject it up front with an
        // explanation rather than silently saving a rule that will never bridge.
        if (info.IsWireless)
        {
            _notify(NetworkTitle, NetworkStatusUi.AddRuleWirelessMessage(info.AdapterDescription), true);
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
            _notify(NetworkTitle, NetworkStatusUi.AddRuleDuplicateMessage(duplicate.Name), false);
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
            "Add current network",
            $"Name this network (adapter description: {info.AdapterDescription}):",
            defaultName);
        if (name is null) return;

        // The one modal left in this class, and it stays (issue #51): this ASKS — the user's answer
        // decides whether a rule is written at all — and blocking is the point of asking. Only REPORTS
        // moved to the balloon. See docs/DISPLAY-VOCABULARY.md.
        //
        // The rule summary names each field with the pinned vocabulary (issue #42): the value beside
        // "Adapter" is the adapter's DESCRIPTION, not its Windows name/alias — the very distinction
        // whose absence made a working rename look broken.
        if (!NativeMethods.Confirm(
                $"Add the following rule?\n\n" +
                $"  Name                :  {name}\n" +
                $"  Adapter description :  {info.AdapterDescription}\n" +
                $"  MAC                 :  {info.Mac}\n" +
                $"  Network             :  {info.IpCidr}\n" +
                $"  Virtual switch      :  {bridgedSwitch}",
                "Add current network"))
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
            _notify(NetworkTitle, NetworkStatusUi.AddRuleSaveFailedMessage(ex.Message), true);
            return;
        }

        // Apply the new rule AND report whether it actually took effect — a rule that saves fine but
        // fails to bind would otherwise look like a success (issue #37).
        await ReCheckNetworkAsync();
    }
}
