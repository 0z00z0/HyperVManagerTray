using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Pure decisions and copy for the "which VMs does this app manage?" surface (issues #34 / #47) — the
/// set arithmetic behind the tray's Manage VMs list and Settings' add-picker, the switch set the two
/// override surfaces share, and the confirmed/unconfirmed message pairs that keep both surfaces honest.
/// </summary>
public class VmConfigUiTests
{
    // ── UnmanagedVms ────────────────────────────────────────────────────────────

    private const string IdA = "AAAAAAAA-0000-0000-0000-000000000001";
    private const string IdB = "BBBBBBBB-0000-0000-0000-000000000002";
    private const string IdC = "CCCCCCCC-0000-0000-0000-000000000003";

    private static VmTarget Managed(string id, string name) => new() { Id = id, Name = name };

    [Fact]
    public void UnmanagedVms_ReturnsHostVmsNotInConfig()
    {
        var result = VmConfigUi.UnmanagedVms(
            [new HostVm(IdA, "Alpha"), new HostVm(IdB, "Beta"), new HostVm(IdC, "Gamma")], [Managed(IdB, "Beta")]);
        Assert.Equal([IdA, IdC], result.Select(v => v.Id));
    }

    /// <summary>Managed is a question about the VM ID, not the name: a host VM sharing its name with a
    /// managed one is a different VM and is still offered.</summary>
    [Fact]
    public void UnmanagedVms_OffersAVmThatSharesItsNameWithAManagedOne()
    {
        var result = VmConfigUi.UnmanagedVms([new HostVm(IdA, "Dev"), new HostVm(IdB, "Dev")], [Managed(IdA, "Dev")]);
        Assert.Equal([IdB], result.Select(v => v.Id));
    }

    /// <summary>WMI and the config may spell a GUID in different cases; it is still the same VM.</summary>
    [Fact]
    public void UnmanagedVms_ComparesIdsWithoutRegardToCase()
    {
        var result = VmConfigUi.UnmanagedVms([new HostVm(IdA.ToLowerInvariant(), "Dev")], [Managed(IdA, "Dev")]);
        Assert.Empty(result);
    }

    [Fact]
    public void UnmanagedVms_IsOrderedByName_SoTheMenuDoesNotReshuffleBetweenOpens()
    {
        var result = VmConfigUi.UnmanagedVms(
            [new HostVm(IdA, "zeta"), new HostVm(IdB, "Alpha"), new HostVm(IdC, "mid")], []);
        Assert.Equal(["Alpha", "mid", "zeta"], result.Select(v => v.Name));
    }

    [Fact]
    public void UnmanagedVms_NoHostVms_ReturnsEmpty_RatherThanThrowing()
    {
        // The cache is null / the host is unreachable for the whole of the app's first few seconds.
        Assert.Empty(VmConfigUi.UnmanagedVms([], [Managed(IdA, "A")]));
        Assert.Empty(VmConfigUi.UnmanagedVms(null, null));
    }

    // ── OverrideSwitches ────────────────────────────────────────────────────────

    [Fact]
    public void OverrideSwitches_IncludesFallbackAndEveryRuleSwitch_ByIdOnce()
    {
        var fallback = new FallbackAction { SwitchId = "SW-DEFAULT", SwitchName = "Default Switch" };
        NetworkRule[] rules =
        [
            new() { SwitchId = "SW-BRIDGED", SwitchName = "Bridged" },
            new() { SwitchId = "sw-default", SwitchName = "Default Switch" },   // the fallback's, again
            new() { SwitchId = "",           SwitchName = "" },                 // not identified: nothing to offer
        ];

        var result = VmConfigUi.OverrideSwitches(fallback, rules);

        Assert.Equal(["SW-BRIDGED", "SW-DEFAULT"], result.Select(s => s.Id));
    }

    [Fact]
    public void OverrideSwitches_NoRulesAndNoFallback_ReturnsEmpty()
    {
        Assert.Empty(VmConfigUi.OverrideSwitches(null, []));
    }

    // ── The messages ────────────────────────────────────────────────────────────

    [Fact]
    public void RemoveConfirmPrompt_SaysTheVmItselfIsNotDeleted()
    {
        // The single most important sentence in this flow: "remove" beside a VM name reads as "delete the
        // virtual machine". If this reassurance ever goes missing, the confirmation is actively misleading.
        var prompt = VmConfigUi.RemoveConfirmPrompt("DevBox");
        Assert.Contains("DevBox", prompt);
        Assert.Contains("not deleted", prompt);
    }

    [Fact]
    public void RemovedMessage_AndRemoveNotConfirmedMessage_MakeOppositeClaims()
    {
        // The pair exists so the caller can report what it VERIFIED rather than what it attempted (#37).
        // A refactor that collapsed them into one string would silently reintroduce the overclaim.
        var ok  = VmConfigUi.RemovedMessage("DevBox");
        var bad = VmConfigUi.RemoveNotConfirmedMessage("DevBox");

        Assert.Contains("no longer managed", ok);
        Assert.Contains("still managed",     bad);
        Assert.NotEqual(ok, bad);
    }

    [Fact]
    public void AddedMessage_AndAddNotConfirmedMessage_MakeOppositeClaims()
    {
        var ok  = VmConfigUi.AddedMessage("DevBox");
        var bad = VmConfigUi.AddNotConfirmedMessage("DevBox");

        Assert.Contains("now managed",   ok);
        Assert.Contains("could not be",  bad);
        Assert.NotEqual(ok, bad);
    }

    [Theory]
    [InlineData("DevBox")]
    [InlineData("VM with spaces")]
    [InlineData("VM, with a comma")]
    public void EveryMessage_NamesTheVm(string vmName)
    {
        // A balloon that doesn't say which VM it is about is useless when several are managed.
        Assert.Contains(vmName, VmConfigUi.RemoveConfirmPrompt(vmName));
        Assert.Contains(vmName, VmConfigUi.RemovedMessage(vmName));
        Assert.Contains(vmName, VmConfigUi.RemoveNotConfirmedMessage(vmName));
        Assert.Contains(vmName, VmConfigUi.AddedMessage(vmName));
        Assert.Contains(vmName, VmConfigUi.AddNotConfirmedMessage(vmName));
        Assert.Contains(vmName, VmConfigUi.WriteFailedMessage(vmName, "denied"));
    }

    [Fact]
    public void WriteFailedMessage_CarriesTheUnderlyingError()
    {
        var msg = VmConfigUi.WriteFailedMessage("DevBox", "The process cannot access the file");
        Assert.Contains("The process cannot access the file", msg);
    }

    // ── FindManagedVm: the one lookup behind "is this VM ours?" ───────────────────

    private static readonly VmTarget[] ManagedSet =
    [
        new() { Id = IdA, Name = "DevBox", NicName = "Network Adapter" },
        new() { Id = IdB, Name = "DevBox", NicName = "NIC 2" },   // a second VM of the same name
    ];

    /// <summary>By VM ID: two VMs of one name are two answers, and a name found only the first of them.</summary>
    [Fact]
    public void FindManagedVm_FindsTheVmByIdEvenWhenItsNameIsShared()
    {
        Assert.Equal("NIC 2", VmConfigUi.FindManagedVm(ManagedSet, IdB)?.NicName);
        Assert.Equal("Network Adapter", VmConfigUi.FindManagedVm(ManagedSet, IdA.ToLowerInvariant())?.NicName);
    }

    /// <summary>A name is never an identifier, so looking one up finds nothing.</summary>
    [Fact]
    public void FindManagedVm_NeverFindsAVmByItsName() =>
        Assert.Null(VmConfigUi.FindManagedVm(ManagedSet, "DevBox"));

    [Fact]
    public void FindManagedVm_HandlesAnEmptyOrNullManagedSetOrId()
    {
        Assert.Null(VmConfigUi.FindManagedVm([], IdA));
        Assert.Null(VmConfigUi.FindManagedVm(null, IdA));
        Assert.Null(VmConfigUi.FindManagedVm(ManagedSet, null));
    }

    // ── SeedNic: the one answer both add-a-VM surfaces use (issue #41) ────────────────

    /// <summary>
    /// The tray seeded a new managed VM's adapter from the order WMI returned the rows in, and Settings
    /// from a sorted list — two surfaces, two adapters persisted. The pick among several adapters is
    /// arbitrary; what matters is that it is the same pick whatever the order the adapters arrive in.
    /// </summary>
    [Fact]
    public void SeedNic_IsIndependentOfTheOrderTheAdaptersArriveIn()
    {
        HostNic ethernet = new("NIC-E", "Ethernet 2"), standard = new("NIC-S", "Network Adapter");

        Assert.Equal(VmConfigUi.SeedNic([ethernet, standard]), VmConfigUi.SeedNic([standard, ethernet]));
        Assert.Equal("NIC-E", VmConfigUi.SeedNic([standard, ethernet])!.Id);
    }

    /// <summary>A VM with no adapter seeds none, which leaves "its only adapter" to be decided later.</summary>
    [Fact]
    public void SeedNic_NoAdapter_SeedsNone()
    {
        Assert.Null(VmConfigUi.SeedNic(null));
        Assert.Null(VmConfigUi.SeedNic([]));
    }

    // ── The dashboard's empty state (issues #38 / #42) ────────────────────────────────────

    /// <summary>
    /// The card's own comment claimed it deliberately named no menu path — "a signpost that names a menu
    /// path is a signpost that goes stale" — directly above a string reading "Right-click the tray icon
    /// and use Manage VMs to add one". It then went stale exactly as predicted: #47 added a Settings
    /// route the card never mentioned. Names surfaces, never the items inside them (docs/STYLE.md).
    /// </summary>
    [Fact]
    public void NoManagedVmsMessage_NamesBothRoutesAndNoMenuPath()
    {
        var msg = VmConfigUi.NoManagedVmsMessage;

        Assert.Contains("tray", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Settings", msg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Manage VMs", msg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Right-click", msg, StringComparison.OrdinalIgnoreCase);
    }
}
