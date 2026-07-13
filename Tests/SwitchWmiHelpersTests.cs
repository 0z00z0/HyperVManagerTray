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
}
