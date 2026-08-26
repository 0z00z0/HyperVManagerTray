using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using HyperVManagerTray.Services;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// What the app last observed, held for the publish thread to read (issue #75). The cache is fed only
/// from events the app already raises, so the tests here hand it exactly what those events carry.
/// </summary>
public class MqttStateCacheTests
{
    private static VmStatus Vm(string name, string state = "Running", string? uptime = null) =>
        new() { Name = name, State = state, Uptime = uptime };

    // ── The network slot ────────────────────────────────────────────────────────

    /// <summary>Null before the first evaluation — the entities publish "no reading" rather than a
    /// confident default nothing measured.</summary>
    [Fact]
    public void Network_IsNullBeforeTheFirstEvaluation() => Assert.Null(new MqttStateCache().Network);

    [Fact]
    public void Network_HoldsTheLastAppliedOutcome()
    {
        var cache = new MqttStateCache();
        var result = new MatchResult("Office", "Bridged", ["Dev"])
        {
            ApplyStatus = NetworkStatusUi.SwitchApplyStatus.Applied,
        };

        cache.SetNetwork(result);

        Assert.Same(result, cache.Network);
    }

    [Fact]
    public void Network_ClearsWhenNoRuleMatched()
    {
        var cache = new MqttStateCache();
        cache.SetNetwork(new MatchResult("Office", "Bridged", ["Dev"]));

        cache.SetNetwork(null);

        Assert.Null(cache.Network);
    }

    // ── The VM slot ─────────────────────────────────────────────────────────────

    [Fact]
    public void Vm_IsNullForAVmNoStatusHasBeenSeenFor()
        => Assert.Null(new MqttStateCache().Vm("Dev"));

    /// <summary>Hyper-V VM names are case-insensitive, and the name the entity table holds came from
    /// config.json while the status came from WMI — the two need not agree on casing.</summary>
    [Fact]
    public void Vm_LooksUpCaseInsensitively()
    {
        var cache = new MqttStateCache();
        cache.SetVms([Vm("Dev")]);

        Assert.Equal("Running", cache.Vm("dev")?.State);
        Assert.Equal("Running", cache.Vm("DEV")?.State);
    }

    /// <summary>A read on the publish thread must never throw: it is reading a name from a config the
    /// user may have hand-edited.</summary>
    [Fact]
    public void Vm_ReturnsNullForANullName() => Assert.Null(new MqttStateCache().Vm(null!));

    /// <summary>The statuses event carries the WHOLE list, so a VM missing from it has gone — leaving
    /// its last status standing would publish a running VM that no longer exists.</summary>
    [Fact]
    public void SetVms_ReplacesTheWholeMapRatherThanMergingIntoIt()
    {
        var cache = new MqttStateCache();
        cache.SetVms([Vm("Dev"), Vm("Build")]);

        cache.SetVms([Vm("Dev", "Off")]);

        Assert.Equal("Off", cache.Vm("Dev")?.State);
        Assert.Null(cache.Vm("Build"));
    }

    [Fact]
    public void SetVms_TolerantOfANullListAndOfNamelessEntries()
    {
        var cache = new MqttStateCache();
        cache.SetVms([Vm("Dev"), new VmStatus { Name = "" }, null!]);

        Assert.NotNull(cache.Vm("Dev"));

        cache.SetVms(null);
        Assert.Null(cache.Vm("Dev"));
    }

    // ── The operation slot ──────────────────────────────────────────────────────

    [Fact]
    public void Operation_IsNullBeforeAnyProgressIsReported()
        => Assert.Null(new MqttStateCache().Operation("Dev"));

    /// <summary>
    /// The reason this slot takes a lock while the other two swap a reference. A rule's autostart runs a
    /// power action PER VM, each on its own thread, so two VMs report progress at the same moment; a
    /// writer that built its map from a stale copy would drop one of them — and the dropped VM's "last
    /// operation" would then stay whatever it was before, silently.
    /// </summary>
    [Fact]
    public void SetOperation_KeepsEveryVmProgressingAtOnce()
    {
        var cache = new MqttStateCache();
        string[] names = [.. Enumerable.Range(0, 24).Select(i => $"vm{i}")];

        Parallel.ForEach(names, name => cache.SetOperation(
            new VmOperationProgress(name, VmOpKind.Start, VmOpPhase.Running, 50, "Starting…")));

        Assert.All(names, name => Assert.NotNull(cache.Operation(name)));
    }

    [Fact]
    public void SetOperation_ReplacesTheLastMessageForThatVmOnly()
    {
        var cache = new MqttStateCache();
        cache.SetOperation(new VmOperationProgress("Dev", VmOpKind.Start, VmOpPhase.Requested, null, null));
        cache.SetOperation(new VmOperationProgress("Build", VmOpKind.Save, VmOpPhase.Running, 10, null));
        cache.SetOperation(new VmOperationProgress("Dev", VmOpKind.Start, VmOpPhase.Succeeded, null, null));

        Assert.Equal("Start Succeeded", cache.Operation("Dev"));
        Assert.Equal("Save Running",    cache.Operation("Build"));
    }

    [Fact]
    public void SetOperation_IgnoresProgressThatNamesNoVm()
    {
        var cache = new MqttStateCache();
        cache.SetOperation(new VmOperationProgress("", VmOpKind.Start, VmOpPhase.Running, null, "x"));

        Assert.Null(cache.Operation(""));
    }

    [Fact]
    public void Operation_LooksUpCaseInsensitively()
    {
        var cache = new MqttStateCache();
        cache.SetOperation(new VmOperationProgress("Dev", VmOpKind.Pause, VmOpPhase.Failed, null, "no memory"));

        Assert.Equal("Pause Failed: no memory", cache.Operation("dev"));
    }

    // ── The one line an operation reads as ──────────────────────────────────────

    /// <summary>The verb and its phase always; the WMI job's own text only when it said something.
    /// A trailing ": " with nothing after it is what a blank message would otherwise publish.</summary>
    [Theory]
    [InlineData("Saving (47%)…", "Save Running: Saving (47%)…")]
    [InlineData("",              "Save Running")]
    [InlineData("   ",           "Save Running")]
    [InlineData(null,            "Save Running")]
    public void Describe_JoinsTheVerbPhaseAndWhateverTheJobSaid(string? message, string expected)
        => Assert.Equal(expected, MqttStateCache.Describe(
            new VmOperationProgress("Dev", VmOpKind.Save, VmOpPhase.Running, 47, message)));
}
