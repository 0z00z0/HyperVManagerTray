using HyperVManagerTray.Helpers;
using HyperVManagerTray.Services;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// What the user is told about an update check — the wording half, the counterpart to
/// <see cref="UpdateCheckerTests"/>'s classification half.
///
/// <para>The load-bearing test is <see cref="OnlyAnUnreachableGitHubBlamesTheUsersConnection"/>. It is
/// the reported defect in one assertion: a GitHub rate-limit refusal was answered with "Check your
/// internet connection", sending the user to inspect a connection that was working. That sentence now
/// belongs to <see cref="UpdateCheckOutcome.NetworkUnavailable"/> and to nothing else.
/// <see cref="EveryOutcomeSaysSomethingDifferent"/> enumerates the enum rather than listing cases, so an
/// outcome added later cannot quietly reuse a sibling's message.</para>
/// </summary>
public class UpdateStatusUiTests
{
    private const string Running = "2.5.11";

    /// <summary>Local midday, so "does the reset fall on another day?" has the same answer in every
    /// time zone this ever runs in.</summary>
    private static readonly DateTimeOffset Now = new(new DateTime(2026, 8, 12, 12, 0, 0, DateTimeKind.Local));

    // ── The invariants ───────────────────────────────────────────────────────────

    /// <summary>
    /// The defect. Only the outcome that means "nothing came back at all" may point at the user's
    /// network; a throttle, a 500, a timeout and an unreadable tag are all things the user's connection
    /// had nothing to do with.
    /// </summary>
    [Fact]
    public void OnlyAnUnreachableGitHubBlamesTheUsersConnection()
    {
        foreach (var outcome in Enum.GetValues<UpdateCheckOutcome>())
        {
            if (UpdateStatusUi.ReportFor(ResultFor(outcome), Running, Now) is not { } report) continue;

            // "not a problem with your connection" is the opposite of blame, so the test looks for the
            // phrases that send the user to go and check something: the offending sentence itself, and
            // the obvious rewording of it.
            bool blamesTheNetwork = report.Message.Contains("internet connection", StringComparison.OrdinalIgnoreCase)
                                 || report.Message.Contains("your network", StringComparison.OrdinalIgnoreCase);

            Assert.Equal(outcome == UpdateCheckOutcome.NetworkUnavailable, blamesTheNetwork);
        }
    }

    /// <summary>Every outcome gets its own sentence — the whole point of replacing the sentinels. If two
    /// ever collapse into one string, a cause has become undiagnosable again.</summary>
    [Fact]
    public void EveryOutcomeSaysSomethingDifferent()
    {
        var seen = new Dictionary<string, UpdateCheckOutcome>();

        foreach (var outcome in Enum.GetValues<UpdateCheckOutcome>())
        {
            var report = UpdateStatusUi.ReportFor(ResultFor(outcome), Running, Now);

            // The offered update is answered by the update dialog, not by a sentence beside it.
            if (outcome == UpdateCheckOutcome.UpdateAvailable) { Assert.Null(report); continue; }

            Assert.NotNull(report);
            Assert.False(string.IsNullOrWhiteSpace(report!.Value.Message));
            Assert.False(seen.TryGetValue(report.Value.Message, out var clash),
                         $"{outcome} reuses {clash}'s message: {report.Value.Message}");
            seen[report.Value.Message] = outcome;
        }
    }

    /// <summary>Only the two outcomes where nothing went wrong are shown as information; every failure is
    /// told as one (docs/DISPLAY-VOCABULARY.md, corollary 2).</summary>
    [Theory]
    [InlineData(UpdateCheckOutcome.UpToDate,           false)]
    [InlineData(UpdateCheckOutcome.NoReleases,         false)]
    [InlineData(UpdateCheckOutcome.RateLimited,        true)]
    [InlineData(UpdateCheckOutcome.HttpError,          true)]
    [InlineData(UpdateCheckOutcome.NetworkUnavailable, true)]
    [InlineData(UpdateCheckOutcome.TimedOut,           true)]
    [InlineData(UpdateCheckOutcome.UnreadableRelease,  true)]
    public void IsErrorSeparatesFailuresFromConfirmations(UpdateCheckOutcome outcome, bool expected) =>
        Assert.Equal(expected, UpdateStatusUi.ReportFor(ResultFor(outcome), Running, Now)!.Value.IsError);

    // ── The unchanged messages ───────────────────────────────────────────────────

    [Fact]
    public void UpToDate_NamesTheRunningVersion() =>
        Assert.Equal("You're on the latest version (2.5.11).",
                     UpdateStatusUi.ReportFor(ResultFor(UpdateCheckOutcome.UpToDate), Running, Now)!.Value.Message);

    [Fact]
    public void NoReleases_SaysNothingHasBeenPublished() =>
        Assert.Equal("No releases have been published yet.",
                     UpdateStatusUi.ReportFor(ResultFor(UpdateCheckOutcome.NoReleases), Running, Now)!.Value.Message);

    // ── Rate limited ─────────────────────────────────────────────────────────────

    [Fact]
    public void RateLimited_SaysTheLimitIsGitHubs()
    {
        var message = Message(UpdateChecker.CheckResult.RateLimited(403, null));

        Assert.Contains("GitHub", message);
        Assert.Contains("not a problem with your connection", message);
        Assert.DoesNotContain("internet connection", message);
    }

    [Fact]
    public void RateLimited_QuotesTheResetTimeInLocalTime()
    {
        var resetsAt = Now.AddMinutes(37);
        var message  = Message(UpdateChecker.CheckResult.RateLimited(403, resetsAt));

        Assert.Contains($"Try again after {resetsAt.ToLocalTime():HH:mm}.", message);
    }

    /// <summary>A reset on another day carries the date, in ISO order — "after 12:00" alone would read as
    /// twelve minutes from now.</summary>
    [Fact]
    public void RateLimited_IncludesTheDateWhenTheResetIsNotToday()
    {
        var resetsAt = Now.AddDays(1);

        Assert.Contains($"Try again after {resetsAt.ToLocalTime():yyyy-MM-dd HH:mm}.",
                        Message(UpdateChecker.CheckResult.RateLimited(403, resetsAt)));
    }

    /// <summary>No reset header, or one already in the past (a stale response, a skewed clock) — the
    /// message must not name a time it cannot stand behind.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(-5)]
    [InlineData(0)]
    public void RateLimited_StaysVagueWhenTheResetIsUnknownOrPast(int? minutesFromNow)
    {
        var resetsAt = minutesFromNow is { } m ? Now.AddMinutes(m) : (DateTimeOffset?)null;
        var message  = Message(UpdateChecker.CheckResult.RateLimited(429, resetsAt));

        Assert.Contains("Try again in a few minutes.", message);
        Assert.DoesNotContain("Try again after", message);
    }

    // ── HTTP error ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(403)]
    public void HttpError_ReportsTheStatusCode(int status) =>
        Assert.Contains($"HTTP {status}", Message(UpdateChecker.CheckResult.HttpError(status)));

    // ── Timeout ──────────────────────────────────────────────────────────────────

    [Fact]
    public void TimedOut_SaysItTimedOutAndNamesTheBudget()
    {
        var message = Message(UpdateChecker.CheckResult.TimedOut());

        Assert.Contains("timed out", message);
        Assert.Contains($"{UpdateChecker.TimeoutSeconds} seconds", message);
        Assert.DoesNotContain("internet connection", message);
    }

    // ── Network unavailable ──────────────────────────────────────────────────────

    [Fact]
    public void NetworkUnavailable_IsTheOneMessageThatMentionsTheConnection() =>
        Assert.Equal("Could not reach GitHub to check for updates. Check your internet connection.",
                     Message(UpdateChecker.CheckResult.NetworkUnavailable()));

    // ── Unreadable release ───────────────────────────────────────────────────────

    [Fact]
    public void UnreadableRelease_QuotesTheTagAndDeniesItIsTheNetwork()
    {
        var message = Message(UpdateChecker.CheckResult.UnreadableRelease("latest"));

        Assert.Contains("'latest'", message);
        Assert.Contains("not with your connection", message);
    }

    [Fact]
    public void UnreadableRelease_WithoutATagStillSaysItIsNotTheNetwork()
    {
        var message = Message(UpdateChecker.CheckResult.UnreadableRelease(string.Empty));

        Assert.Contains("Could not read the release information", message);
        Assert.Contains("not a network problem", message);
        Assert.DoesNotContain("''", message);   // no hole where the tag would have gone
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────────

    private static string Message(UpdateChecker.CheckResult result) =>
        UpdateStatusUi.ReportFor(result, Running, Now)!.Value.Message;

    /// <summary>
    /// A representative result per outcome. Deliberately a total switch that throws: a new
    /// <see cref="UpdateCheckOutcome"/> fails this helper rather than quietly skipping the enumerating
    /// tests above, which are the only thing stopping a new cause from inheriting an old cause's wording.
    /// </summary>
    private static UpdateChecker.CheckResult ResultFor(UpdateCheckOutcome outcome) => outcome switch
    {
        UpdateCheckOutcome.UpToDate           => UpdateChecker.CheckResult.Release(false, Running, "", "", ""),
        UpdateCheckOutcome.UpdateAvailable    => UpdateChecker.CheckResult.Release(true, "2.6.0", "", "", ""),
        UpdateCheckOutcome.NoReleases         => UpdateChecker.CheckResult.NoReleases(),
        UpdateCheckOutcome.RateLimited        => UpdateChecker.CheckResult.RateLimited(403, Now.AddMinutes(37)),
        UpdateCheckOutcome.HttpError          => UpdateChecker.CheckResult.HttpError(500),
        UpdateCheckOutcome.NetworkUnavailable => UpdateChecker.CheckResult.NetworkUnavailable(),
        UpdateCheckOutcome.TimedOut           => UpdateChecker.CheckResult.TimedOut(),
        UpdateCheckOutcome.UnreadableRelease  => UpdateChecker.CheckResult.UnreadableRelease("latest"),
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome,
                 "New UpdateCheckOutcome — give it a representative result so the enumerating tests cover it."),
    };
}
