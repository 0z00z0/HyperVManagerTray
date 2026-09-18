using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// The guards that refuse stopping a Hyper-V service while any VM on the host might be running (issue
/// #114). A stop that slips past them leaves a VM running with nothing able to manage it.
/// </summary>
public class ServiceStopGuardTests
{
    private static VmStatus Vm(string name, string state) => new() { Id = name, Name = name, State = state };

    [Theory]
    [InlineData(HyperVServiceKind.VirtualMachineManagement, "Running")]
    [InlineData(HyperVServiceKind.VirtualMachineManagement, "Paused")]
    [InlineData(HyperVServiceKind.HostCompute,              "Running")]
    [InlineData(HyperVServiceKind.HostCompute,              "Paused")]
    public void Any_running_or_paused_vm_on_the_host_refuses_the_stop_and_offers_to_save_it(HyperVServiceKind kind, string state)
    {
        // "Scratch" is in no rule and on no dashboard card: it counts all the same.
        var decision = ServiceStopGuard.Evaluate(kind, [Vm("Dev", "Off"), Vm("Scratch", state)], statesKnown: true);

        Assert.Equal(ServiceStopGuard.Verdict.OfferSaveFirst, decision.Verdict);
        Assert.Equal(["Scratch"], decision.VmsToSave.Select(v => v.Id));
    }

    /// <summary>
    /// Hyper-V lets two VMs share a name. Read keyed by name, the host's rows collapsed into one and the
    /// last row read hid the other — here a running VM the guard then never saw, so the service stopped
    /// under it. Keyed by VM ID, both reach the guard and the running one is saved first.
    /// </summary>
    [Fact]
    public void Two_vms_sharing_a_name_are_both_seen_by_the_stop_guard()
    {
        const string running = "11111111-1111-1111-1111-111111111111";
        const string off     = "22222222-2222-2222-2222-222222222222";
        VmRow[] rows =
        [
            new(running, "Dev", 2),   // EnabledState 2: running
            new(off,     "Dev", 3),   // EnabledState 3: off — read last, so a name-keyed read kept only this one
        ];

        var statuses = HostIdentity.IndexVms(rows).Values
            .Select(r =>
            {
                var s = WmiVmMapper.BuildStatus(r.Name, r.EnabledState, 0, 0, 0, 0, "", null);
                s.Id = r.Id;
                return s;
            })
            .ToList();
        var decision = ServiceStopGuard.Evaluate(HyperVServiceKind.VirtualMachineManagement, statuses, statesKnown: true);

        Assert.Equal(ServiceStopGuard.Verdict.OfferSaveFirst, decision.Verdict);
        Assert.Equal([running], decision.VmsToSave.Select(v => v.Id));
    }

    [Theory]
    [InlineData("Starting")]
    [InlineData("Saving")]
    [InlineData("Unknown")]
    public void A_vm_in_transition_or_unreadable_refuses_the_stop(string state)
    {
        var decision = ServiceStopGuard.Evaluate(
            HyperVServiceKind.VirtualMachineManagement, [Vm("Scratch", state)], statesKnown: true);

        Assert.Equal(ServiceStopGuard.Verdict.RefusedBusy, decision.Verdict);
    }

    [Fact]
    public void Unknown_vm_states_refuse_the_stop_even_when_the_list_looks_empty()
    {
        var decision = ServiceStopGuard.Evaluate(HyperVServiceKind.HostCompute, [], statesKnown: false);

        Assert.Equal(ServiceStopGuard.Verdict.RefusedStatesUnknown, decision.Verdict);
    }

    [Fact]
    public async Task Declining_the_save_never_stops_the_service()
    {
        bool stopped = false;
        var result = await ServiceStopFlow.RunAsync(
            HyperVServiceKind.VirtualMachineManagement,
            read:    () => Task.FromResult(new ServiceStopFlow.VmRead([Vm("Dev", "Running")], true)),
            confirm: _ => false,
            saveAll: _ => Task.FromResult<string?>(null),
            stop:    () => { stopped = true; return Task.FromResult<string?>(null); });

        Assert.False(stopped);
        Assert.Equal(ServiceStopFlow.Outcome.Declined, result.Outcome);
    }

    [Fact]
    public async Task A_failed_save_never_stops_the_service()
    {
        bool stopped = false;
        var result = await ServiceStopFlow.RunAsync(
            HyperVServiceKind.VirtualMachineManagement,
            read:    () => Task.FromResult(new ServiceStopFlow.VmRead([Vm("Dev", "Running")], true)),
            confirm: _ => true,
            saveAll: _ => Task.FromResult<string?>("Dev could not be saved."),
            stop:    () => { stopped = true; return Task.FromResult<string?>(null); });

        Assert.False(stopped);
        Assert.Equal(ServiceStopFlow.Outcome.SaveFailed, result.Outcome);
    }

    [Fact]
    public async Task A_vm_running_again_after_the_save_never_stops_the_service()
    {
        // The saves report success, but the fresh read afterwards still shows a VM running — started by
        // something else meanwhile. Only that second read may clear the stop.
        bool stopped = false;
        var result = await ServiceStopFlow.RunAsync(
            HyperVServiceKind.HostCompute,
            read:    () => Task.FromResult(new ServiceStopFlow.VmRead([Vm("Dev", "Running")], true)),
            confirm: _ => true,
            saveAll: _ => Task.FromResult<string?>(null),
            stop:    () => { stopped = true; return Task.FromResult<string?>(null); });

        Assert.False(stopped);
        Assert.Equal(ServiceStopFlow.Outcome.Refused, result.Outcome);
    }

    [Fact]
    public async Task A_rule_or_mqtt_stop_saves_every_running_vm_before_the_service_stops()
    {
        // Nobody is asked on this path, so the save is the only thing between a running VM and a stop.
        var steps  = new List<string>();
        bool saved = false;
        var result = await ServiceStopFlow.RunUnattendedAsync(
            HyperVServiceKind.VirtualMachineManagement,
            read:    () => Task.FromResult(new ServiceStopFlow.VmRead(
                         saved ? [Vm("Dev", "Saved"), Vm("Scratch", "Saved")]
                               : [Vm("Dev", "Running"), Vm("Scratch", "Paused")], true)),
            saveAll: vms => { steps.Add("save " + string.Join(",", vms.Select(v => v.Id))); saved = true; return Task.FromResult<string?>(null); },
            stop:    () => { steps.Add("stop"); return Task.FromResult<string?>(null); });

        Assert.Equal(["save Dev,Scratch", "stop"], steps);
        Assert.Equal(ServiceStopFlow.Outcome.Stopped, result.Outcome);
    }

    [Fact]
    public async Task A_rule_or_mqtt_can_never_stop_the_host_compute_service()
    {
        // Nothing is running, so every other guard would let it through: the refusal has to come from the
        // source alone. A rule that says Stop does not list it either, whatever the file holds.
        bool touched = false;
        var result = await ServiceStopFlow.RunUnattendedAsync(
            HyperVServiceKind.HostCompute,
            read:    () => Task.FromResult(new ServiceStopFlow.VmRead([Vm("Dev", "Off")], true)),
            saveAll: _ => { touched = true; return Task.FromResult<string?>(null); },
            stop:    () => { touched = true; return Task.FromResult<string?>(null); });
        var rule = new NetworkRule
        {
            HostComputeService  = RuleServiceAction.Stop,
            VmManagementService = RuleServiceAction.Stop,
        };

        Assert.False(touched);
        Assert.Equal(ServiceStopFlow.Outcome.Refused, result.Outcome);
        Assert.Equal([HyperVServiceKind.VirtualMachineManagement], rule.ServicesToStop());
    }

    [Fact]
    public async Task A_rule_or_mqtt_stop_whose_save_fails_never_stops_the_service()
    {
        bool stopped = false;
        var result = await ServiceStopFlow.RunUnattendedAsync(
            HyperVServiceKind.VirtualMachineManagement,
            read:    () => Task.FromResult(new ServiceStopFlow.VmRead([Vm("Dev", "Running")], true)),
            saveAll: _ => Task.FromResult<string?>("Dev could not be saved."),
            stop:    () => { stopped = true; return Task.FromResult<string?>(null); });

        Assert.False(stopped);
        Assert.Equal(ServiceStopFlow.Outcome.SaveFailed, result.Outcome);
    }
}
