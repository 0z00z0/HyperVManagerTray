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
/// <para>The shared module reports six outcomes where this app writes eight sentences: its
/// <c>Unreachable</c> covers both a timeout and an unreachable host, and its <c>InvalidResponse</c>
/// covers both an unsuccessful HTTP status and a release whose metadata will not parse. The two
/// splits are recovered from evidence the shared result carries — the exception type for the first,
/// the shape of the detail sentence for the second — and both are pinned by tests driving the real
/// shared release source, so a reword there fails here rather than silently collapsing two
/// sentences into one.</para>
///
/// <para>A detail sentence that matches neither shape degrades to
/// <see cref="UpdateCheckReason.UnreadableRelease"/> with no tag, whose wording is true of every
/// invalid response and — the property that matters — still refuses to blame the network.</para>
/// </summary>
/// <param name="StatusCode">The HTTP status the response carried. Zero unless
/// <see cref="Reason"/> is <see cref="UpdateCheckReason.HttpError"/>.</param>
/// <param name="ReleaseTag">The tag that would not parse. Empty when the detail named none.</param>
internal readonly record struct UpdateCheckDetail(UpdateCheckReason Reason, int StatusCode, string ReleaseTag)
{
    /// <summary>Written by <c>GitHubReleaseSource</c> for an unsuccessful status.</summary>
    private static readonly Regex HttpStatusDetail =
        new(@"^HTTP (?<status>\d{3}) from ", RegexOptions.CultureInvariant);

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
            UpdateCheckOutcome.Unreachable     => Unreachable(result),

            // Every remaining outcome, named and unnamed, is something the app could not read. It must
            // never fall through to a reason that blames the network.
            _ => InvalidResponse(result),
        };
    }

    /// <summary>
    /// The shared source raises a cancellation when its own request budget expires and an HTTP
    /// request failure when nothing answered, so the exception type — a typed value, not prose —
    /// is what separates a slow GitHub from an unreachable one.
    /// </summary>
    private static UpdateCheckDetail Unreachable(UpdateCheckResult result) =>
        result.Error is OperationCanceledException
            ? new(UpdateCheckReason.TimedOut, 0, string.Empty)
            : new(UpdateCheckReason.NetworkUnavailable, 0, string.Empty);

    private static UpdateCheckDetail InvalidResponse(UpdateCheckResult result)
    {
        var status = HttpStatusDetail.Match(result.Detail);
        if (status.Success
            && int.TryParse(status.Groups["status"].ValueSpan, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out var code))
            return new(UpdateCheckReason.HttpError, code, string.Empty);

        var tag = UnparseableTagDetail.Match(result.Detail);
        return new(UpdateCheckReason.UnreadableRelease, 0, tag.Success ? tag.Groups["tag"].Value : string.Empty);
    }
}
