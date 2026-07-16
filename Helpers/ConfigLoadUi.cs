namespace HyperVManagerTray.Helpers;

/// <summary>
/// What a <see cref="Services.ConfigManager.Load"/> call actually did (issue #39). Before this type,
/// <c>Load</c> returned <c>void</c> and swallowed every exception into a log line, so no caller could
/// tell a successful reload from a failed one — which is exactly how "Reload config from disk" came to
/// rebuild the whole Settings window from the STALE config after a parse failure, showing the user
/// their old values as though they had just been read off disk.
/// </summary>
/// <remarks>
/// Deliberately constructed only through <see cref="Success"/> / <see cref="Failure"/>: the default
/// <c>Succeeded</c> for a bare <c>new ConfigLoadOutcome()</c> is <c>false</c>, so a half-initialised
/// outcome fails safe rather than reading as success.
/// </remarks>
public sealed record ConfigLoadOutcome
{
    /// <summary>True only when config.json was read AND parsed. False for a missing, unreadable or
    /// malformed file — in which case the previously loaded config is still the live one.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Rules in the freshly loaded config. Meaningless (0) on failure.</summary>
    public int RuleCount { get; init; }

    /// <summary>Managed VMs in the freshly loaded config. Meaningless (0) on failure.</summary>
    public int VmCount { get; init; }

    /// <summary>The exception message on failure; null on success. A <see cref="System.Text.Json.JsonException"/>
    /// message already names the line and byte position, which is the single most useful thing we can
    /// tell someone who has just hand-edited the file.</summary>
    public string? Error { get; init; }

    public static ConfigLoadOutcome Success(int ruleCount, int vmCount) =>
        new() { Succeeded = true, RuleCount = ruleCount, VmCount = vmCount };

    public static ConfigLoadOutcome Failure(string error) =>
        new() { Succeeded = false, Error = error };
}

/// <summary>
/// Pure outcome → UI decisions for config loading (issue #39), the config-side counterpart to
/// <see cref="NetworkStatusUi"/>. No file IO, no WinUI — every decision here is unit-testable.
///
/// <para><b>The invariant this file exists to hold</b> — the same one <see cref="NetworkStatusUi"/>
/// holds for the network path, and for the same reason: <i>a surface must never report a state the app
/// has not confirmed</i>. Concretely, a failed load must never be presentable as a successful reload.
/// <see cref="SuccessMessage"/> returns null for anything that is not
/// <see cref="ConfigLoadOutcome.Succeeded"/>, and <see cref="ShouldRebuildFromConfig"/> — the gate the
/// Settings "Reload config from disk" button consults before re-rendering its sections — is simply
/// that same flag, named so the call site reads as the promise it is making.
/// <c>ConfigLoadUiTests.FailedLoadCanNeverBeReportedAsSuccess</c> enforces it.</para>
/// </summary>
public static class ConfigLoadUi
{
    /// <summary>
    /// The inline confirmation shown next to the Reload button on the happy path — it names WHAT was
    /// loaded rather than just "OK", because "it reloaded" is not the question the user has; "did it
    /// pick up my edit?" is, and a rule/VM count answers it. Null when the load did not succeed:
    /// there is no success sentence for a failure, by construction.
    /// </summary>
    public static string? SuccessMessage(ConfigLoadOutcome outcome) =>
        outcome is null ? null
        : !outcome.Succeeded ? null
        : $"Reloaded — {Count(outcome.RuleCount, "rule")}, {Count(outcome.VmCount, "VM")}";

    /// <summary>
    /// The dialog body for a failed manual reload. States the parse error (which carries the line
    /// number) AND, crucially, that the previous settings are still active — without that second
    /// sentence the user is left to guess whether the app is now running on nothing. Null on success.
    /// </summary>
    public static string? FailureMessage(ConfigLoadOutcome outcome) =>
        outcome is null || outcome.Succeeded ? null
        : "config.json could not be read, so nothing was reloaded.\n\n"
          + $"{Detail(outcome.Error)}\n\n"
          + "The settings that were already loaded are still active. Fix the file and reload again.";

    /// <summary>
    /// The tray balloon for a failed load that the user did not explicitly ask for — a broken save
    /// picked up by the file watcher, or a corrupt file at startup. Short by necessity (Win32 caps the
    /// balloon text), so it leads with the fact that the edit did NOT take effect. Null on success.
    /// </summary>
    public static string? BalloonMessage(ConfigLoadOutcome outcome) =>
        outcome is null || outcome.Succeeded ? null
        : $"config.json has an error — keeping the previous settings.\n{FirstLine(Detail(outcome.Error))}";

    /// <summary>
    /// Whether a caller may re-render its UI from <see cref="Services.ConfigManager.Current"/> after a
    /// load. False on failure: <c>Current</c> then still holds the PREVIOUS config, and rebuilding from
    /// it is what made a broken edit look applied.
    /// </summary>
    public static bool ShouldRebuildFromConfig(ConfigLoadOutcome outcome) =>
        outcome is not null && outcome.Succeeded;

    /// <summary>"1 rule" / "3 rules" — a count is worthless if the user has to decode "rule(s)".</summary>
    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    /// <summary>Guards against an empty/blank error string producing a message with a hole in it.</summary>
    private static string Detail(string? error) =>
        string.IsNullOrWhiteSpace(error) ? "The file could not be parsed." : error.Trim();

    /// <summary>First line only — a JsonException message is one line, but an IO exception may not be.</summary>
    private static string FirstLine(string text)
    {
        int i = text.IndexOfAny(['\r', '\n']);
        return i < 0 ? text : text[..i];
    }
}
