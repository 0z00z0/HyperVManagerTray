using ZeroZero.Update;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// Pure outcome → UI decisions for the update flow — the update-side counterpart to
/// <see cref="NetworkStatusUi"/> and <see cref="ConfigLoadUi"/>. No HTTP, no WinUI, so every sentence
/// the user can read about an update is assertable here (docs/DISPLAY-VOCABULARY.md, corollary 4).
/// A report states only what was verified (corollary 3): each arm names what actually happened, and only
/// <see cref="UpdateCheckReason.NetworkUnavailable"/> may mention the user's connection.
/// </summary>
internal static class UpdateStatusUi
{
    /// <summary>What to tell the user, and whether it is a failure. Mirrors
    /// <see cref="NetworkStatusUi.RepairReport"/>: the caller picks the channel, never the wording.</summary>
    public readonly record struct UpdateReport(string Message, bool IsError);

    /// <summary>
    /// The message for a completed check. Null for <see cref="UpdateCheckReason.UpdateAvailable"/> —
    /// that outcome is answered by the update dialog, which says considerably more than a sentence
    /// could, and a second message beside it would be the "one action, one report" break (corollary 5).
    /// </summary>
    /// <param name="runningVersion">The build the user is on, for the up-to-date confirmation.</param>
    /// <param name="now">Reference instant for phrasing the rate-limit retry time.</param>
    public static UpdateReport? ReportFor(UpdateCheckResult result, string runningVersion, DateTimeOffset now)
    {
        if (result is null) return null;
        return ReportFor(UpdateCheckDetail.Of(result), result.RateLimitResetsAt, runningVersion, now);
    }

    /// <summary>The same decision over a reading already taken, so the eight-way split and the
    /// wording can each be exercised without the other.</summary>
    public static UpdateReport? ReportFor(UpdateCheckDetail detail, DateTimeOffset? rateLimitResetsAt,
                                          string runningVersion, DateTimeOffset now) =>
        detail.Reason switch
        {
            UpdateCheckReason.UpdateAvailable => null,

            UpdateCheckReason.UpToDate =>
                new UpdateReport($"You're on the latest version ({runningVersion}).", IsError: false),

            // Not a failure: the app works, there is simply nothing published to compare against.
            UpdateCheckReason.NoReleases =>
                new UpdateReport("No releases have been published yet.", IsError: false),

            UpdateCheckReason.RateLimited =>
                new UpdateReport(
                    "GitHub is limiting how many update checks it will answer, so this one could not run. "
                    + "The limit is GitHub's, on requests that aren't signed in — it is not a problem with "
                    + "your connection.\n\n"
                    + RetrySentence(rateLimitResetsAt, now),
                    IsError: true),

            // Names the status code: it is the whole difference between "GitHub is broken" (5xx) and
            // "GitHub is refusing us" (a 403 that isn't a throttle), and the user can quote it.
            UpdateCheckReason.HttpError =>
                new UpdateReport(
                    $"GitHub answered the update check with HTTP {detail.StatusCode}, so no version could be "
                    + "read. Try again later — see switcher.log.",
                    IsError: true),

            // The one arm entitled to blame the network: nothing came back at all.
            UpdateCheckReason.NetworkUnavailable =>
                new UpdateReport("Could not reach GitHub to check for updates. Check your internet connection.",
                                 IsError: true),

            UpdateCheckReason.TimedOut =>
                new UpdateReport(
                    $"The update check timed out — GitHub did not answer within "
                    + $"{AppUpdateOptions.RequestTimeoutSeconds} seconds. It may be slow or unreachable right "
                    + "now; try again in a moment.",
                    IsError: true),

            UpdateCheckReason.UnreadableRelease => new UpdateReport(UnreadableMessage(detail.ReleaseTag),
                                                                    IsError: true),

            // A future reason must not inherit a sibling's claim, so it says only what is certain.
            _ => new UpdateReport("Could not check for updates — see switcher.log.", IsError: true),
        };

    /// <summary>
    /// What is said between the user choosing to install and the installer starting. It promises a
    /// check rather than a launch: the download is verified before it runs, and a refusal means
    /// nothing runs at all — so a sentence guaranteeing the installer will start would be a claim
    /// this flow exists to be able to break.
    /// </summary>
    public static string DownloadingMessage(string version) =>
        // "…", not "..." — the ellipsis character everywhere else in the app (issue #42).
        $"Downloading v{version}…\n\nThe download is checked against the release before it runs. "
        + "If the check fails, nothing is run.";

    /// <summary>
    /// Why a chosen update did not install. Every arm states that nothing has been run, because that
    /// is the fact the user needs and the one a refused download must never leave in doubt.
    /// </summary>
    public static UpdateReport CannotInstallReport(PreparedUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        var message = update.Outcome switch
        {
            PrepareOutcome.Refused =>
                $"The downloaded installer was refused and has not been run: {update.Verification?.Detail ?? update.Detail}.\n\n"
                + "The file has been deleted. " + RefusalAdvice(update.Verification),

            PrepareOutcome.HashNotPublished =>
                "This release publishes no checksum for its installer, so a download could not be checked "
                + "against it. Nothing was downloaded and nothing has been run.",

            PrepareOutcome.HashAmbiguous =>
                "This release publishes more than one checksum, so the installer's cannot be told from the "
                + "rest. Nothing was downloaded and nothing has been run.",

            PrepareOutcome.InstallerAssetMissing =>
                $"This release carries no file named {update.InstallerFileName}, so there is nothing to "
                + "install from. Nothing has been run.",

            PrepareOutcome.DownloadFailed =>
                $"The download did not complete: {update.Detail}.\n\nNothing has been run — try updating "
                + "from the releases page.",

            _ => $"The update was not installed: {update.Detail}.\n\nNothing has been run — see switcher.log.",
        };

        return new UpdateReport(message, IsError: true);
    }

    /// <summary>
    /// Whether the releases page is offered after a failure. It is offered when the release itself is
    /// reachable but this app could not fetch from it, and withheld after a refusal: a file rejected
    /// because it is not the publisher's is not one to send the user to download by hand.
    /// </summary>
    public static bool OffersReleasePageAfter(PrepareOutcome outcome) =>
        outcome is PrepareOutcome.DownloadFailed or PrepareOutcome.InstallerAssetMissing;

    /// <summary>The verified installer existed and would not start. Nothing was replaced.</summary>
    public static UpdateReport LaunchFailedReport(LaunchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new UpdateReport(
            $"The installer could not be started: {result.Detail}.\n\nThis version is untouched — see "
            + "switcher.log.",
            IsError: true);
    }

    /// <summary>
    /// What to do about a refused file. A failed checksum is a bad download and worth retrying; a
    /// failed signature is not, and the advice says so rather than inviting the user to run it anyway.
    /// </summary>
    private static string RefusalAdvice(VerificationResult? verification) => verification?.Verdict switch
    {
        VerificationVerdict.HashMismatch =>
            "The download is not the file this release published. Try again later; if it happens again, "
            + "take the installer from the releases page.",

        VerificationVerdict.NotSigned or VerificationVerdict.SignatureInvalid
            or VerificationVerdict.SignerMismatch or VerificationVerdict.CertificateNotPinned =>
            "The file is not signed by the publisher this version expects. Do not run it by hand.",

        _ => "See switcher.log.",
    };

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
