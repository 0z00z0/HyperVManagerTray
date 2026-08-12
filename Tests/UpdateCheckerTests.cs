using System.Net;
using System.Net.Http;
using System.Text;
using HyperVManagerTray.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// What a GitHub response actually means — the classification half of the update check.
///
/// <para><b>What these tests exist for.</b> <c>CheckAsync</c> used to signal with two string sentinels:
/// <c>LatestVersion == "none"</c> for a 404, and an empty <c>LatestVersion</c> for <i>everything else
/// that went wrong</i>. A spent GitHub quota, a 500, a DNS failure, a timeout and an unparseable tag
/// were the same value by the time a caller saw them, so all five were reported as "Check your internet
/// connection." Each now has its own <see cref="UpdateCheckOutcome"/>, and these tests pin the
/// distinctions — most of which cannot be produced on demand against the real api.github.com, which is
/// why the responses are canned.</para>
///
/// <para>The load-bearing one is <see cref="OnlyANewerReleaseEverReportsAnUpdateAvailable"/>: the silent
/// startup check raises the tray badge on that flag alone, so no failure may set it.</para>
/// </summary>
public class UpdateCheckerTests
{
    /// <summary>The build the checker compares GitHub's tag against, handed in explicitly so these tests
    /// do not depend on whichever assembly happens to be hosting the linked source.</summary>
    private static readonly Version Running = new(2, 5, 11);

    // ── Success: the version comparison ──────────────────────────────────────────

    [Fact]
    public async Task UpdateAvailable_WhenTheTagIsNewerThanTheRunningBuild()
    {
        var result = await Check(() => Json(HttpStatusCode.OK, ReleaseJson("v2.6.0")));

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
        Assert.True(result.UpdateAvailable);
        Assert.Equal("2.6.0", result.LatestVersion);
        Assert.Equal("https://example.invalid/HyperVManagerTray-Setup.exe", result.InstallerUrl);
        Assert.Contains("A thing", result.ReleaseNotes);
    }

    [Theory]
    [InlineData("v2.5.11")]   // exactly the running build
    [InlineData("v2.5.10")]   // older — a downgrade is not an update
    [InlineData("v1.0.0")]
    public async Task UpToDate_WhenTheTagIsNotNewer(string tag)
    {
        var result = await Check(() => Json(HttpStatusCode.OK, ReleaseJson(tag)));

        Assert.Equal(UpdateCheckOutcome.UpToDate, result.Outcome);
        Assert.False(result.UpdateAvailable);
    }

    // ── 404: no releases ─────────────────────────────────────────────────────────

    [Fact]
    public async Task NoReleases_On404()
    {
        var result = await Check(() => Json(HttpStatusCode.NotFound, """{"message":"Not Found"}"""));

        Assert.Equal(UpdateCheckOutcome.NoReleases, result.Outcome);
        Assert.Equal(404, result.StatusCode);
    }

    // ── 403 / 429: the rate limit, which status alone cannot identify ────────────

    [Fact]
    public async Task RateLimited_On403WithRemainingZero()
    {
        var resetsAt = DateTimeOffset.UtcNow.AddMinutes(37);
        var result   = await Check(() => Json(HttpStatusCode.Forbidden, RateLimitBody(),
            ("X-RateLimit-Limit",     "60"),
            ("X-RateLimit-Remaining", "0"),
            ("X-RateLimit-Reset",     resetsAt.ToUnixTimeSeconds().ToString())));

        Assert.Equal(UpdateCheckOutcome.RateLimited, result.Outcome);
        Assert.Equal(403, result.StatusCode);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(resetsAt.ToUnixTimeSeconds()), result.RateLimitResetsAt);
    }

    /// <summary>
    /// The distinction the whole 403 arm turns on: GitHub answers a plain refusal with the same status as
    /// a spent quota. With budget left, this is a forbidden and must be reported as one — telling the user
    /// to wait for a limit that is not in force would be as wrong as blaming their network.
    /// </summary>
    [Fact]
    public async Task HttpError_On403WithRateLimitBudgetRemaining()
    {
        var result = await Check(() => Json(HttpStatusCode.Forbidden, """{"message":"Forbidden"}""",
            ("X-RateLimit-Limit",     "60"),
            ("X-RateLimit-Remaining", "42")));

        Assert.Equal(UpdateCheckOutcome.HttpError, result.Outcome);
        Assert.Equal(403, result.StatusCode);
        Assert.Null(result.RateLimitResetsAt);
    }

    [Fact]
    public async Task HttpError_On403WithNoRateLimitHeadersAtAll()
    {
        var result = await Check(() => Json(HttpStatusCode.Forbidden, """{"message":"Forbidden"}"""));

        Assert.Equal(UpdateCheckOutcome.HttpError, result.Outcome);
        Assert.Equal(403, result.StatusCode);
    }

    /// <summary>A 403 that carries no remaining-count but does carry Retry-After is GitHub asking us to
    /// come back — a throttle, and the reset time comes off that header.</summary>
    [Fact]
    public async Task RateLimited_On403WithRetryAfterOnly()
    {
        var before = DateTimeOffset.UtcNow;
        var result = await Check(() => Json(HttpStatusCode.Forbidden, RateLimitBody(), ("Retry-After", "120")));

        Assert.Equal(UpdateCheckOutcome.RateLimited, result.Outcome);
        Assert.NotNull(result.RateLimitResetsAt);
        Assert.InRange(result.RateLimitResetsAt!.Value,
                       before.AddSeconds(120), DateTimeOffset.UtcNow.AddSeconds(121));
    }

    /// <summary>429 is GitHub's secondary/abuse limit. Always a throttle — no header has to confirm it.</summary>
    [Fact]
    public async Task RateLimited_On429WithoutAnyRateLimitHeaders()
    {
        var result = await Check(() => Json(HttpStatusCode.TooManyRequests, RateLimitBody()));

        Assert.Equal(UpdateCheckOutcome.RateLimited, result.Outcome);
        Assert.Equal(429, result.StatusCode);
        Assert.Null(result.RateLimitResetsAt);   // nothing said when — the message must not invent one
    }

    // ── Other unsuccessful statuses ──────────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, 500)]
    [InlineData(HttpStatusCode.BadGateway,          502)]
    [InlineData(HttpStatusCode.ServiceUnavailable,  503)]
    [InlineData(HttpStatusCode.Unauthorized,        401)]
    public async Task HttpError_CarriesTheStatusCode(HttpStatusCode status, int expected)
    {
        var result = await Check(() => Json(status, """{"message":"nope"}"""));

        Assert.Equal(UpdateCheckOutcome.HttpError, result.Outcome);
        Assert.Equal(expected, result.StatusCode);
    }

    // ── No response at all, and no response in time ──────────────────────────────

    [Fact]
    public async Task NetworkUnavailable_WhenTheRequestCannotReachGitHub()
    {
        var result = await Check(() => throw new HttpRequestException(
            "No such host is known. (api.github.com:443)"));

        Assert.Equal(UpdateCheckOutcome.NetworkUnavailable, result.Outcome);
        Assert.Equal(0, result.StatusCode);   // nothing answered, so there is no status to report
    }

    /// <summary>
    /// A timeout must not read as an unreachable host. HttpClient surfaces one as
    /// <see cref="TaskCanceledException"/> — an <see cref="OperationCanceledException"/>, not an
    /// <see cref="HttpRequestException"/> — and before this arm existed it fell into the catch-all and
    /// was reported as a broken connection.
    ///
    /// <para>Thrown by the handler rather than waited out: the budget is 10 s, and the guard under test
    /// is which catch arm claims the exception, not the clock that produces it.</para>
    /// </summary>
    [Fact]
    public async Task TimedOut_WhenTheRequestIsCancelled()
    {
        var result = await Check(() => throw new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout of 10 seconds elapsing.",
            new TimeoutException()));

        Assert.Equal(UpdateCheckOutcome.TimedOut, result.Outcome);
    }

    // ── The response arrived but cannot be understood ────────────────────────────

    [Theory]
    [InlineData("latest")]        // a moving tag, not a version
    [InlineData("v2.5.x")]
    [InlineData("release-7")]
    [InlineData("")]
    public async Task UnreadableRelease_WhenTheTagWillNotParse(string tag)
    {
        var result = await Check(() => Json(HttpStatusCode.OK, ReleaseJson(tag)));

        Assert.Equal(UpdateCheckOutcome.UnreadableRelease, result.Outcome);
        Assert.Equal(tag, result.ReleaseTag);        // quoted back to the user, so it must survive
        Assert.Empty(result.LatestVersion);
    }

    [Fact]
    public async Task UnreadableRelease_WhenTagNameIsMissingEntirely()
    {
        var result = await Check(() => Json(HttpStatusCode.OK, """{"html_url":"https://example.invalid"}"""));

        Assert.Equal(UpdateCheckOutcome.UnreadableRelease, result.Outcome);
    }

    [Fact]
    public async Task UnreadableRelease_WhenTheBodyIsNotJson()
    {
        var result = await Check(() => Json(HttpStatusCode.OK, "<html><body>502 Bad Gateway</body></html>"));

        Assert.Equal(UpdateCheckOutcome.UnreadableRelease, result.Outcome);
    }

    // ── The invariant ────────────────────────────────────────────────────────────

    /// <summary>
    /// Every failure and every non-newer release must leave <c>UpdateAvailable</c> false. That flag is the
    /// sole gate on <c>App.CheckForUpdatesOnStartupAsync</c> raising the tray badge, so a failure that set
    /// it would announce an update the app never found — silently, with no window for the user to dismiss.
    /// </summary>
    [Fact]
    public async Task OnlyANewerReleaseEverReportsAnUpdateAvailable()
    {
        Func<HttpResponseMessage>[] nonUpdates =
        [
            () => Json(HttpStatusCode.OK,              ReleaseJson("v2.5.11")),
            () => Json(HttpStatusCode.OK,              ReleaseJson("v1.0.0")),
            () => Json(HttpStatusCode.OK,              ReleaseJson("latest")),
            () => Json(HttpStatusCode.OK,              "not json at all"),
            () => Json(HttpStatusCode.NotFound,        """{"message":"Not Found"}"""),
            () => Json(HttpStatusCode.Forbidden,       RateLimitBody(), ("X-RateLimit-Remaining", "0")),
            () => Json(HttpStatusCode.Forbidden,       """{"message":"Forbidden"}"""),
            () => Json(HttpStatusCode.TooManyRequests, RateLimitBody()),
            () => Json(HttpStatusCode.InternalServerError, "boom"),
            () => throw new HttpRequestException("no route to host"),
            () => throw new TaskCanceledException(),
        ];

        foreach (var respond in nonUpdates)
        {
            var result = await Check(respond);
            Assert.False(result.UpdateAvailable);
            Assert.NotEqual(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
        }
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────────

    private static async Task<UpdateChecker.CheckResult> Check(Func<HttpResponseMessage> respond)
    {
        using var http = new HttpClient(new StubHandler(respond));
        return await new UpdateChecker(http, NullLogger<UpdateChecker>.Instance, Running).CheckAsync();
    }

    private static string ReleaseJson(string tag) => $$"""
        {
          "tag_name": "{{tag}}",
          "html_url": "https://github.com/0z00z0/HyperVManagerTray/releases/tag/{{tag}}",
          "body": "## What's new\n- A thing",
          "assets": [
            { "name": "HyperVManagerTray-Setup.exe",
              "browser_download_url": "https://example.invalid/HyperVManagerTray-Setup.exe" }
          ]
        }
        """;

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
    /// Answers every request from one delegate — the minimal seam for driving <c>CheckAsync</c>'s branches.
    /// There was no HttpClient fake in this project before; the alternative is a live call to GitHub, which
    /// can produce neither a rate-limit refusal nor a 500 on request.
    /// </summary>
    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond());
    }
}
