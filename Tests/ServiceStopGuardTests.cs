using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// The guards that refuse stopping a Hyper-V service while a managed VM might be running (issue #114).
/// A stop that slips past them leaves a VM running with nothing able to manage it.
/// </summary>
public class ServiceStopGuardTests
{
    private static readonly string[] Managed = ["Dev", "Build"];

    private static VmStatus Vm(string name, string state) => new() { Name = name, State = state };

    [Theory]
    [InlineData(HyperVServiceKind.VirtualMachineManagement, "Running")]
    [InlineData(HyperVServiceKind.VirtualMachineManagement, "Paused")]
    [InlineData(HyperVServiceKind.HostCompute,              "Running")]
    [InlineData(HyperVServiceKind.HostCompute,              "Paused")]
    public void A_running_or_paused_managed_vm_refuses_the_stop_and_offers_to_save_it(HyperVServiceKind kind, string state)
    {
        var decision = ServiceStopGuard.Evaluate(kind, Managed, [Vm("Dev", state), Vm("Build", "Off")], statesKnown: true);

        Assert.Equal(ServiceStopGuard.Verdict.OfferSaveFirst, decision.Verdict);
        Assert.Equal(["Dev"], decision.VmsToSave);
    }

    [Theory]
    [InlineData("Starting")]
    [InlineData("Saving")]
    [InlineData("Unknown")]
    public void A_managed_vm_in_transition_or_unreadable_refuses_the_stop(string state)
    {
        var decision = ServiceStopGuard.Evaluate(
            HyperVServiceKind.VirtualMachineManagement, Managed, [Vm("Dev", state)], statesKnown: true);

        Assert.Equal(ServiceStopGuard.Verdict.RefusedBusy, decision.Verdict);
    }

    [Fact]
    public void Unknown_vm_states_refuse_the_stop_even_when_the_list_looks_empty()
    {
        var decision = ServiceStopGuard.Evaluate(HyperVServiceKind.HostCompute, Managed, [], statesKnown: false);

        Assert.Equal(ServiceStopGuard.Verdict.RefusedStatesUnknown, decision.Verdict);
    }

    [Fact]
    public async Task Declining_the_save_never_stops_the_service()
    {
        bool stopped = false;
        var result = await ServiceStopFlow.RunAsync(
            HyperVServiceKind.VirtualMachineManagement, Managed,
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
            HyperVServiceKind.VirtualMachineManagement, Managed,
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
            HyperVServiceKind.HostCompute, Managed,
            read:    () => Task.FromResult(new ServiceStopFlow.VmRead([Vm("Dev", "Running")], true)),
            confirm: _ => true,
            saveAll: _ => Task.FromResult<string?>(null),
            stop:    () => { stopped = true; return Task.FromResult<string?>(null); });

        Assert.False(stopped);
        Assert.Equal(ServiceStopFlow.Outcome.Refused, result.Outcome);
    }
}
