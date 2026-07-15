using HyperVManagerTray.Services;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Unit tests for <see cref="SwitchWmiHelpers"/> — the pure port-classification logic extracted from the
/// WMI switch-binding path so it can be verified without a live Hyper-V host. The rules must stay in
/// lock-step with the Microsoft "Hyper-V networking" sample's <c>DeterminePortType</c>: HostResource
/// class decides internal vs external; Parent class identifies a VM NIC connection.
/// </summary>
public class SwitchWmiHelpersTests
{
    [Fact]
    public void Classify_HostComputerSystem_IsInternal()
    {
        Assert.Equal(SwitchWmiHelpers.PortKind.Internal,
            SwitchWmiHelpers.Classify("Msvm_ComputerSystem", null));
    }

    [Fact]
    public void Classify_ExternalEthernetPort_IsExternal()
    {
        Assert.Equal(SwitchWmiHelpers.PortKind.External,
            SwitchWmiHelpers.Classify("Msvm_ExternalEthernetPort", null));
    }

    [Fact]
    public void Classify_WiFiPort_IsExternal()
    {
        Assert.Equal(SwitchWmiHelpers.PortKind.External,
            SwitchWmiHelpers.Classify("Msvm_WiFiPort", null));
    }

    [Theory]
    [InlineData("Msvm_SyntheticEthernetPortSettingData")]
    [InlineData("Msvm_EmulatedEthernetPortSettingData")]
    public void Classify_VmNicParent_IsVirtualMachine(string parentClass)
    {
        // A VM connection has no host/external HostResource endpoint — it's identified by its Parent.
        Assert.Equal(SwitchWmiHelpers.PortKind.VirtualMachine,
            SwitchWmiHelpers.Classify(null, parentClass));
    }

    [Fact]
    public void Classify_HostResource_TakesPrecedenceOverParent()
    {
        // A switch's internal/external ports also carry a Parent, but the HostResource endpoint is the
        // authoritative signal and must win.
        Assert.Equal(SwitchWmiHelpers.PortKind.Internal,
            SwitchWmiHelpers.Classify("Msvm_ComputerSystem", "Msvm_SyntheticEthernetPortSettingData"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("Msvm_SomethingElse", "Msvm_Unrelated")]
    public void Classify_NothingRecognised_IsNone(string? host, string? parent)
    {
        Assert.Equal(SwitchWmiHelpers.PortKind.None, SwitchWmiHelpers.Classify(host, parent));
    }

    [Fact]
    public void Classify_IsCaseInsensitive()
    {
        Assert.Equal(SwitchWmiHelpers.PortKind.External,
            SwitchWmiHelpers.Classify("msvm_externalethernetport", null));
        Assert.Equal(SwitchWmiHelpers.PortKind.Internal,
            SwitchWmiHelpers.Classify("MSVM_COMPUTERSYSTEM", null));
    }

    [Theory]
    [InlineData("Msvm_ExternalEthernetPort", true)]
    [InlineData("Msvm_WiFiPort", true)]
    [InlineData("Msvm_ComputerSystem", false)]
    [InlineData(null, false)]
    public void IsExternalPortClass_Works(string? className, bool expected)
    {
        Assert.Equal(expected, SwitchWmiHelpers.IsExternalPortClass(className));
    }

    [Theory]
    [InlineData("Msvm_ComputerSystem", true)]
    [InlineData("Msvm_ExternalEthernetPort", false)]
    [InlineData(null, false)]
    public void IsInternalPortClass_Works(string? className, bool expected)
    {
        Assert.Equal(expected, SwitchWmiHelpers.IsInternalPortClass(className));
    }

    [Fact]
    public void Classify_EmulatedVmNicWithNoHostResource_IsVirtualMachine()
    {
        // A legacy (emulated) VM NIC still classifies as a VM connection from its Parent alone.
        Assert.Equal(SwitchWmiHelpers.PortKind.VirtualMachine,
            SwitchWmiHelpers.Classify(hostResourceClassName: null, parentClassName: "Msvm_EmulatedEthernetPortSettingData"));
    }

    [Fact]
    public void Classify_ExternalHostResource_WinsOverVmParent()
    {
        // Defensive: an external endpoint must never be misread as a VM connection even if a Parent is set.
        Assert.Equal(SwitchWmiHelpers.PortKind.External,
            SwitchWmiHelpers.Classify("Msvm_WiFiPort", "Msvm_SyntheticEthernetPortSettingData"));
    }

    // ── ConnectionTargetsSwitch (ApplySwitch SKIP guard) ─────────────────────────

    [Fact]
    public void ConnectionTargetsSwitch_HostResourceContainsSwitchId_IsTrue()
    {
        const string id = "C4E8B2A1-1234-4567-89AB-000000000001";
        var hr = new[] { $@"\\HOST\root\virtualization\v2:Msvm_VirtualEthernetSwitch.Name=""{id}""" };
        Assert.True(SwitchWmiHelpers.ConnectionTargetsSwitch(hr, id));
    }

    [Fact]
    public void ConnectionTargetsSwitch_IsCaseInsensitive()
    {
        var hr = new[] { @"...Msvm_VirtualEthernetSwitch.Name=""c4e8b2a1-aaaa""" };
        Assert.True(SwitchWmiHelpers.ConnectionTargetsSwitch(hr, "C4E8B2A1-AAAA"));
    }

    [Fact]
    public void ConnectionTargetsSwitch_DifferentSwitchId_IsFalse()
    {
        var hr = new[] { @"...Name=""11111111-1111""" };
        Assert.False(SwitchWmiHelpers.ConnectionTargetsSwitch(hr, "22222222-2222"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ConnectionTargetsSwitch_EmptySwitchId_IsFalse(string? switchId)
    {
        // Fail-open: an unknown switch id must never claim "already connected" (caller then connects).
        var hr = new[] { @"...Name=""anything""" };
        Assert.False(SwitchWmiHelpers.ConnectionTargetsSwitch(hr, switchId));
    }

    [Fact]
    public void ConnectionTargetsSwitch_NullOrEmptyHostResource_IsFalse()
    {
        Assert.False(SwitchWmiHelpers.ConnectionTargetsSwitch(null, "id"));
        Assert.False(SwitchWmiHelpers.ConnectionTargetsSwitch(System.Array.Empty<string>(), "id"));
        Assert.False(SwitchWmiHelpers.ConnectionTargetsSwitch(new string?[] { null }, "id"));
        Assert.False(SwitchWmiHelpers.ConnectionTargetsSwitch(new string?[] { "" }, "id"));
    }

    // ── NicConnectionMatches (FindNicConnection correlation, issue #17) ──────────

    // A realistic synthetic-NIC (SEPSD) InstanceID: Microsoft:<vmguid>\<portguid>.
    private const string VmGuid   = "4021A7DA-1234-4567-89AB-CDEF01234567";
    private const string PortGuid = "B0C4E8B2-AAAA-1111-2222-333344445555";
    private const string NicInstanceId = @"Microsoft:4021A7DA-1234-4567-89AB-CDEF01234567\B0C4E8B2-AAAA-1111-2222-333344445555";

    [Fact]
    public void NicConnectionMatches_EscapedParentPath_Matches()
    {
        // The Parent REF path doubles the embedded backslash — the exact shape a raw Contains missed.
        var parent = $@"Msvm_SyntheticEthernetPortSettingData.InstanceID=""Microsoft:{VmGuid}\\{PortGuid}""";
        Assert.True(SwitchWmiHelpers.NicConnectionMatches(epasdInstanceId: null, parent, NicInstanceId));
    }

    [Fact]
    public void NicConnectionMatches_NamespacePrefixedQuotedParent_Matches()
    {
        // Full remote-path rendering: host + namespace prefix (its own backslashes) plus the doubled
        // separator inside the quoted InstanceID.
        var parent = $@"\\HOST\root\virtualization\v2:Msvm_SyntheticEthernetPortSettingData.InstanceID=""Microsoft:{VmGuid}\\{PortGuid}""";
        Assert.True(SwitchWmiHelpers.NicConnectionMatches(null, parent, NicInstanceId));
    }

    [Fact]
    public void NicConnectionMatches_EpasdInstanceIdPrefix_Matches()
    {
        // The EPASD's own InstanceID is the SEPSD InstanceID plus a trailing connection segment; used as
        // the fallback signal when Parent is unavailable.
        var epasdId = NicInstanceId + @"\C0F45C4E-9999-8888-7777-666655554444";
        Assert.True(SwitchWmiHelpers.NicConnectionMatches(epasdId, epasdParent: null, NicInstanceId));
    }

    [Fact]
    public void NicConnectionMatches_IsCaseInsensitive()
    {
        var parent = $@"Msvm_SyntheticEthernetPortSettingData.InstanceID=""microsoft:{VmGuid.ToLowerInvariant()}\\{PortGuid.ToLowerInvariant()}""";
        Assert.True(SwitchWmiHelpers.NicConnectionMatches(null, parent, NicInstanceId));
    }

    [Fact]
    public void NicConnectionMatches_DifferentPortOnSameVm_DoesNotMatch()
    {
        // Same VM GUID, different synthetic port GUID — must NOT be treated as this NIC's connection.
        const string otherPort = "99999999-0000-0000-0000-999999999999";
        var epasdId = $@"Microsoft:{VmGuid}\{otherPort}\C0F45C4E-9999-8888-7777-666655554444";
        var parent  = $@"Msvm_SyntheticEthernetPortSettingData.InstanceID=""Microsoft:{VmGuid}\\{otherPort}""";
        Assert.False(SwitchWmiHelpers.NicConnectionMatches(epasdId, parent, NicInstanceId));
    }

    [Fact]
    public void NicConnectionMatches_DifferentVm_DoesNotMatch()
    {
        const string otherVm = "00000000-DEAD-BEEF-0000-000000000000";
        var parent = $@"Msvm_SyntheticEthernetPortSettingData.InstanceID=""Microsoft:{otherVm}\\{PortGuid}""";
        Assert.False(SwitchWmiHelpers.NicConnectionMatches(null, parent, NicInstanceId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NicConnectionMatches_EmptyNicInstanceId_IsFalse(string? nicId)
    {
        var parent = $@"...InstanceID=""Microsoft:{VmGuid}\\{PortGuid}""";
        Assert.False(SwitchWmiHelpers.NicConnectionMatches("anything", parent, nicId));
    }

    [Fact]
    public void NicConnectionMatches_BothCandidatesNullOrEmpty_IsFalse()
    {
        Assert.False(SwitchWmiHelpers.NicConnectionMatches(null, null, NicInstanceId));
        Assert.False(SwitchWmiHelpers.NicConnectionMatches("", "", NicInstanceId));
    }

    [Fact]
    public void NicConnectionMatches_UnrelatedEpasd_DoesNotMatch()
    {
        // A host/external port allocation (parented on the switch, not this SEPSD) must not correlate.
        var parent  = @"Msvm_VirtualEthernetSwitchSettingData.InstanceID=""Microsoft:VirtualSystem\11112222-3333""";
        var epasdId = @"Microsoft:11112222-3333-4444-5555-666677778888\AAAA";
        Assert.False(SwitchWmiHelpers.NicConnectionMatches(epasdId, parent, NicInstanceId));
    }

    [Fact]
    public void CollapseBackslashes_RemovesAllEscapingLevels()
    {
        Assert.Equal("Microsoft:ABCDEF", SwitchWmiHelpers.CollapseBackslashes(@"Microsoft:ABC\DEF"));
        Assert.Equal("Microsoft:ABCDEF", SwitchWmiHelpers.CollapseBackslashes(@"Microsoft:ABC\\DEF"));
        Assert.Equal("Microsoft:ABCDEF", SwitchWmiHelpers.CollapseBackslashes(@"Microsoft:ABC\\\\DEF"));
        Assert.Equal("noslashes", SwitchWmiHelpers.CollapseBackslashes("noslashes"));
    }

    // ── DecideHostVNicRepair (RepairHostVNic policy) ─────────────────────────────

    [Theory]
    [InlineData(2, true)]
    [InlineData(2, false)]   // even an Internal/Private switch with two host vNICs is a duplicate to collapse
    [InlineData(5, true)]
    public void DecideHostVNicRepair_MoreThanOneInternal_RemovesExtras(int count, bool external)
    {
        Assert.Equal(SwitchWmiHelpers.HostVNicRepair.RemoveExtraInternalPorts,
            SwitchWmiHelpers.DecideHostVNicRepair(count, external));
    }

    [Fact]
    public void DecideHostVNicRepair_ExternalWithNoHost_AddsOne()
    {
        Assert.Equal(SwitchWmiHelpers.HostVNicRepair.AddInternalPort,
            SwitchWmiHelpers.DecideHostVNicRepair(internalPortCount: 0, switchIsExternal: true));
    }

    [Fact]
    public void DecideHostVNicRepair_ExactlyOneHost_IsNoOp()
    {
        Assert.Equal(SwitchWmiHelpers.HostVNicRepair.None,
            SwitchWmiHelpers.DecideHostVNicRepair(1, switchIsExternal: true));
        Assert.Equal(SwitchWmiHelpers.HostVNicRepair.None,
            SwitchWmiHelpers.DecideHostVNicRepair(1, switchIsExternal: false));
    }

    [Fact]
    public void DecideHostVNicRepair_InternalSwitchWithNoHost_IsNoOp()
    {
        // An Internal/Private switch legitimately has zero host vNICs — must NOT try to add one.
        Assert.Equal(SwitchWmiHelpers.HostVNicRepair.None,
            SwitchWmiHelpers.DecideHostVNicRepair(internalPortCount: 0, switchIsExternal: false));
    }

    // ── ExternalPortMatchesAdapter (physical-NIC endpoint matching) ──────────────

    [Fact]
    public void ExternalPortMatchesAdapter_MacMatch_IgnoresSeparatorsAndCase()
    {
        Assert.True(SwitchWmiHelpers.ExternalPortMatchesAdapter(
            candidateMac: "aa-bb-cc-dd-ee-ff", candidateDesc: "Intel NIC",
            targetMac: "AABBCCDDEEFF", targetDesc: "something else"));
    }

    [Fact]
    public void ExternalPortMatchesAdapter_MacTakesPrecedence_DescriptionNotConsultedOnMacHit()
    {
        Assert.True(SwitchWmiHelpers.ExternalPortMatchesAdapter(
            "AA:BB:CC:DD:EE:FF", candidateDesc: null, targetMac: "aabbccddeeff", targetDesc: null));
    }

    [Fact]
    public void ExternalPortMatchesAdapter_DescriptionFallback_WhenMacDiffers()
    {
        Assert.True(SwitchWmiHelpers.ExternalPortMatchesAdapter(
            candidateMac: "111111111111", candidateDesc: "Lenovo USB Ethernet",
            targetMac: "222222222222", targetDesc: "lenovo usb ethernet"));
    }

    [Fact]
    public void ExternalPortMatchesAdapter_NoMacNoDescMatch_IsFalse()
    {
        Assert.False(SwitchWmiHelpers.ExternalPortMatchesAdapter(
            "111111111111", "Adapter A", "222222222222", "Adapter B"));
    }

    [Fact]
    public void ExternalPortMatchesAdapter_MissingCandidateMac_FallsBackToDescription()
    {
        Assert.True(SwitchWmiHelpers.ExternalPortMatchesAdapter(
            candidateMac: null, candidateDesc: "NIC-1", targetMac: "AABBCCDDEEFF", targetDesc: "NIC-1"));
        Assert.False(SwitchWmiHelpers.ExternalPortMatchesAdapter(
            candidateMac: null, candidateDesc: null, targetMac: "AABBCCDDEEFF", targetDesc: "NIC-1"));
    }

    [Fact]
    public void ExternalPortMatchesAdapter_NoTargetDesc_MacOnly()
    {
        // When we only know the target's MAC, a description-only candidate must not spuriously match.
        Assert.False(SwitchWmiHelpers.ExternalPortMatchesAdapter(
            candidateMac: "111111111111", candidateDesc: "NIC-1", targetMac: "222222222222", targetDesc: null));
    }
}
