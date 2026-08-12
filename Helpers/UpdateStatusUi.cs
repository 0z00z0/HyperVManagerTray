using HyperVManagerTray.Services;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// Pure outcome → UI decisions for the update check — the update-side counterpart to
/// <see cref="NetworkStatusUi"/> and <see cref="ConfigLoadUi"/>. No HTTP, no WinUI, so every sentence
/// the user can read about an update check is assertable here (docs/DISPLAY-VOCABULARY.md, corollary 4).
/// A report states only what was verified (corollary 3): each arm names what actually happened, and only
/// <see cref="UpdateCheckOutcome.NetworkUnavailable"/> may mention the user's connection.
/// </summary>
internal static class UpdateStatusUi
{
    /// <summary>What to tell the user, and whether it is a failure. Mirrors
    /// <see cref="NetworkStatusUi.RepairReport"/>: the caller picks the channel, never the wording.</summary>
    public readonly record struct UpdateReport(string Message, bool IsError);

    /// <summary>
    /// The message for a completed check. Null for <see cref="UpdateCheckOutcome.UpdateAvailable"/> —
    /// that outcome is answered by the update dialog, which says considerably more than a sentence
    /// could, and a second message beside it would be the "one action, one report" break (corollary 5).
    /// </summary>
    /// <param name="runningVersion">The build the user is on, for the up-to-date confirmation.</param>
    /// <param name="now">Reference instant for phrasing the rate-limit retry time.</param>
    public static UpdateReport? ReportFor(UpdateChecker.CheckResult result, string runningVersion, DateTimeOffset now)
    {
        if (result is null) return null;

        return result.Outcome switch
        {
            UpdateCheckOutcome.UpdateAvailable => null,

            UpdateCheckOutcome.UpToDate =>
                new UpdateReport($"You're on the latest version ({runningVersion}).", IsError: false),

            // Not a failure: the app works, there is simply nothing published to compare against.
            UpdateCheckOutcome.NoReleases =>
                new UpdateReport("No releases have been published yet.", IsError: false),

            UpdateCheckOutcome.RateLimited =>
                new UpdateReport(
                    "GitHub is limiting how many update checks it will answer, so this one could not run. "
                    + "The limit is GitHub's, on requests that aren't signed in — it is not a problem with "
                    + "your connection.\n\n"
                    + RetrySentence(result.RateLimitResetsAt, now),
                    IsError: true),

            // Names the status code: it is the whole difference between "GitHub is broken" (5xx) and
            // "GitHub is refusing us" (a 403 that isn't a throttle), and the user can quote it.
            UpdateCheckOutcome.HttpError =>
                new UpdateReport(
                    $"GitHub answered the update check with HTTP {result.StatusCode}, so no version could be "
                    + "read. Try again later — see switcher.log.",
                    IsError: true),

            // The one arm entitled to blame the network: nothing came back at all.
            UpdateCheckOutcome.NetworkUnavailable =>
                new UpdateReport("Could not reach GitHub to check for updates. Check your internet connection.",
                                 IsError: true),

            UpdateCheckOutcome.TimedOut =>
                new UpdateReport(
                    $"The update check timed out — GitHub did not answer within {UpdateChecker.TimeoutSeconds} "
                    + "seconds. It may be slow or unreachable right now; try again in a moment.",
                    IsError: true),

            UpdateCheckOutcome.UnreadableRelease => new UpdateReport(UnreadableMessage(result.ReleaseTag),
                                                                     IsError: true),

            // A future outcome must not inherit a sibling's claim, so it says only what is certain.
            _ => new UpdateReport("Could not check for updates — see switcher.log.", IsError: true),
        };
    }

    /// <summary>
    /// When to come back. A reset instant is only quoted if it is still ahead of <paramref name="now"/> —
    /// a stale or clock-skewed header would otherwise tell the user to wait until a time that has passed.
    /// </summary>
    private static string RetrySentence(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is not { } reset || reset <= now) return "Try again in a few minutes.";

        var local = reset.ToLocalTime();
        // ISO date only when the reset lands on another day — within the hour, the clock time is the answer.
        return local.Date == now.ToLocalTime().Date
            ? $"Try again after {local:HH:mm}."
            : $"Try again after {local:yyyy-MM-dd HH:mm}.";
    }

    /// <summary>
    /// Two different unreadable releases. A tag we can quote points at the release itself; no tag means
    /// the body never parsed. Both state that the network is not the culprit, because that is exactly the
    /// wrong conclusion the old single message invited.
    /// </summary>
    private static string UnreadableMessage(string tag) =>
        string.IsNullOrWhiteSpace(tag)
            ? "Could not read the release information GitHub returned, so no version could be compared. "
              + "This is not a network problem — see switcher.log."
            : $"GitHub's latest release is tagged '{tag}', which is not a version this app can compare "
              + "against. This is a problem with the release, not with your connection — see switcher.log.";
}
