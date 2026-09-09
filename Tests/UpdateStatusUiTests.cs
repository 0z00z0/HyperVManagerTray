using HyperVManagerTray.Helpers;
using Xunit;
using ZeroZero.Update;

namespace HyperVManagerTray.Tests;

/// <summary>
/// What the user is told about an update — the wording half, the counterpart to
/// <see cref="UpdateCheckDetailTests"/>'s classification half.
///
/// <para>The load-bearing test is <see cref="OnlyAnUnreachableGitHubBlamesTheUsersConnection"/>. It is
/// the reported defect in one assertion: a GitHub rate-limit refusal was answered with "Check your
/// internet connection", sending the user to inspect a connection that was working. That sentence
/// belongs to <see cref="UpdateCheckReason.NetworkUnavailable"/> and to nothing else.
/// <see cref="EveryReasonSaysSomethingDifferent"/> enumerates the enum rather than listing cases, so a
/// reason added later cannot quietly reuse a sibling's message.</para>
///
/// <para><see cref="EveryPrepareOutcomeSaysNothingHasBeenRun"/> is the same instrument turned on the
/// half that is new: an update that was fetched and refused must leave the user in no doubt that
/// nothing ran, whichever way it was refused.</para>
/// </summary>
public class UpdateStatusUiTests
{
    private const string Running = "2.5.11";

    /// <summary>Local midday, so "does the reset fall on another day?" has the same answer in every
    /// time zone this ever runs in.</summary>
    private static readonly DateTimeOffset Now = new(new DateTime(2026, 8, 12, 12, 0, 0, DateTimeKind.Local));

    // ── The invariants ───────────────────────────────────────────────────────────

    /// <summary>
    /// The defect. Only the reason that means "nothing came back at all" may point at the user's
    /// network; a throttle, a 500, a timeout and an unreadable tag are all things the user's connection
    /// had nothing to do with.
    /// </summary>
    [Fact]
    public void OnlyAnUnreachableGitHubBlamesTheUsersConnection()
    {
        foreach (var reason in Enum.GetValues<UpdateCheckReason>())
        {
            if (Report(reason) is not { } report) continue;

            // "not a problem with your connection" is the opposite of blame, so the test looks for the
            // phrases that send the user to go and check something: the offending sentence itself, and
            // the obvious rewording of it.
            bool blamesTheNetwork = report.Message.Contains("internet connection", StringComparison.OrdinalIgnoreCase)
                                 || report.Message.Contains("your network", StringComparison.OrdinalIgnoreCase);

            Assert.Equal(reason == UpdateCheckReason.NetworkUnavailable, blamesTheNetwork);
        }
    }

    /// <summary>Every reason gets its own sentence — the whole point of replacing the sentinels. If two
    /// ever collapse into one string, a cause has become undiagnosable again.</summary>
    [Fact]
    public void EveryReasonSaysSomethingDifferent()
    {
        var seen = new Dictionary<string, UpdateCheckReason>();

        foreach (var reason in Enum.GetValues<UpdateCheckReason>())
        {
            var report = Report(reason);

            // The offered update is answered by the update dialog, not by a sentence beside it.
            if (reason == UpdateCheckReason.UpdateAvailable) { Assert.Null(report); continue; }

            Assert.NotNull(report);
            Assert.False(string.IsNullOrWhiteSpace(report!.Value.Message));
            Assert.False(seen.TryGetValue(report.Value.Message, out var clash),
                         $"{reason} reuses {clash}'s message: {report.Value.Message}");
            seen[report.Value.Message] = reason;
        }
    }

    /// <summary>Only the two reasons where nothing went wrong are shown as information; every failure is
    /// told as one (docs/DISPLAY-VOCABULARY.md, corollary 2).</summary>
    [Theory]
    [InlineData(UpdateCheckReason.UpToDate,           false)]
    [InlineData(UpdateCheckReason.NoReleases,         false)]
    [InlineData(UpdateCheckReason.RateLimited,        true)]
    [InlineData(UpdateCheckReason.HttpError,          true)]
    [InlineData(UpdateCheckReason.NetworkUnavailable, true)]
    [InlineData(UpdateCheckReason.TimedOut,           true)]
    [InlineData(UpdateCheckReason.UnreadableRelease,  true)]
    public void IsErrorSeparatesFailuresFromConfirmations(UpdateCheckReason reason, bool expected) =>
        Assert.Equal(expected, Report(reason)!.Value.IsError);

    /// <summary>
    /// The shared result reaches the same wording as the reading taken from it, so the two halves of
    /// this pair cannot drift: a message asserted here through a reason is the message a real check
    /// produces.
    /// </summary>
    [Fact]
    public void TheSharedResultAndTheReadingTakenFromItSayTheSameThing()
    {
        var result = new UpdateCheckResult(UpdateCheckOutcome.RateLimited, new Version(2, 5, 11),
                                           RateLimitResetsAt: Now.AddMinutes(37));

        Assert.Equal(UpdateStatusUi.ReportFor(UpdateCheckDetail.Of(result), result.RateLimitResetsAt, Running, Now),
                     UpdateStatusUi.ReportFor(result, Running, Now));
    }

    // ── The unchanged messages ───────────────────────────────────────────────────

    [Fact]
    public void UpToDate_NamesTheRunningVersion() =>
        Assert.Equal("You're on the latest version (2.5.11).", Message(UpdateCheckReason.UpToDate));

    [Fact]
    public void NoReleases_SaysNothingHasBeenPublished() =>
        Assert.Equal("No releases have been published yet.", Message(UpdateCheckReason.NoReleases));

    // ── Rate limited ─────────────────────────────────────────────────────────────

    [Fact]
    public void RateLimited_SaysTheLimitIsGitHubs()
    {
        var message = RateLimitMessage(null);

        Assert.Contains("GitHub", message);
        Assert.Contains("not a problem with your connection", message);
        Assert.DoesNotContain("internet connection", message);
    }

    [Fact]
    public void RateLimited_QuotesTheResetTimeInLocalTime()
    {
        var resetsAt = Now.AddMinutes(37);

        Assert.Contains($"Try again after {resetsAt.ToLocalTime():HH:mm}.", RateLimitMessage(resetsAt));
    }

    /// <summary>A reset on another day carries the date, in ISO order — "after 12:00" alone would read as
    /// twelve minutes from now.</summary>
    [Fact]
    public void RateLimited_IncludesTheDateWhenTheResetIsNotToday()
    {
        var resetsAt = Now.AddDays(1);

        Assert.Contains($"Try again after {resetsAt.ToLocalTime():yyyy-MM-dd HH:mm}.", RateLimitMessage(resetsAt));
    }

    /// <summary>No reset header, or one already in the past (a stale response, a skewed clock) — the
    /// message must not name a time it cannot stand behind.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(-5)]
    [InlineData(0)]
    public void RateLimited_StaysVagueWhenTheResetIsUnknownOrPast(int? minutesFromNow)
    {
        var message = RateLimitMessage(minutesFromNow is { } m ? Now.AddMinutes(m) : null);

        Assert.Contains("Try again in a few minutes.", message);
        Assert.DoesNotContain("Try again after", message);
    }

    // ── HTTP error ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(403)]
    public void HttpError_ReportsTheStatusCode(int status) =>
        Assert.Contains($"HTTP {status}",
                        UpdateStatusUi.ReportFor(new UpdateCheckDetail(UpdateCheckReason.HttpError, status, ""),
                                                 null, Running, Now)!.Value.Message);

    // ── Timeout ──────────────────────────────────────────────────────────────────

    [Fact]
    public void TimedOut_SaysItTimedOutAndNamesTheBudget()
    {
        var message = Message(UpdateCheckReason.TimedOut);

        Assert.Contains("timed out", message);
        Assert.Contains($"{AppUpdateOptions.RequestTimeoutSeconds} seconds", message);
        Assert.DoesNotContain("internet connection", message);
    }

    // ── Network unavailable ──────────────────────────────────────────────────────

    [Fact]
    public void NetworkUnavailable_IsTheOneMessageThatMentionsTheConnection() =>
        Assert.Equal("Could not reach GitHub to check for updates. Check your internet connection.",
                     Message(UpdateCheckReason.NetworkUnavailable));

    // ── Unreadable release ───────────────────────────────────────────────────────

    [Fact]
    public void UnreadableRelease_QuotesTheTagAndDeniesItIsTheNetwork()
    {
        var message = UpdateStatusUi.ReportFor(
            new UpdateCheckDetail(UpdateCheckReason.UnreadableRelease, 0, "latest"), null, Running, Now)!.Value.Message;

        Assert.Contains("'latest'", message);
        Assert.Contains("not with your connection", message);
    }

    [Fact]
    public void UnreadableRelease_WithoutATagStillSaysItIsNotTheNetwork()
    {
        var message = Message(UpdateCheckReason.UnreadableRelease);

        Assert.Contains("Could not read the release information", message);
        Assert.Contains("not a network problem", message);
        Assert.DoesNotContain("''", message);   // no hole where the tag would have gone
    }

    // ── The download announcement ────────────────────────────────────────────────

    /// <summary>
    /// It may not promise the installer will run. Verification can refuse the download, and a sentence
    /// guaranteeing a launch is a claim the check exists to be able to break.
    /// </summary>
    [Fact]
    public void DownloadingMessage_PromisesACheckRatherThanALaunch()
    {
        var message = UpdateStatusUi.DownloadingMessage("2.6.0");

        Assert.Contains("2.6.0", message);
        Assert.Contains("checked", message);
        Assert.DoesNotContain("automatically", message);
        Assert.DoesNotContain("...", message);   // the ellipsis character, not three full stops (issue #42)
    }

    // ── Refused, or never fetched ────────────────────────────────────────────────

    /// <summary>
    /// Whichever way an update failed to install, the one fact the user needs is that nothing ran. A
    /// message that omits it leaves a refused installer looking like a half-finished upgrade.
    /// </summary>
    [Fact]
    public void EveryPrepareOutcomeSaysNothingHasBeenRun()
    {
        foreach (var outcome in Enum.GetValues<PrepareOutcome>())
        {
            if (outcome == PrepareOutcome.Ready) continue;

            var report = UpdateStatusUi.CannotInstallReport(Prepared(outcome));

            Assert.True(report.IsError);
            Assert.True(report.Message.Contains("has not been run", StringComparison.OrdinalIgnoreCase)
                     || report.Message.Contains("nothing has been run", StringComparison.OrdinalIgnoreCase),
                        $"{outcome} does not say that nothing has been run: {report.Message}");
        }
    }

    /// <summary>A file rejected because it is not the publisher's must not be recommended for a manual
    /// run, and the user must not be sent to the page to fetch it by hand.</summary>
    [Theory]
    [InlineData(VerificationVerdict.NotSigned)]
    [InlineData(VerificationVerdict.SignatureInvalid)]
    [InlineData(VerificationVerdict.SignerMismatch)]
    [InlineData(VerificationVerdict.CertificateNotPinned)]
    public void ARefusedSignatureTellsTheUserNotToRunItByHand(VerificationVerdict verdict)
    {
        var report = UpdateStatusUi.CannotInstallReport(Prepared(PrepareOutcome.Refused, verdict));

        Assert.Contains("Do not run it by hand", report.Message);
        Assert.Contains("deleted", report.Message);
        Assert.False(UpdateStatusUi.OffersReleasePageAfter(PrepareOutcome.Refused));
    }

    /// <summary>A bad download is worth retrying, and is the one refusal where the released file is
    /// still the publisher's — so the releases page is offered.</summary>
    [Fact]
    public void AMismatchedChecksumIsWorthRetrying()
    {
        var report = UpdateStatusUi.CannotInstallReport(
            Prepared(PrepareOutcome.Refused, VerificationVerdict.HashMismatch));

        Assert.Contains("Try again later", report.Message);
        Assert.DoesNotContain("Do not run it by hand", report.Message);
    }

    /// <summary>The releases page is offered only where the released file is still trustworthy.</summary>
    [Theory]
    [InlineData(PrepareOutcome.DownloadFailed,        true)]
    [InlineData(PrepareOutcome.InstallerAssetMissing, true)]
    [InlineData(PrepareOutcome.Refused,               false)]
    [InlineData(PrepareOutcome.HashNotPublished,      false)]
    [InlineData(PrepareOutcome.HashAmbiguous,         false)]
    public void TheReleasesPageIsOfferedOnlyWhereTheReleaseIsStillTrustworthy(PrepareOutcome outcome, bool expected) =>
        Assert.Equal(expected, UpdateStatusUi.OffersReleasePageAfter(outcome));

    /// <summary>An installer that would not start replaced nothing, and the message says so rather than
    /// leaving the user wondering which version they now have.</summary>
    [Fact]
    public void LaunchFailed_SaysThisVersionIsUntouched()
    {
        var report = UpdateStatusUi.LaunchFailedReport(new LaunchResult(false, "the file is in use"));

        Assert.True(report.IsError);
        Assert.Contains("untouched", report.Message);
        Assert.Contains("the file is in use", report.Message);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────────

    private static UpdateStatusUi.UpdateReport? Report(UpdateCheckReason reason) =>
        UpdateStatusUi.ReportFor(DetailFor(reason), RateLimitFor(reason), Running, Now);

    private static string Message(UpdateCheckReason reason) => Report(reason)!.Value.Message;

    private static string RateLimitMessage(DateTimeOffset? resetsAt) =>
        UpdateStatusUi.ReportFor(new UpdateCheckDetail(UpdateCheckReason.RateLimited, 0, ""),
                                 resetsAt, Running, Now)!.Value.Message;

    private static DateTimeOffset? RateLimitFor(UpdateCheckReason reason) =>
        reason == UpdateCheckReason.RateLimited ? Now.AddMinutes(37) : null;

    /// <summary>
    /// A representative reading per reason. Deliberately a total switch that throws: a new
    /// <see cref="UpdateCheckReason"/> fails this helper rather than quietly skipping the enumerating
    /// tests above, which are the only thing stopping a new cause from inheriting an old cause's wording.
    /// </summary>
    private static UpdateCheckDetail DetailFor(UpdateCheckReason reason) => reason switch
    {
        UpdateCheckReason.UpToDate           => new(reason, 0, ""),
        UpdateCheckReason.UpdateAvailable    => new(reason, 0, ""),
        UpdateCheckReason.NoReleases         => new(reason, 0, ""),
        UpdateCheckReason.RateLimited        => new(reason, 0, ""),
        UpdateCheckReason.HttpError          => new(reason, 500, ""),
        UpdateCheckReason.NetworkUnavailable => new(reason, 0, ""),
        UpdateCheckReason.TimedOut           => new(reason, 0, ""),
        UpdateCheckReason.UnreadableRelease  => new(reason, 0, ""),
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason,
                 "New UpdateCheckReason — give it a representative reading so the enumerating tests cover it."),
    };

    private static PreparedUpdate Prepared(PrepareOutcome outcome, VerificationVerdict? verdict = null)
    {
        var release = new ReleaseInfo("v2.6.0", new Version(2, 6, 0, 0), "2.6.0", "2.6.0", "notes",
                                      new Uri("https://example.invalid/releases/tag/v2.6.0"), null, []);
        var verification = verdict is { } value
            ? new VerificationResult(value, $"refused: {value}")
            : null;

        return new PreparedUpdate(outcome, release, "HyperVManagerTray-Setup-2.6.0.exe", null, null,
                                  verification, $"detail for {outcome}");
    }
}
