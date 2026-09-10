using System.Globalization;
using System.Text.RegularExpressions;
using ZeroZero.Update;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// Why an update check ended, at the grain this app's messages are written to. A throttle, a 500, a
/// DNS failure, a timeout and an unparseable tag are five different things, and only one of them is
/// the user's network.
/// </summary>
public enum UpdateCheckReason
{
    /// <summary>The latest release is not newer than the running build.
    ///
    /// <para>Deliberately the default (0) rather than <see cref="UpdateAvailable"/>: a zero-valued
    /// reason must never announce an update, since that is the single gate the silent startup badge
    /// is raised on.</para></summary>
    UpToDate,

    /// <summary>A newer release exists. The only reason that may raise the tray badge.</summary>
    UpdateAvailable,

    /// <summary>The repository has no published releases (tags are not releases on GitHub).</summary>
    NoReleases,

    /// <summary>GitHub refused because the anonymous request quota is spent. Nothing to do with the
    /// user's network.</summary>
    RateLimited,

    /// <summary>GitHub answered with an unsuccessful status. The status code is carried so it is
    /// diagnosable and the user can quote it.</summary>
    HttpError,

    /// <summary>No response arrived at all: DNS failure, no route, refused connection. The only
    /// reason for which "check your internet connection" is honest.</summary>
    NetworkUnavailable,

    /// <summary>The request did not complete inside the check's time budget. Distinct from
    /// <see cref="NetworkUnavailable"/> — the network may be fine and GitHub merely slow.</summary>
    TimedOut,

    /// <summary>The response was read but could not be understood: an unparseable tag, or a body
    /// that is not the release JSON. A problem with the release metadata or this app, and
    /// explicitly not a network problem.</summary>
    UnreadableRelease,
}

/// <summary>
/// The shared check result read at this app's grain.
///
/// <para>The shared module names an outcome for every sentence this app writes, so the reason is a
/// straight mapping. Two of those sentences quote a value the result carries only inside its own
/// detail sentence — the status a failed request was answered with, and the tag that would not
/// parse — and those two readings are what this type is for. Both are pinned by tests driving the
/// real shared release source, so a reword there fails here rather than silently dropping a number
/// or a tag from a message.</para>
///
/// <para>A detail sentence that matches neither shape degrades to
/// <see cref="UpdateCheckReason.UnreadableRelease"/> with no tag, whose wording is true of every
/// answer that yielded no version and — the property that matters — still refuses to blame the
/// network.</para>
/// </summary>
/// <param name="StatusCode">The HTTP status the response carried. Zero unless
/// <see cref="Reason"/> is <see cref="UpdateCheckReason.HttpError"/>.</param>
/// <param name="ReleaseTag">The tag that would not parse. Empty when the detail named none.</param>
internal readonly record struct UpdateCheckDetail(UpdateCheckReason Reason, int StatusCode, string ReleaseTag)
{
    /// <summary>Written by <c>GitHubReleaseSource</c> for a failure status.</summary>
    private static readonly Regex HttpStatusDetail =
        new(@"answered HTTP (?<status>\d{3}) rather than a release$", RegexOptions.CultureInvariant);

    /// <summary>Written by <c>GitHubReleaseSource</c> for a tag that is not a version.</summary>
    private static readonly Regex UnparseableTagDetail =
        new(@"^the release tag '(?<tag>.*)' is not a version$", RegexOptions.CultureInvariant | RegexOptions.Singleline);

    /// <summary>True only for <see cref="UpdateCheckReason.UpdateAvailable"/>. Derived rather than
    /// stored so a reading can never claim an update its reason does not support.</summary>
    public bool UpdateAvailable => Reason == UpdateCheckReason.UpdateAvailable;

    public static UpdateCheckDetail Of(UpdateCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Outcome switch
        {
            UpdateCheckOutcome.UpdateAvailable => new(UpdateCheckReason.UpdateAvailable, 0, string.Empty),
            UpdateCheckOutcome.UpToDate        => new(UpdateCheckReason.UpToDate, 0, string.Empty),
            UpdateCheckOutcome.NoReleases      => new(UpdateCheckReason.NoReleases, 0, string.Empty),
            UpdateCheckOutcome.RateLimited     => new(UpdateCheckReason.RateLimited, 0, string.Empty),
            UpdateCheckOutcome.Unreachable     => new(UpdateCheckReason.NetworkUnavailable, 0, string.Empty),
            UpdateCheckOutcome.TimedOut        => new(UpdateCheckReason.TimedOut, 0, string.Empty),
            UpdateCheckOutcome.RequestFailed   => RequestFailed(result),

            // InvalidResponse, and every outcome a later shared version may add. Neither may fall
            // through to a reason that blames the network.
            _ => UnreadableRelease(result),
        };
    }

    /// <summary>
    /// The status is the whole difference between a service that is broken and one that is refusing
    /// this app, and it is carried only inside the detail sentence. A sentence it cannot be read
    /// from leaves an answer that yielded no version, which is still true and still not the network.
    /// </summary>
    private static UpdateCheckDetail RequestFailed(UpdateCheckResult result)
    {
        var status = HttpStatusDetail.Match(result.Detail);
        return status.Success
            && int.TryParse(status.Groups["status"].ValueSpan, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out var code)
            ? new(UpdateCheckReason.HttpError, code, string.Empty)
            : new(UpdateCheckReason.UnreadableRelease, 0, string.Empty);
    }

    /// <summary>The tag is carried only inside the detail sentence, and a body that never parsed
    /// names none. An unnamed tag must not be invented.</summary>
    private static UpdateCheckDetail UnreadableRelease(UpdateCheckResult result)
    {
        var tag = UnparseableTagDetail.Match(result.Detail);
        return new(UpdateCheckReason.UnreadableRelease, 0, tag.Success ? tag.Groups["tag"].Value : string.Empty);
    }
}
