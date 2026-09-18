using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// The migration of an older settings document's names to identifiers. The one rule that must hold: a
/// name that more than one object on the host carries is kept and reported, never guessed — a guess binds
/// a switch or saves a VM nobody asked for.
/// </summary>
public class ConfigIdentityMigrationTests
{
    [Fact]
    public void A_name_several_objects_carry_is_kept_and_reported_never_guessed()
    {
        var current = new AppConfig
        {
            VirtualMachines = [new VmTarget { Name = "Dev" }],
            Rules =
            [
                new NetworkRule
                {
                    Id = "0123456789abcdef0123456789abcdef", Name = "Office",
                    LegacyVirtualSwitch = "Bridged", LegacyTargetVms = ["Dev"],
                },
            ],
        };
        var host = new HyperVInventory(
            Readable:   true,
            Switches:   [new HostSwitch("11111111-0000-0000-0000-000000000001", "Bridged"),
                         new HostSwitch("11111111-0000-0000-0000-000000000002", "bridged")],
            Vms:        [new HostVm("22222222-0000-0000-0000-000000000001", "Dev"),
                         new HostVm("22222222-0000-0000-0000-000000000002", "Dev")],
            NicsByVmId: new Dictionary<string, IReadOnlyList<HostNic>>(StringComparer.OrdinalIgnoreCase));

        var result = ConfigIdentityMigration.Plan(current, host);

        var vm   = Assert.Single(result.Config.VirtualMachines);
        var rule = Assert.Single(result.Config.Rules);
        Assert.Equal("", vm.Id);                          // no VM picked for the managed entry
        Assert.Equal("", rule.SwitchId);                  // no switch picked for the rule
        Assert.Equal("Bridged", rule.LegacyVirtualSwitch);
        Assert.Empty(rule.TargetVmIds);                   // no VM picked for the rule's target
        Assert.Equal(["Dev"], rule.LegacyTargetVms);
        Assert.False(result.Changed);                     // nothing to write, so nothing is written

        Assert.Equal(3, result.Unresolved.Count);
        Assert.All(result.Unresolved, u => Assert.Equal(2, u.Matches));
    }
}
