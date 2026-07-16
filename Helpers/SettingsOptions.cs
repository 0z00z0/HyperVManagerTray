using HyperVManagerTray.Models;
using HyperVManagerTray.Services;
using Microsoft.Extensions.Logging;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// Pure, WinUI-free mapping logic between the persisted <see cref="Models.AppConfig"/> values and
/// the Settings window's ComboBox indices (issue #18).  Kept dependency-free (only
/// <see cref="LogLevel"/> from Microsoft.Extensions.Logging) so it can be unit-tested in isolation
/// and linked straight into the test assembly — the round-trip guarantees (index ⇄ value with no
/// silent loss of a hand-edited config value) are the load-bearing correctness checks and are
/// exercised by tests.
/// </summary>
public static class SettingsOptions
{
    // ── On-bridge-lost action (VmTarget.OnBridgeLostAction) ─────────────────────
    // The persisted value is a nullable string: null / "none" = do nothing, else one of
    // "pause" / "save" / "shutdown" (case-insensitive on read, canonical lower-case on write).

    /// <summary>The picker's rows, in display order. <c>Value</c> is the canonical persisted string (null = do nothing).</summary>
    public static readonly IReadOnlyList<(string Label, string? Value)> BridgeLostActions =
    [
        ("Do nothing", null),
        ("Pause",      "pause"),
        ("Save",       "save"),
        ("Shut down",  "shutdown"),
    ];

    /// <summary>
    /// Canonicalises a stored action string: trims, lower-cases, and maps anything that isn't a
    /// recognised action (empty, "none", or an unknown token) to <c>null</c> ("do nothing").
    /// </summary>
    public static string? NormalizeBridgeLostAction(string? action)
    {
        var t = action?.Trim().ToLowerInvariant();
        return t switch
        {
            "pause" or "save" or "shutdown" => t,
            _                               => null,
        };
    }

    /// <summary>Index into <see cref="BridgeLostActions"/> for a stored action (0 = "Do nothing" for anything unrecognised).</summary>
    public static int BridgeLostActionToIndex(string? action) =>
        IndexOfValue(BridgeLostActions, NormalizeBridgeLostAction(action), fallback: 0);

    /// <summary>The canonical action string for a picker index (out-of-range → null).</summary>
    public static string? IndexToBridgeLostAction(int index) =>
        index >= 0 && index < BridgeLostActions.Count ? BridgeLostActions[index].Value : null;

    // ── On-bridge-lost delay (VmTarget.OnBridgeLostDelaySeconds) ────────────────

    /// <summary>Preset delays offered in the picker (seconds).  A stored non-preset value is shown as a custom entry by the UI.</summary>
    public static readonly IReadOnlyList<int> BridgeLostDelaySeconds = [0, 5, 10, 30, 60, 120, 300];

    /// <summary>Clamps a delay to a sane range [0, 86400]; negatives (a hand-edited config) fall back to the model default of 30.</summary>
    public static int NormalizeDelaySeconds(int seconds) =>
        seconds < 0 ? 30 : Math.Min(seconds, 86_400);

    /// <summary>
    /// The delay <c>NetworkMonitor</c> actually waits before running a VM's on-bridge-lost action — the
    /// single place that answers "how long?", so the picker and the monitor cannot disagree.
    ///
    /// <para><b>0 is a value, not a sentinel.</b> <see cref="BridgeLostDelaySeconds"/> offers 0 and
    /// <see cref="FormatDelay"/> renders it "Immediate": it is user-selectable and correctly persisted.
    /// The monitor used to read it inline as <c>delay &gt; 0 ? delay : 30</c>, treating the user's
    /// explicit "Immediate" as "unset" and silently waiting 30 s instead — so the picker stated a delay
    /// the app did not honour. "Unset" needs no sentinel here and never did: it is already handled where
    /// it belongs, by <c>VmTarget.OnBridgeLostDelaySeconds</c>'s own default of 30, so an omitted value
    /// never arrives as 0 in the first place.</para>
    /// </summary>
    public static int EffectiveBridgeLostDelaySeconds(VmTarget vm) =>
        NormalizeDelaySeconds(vm.OnBridgeLostDelaySeconds);

    /// <summary>
    /// Human label for a delay in seconds ("Immediate" for 0, "45 s", "1 min 30 s", "5 min",
    /// "1 h 30 min").
    ///
    /// <para>One style throughout (issue #42): every unit is spaced and spelled the same way at every
    /// magnitude. The hours branch used to switch to a compact, unspaced "1h 30m" while the minutes
    /// branch said "1 min 30 s", so a single picker showed two conventions in one drop-down.</para>
    /// </summary>
    public static string FormatDelay(int seconds)
    {
        if (seconds <= 0) return "Immediate";
        if (seconds < 60) return $"{seconds} s";
        int m = seconds / 60, s = seconds % 60;
        if (m < 60) return s == 0 ? $"{m} min" : $"{m} min {s} s";
        int h = m / 60; m %= 60;
        return m == 0 ? $"{h} h" : $"{h} h {m} min";
    }

    // ── Log level (AppConfig.LogLevel) ──────────────────────────────────────────

    /// <summary>The log-level picker rows, coarse-to-verbose top-down (Trace most verbose).</summary>
    public static readonly IReadOnlyList<(string Label, LogLevel Value)> LogLevels =
    [
        ("Trace — most verbose", LogLevel.Trace),
        ("Debug (default)",       LogLevel.Debug),
        ("Information",           LogLevel.Information),
        ("Warning",               LogLevel.Warning),
        ("Error",                 LogLevel.Error),
        ("Critical",              LogLevel.Critical),
        ("None — logging off",    LogLevel.None),
    ];

    /// <summary>Index into <see cref="LogLevels"/> for a level (unknown → Debug's index).</summary>
    public static int LogLevelToIndex(LogLevel level) =>
        IndexOfValue(LogLevels, level, fallback: IndexOfValue(LogLevels, LogLevel.Debug, fallback: 0));

    /// <summary>The <see cref="LogLevel"/> for a picker index (out-of-range → Debug).</summary>
    public static LogLevel IndexToLogLevel(int index) =>
        index >= 0 && index < LogLevels.Count ? LogLevels[index].Value : LogLevel.Debug;

    // ── Network rules editor (issue #23) ────────────────────────────────────────
    // Pure, WinUI-free validation/normalisation for the rules editor, so the round-trip guarantees
    // (a hand-edited config value survives edit-something-else → save without silent loss) are testable.

    /// <summary>
    /// True when <paramref name="mac"/> is a well-formed 48-bit MAC (12 hex digits, optionally separated
    /// by ':' or '-'). Null/blank is treated as valid (means "don't match on MAC").
    /// </summary>
    public static bool IsValidMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return true;
        var clean = mac.Replace(":", "").Replace("-", "").Trim();
        return clean.Length == 12 && clean.All(Uri.IsHexDigit);
    }

    /// <summary>
    /// Canonicalises a MAC to upper-case colon form ("AA:BB:CC:DD:EE:FF"). Blank → null. A value that
    /// isn't a well-formed 12-hex-digit MAC is returned trimmed and unchanged, so a hand-typed value in
    /// progress is never silently mangled (the UI gates saving on <see cref="IsValidMac"/>). The final
    /// colon-grouping delegates to <see cref="AdapterMatcher.FormatMac"/> so a MAC canonicalises
    /// identically whether it flows through Settings or the tray (cleanup 12).
    /// </summary>
    public static string? CanonicalizeMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return null;
        var clean = mac.Replace(":", "").Replace("-", "").Trim().ToUpperInvariant();
        if (clean.Length != 12 || !clean.All(Uri.IsHexDigit)) return mac.Trim();
        return AdapterMatcher.FormatMac(clean);
    }

    /// <summary>
    /// True when <paramref name="cidr"/> is a well-formed IPv4 CIDR ("10.0.0.0/23"): a dotted-quad and a
    /// prefix length in [0, 32]. Null/blank is valid (means "don't match on IP").
    /// </summary>
    public static bool IsValidCidr(string? cidr)
    {
        if (string.IsNullOrWhiteSpace(cidr)) return true;
        var parts = cidr.Trim().Split('/');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[1], out int prefix) || prefix < 0 || prefix > 32) return false;
        if (!System.Net.IPAddress.TryParse(parts[0], out var ip)) return false;
        return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
    }

    /// <summary>Trims a MAC/CIDR field to null when blank, else the trimmed string.</summary>
    public static string? BlankToNull(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>
    /// True when a rule declares no match condition at all — neither a MAC nor a CIDR.
    ///
    /// <para><b>Such a rule matches EVERY network.</b> That is not a quirk, it is
    /// <c>AdapterMatcher.MatchingNic</c>'s documented contract ("a rule with no conditions matches the
    /// current primary adapter unconditionally"): a null <c>AdapterMac</c> passes the MAC test, and a
    /// null <c>IpCidr</c> returns the NIC there and then. Combined with the first-match-wins evaluation
    /// order, one conditionless rule at a low priority shadows every rule after it and permanently
    /// suppresses the fallback.</para>
    /// </summary>
    public static bool DeclaresNoCondition(Models.NetworkRule? rule) =>
        BlankToNull(rule?.Conditions?.AdapterMac) is null && BlankToNull(rule?.Conditions?.IpCidr) is null;

    /// <summary>
    /// True when a rule is complete enough to be worth writing to config.json: it names a virtual switch
    /// to bind, AND it declares at least one condition saying WHEN to bind it.
    ///
    /// <para><b>What this exists to stop.</b> Settings' "Add rule" button used to persist its blank
    /// template — <c>{name:"New rule", priority:100, virtualSwitch:"", conditions:{}}</c> — the instant
    /// it was clicked, before the user had typed anything. Both halves of that are harmful, and the
    /// combination is worse than either. No conditions means it matches every network
    /// (<see cref="DeclaresNoCondition"/>), so it wins evaluation ahead of any real rule the user goes on
    /// to write; a blank switch means the bind then fails, which paints the tray icon red. Because the
    /// active result's rule name is that rule's and never "Fallback",
    /// <c>App.HandleBridgeTransition</c>'s <c>bridgeJustLost</c> edge can never fire either — so every
    /// VM's configured on-bridge-lost pause/save/shutdown silently stops running. Issue #38's empty
    /// default config makes this blank rule the ONLY rule on a fresh install, i.e. the first thing a new
    /// user does breaks the app quietly.</para>
    ///
    /// <para>Judge a rule by its CLEANED form (<c>ConfigManager.CleanRule</c>), which drops a malformed
    /// MAC or CIDR to null: a rule whose only condition is unparseable is a catch-all once persisted, so
    /// it must be treated as one here rather than on the strength of the text the user typed.</para>
    /// </summary>
    public static bool IsPersistableRule(Models.NetworkRule? rule) =>
        rule is not null
        && !string.IsNullOrWhiteSpace(rule.VirtualSwitch)
        && !DeclaresNoCondition(rule);

    /// <summary>
    /// Splits a comma- and/or newline-separated VM list into a cleaned list: trimmed, blanks dropped,
    /// duplicates removed case-insensitively (first spelling wins). Backs both the rule and fallback
    /// target-VM editors so a hand-edited config value round-trips predictably.
    /// </summary>
    public static List<string> ParseVmList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        return CleanVmList(text.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Splits a strictly NEWLINE-separated VM list into a cleaned list (trimmed, blanks dropped,
    /// case-insensitive dedupe). Unlike <see cref="ParseVmList"/> it does NOT treat a comma as a
    /// separator, so a VM whose name legitimately contains a comma (e.g. "Web, App") survives the
    /// round-trip intact. This is the representation the Settings editor uses — one VM per line — which
    /// is unambiguous where the old single-line comma form corrupted such names.
    /// </summary>
    public static List<string> ParseVmLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        return CleanVmList(text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Joins a VM list one-per-line for the multi-line editor (the unambiguous counterpart to
    /// <see cref="ParseVmLines"/>; safe even when a name contains a comma).</summary>
    public static string JoinVmLines(IEnumerable<string> names) => string.Join(Environment.NewLine, names);

    /// <summary>Trims, drops blanks, and removes case-insensitive duplicates (first spelling wins).</summary>
    public static List<string> CleanVmList(IEnumerable<string> names)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in names)
        {
            var name = raw.Trim();
            if (name.Length > 0 && seen.Add(name)) result.Add(name);
        }
        return result;
    }

    /// <summary>Joins a VM list for display in a single-line editor ("VM1, VM2").</summary>
    public static string JoinVmList(IEnumerable<string> names) => string.Join(", ", names);

    /// <summary>Clamps a rule priority to a sane, non-negative range (a hand-edited negative → 0).</summary>
    public static int NormalizePriority(int priority) => Math.Clamp(priority, 0, 100_000);

    // ── Live-value suggestions (issue #41) ──────────────────────────────────────
    // The identity fields (virtual switch, target VM, adapter MAC, a managed VM's NIC name) name things
    // the app can already enumerate off the host, yet were hand-typed — so a typo produced a rule that
    // silently never matched. These helpers shape the enumerated values into picker rows. They are
    // deliberately ASSISTIVE, never restrictive: the current value always survives, and a value that is
    // not in the live list is never dropped, because a rule is legitimately written ahead of the switch
    // or VM it names (and the host may simply be offline when Settings is opened).

    /// <summary>
    /// The rows an editable picker offers for an identity field: <paramref name="current"/> first when it
    /// is a real value the live list doesn't already contain (so a rule naming a not-yet-created switch,
    /// or written while the host was unreachable, still shows its own value as a choice), then the live
    /// values sorted case-insensitively. Blank/whitespace entries are dropped and duplicates removed
    /// case-insensitively (first spelling wins).
    ///
    /// <para>The result is only ever a SUGGESTION list — the picker stays editable, so a value absent
    /// from it can still be typed and persisted. An empty result (host offline, enumeration failed) is
    /// therefore not a failure state: the control simply behaves as the plain text box it replaced.</para>
    /// </summary>
    public static List<string> SuggestionItems(string? current, IEnumerable<string>? live)
    {
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        var liveClean = (live ?? [])
            .Select(v => v?.Trim() ?? string.Empty)
            .Where(v => v.Length > 0)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var cur = current?.Trim();
        if (!string.IsNullOrEmpty(cur) && !liveClean.Contains(cur, StringComparer.OrdinalIgnoreCase))
        {
            result.Add(cur);
            seen.Add(cur);
        }

        foreach (var v in liveClean)
            if (seen.Add(v)) result.Add(v);

        return result;
    }

    /// <summary>
    /// Canonicalises a managed VM's NIC name (<see cref="Models.VmTarget.NicName"/>): trims, and maps
    /// blank to the Hyper-V default <c>"Network Adapter"</c> — the same default
    /// <see cref="Services.ConfigManager.AddVmToConfig"/> applies, so clearing the new NIC editor
    /// restores the default rather than persisting an empty name that would match no adapter.
    /// A non-blank exotic value is preserved verbatim (bar trimming): the VM's adapter may have been
    /// renamed to anything, and this app is not the authority on what Hyper-V allows.
    /// </summary>
    public static string NormalizeNicName(string? nicName) =>
        string.IsNullOrWhiteSpace(nicName) ? DefaultNicName : nicName.Trim();

    /// <summary>The Hyper-V default name of a VM's first synthetic network adapter.</summary>
    public const string DefaultNicName = "Network Adapter";

    /// <summary>
    /// Appends <paramref name="vmName"/> to a one-VM-per-line editor's text, returning the new text.
    /// Backs the "Add from discovered VMs" affordance on the rule/fallback target-VM boxes: picking a VM
    /// must ADD to what the user has, never replace it, and picking the same VM twice must not duplicate
    /// it (the comparison is case-insensitive, matching <see cref="CleanVmList"/>'s dedupe). Returns the
    /// text unchanged when the name is blank or already listed.
    /// </summary>
    public static string AppendVmLine(string? existingText, string? vmName)
    {
        var name = vmName?.Trim();
        if (string.IsNullOrEmpty(name)) return existingText ?? string.Empty;

        var lines = ParseVmLines(existingText);
        if (lines.Contains(name, StringComparer.OrdinalIgnoreCase)) return existingText ?? string.Empty;

        lines.Add(name);
        return JoinVmLines(lines);
    }

    // ── Shared ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Linear index of the first option whose <c>Value</c> equals <paramref name="value"/>, else
    /// <paramref name="fallback"/>. Backs the *ToIndex mappers so the twin search loops don't drift.
    /// </summary>
    private static int IndexOfValue<T>(IReadOnlyList<(string Label, T Value)> options, T value, int fallback)
    {
        for (int i = 0; i < options.Count; i++)
            if (EqualityComparer<T>.Default.Equals(options[i].Value, value)) return i;
        return fallback;
    }
}
