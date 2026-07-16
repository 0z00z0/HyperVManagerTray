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

    [Fact]
    public void UnmanagedVms_ReturnsHostVmsNotInConfig()
    {
        var result = VmConfigUi.UnmanagedVms(["Alpha", "Beta", "Gamma"], ["Beta"]);
        Assert.Equal(["Alpha", "Gamma"], result);
    }

    [Fact]
    public void UnmanagedVms_IsCaseInsensitive()
    {
        // Hyper-V VM names are case-insensitive; a managed "devbox" must not be offered again as "DevBox".
        var result = VmConfigUi.UnmanagedVms(["DevBox", "Other"], ["devbox"]);
        Assert.Equal(["Other"], result);
    }

    [Fact]
    public void UnmanagedVms_IsOrderedByName_SoTheMenuDoesNotReshuffleBetweenOpens()
    {
        var result = VmConfigUi.UnmanagedVms(["zeta", "Alpha", "mid"], []);
        Assert.Equal(["Alpha", "mid", "zeta"], result);
    }

    [Fact]
    public void UnmanagedVms_DeduplicatesAndTrims()
    {
        var result = VmConfigUi.UnmanagedVms([" Alpha ", "Alpha", "ALPHA"], []);
        Assert.Equal(["Alpha"], result);
    }

    [Fact]
    public void UnmanagedVms_SkipsBlankNames()
    {
        var result = VmConfigUi.UnmanagedVms(["", "   ", "Real"], []);
        Assert.Equal(["Real"], result);
    }

    [Fact]
    public void UnmanagedVms_EverythingManaged_ReturnsEmpty()
    {
        Assert.Empty(VmConfigUi.UnmanagedVms(["A", "B"], ["A", "B"]));
    }

    [Fact]
    public void UnmanagedVms_NoHostVms_ReturnsEmpty_RatherThanThrowing()
    {
        // The cache is null / the host is unreachable for the whole of the app's first few seconds.
        Assert.Empty(VmConfigUi.UnmanagedVms([], ["A"]));
    }

    [Fact]
    public void UnmanagedVms_ManagedVmAbsentFromHost_DoesNotAppear()
    {
        // A config may name a VM that does not exist on this host. It is managed, so it is not "unmanaged"
        // — it simply isn't in the host list at all, and must not be conjured into the add-list.
        var result = VmConfigUi.UnmanagedVms(["OnHost"], ["Ghost"]);
        Assert.Equal(["OnHost"], result);
    }

    // ── OverrideSwitchNames ─────────────────────────────────────────────────────

    [Fact]
    public void OverrideSwitchNames_IncludesFallbackAndEveryRuleSwitch()
    {
        var result = VmConfigUi.OverrideSwitchNames("Default Switch", ["Bridged", "Office"]);
        Assert.Equal(["Bridged", "Default Switch", "Office"], result);
    }

    [Fact]
    public void OverrideSwitchNames_DeduplicatesFallbackAlsoNamedByARule()
    {
        var result = VmConfigUi.OverrideSwitchNames("Bridged", ["Bridged", "Bridged"]);
        Assert.Equal(["Bridged"], result);
    }

    [Fact]
    public void OverrideSwitchNames_SkipsBlanks()
    {
        var result = VmConfigUi.OverrideSwitchNames("  ", ["Bridged", "", "   "]);
        Assert.Equal(["Bridged"], result);
    }

    [Fact]
    public void OverrideSwitchNames_TrimsNames()
    {
        var result = VmConfigUi.OverrideSwitchNames(" Default Switch ", [" Bridged "]);
        Assert.Equal(["Bridged", "Default Switch"], result);
    }

    [Fact]
    public void OverrideSwitchNames_NoRulesAndNoFallback_ReturnsEmpty()
    {
        Assert.Empty(VmConfigUi.OverrideSwitchNames(null, []));
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

    private static readonly VmTarget[] Managed =
    [
        new() { Name = "DevBox",  NicName = "Network Adapter" },   // as the "Add VM" picker stores it
        new() { Name = "vBuild",  NicName = "NIC 2" },
    ];

    [Fact]
    public void FindManagedVm_FindsTheVmAndItsNic()
    {
        var vm = VmConfigUi.FindManagedVm(Managed, "DevBox");

        Assert.NotNull(vm);
        Assert.Equal("Network Adapter", vm.NicName);
    }

    /// <summary>
    /// THE defect. The two surfaces that name a VM disagree on casing BY CONSTRUCTION: issue #47's
    /// "Start managing a VM" prompt takes free text ("devbox"), while issue #41's "Add VM" picker
    /// carries Hyper-V's exact casing ("DevBox"). An ordinal compare never matched them, so a rule
    /// targeting a VM the app's own picker added landed in failedVms → VmConnectFailed → a permanently
    /// red icon and a VM that was never reconnected.
    /// </summary>
    [Theory]
    [InlineData("devbox")]   // typed into the free-text "Start managing a VM" prompt
    [InlineData("DEVBOX")]
    [InlineData("DevBox")]   // exactly as the picker sources it from the host
    public void FindManagedVm_MatchesRegardlessOfCasing(string typed)
    {
        var vm = VmConfigUi.FindManagedVm(Managed, typed);

        Assert.NotNull(vm);
        Assert.Equal("Network Adapter", vm.NicName);   // resolved the NIC, so the VM can be reconnected
    }

    // A genuinely unmanaged VM must still be reported as such — the casing fix must not make the lookup
    // match things it shouldn't.
    [Fact]
    public void FindManagedVm_ReturnsNullForAVmThisAppDoesNotManage() =>
        Assert.Null(VmConfigUi.FindManagedVm(Managed, "SomeoneElsesVm"));

    [Fact]
    public void FindManagedVm_HandlesAnEmptyOrNullManagedSet()
    {
        Assert.Null(VmConfigUi.FindManagedVm([], "DevBox"));
        Assert.Null(VmConfigUi.FindManagedVm(null, "DevBox"));
    }

    /// <summary>
    /// The lookup and the add-picker must agree on identity: a VM the picker considers already managed
    /// (so does NOT offer to add) must be findable by the monitor. If these two drift, one surface says
    /// "already managed" while the other says "not a managed VM" for the same name.
    /// </summary>
    [Fact]
    public void FindManagedVm_AgreesWithUnmanagedVmsAboutIdentity()
    {
        var offered = VmConfigUi.UnmanagedVms(["devbox"], Managed.Select(v => v.Name));

        Assert.Empty(offered);                                        // the picker: already managed
        Assert.NotNull(VmConfigUi.FindManagedVm(Managed, "devbox"));  // the monitor must agree
    }

    // ── SeedNicName: the one answer both add-a-VM surfaces use (issue #41) ────────────────

    /// <summary>
    /// THE property, and the bug that made it necessary. The tray seeded a new managed VM's NIC from
    /// <c>VmService.ReadDiscovered</c>, which assigned into a dictionary per WMI row — so the LAST row
    /// won, in whatever order WMI returned them. Settings seeded from
    /// <c>HostInventory.NicNamesFor(vm).FirstOrDefault()</c>, and that list is sorted. Same act, two
    /// surfaces, two different adapters persisted; whichever one picked the non-primary NIC wrote a name
    /// <c>FindSyntheticNic</c> never matches, so <c>ApplySwitchAsync</c> failed every pass and the VM was
    /// silently never reconnected — the failure #41 exists to fix.
    ///
    /// <para>Order-independence is exactly what the tray's path lacked, so it is what this asserts.</para>
    /// </summary>
    [Fact]
    public void SeedNicName_IsIndependentOfTheOrderTheNamesArriveIn()
    {
        // The order WMI happens to return the rows in ("last row wins" gave "Network Adapter") …
        var asWmiReturnedThem = VmConfigUi.SeedNicName(["Ethernet 2", "Network Adapter"]);
        // … versus the sorted order the inventory hands Settings ("first wins" gave "Ethernet 2").
        var asSettingsSawThem = VmConfigUi.SeedNicName(["Network Adapter", "Ethernet 2"]);

        Assert.Equal(asSettingsSawThem, asWmiReturnedThem);
    }

    /// <summary>Deterministic between reads, matching HostInventory's OrdinalIgnoreCase ordering: the
    /// pick among several adapters is arbitrary, but it must be the SAME arbitrary pick every time.</summary>
    [Fact]
    public void SeedNicName_PicksTheFirstNameInOrdinalIgnoreCaseOrder()
    {
        Assert.Equal("Ethernet 2", VmConfigUi.SeedNicName(["Network Adapter", "Ethernet 2"]));
        Assert.Equal("Ethernet 2", VmConfigUi.SeedNicName(["Ethernet 2"]));
        // Case-insensitive ordering, so a lowercase name still sorts ahead of a later letter rather than
        // ahead of everything (an Ordinal sort would put "ethernet 2" AFTER "Network Adapter").
        Assert.Equal("ethernet 2", VmConfigUi.SeedNicName(["Network Adapter", "ethernet 2"]));
    }

    /// <summary>
    /// A VM the host couldn't be read for (or that doesn't exist yet) seeds the Hyper-V default — the
    /// same value <c>ConfigManager.AddVmToConfig</c> falls back to for a blank, so the two agree by
    /// construction. Never an empty name, which would match no adapter at all.
    /// </summary>
    [Fact]
    public void SeedNicName_FallsBackToTheHyperVDefault()
    {
        Assert.Equal(SettingsOptions.DefaultNicName, VmConfigUi.SeedNicName(null));
        Assert.Equal(SettingsOptions.DefaultNicName, VmConfigUi.SeedNicName([]));
    }

    /// <summary>Blank/whitespace rows are not names — they must not be seeded in place of a real one.</summary>
    [Fact]
    public void SeedNicName_IgnoresBlankNames()
    {
        Assert.Equal("Network Adapter", VmConfigUi.SeedNicName(["", "   ", "Network Adapter"]));
        Assert.Equal(SettingsOptions.DefaultNicName, VmConfigUi.SeedNicName(["", "  "]));
        Assert.Equal("Ethernet 2", VmConfigUi.SeedNicName(["  Ethernet 2  "]));   // trimmed
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
