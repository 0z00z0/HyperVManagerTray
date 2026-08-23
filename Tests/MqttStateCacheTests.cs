using HyperVManagerTray.Models;
using HyperVManagerTray.Services;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>The picture the MQTT channels publish from. Its writers arrive on three unrelated threads
/// at once — the WMI watcher, the debounce timer and whichever pool thread a power action landed on —
/// so the concurrency is the point, not a caveat.</summary>
public class MqttStateCacheTests
{
    private static VmOperationProgress Progress(string vm, VmOpPhase phase = VmOpPhase.Running,
                                                string? message = null) =>
        new(vm, VmOpKind.Start, phase, null, message);

    // ── What each slot holds ───────────────────────────────────────────────────

    [Fact]
    public void An_unseen_vm_reads_as_nothing_known()
    {
        var cache = new MqttStateCache();

        Assert.Null(cache.Network);
        Assert.Null(cache.Vm("DevBox"));
        Assert.Null(cache.Operation("DevBox"));
    }

    /// <summary>A null name must not throw out of the lookup: the entity payload providers run on the
    /// publish thread, where a throw loses the whole pass.</summary>
    [Fact]
    public void A_null_vm_name_reads_as_nothing_known()
    {
        var cache = new MqttStateCache();

        Assert.Null(cache.Vm(null!));
        Assert.Null(cache.Operation(null!));
    }

    [Fact]
    public void Vm_names_match_case_insensitively()
    {
        var cache = new MqttStateCache();
        cache.SetVms([new VmStatus { Name = "DevBox", State = "Running" }]);

        Assert.Equal("Running", cache.Vm("devbox")?.State);
    }

    /// <summary>A status push is the whole picture, so a VM that has left the host leaves the cache
    /// with it rather than lingering as a stale "Running".</summary>
    [Fact]
    public void A_status_push_replaces_the_whole_set()
    {
        var cache = new MqttStateCache();
        cache.SetVms([new VmStatus { Name = "DevBox" }, new VmStatus { Name = "Lab" }]);
        cache.SetVms([new VmStatus { Name = "Lab" }]);

        Assert.Null(cache.Vm("DevBox"));
        Assert.NotNull(cache.Vm("Lab"));
    }

    [Fact]
    public void A_null_status_push_empties_the_set()
    {
        var cache = new MqttStateCache();
        cache.SetVms([new VmStatus { Name = "DevBox" }]);
        cache.SetVms(null);

        Assert.Null(cache.Vm("DevBox"));
    }

    [Fact]
    public void An_operation_reads_back_as_its_verb_and_phase()
    {
        var cache = new MqttStateCache();
        cache.SetOperation(Progress("DevBox", VmOpPhase.Failed, "not enough memory"));

        Assert.Equal("Start — Failed: not enough memory", cache.Operation("DevBox"));
    }

    [Fact]
    public void An_operation_with_no_message_names_only_the_phase()
    {
        var cache = new MqttStateCache();
        cache.SetOperation(Progress("DevBox", VmOpPhase.Requested));

        Assert.Equal("Start — Requested", cache.Operation("DevBox"));
    }

    // ── Concurrent delivery ────────────────────────────────────────────────────

    /// <summary>Unlike the other two slots, the operation map is a read-modify-write, and a rule's
    /// autostart runs one power action per VM, each raising progress from its own thread. Unlocked, two
    /// landing together copy the same base map and one overwrites the other.</summary>
    [Fact]
    public void Concurrent_operations_all_survive()
    {
        var cache = new MqttStateCache();
        string[] vms = [.. Enumerable.Range(0, 64).Select(i => $"vm{i}")];

        Parallel.ForEach(vms, vm => cache.SetOperation(Progress(vm)));

        Assert.All(vms, vm => Assert.Equal("Start — Running", cache.Operation(vm)));
    }

    /// <summary>Repeated writes to the same VM under contention leave one coherent answer, never a
    /// half-built map or a throw out of the publish thread's read.</summary>
    [Fact]
    public void Concurrent_writers_and_readers_never_fault()
    {
        var cache = new MqttStateCache();
        var faults = new List<Exception>();

        Parallel.For(0, 256, i =>
        {
            try
            {
                cache.SetOperation(Progress(i % 2 == 0 ? "DevBox" : "Lab"));
                cache.SetVms([new VmStatus { Name = "DevBox", State = "Running" }]);
                _ = cache.Operation("DevBox");
                _ = cache.Vm("Lab");
            }
            catch (Exception ex) { lock (faults) faults.Add(ex); }
        });

        Assert.Empty(faults);
        Assert.Equal("Start — Running", cache.Operation("DevBox"));
        Assert.Equal("Start — Running", cache.Operation("Lab"));
    }
}
