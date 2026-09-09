using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Guards the three <c>_evalLock.Release()</c> call sites against the disposal race that killed the
/// process on 2026-09-04.
///
/// <para><c>Services\NetworkMonitor.cs</c> composes <c>HyperVManager</c>, <c>VmService</c> and the live
/// <c>NetworkChange</c> events, so it is deliberately not linked into this runtime-free test assembly
/// (see the csproj's link list) and no behavioural test can reach these call sites. The failure is a
/// race between <c>Dispose()</c> and an in-flight pass that is already past its acquire — not
/// reproducible on demand even where the type could be constructed. Removing any one of the three
/// guards restores the crash and leaves every other test in the suite green; this is the same
/// instrument, and the same limits, as <see cref="StartupManagerSourceTests"/> and
/// <see cref="StartupVersionLogSourceTests"/>.</para>
/// </summary>
public class NetworkMonitorSourceTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!;

    private static string Source()
    {
        var path = Path.Combine(RepoRoot(), "Services", "NetworkMonitor.cs");
        Assert.True(File.Exists(path), $"'{path}' not found — fix this test's path, don't skip it.");
        return File.ReadAllText(path);
    }

    /// <summary>Comments stripped: the guard's own explanatory note names the semaphore, and the
    /// surrounding prose discusses <c>_evalLock</c> in several places that are not call sites.</summary>
    private static string Code() => Regex.Replace(Source(), @"//[^\n]*", "");

    /// <summary>The whole release-with-guard shape, as the file writes it.</summary>
    private static readonly Regex GuardedRelease =
        new(@"try\s*\{\s*_evalLock\.Release\(\);\s*\}\s*catch\s*\(\s*ObjectDisposedException\s*\)");

    private static readonly Regex AnyRelease = new(@"_evalLock\.Release\(\)");

    /// <summary>Bounded window from a member's signature to the start of the next member — same approach
    /// as <see cref="StartupManagerSourceTests"/>, since these bodies contain nested blocks and do not
    /// close on a single brace at member indent.</summary>
    private static string Body(string startAnchor, string endAnchor)
    {
        var code = Code();

        int start = code.IndexOf(startAnchor, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{startAnchor}' is gone — this test anchors on it; fix the anchor, don't skip it.");

        int end = code.IndexOf(endAnchor, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not find '{endAnchor}' after '{startAnchor}' — fix this test, don't skip it.");

        return code[start..end];
    }

    /// <summary>THE test for the observed crash: the timer callback is <c>async void</c>, so an
    /// <c>ObjectDisposedException</c> escaping its <c>finally</c> reaches
    /// <c>AppDomain.UnhandledException</c> and takes the process down.</summary>
    [Fact]
    public void OnDebounceElapsed_ReleaseToleratesADisposedSemaphore()
    {
        var body = Body("private async void OnDebounceElapsed", "public async Task<MatchResult?> ForceEvaluateAsync");

        Assert.True(AnyRelease.IsMatch(body), "OnDebounceElapsed no longer releases _evalLock — fix this test's anchor.");
        Assert.True(GuardedRelease.IsMatch(body),
            "The _evalLock release in OnDebounceElapsed is unguarded again. Dispose() can land between the "
          + "acquire and this release; in an async void callback the ObjectDisposedException is fatal — it "
          + "killed the app on 2026-09-04 during a dock adapter rename.");
    }

    /// <summary>The tray's "Re-check network now" awaits this, so the throw would surface as a failed
    /// command rather than a dead process — still wrong: the pass it reports on had already finished.</summary>
    [Fact]
    public void ForceEvaluateAsync_ReleaseToleratesADisposedSemaphore()
    {
        var body = Body("public async Task<MatchResult?> ForceEvaluateAsync", "public async Task RefreshDisplayAsync");

        Assert.True(AnyRelease.IsMatch(body), "ForceEvaluateAsync no longer releases _evalLock — fix this test's anchor.");
        Assert.True(GuardedRelease.IsMatch(body),
            "The _evalLock release in ForceEvaluateAsync is unguarded again — Dispose() can land between the "
          + "acquire and this release.");
    }

    /// <summary>Invoked fire-and-forget by the adapter rename, so an escaping throw here is unobserved —
    /// and the rename is exactly the operation in flight when the observed crash happened.</summary>
    [Fact]
    public void RefreshDisplayAsync_ReleaseToleratesADisposedSemaphore()
    {
        var body = Body("public async Task RefreshDisplayAsync", "public enum OverrideOutcome");

        Assert.True(AnyRelease.IsMatch(body), "RefreshDisplayAsync no longer releases _evalLock — fix this test's anchor.");
        Assert.True(GuardedRelease.IsMatch(body),
            "The _evalLock release in RefreshDisplayAsync is unguarded again — Dispose() can land between the "
          + "acquire and this release.");
    }

    /// <summary>Catches a FOURTH release added later without the guard, which the three anchored tests
    /// above would not see. The waits are all guarded; a release that is not is the asymmetry itself.</summary>
    [Fact]
    public void EveryEvalLockReleaseIsGuarded()
    {
        var code = Code();

        int releases = AnyRelease.Matches(code).Count;
        int guarded  = GuardedRelease.Matches(code).Count;

        Assert.Equal(releases, guarded);
    }
}
