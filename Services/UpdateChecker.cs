using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace HyperVManagerTray.Services;

/// <summary>
/// Why an update check ended the way it did. Each cause must stay distinguishable all the way to
/// <see cref="Helpers.UpdateStatusUi"/>, which owns the wording: a throttle, a 500, a DNS failure, a
/// timeout and an unparseable tag are five different things, and only one of them is the user's network.
/// </summary>
public enum UpdateCheckOutcome
{
    /// <summary>GitHub's latest release is not newer than the running build.
    ///
    /// <para>Deliberately the default (0) rather than <see cref="UpdateAvailable"/>: a zero-valued
    /// outcome must never announce an update, since <see cref="UpdateChecker.CheckResult.UpdateAvailable"/>
    /// is the single gate the silent startup badge is raised on.</para></summary>
    UpToDate,

    /// <summary>A newer release exists. The only outcome that may raise the tray badge.</summary>
    UpdateAvailable,

    /// <summary>The repository has no published releases (HTTP 404 — tags ≠ releases on GitHub).</summary>
    NoReleases,

    /// <summary>GitHub refused because the anonymous request quota is spent (HTTP 403 with the rate-limit
    /// headers, or HTTP 429). Nothing to do with the user's network.</summary>
    RateLimited,

    /// <summary>GitHub answered, but with an unsuccessful status that is not one of the above — a 5xx, or
    /// a 403 that is a plain refusal rather than a throttle. The status code is reported so it is
    /// diagnosable.</summary>
    HttpError,

    /// <summary>No response arrived at all: DNS failure, no route, refused connection. The only outcome
    /// for which "check your internet connection" is honest.</summary>
    NetworkUnavailable,

    /// <summary>The request did not complete inside the check's time budget. Distinct from
    /// <see cref="NetworkUnavailable"/> — the network may be fine and GitHub merely slow.</summary>
    TimedOut,

    /// <summary>The response was read but could not be understood: an unparseable <c>tag_name</c>, or a
    /// body that is not the JSON we expect. A problem with the release metadata or this app, and
    /// explicitly not a network problem.</summary>
    UnreadableRelease,
}

/// <summary>
/// Checks the GitHub Releases API for a newer version and can download + launch the installer.
/// </summary>
/// <param name="runningVersion">The build to compare GitHub's latest against. Defaults to the running
/// assembly's version; it is a parameter so the up-to-date/update-available decision can be exercised
/// against a known build instead of against whichever assembly happens to be hosting the code.</param>
internal sealed class UpdateChecker(HttpClient http, ILogger<UpdateChecker> logger, Version? runningVersion = null)
{
    private const string ReleasesApiUrl = "https://api.github.com/repos/0z00z0/HyperVManagerTray/releases/latest";

    /// <summary>How long the check may take before it is abandoned. Named because
    /// <see cref="Helpers.UpdateStatusUi"/> tells the user the number.</summary>
    internal const int TimeoutSeconds = 10;

    /// <summary>
    /// The result of one <see cref="CheckAsync"/> call. Constructed only through the factories below, so
    /// every arm has to state its <see cref="Outcome"/> — there is no longer any way to produce a result
    /// whose cause the caller has to infer from an empty string.
    /// </summary>
    public sealed record CheckResult
    {
        /// <summary>Why the check ended as it did.</summary>
        public UpdateCheckOutcome Outcome { get; init; }

        /// <summary>The latest release version, e.g. "2.5.11". Empty unless a tag was read and parsed.</summary>
        public string LatestVersion { get; init; } = string.Empty;

        public string ReleasePageUrl { get; init; } = string.Empty;

        /// <summary>Direct .exe download URL from the release assets; empty if not found.</summary>
        public string InstallerUrl { get; init; } = string.Empty;

        /// <summary>Body text from the GitHub release, stripped of markdown.</summary>
        public string ReleaseNotes { get; init; } = string.Empty;

        /// <summary>The HTTP status GitHub answered with. 0 when no response arrived.</summary>
        public int StatusCode { get; init; }

        /// <summary>When GitHub's quota refills, read off <c>X-RateLimit-Reset</c> or <c>Retry-After</c>.
        /// Null when the response did not say — the message then stays vague rather than invent a time.</summary>
        public DateTimeOffset? RateLimitResetsAt { get; init; }

        /// <summary>The raw <c>tag_name</c> that would not parse — the only thing worth reporting for
        /// <see cref="UpdateCheckOutcome.UnreadableRelease"/>. Empty when the body never got that far.</summary>
        public string ReleaseTag { get; init; } = string.Empty;

        /// <summary>True only for <see cref="UpdateCheckOutcome.UpdateAvailable"/>. Derived rather than
        /// stored so a result can never claim an update its outcome does not support.</summary>
        public bool UpdateAvailable => Outcome == UpdateCheckOutcome.UpdateAvailable;

        public static CheckResult Release(bool newer, string version, string pageUrl,
                                          string installerUrl, string notes) => new()
        {
            Outcome        = newer ? UpdateCheckOutcome.UpdateAvailable : UpdateCheckOutcome.UpToDate,
            LatestVersion  = version,
            ReleasePageUrl = pageUrl,
            InstallerUrl   = installerUrl,
            ReleaseNotes   = notes,
            StatusCode     = (int)HttpStatusCode.OK,
        };

        public static CheckResult NoReleases() => new()
        {
            Outcome    = UpdateCheckOutcome.NoReleases,
            StatusCode = (int)HttpStatusCode.NotFound,
        };

        public static CheckResult RateLimited(int statusCode, DateTimeOffset? resetsAt) => new()
        {
            Outcome           = UpdateCheckOutcome.RateLimited,
            StatusCode        = statusCode,
            RateLimitResetsAt = resetsAt,
        };

        public static CheckResult HttpError(int statusCode) => new()
        {
            Outcome    = UpdateCheckOutcome.HttpError,
            StatusCode = statusCode,
        };

        public static CheckResult NetworkUnavailable() => new() { Outcome = UpdateCheckOutcome.NetworkUnavailable };

        public static CheckResult TimedOut() => new() { Outcome = UpdateCheckOutcome.TimedOut };

        public static CheckResult UnreadableRelease(string tag) => new()
        {
            Outcome    = UpdateCheckOutcome.UnreadableRelease,
            ReleaseTag = tag,
        };
    }

    /// <summary>
    /// Queries the GitHub Releases API. Never throws.
    /// </summary>
    public async Task<CheckResult> CheckAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApiUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("HyperVManagerTray", null));

            using var cts      = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSeconds));
            using var response = await http.SendAsync(request, cts.Token).ConfigureAwait(false);

            // 404 = no releases published yet (tags ≠ releases on GitHub)
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogInformation("Update check: no releases found on GitHub yet");
                return CheckResult.NoReleases();
            }

            // Checked before the generic unsuccessful-status arm: a spent quota is the one HTTP failure
            // the user can neither fix nor should be blamed for, so it must not be reported as a fault.
            if (IsRateLimited(response))
            {
                var resetsAt = RateLimitReset(response);
                logger.LogWarning("Update check: GitHub rate limit reached (HTTP {Status}); resets {Reset}",
                                  (int)response.StatusCode, resetsAt?.ToString("u") ?? "unknown");
                return CheckResult.RateLimited((int)response.StatusCode, resetsAt);
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Update check: GitHub returned HTTP {Status} {Reason}",
                                  (int)response.StatusCode, response.ReasonPhrase);
                return CheckResult.HttpError((int)response.StatusCode);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);

            var root        = doc.RootElement;
            // TryGetProperty, not GetProperty: a release without a tag is metadata we cannot read, which
            // is a reportable outcome below — not an exception for the catch-all arm to guess at.
            var tagName     = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            var releaseUrl  = root.TryGetProperty("html_url",  out var u) ? u.GetString() ?? string.Empty : string.Empty;
            var bodyMd      = root.TryGetProperty("body",      out var b) ? b.GetString() : null;
            var releaseNotes = StripMarkdown(bodyMd);

            // Parse installer URL from assets array — find the first .exe asset
            var installerUrl = string.Empty;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        installerUrl = asset.TryGetProperty("browser_download_url", out var dl)
                            ? dl.GetString() ?? string.Empty : string.Empty;
                        break;
                    }
                }
            }

            // tag_name is e.g. "v2.1.2" — strip leading 'v'
            var latestStr = tagName.TrimStart('v');
            if (!Version.TryParse(latestStr, out var latest))
            {
                logger.LogWarning("Update check: GitHub returned an unparseable tag_name: '{Tag}'", tagName);
                return CheckResult.UnreadableRelease(tagName);
            }

            var running = runningVersion ?? Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            logger.LogInformation("Update check: running={Running} latest={Latest}", running, latest);

            return CheckResult.Release(latest > running, latestStr, releaseUrl, installerUrl, releaseNotes);
        }
        catch (OperationCanceledException ex)
        {
            // HttpClient surfaces a timeout as TaskCanceledException (an OperationCanceledException), so
            // without this arm a slow GitHub lands in the catch-all and reads as a broken connection. No
            // caller token reaches this method, so a cancellation here can only be the budget expiring.
            logger.LogWarning(ex, "Update check timed out after {Seconds} s", TimeoutSeconds);
            return CheckResult.TimedOut();
        }
        catch (HttpRequestException ex)
        {
            // No usable response at all — DNS failure, no route, refused or reset connection.
            logger.LogWarning(ex, "Update check could not reach GitHub");
            return CheckResult.NetworkUnavailable();
        }
        catch (Exception ex)
        {
            // The transport worked and the two network arms above did not fire, so whatever failed here
            // is on our side of the wire — a malformed body, or a bug. Logged at Error because, unlike
            // the arms above, it is not something the outside world is expected to do to us.
            logger.LogError(ex, "Update check failed while reading GitHub's response");
            return CheckResult.UnreadableRelease(string.Empty);
        }
    }

    /// <summary>
    /// True when GitHub refused because the quota is spent, as opposed to refusing outright.
    ///
    /// <para>Status alone cannot decide it: 403 is GitHub's answer for the primary rate limit <i>and</i>
    /// for a plain forbidden, so the headers arbitrate — <c>X-RateLimit-Remaining: 0</c>, or a
    /// <c>Retry-After</c>, which GitHub only sends when it wants us to come back later. 429 is the
    /// secondary/abuse limit and is always a throttle.</para>
    /// </summary>
    private static bool IsRateLimited(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests) return true;
        if (response.StatusCode != HttpStatusCode.Forbidden)       return false;

        // Invariant: an HTTP header is a wire value, never locale-formatted text.
        if (HeaderValue(response, "X-RateLimit-Remaining") is { } remaining
            && int.TryParse(remaining, NumberStyles.Integer, CultureInfo.InvariantCulture, out var left))
            return left <= 0;

        return response.Headers.RetryAfter is not null;
    }

    /// <summary>
    /// When the quota refills. <c>X-RateLimit-Reset</c> (unix seconds) is preferred because it is an
    /// absolute instant; <c>Retry-After</c> is the fallback. Null when the response carried neither.
    /// </summary>
    private static DateTimeOffset? RateLimitReset(HttpResponseMessage response)
    {
        if (HeaderValue(response, "X-RateLimit-Reset") is { } reset
            && long.TryParse(reset, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds))
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);

        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta) return DateTimeOffset.UtcNow.Add(delta);
        if (retryAfter?.Date  is { } date)  return date;

        return null;
    }

    private static string? HeaderValue(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    /// <summary>
    /// Downloads the installer to %TEMP% and returns the local file path.
    /// Reports download progress (0–100) via <paramref name="progress"/>.
    /// Throws on failure — callers should catch.
    /// </summary>
    public async Task<string> DownloadInstallerAsync(string url, IProgress<int>? progress = null,
                                                      CancellationToken ct = default)
    {
        var dest = Path.Combine(Path.GetTempPath(), "HyperVManagerTray-Setup.exe");

        using var request  = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("HyperVManagerTray", null));

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                                       .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total   = response.Content.Headers.ContentLength ?? -1L;
        var buffer  = new byte[81920];
        long downloaded = 0;
        int  lastPct    = -1;

        await using var src  = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst  = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None,
                                              bufferSize: 81920, useAsync: true);
        int read;
        while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            downloaded += read;
            if (progress != null && total > 0)
            {
                var pct = (int)(downloaded * 100 / total);
                if (pct != lastPct) { progress.Report(pct); lastPct = pct; }
            }
        }

        logger.LogInformation("Installer downloaded to {Path} ({Bytes:N0} bytes)", dest, downloaded);
        return dest;
    }

    /// <summary>
    /// Converts a GitHub-flavoured Markdown string to plain text suitable for display in a
    /// Win32 Task Dialog.  Handles the common patterns used in release notes:
    /// ATX headers (## …), bold (**…**), italic (*…*), inline code (`…`), list bullets (- / *).
    /// </summary>
    private static string StripMarkdown(string? md)
    {
        if (string.IsNullOrWhiteSpace(md)) return string.Empty;

        // ATX headers: ## Title → Title
        md = Regex.Replace(md, @"^#{1,6}\s+", string.Empty, RegexOptions.Multiline);
        // Bold / italic: ***text***, **text**, *text* → text
        md = Regex.Replace(md, @"\*{1,3}(.+?)\*{1,3}", "$1");
        // Inline code: `code` → code
        md = Regex.Replace(md, @"`([^`]+)`", "$1");
        // Unordered list: - item or * item → • item
        md = Regex.Replace(md, @"^[ \t]*[-*]\s+", "• ", RegexOptions.Multiline);
        // Collapse 3+ consecutive blank lines to 2
        md = Regex.Replace(md, @"\n{3,}", "\n\n");

        return md.Trim();
    }
}
