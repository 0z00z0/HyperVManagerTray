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
    private static VmStatus Vm(string name, string state) => new() { Name = name, State = state };

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
        Assert.Equal(["Scratch"], decision.VmsToSave);
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
            saveAll: names => { steps.Add("save " + string.Join(",", names)); saved = true; return Task.FromResult<string?>(null); },
            stop:    () => { steps.Add("stop"); return Task.FromResult<string?>(null); });

        Assert.Equal(["save Dev,Scratch", "stop"], steps);
        Assert.Equal(ServiceStopFlow.Outcome.Stopped, result.Outcome);
    }

    [Fact]
    public async Task A_rule_or_mqtt_stop_whose_save_fails_never_stops_the_service()
    {
        bool stopped = false;
        var result = await ServiceStopFlow.RunUnattendedAsync(
            HyperVServiceKind.HostCompute,
            read:    () => Task.FromResult(new ServiceStopFlow.VmRead([Vm("Dev", "Running")], true)),
            saveAll: _ => Task.FromResult<string?>("Dev could not be saved."),
            stop:    () => { stopped = true; return Task.FromResult<string?>(null); });

        Assert.False(stopped);
        Assert.Equal(ServiceStopFlow.Outcome.SaveFailed, result.Outcome);
    }
}
