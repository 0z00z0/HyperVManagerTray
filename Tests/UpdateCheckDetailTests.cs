using System.Net;
using System.Net.Http;
using System.Text;
using HyperVManagerTray.Helpers;
using Xunit;
using ZeroZero.Primitives;
using ZeroZero.Update;

namespace HyperVManagerTray.Tests;

/// <summary>
/// What a GitHub response actually means — the classification half of the update check.
///
/// <para><b>What these tests exist for.</b> A spent GitHub quota, a 500, a DNS failure, a timeout and
/// an unparseable tag are five different things, and reporting all five as "Check your internet
/// connection" is the failure this split exists to prevent. The shared component names an outcome for
/// each, and two of the five messages additionally quote a value it carries only inside its detail
/// sentence: the status a failed request was answered with, and the tag that would not parse.</para>
///
/// <para><b>Why the real release source.</b> Those two values are read out of wording the shared
/// component writes. Asserting against a copy of that wording would prove only that the copy still
/// matches itself, so every case here drives the actual <see cref="GitHubReleaseSource"/> through a
/// stub transport. A reword on the shared side turns these red instead of silently dropping a status
/// code or a tag from a message the user reads.</para>
///
/// <para>The load-bearing one is <see cref="OnlyANewerReleaseEverReportsAnUpdateAvailable"/>: the
/// silent startup check raises the tray badge on that flag alone, so no failure may set it.</para>
/// </summary>
public class UpdateCheckDetailTests
{
    /// <summary>The build GitHub's tag is compared against, handed in explicitly so these tests do not
    /// depend on whichever assembly happens to be hosting the linked source.</summary>
    private static readonly Version Running = new(2, 5, 11);

    // ── Success: the version comparison ──────────────────────────────────────────

    [Fact]
    public async Task UpdateAvailable_WhenTheTagIsNewerThanTheRunningBuild()
    {
        var result = await Check(() => Json(HttpStatusCode.OK, ReleaseJson("v2.6.0")));
        var detail = UpdateCheckDetail.Of(result);

        Assert.Equal(UpdateCheckReason.UpdateAvailable, detail.Reason);
        Assert.True(detail.UpdateAvailable);
        Assert.Equal("2.6.0", result.Release!.VersionText);
        Assert.NotNull(result.Release.FindAsset(AppUpdateOptions.InstallerFileNameFor("2.6.0")));
    }

    [Theory]
    [InlineData("v2.5.11")]   // exactly the running build
    [InlineData("v2.5.10")]   // older — a downgrade is not an update
    [InlineData("v1.0.0")]
    public async Task UpToDate_WhenTheTagIsNotNewer(string tag)
    {
        var detail = UpdateCheckDetail.Of(await Check(() => Json(HttpStatusCode.OK, ReleaseJson(tag))));

        Assert.Equal(UpdateCheckReason.UpToDate, detail.Reason);
        Assert.False(detail.UpdateAvailable);
    }

    // ── 404: no releases ─────────────────────────────────────────────────────────

    [Fact]
    public async Task NoReleases_On404()
    {
        var detail = UpdateCheckDetail.Of(await Check(() => Json(HttpStatusCode.NotFound, """{"message":"Not Found"}""")));

        Assert.Equal(UpdateCheckReason.NoReleases, detail.Reason);
    }

    // ── 403 / 429: the throttle, which is not a fault of the user's ──────────────

    [Fact]
    public async Task RateLimited_On403WithTheQuotaSpent()
    {
        var result = await Check(() => Json(HttpStatusCode.Forbidden, RateLimitBody(),
                                            ("X-RateLimit-Remaining", "0"),
                                            ("X-RateLimit-Reset", "1750000000")));

        Assert.Equal(UpdateCheckReason.RateLimited, UpdateCheckDetail.Of(result).Reason);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1750000000), result.RateLimitResetsAt);
    }

    [Fact]
    public async Task RateLimited_On429()
    {
        var detail = UpdateCheckDetail.Of(
            await Check(() => Json(HttpStatusCode.TooManyRequests, RateLimitBody())));

        Assert.Equal(UpdateCheckReason.RateLimited, detail.Reason);
    }

    /// <summary>
    /// A 403 carrying neither the spent-quota headers nor a Retry-After is a plain refusal, and must
    /// read as an HTTP failure the user can quote — not as a throttle that will lift on its own.
    /// </summary>
    [Fact]
    public async Task HttpError_On403ThatIsNotAThrottle()
    {
        var detail = UpdateCheckDetail.Of(
            await Check(() => Json(HttpStatusCode.Forbidden, """{"message":"Forbidden"}""")));

        Assert.Equal(UpdateCheckReason.HttpError, detail.Reason);
        Assert.Equal(403, detail.StatusCode);
    }

    // ── The InvalidResponse split: a status this app can quote, or metadata it cannot read ──

    /// <summary>
    /// The status code survives the shared component's single <c>InvalidResponse</c> outcome. It is the
    /// whole difference between "GitHub is broken" and "GitHub is refusing us", and the user quotes it.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, 500)]
    [InlineData(HttpStatusCode.BadGateway, 502)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 503)]
    public async Task HttpError_CarriesTheStatusCode(HttpStatusCode status, int expected)
    {
        var detail = UpdateCheckDetail.Of(await Check(() => Json(status, "server error")));

        Assert.Equal(UpdateCheckReason.HttpError, detail.Reason);
        Assert.Equal(expected, detail.StatusCode);
    }

    /// <summary>The tag survives too, so the message can point at the release rather than the network.</summary>
    [Theory]
    [InlineData("latest")]
    [InlineData("release-2026-09")]
    [InlineData("v2.6.0-beta.1")]
    public async Task UnreadableRelease_CarriesTheTagThatWouldNotParse(string tag)
    {
        var detail = UpdateCheckDetail.Of(await Check(() => Json(HttpStatusCode.OK, ReleaseJson(tag))));

        Assert.Equal(UpdateCheckReason.UnreadableRelease, detail.Reason);
        Assert.Equal(tag, detail.ReleaseTag);
    }

    /// <summary>A body that is not the release JSON names no tag, and must not invent one.</summary>
    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("""{"no_tag_here":true}""")]
    public async Task UnreadableRelease_WithNoTagWhenTheBodyNeverParsed(string body)
    {
        var detail = UpdateCheckDetail.Of(await Check(() => Json(HttpStatusCode.OK, body)));

        Assert.Equal(UpdateCheckReason.UnreadableRelease, detail.Reason);
        Assert.Equal(string.Empty, detail.ReleaseTag);
    }

    // ── The Unreachable split: nothing answered, or nothing answered in time ─────

    /// <summary>No response at all — DNS failure, no route, refused connection. The only reason the
    /// user's connection may be blamed for.</summary>
    [Fact]
    public async Task NetworkUnavailable_WhenNothingAnswers()
    {
        var detail = UpdateCheckDetail.Of(
            await Check(() => throw new HttpRequestException("No such host is known.")));

        Assert.Equal(UpdateCheckReason.NetworkUnavailable, detail.Reason);
    }

    /// <summary>
    /// A request abandoned when its own budget expires. Driven by a real budget against a transport that
    /// never answers, so the cancellation is the component's own — the same value the discrimination
    /// reads, produced the way production produces it.
    /// </summary>
    [Fact]
    public async Task TimedOut_WhenTheBudgetExpiresBeforeAnAnswer()
    {
        using var http = new HttpClient(new HangingHandler());
        var options = ShortTimeoutOptions();
        using var service = new UpdateService(options, new GitHubReleaseSource(http, options));

        var detail = UpdateCheckDetail.Of(await service.CheckAsync());

        Assert.Equal(UpdateCheckReason.TimedOut, detail.Reason);
    }

    /// <summary>
    /// A cancellation the caller asked for is a third thing beside the two above: it ends the check by
    /// throwing and never arrives as an outcome. Were it to arrive as one, shutting the app down during
    /// a check would tell the user GitHub was slow.
    /// </summary>
    [Fact]
    public async Task ACallerCancellationThrowsRatherThanReportingAnOutcome()
    {
        var options = AppUpdateOptions.For(Running, NullLogSink.Instance);
        using var http = new HttpClient(new HangingHandler());
        using var service = new UpdateService(options, new GitHubReleaseSource(http, options));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CheckAsync(cancelled.Token));
    }

    /// <summary>The number the timeout message quotes is the budget the check actually runs under.</summary>
    [Fact]
    public void TheRequestBudgetIsWhatTheMessageQuotes() =>
        Assert.Equal(TimeSpan.FromSeconds(AppUpdateOptions.RequestTimeoutSeconds),
                     AppUpdateOptions.For(Running, NullLogSink.Instance).RequestTimeout);

    // ── The gate the silent badge rides on ──────────────────────────────────────

    /// <summary>
    /// Every failure and every non-newer release must leave the update flag false. That flag is the one
    /// gate the startup badge is raised on, so a failure that set it would announce an update this app
    /// never found.
    /// </summary>
    [Fact]
    public async Task OnlyANewerReleaseEverReportsAnUpdateAvailable()
    {
        var responses = new Func<HttpResponseMessage>[]
        {
            () => Json(HttpStatusCode.OK, ReleaseJson("v2.5.11")),
            () => Json(HttpStatusCode.OK, ReleaseJson("v1.0.0")),
            () => Json(HttpStatusCode.OK, ReleaseJson("latest")),
            () => Json(HttpStatusCode.OK, "not json at all"),
            () => Json(HttpStatusCode.NotFound, """{"message":"Not Found"}"""),
            () => Json(HttpStatusCode.Forbidden, RateLimitBody(), ("X-RateLimit-Remaining", "0")),
            () => Json(HttpStatusCode.TooManyRequests, RateLimitBody()),
            () => Json(HttpStatusCode.InternalServerError, "boom"),
            () => throw new HttpRequestException("No such host is known."),
        };

        foreach (var respond in responses)
        {
            var detail = UpdateCheckDetail.Of(await Check(respond));

            Assert.False(detail.UpdateAvailable);
            Assert.NotEqual(UpdateCheckReason.UpdateAvailable, detail.Reason);
        }
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────────

    private static async Task<UpdateCheckResult> Check(Func<HttpResponseMessage> respond)
    {
        var options = AppUpdateOptions.For(Running, NullLogSink.Instance);
        using var http = new HttpClient(new StubHandler(respond));
        using var service = new UpdateService(options, new GitHubReleaseSource(http, options));
        return await service.CheckAsync();
    }

    /// <summary>The production options with a budget short enough for a test to wait out. Everything
    /// else — the signer, the prefix, the asset name — is the app's own.</summary>
    private static UpdateOptions ShortTimeoutOptions()
    {
        var production = AppUpdateOptions.For(Running, NullLogSink.Instance);
        return new UpdateOptions
        {
            RepositoryOwner   = production.RepositoryOwner,
            RepositoryName    = production.RepositoryName,
            ProductName       = production.ProductName,
            RunningVersion    = production.RunningVersion,
            ExpectedSigner    = production.ExpectedSigner,
            DirectoryPrefix   = production.DirectoryPrefix,
            InstallerFileName = production.InstallerFileName,
            RequestTimeout    = TimeSpan.FromMilliseconds(300),
        };
    }

    private static string ReleaseJson(string tag)
    {
        var version = tag.TrimStart('v');
        return $$"""
            {
              "tag_name": "{{tag}}",
              "html_url": "https://github.com/0z00z0/HyperVManagerTray/releases/tag/{{tag}}",
              "body": "## What's new\n- A thing\n\nSHA-256: 0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
              "assets": [
                { "name": "HyperVManagerTray-Setup-{{version}}.exe",
                  "size": 12345,
                  "browser_download_url": "https://example.invalid/HyperVManagerTray-Setup-{{version}}.exe" }
              ]
            }
            """;
    }

    /// <summary>GitHub's actual rate-limit body, abbreviated — present so a test cannot accidentally pass
    /// by matching on body text the classification is deliberately not allowed to read.</summary>
    private static string RateLimitBody() =>
        """{"message":"API rate limit exceeded for 203.0.113.7.","documentation_url":"https://docs.github.com/rest"}""";

    private static HttpResponseMessage Json(HttpStatusCode status, string body,
                                            params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        foreach (var (name, value) in headers)
            response.Headers.TryAddWithoutValidation(name, value);
        return response;
    }

    /// <summary>
    /// Answers every request from one delegate — the minimal seam for driving the release source's
    /// branches. The alternative is a live call to GitHub, which can produce neither a rate-limit
    /// refusal nor a 500 on request.
    /// </summary>
    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond());
    }

    /// <summary>Never answers, so the request's own budget is what ends it.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }
}
