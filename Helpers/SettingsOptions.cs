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

    /// <summary>Human label for a delay in seconds ("Immediate" for 0, "90 s", "5 min", "1h 30m").</summary>
    public static string FormatDelay(int seconds)
    {
        if (seconds <= 0) return "Immediate";
        if (seconds < 60) return $"{seconds} s";
        int m = seconds / 60, s = seconds % 60;
        if (m < 60) return s == 0 ? $"{m} min" : $"{m} min {s} s";
        int h = m / 60; m %= 60;
        return m == 0 ? $"{h} h" : $"{h}h {m}m";
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
