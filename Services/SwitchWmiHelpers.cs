namespace HyperVManagerTray.Services;

/// <summary>
/// Pure (no <c>System.Management</c> dependency) helpers for interpreting Hyper-V virtual-switch WMI
/// objects, split out so the port-classification logic — the part that decides whether a switch port
/// is external, an internal (host / management-OS) vNIC, or a VM NIC connection — can be unit-tested
/// without a live Hyper-V host.
///
/// <para>The rules mirror the Microsoft "Hyper-V networking" sample's <c>DeterminePortType</c> exactly
/// (see <c>querying-networking-objects</c> on Microsoft Learn): a
/// <c>Msvm_EthernetPortAllocationSettingData</c> whose <c>HostResource[0]</c> points to a
/// <c>Msvm_ComputerSystem</c> is an internal (host / management-OS) connection; one pointing to a
/// <c>Msvm_ExternalEthernetPort</c> (or <c>Msvm_WiFiPort</c>) is an external connection; otherwise a
/// <c>Parent</c> of <c>Msvm_SyntheticEthernetPortSettingData</c> or
/// <c>Msvm_EmulatedEthernetPortSettingData</c> means it is a VM NIC connection.</para>
/// </summary>
public static class SwitchWmiHelpers
{
    /// <summary>The kind of thing a switch/VM Ethernet port allocation is connected to.</summary>
    public enum PortKind { None, Internal, External, VirtualMachine }

    // WMI class names used to classify an Msvm_EthernetPortAllocationSettingData's endpoints.
    public const string ComputerSystemClass       = "Msvm_ComputerSystem";
    public const string ExternalEthernetPortClass = "Msvm_ExternalEthernetPort";
    public const string WiFiPortClass             = "Msvm_WiFiPort";
    public const string SyntheticPortSettingClass = "Msvm_SyntheticEthernetPortSettingData";
    public const string EmulatedPortSettingClass  = "Msvm_EmulatedEthernetPortSettingData";

    /// <summary>True when a <c>HostResource</c> endpoint class denotes an external physical adapter
    /// (a wired <c>Msvm_ExternalEthernetPort</c> or a <c>Msvm_WiFiPort</c>).</summary>
    public static bool IsExternalPortClass(string? className) =>
        Eq(className, ExternalEthernetPortClass) || Eq(className, WiFiPortClass);

    /// <summary>True when a <c>HostResource</c> endpoint class denotes the host itself — i.e. a
    /// management-OS (internal) vNIC connection.</summary>
    public static bool IsInternalPortClass(string? className) =>
        Eq(className, ComputerSystemClass);

    /// <summary>
    /// Classifies an <c>Msvm_EthernetPortAllocationSettingData</c> from the WMI class name of its
    /// <c>HostResource[0]</c> endpoint and (only when that is inconclusive) its <c>Parent</c>. Pass the
    /// bare class names (e.g. from <c>ManagementPath.ClassName</c>); either may be <c>null</c>/empty.
    /// </summary>
    public static PortKind Classify(string? hostResourceClassName, string? parentClassName)
    {
        if (IsInternalPortClass(hostResourceClassName)) return PortKind.Internal;
        if (IsExternalPortClass(hostResourceClassName)) return PortKind.External;
        if (Eq(parentClassName, SyntheticPortSettingClass) || Eq(parentClassName, EmulatedPortSettingClass))
            return PortKind.VirtualMachine;
        return PortKind.None;
    }

    private static bool Eq(string? a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
