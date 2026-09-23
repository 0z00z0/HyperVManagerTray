using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Guards the one rule that keeps the link cycle from becoming a hazard: a machine's network is only
/// ever bounced after its switch has actually changed under it.
///
/// <para><c>Services\NetworkMonitor.cs</c> composes <c>HyperVManager</c>, <c>VmService</c> and the live
/// <c>NetworkChange</c> events, so it is deliberately not linked into this runtime-free test assembly
/// (see the csproj's link list) and no behavioural test can reach these call sites. The decision itself
/// — <c>GuestAddressRules.ShouldRenewAfter</c> — is pure and tested directly in
/// <see cref="GuestAddressRulesTests"/>; what cannot be reached that way is whether the call sites
/// actually ask it. Dropping the question at either site makes every evaluation cycle the link of a
/// machine that never moved, which is a real network drop on a running machine and leaves every other
/// test in the suite green. Same instrument, and the same limits, as
/// <see cref="NetworkMonitorSourceTests"/>.</para>
/// </summary>
public class AddressRenewalSourceTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!;

    private static string Source(string folder, string file)
    {
        var path = Path.Combine(RepoRoot(), folder, file);
        Assert.True(File.Exists(path), $"'{path}' not found — fix this test's path, don't skip it.");
        return File.ReadAllText(path);
    }

    /// <summary>Comments stripped: the rule is discussed in prose in several places that are not call
    /// sites.</summary>
    private static string Monitor() => Regex.Replace(Source("Services", "NetworkMonitor.cs"), @"//[^\n]*", "");

    /// <summary>Bounded window from an anchor to the next one — these bodies contain nested blocks and do
    /// not close on a single brace at member indent.</summary>
    private static string Between(string code, string startAnchor, string endAnchor)
    {
        int start = code.IndexOf(startAnchor, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{startAnchor}' is gone — this test anchors on it; fix the anchor, don't skip it.");
        int end = code.IndexOf(endAnchor, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not find '{endAnchor}' after '{startAnchor}' — fix this test, don't skip it.");
        return code[start..end];
    }

    /// <summary>The apply pass moves every VM the rule targets; only the ones that actually moved are
    /// collected for a renewal.</summary>
    [Fact]
    public void ApplyPass_CollectsOnlyMachinesThatActuallyMoved()
    {
        var body = Between(Monitor(), "var moved = await _hyperV.ApplySwitchAsync", "var status = NetworkStatusUi.Classify");
        Assert.Contains("GuestAddressRules.ShouldRenewAfter(moved)", body);
    }

    /// <summary>The manual override asks the same question: a machine the override found already on the
    /// chosen switch has not changed network.</summary>
    [Fact]
    public void ManualOverride_AsksTheSameQuestion()
    {
        var body = Between(Monitor(), "move: async () =>", "_logger.LogInformation(\"Manual override");
        Assert.Contains("GuestAddressRules.ShouldRenewAfter(o)", body);
    }

    /// <summary>The link cycle is reachable from the renewal and from nowhere else. A second call site
    /// would be a network drop nobody gated.</summary>
    [Fact]
    public void TheLinkCycleIsCalledOnlyFromTheRenewal()
    {
        var code = Monitor();
        Assert.Single(Regex.Matches(code, @"CycleNicLinkAsync\s*\("));
        Assert.Contains("CycleNicLinkAsync(", Between(code, "private async Task RunAddressRenewalAsync", "private async Task<bool> WaitForAddressToFitAsync"));
    }

    /// <summary>At most one retry, and never a loop: a guest that will not take a new address is a state
    /// to report, not one to keep bouncing the network over.</summary>
    [Fact]
    public void TheRenewalIsBounded()
    {
        var code = Monitor();
        Assert.Contains("private const int RenewalAttempts = 2;", code);
        Assert.Contains("attempt <= RenewalAttempts", code);
    }
}
