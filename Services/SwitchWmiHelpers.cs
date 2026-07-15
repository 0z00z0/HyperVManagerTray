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

    // ── SKIP-guard logic (ApplySwitchAsync) ──────────────────────────────────────

    /// <summary>
    /// True when a VM NIC's existing connection already targets the given switch, so re-applying it
    /// would be a needless (network-bouncing) no-op. A VM-NIC connection's <c>HostResource[0]</c> is the
    /// switch's WMI object path, which embeds the switch's GUID (<c>Msvm_VirtualEthernetSwitch.Name</c>);
    /// we match on that GUID substring — the same correlation <see cref="Classify"/>'s caller uses — so a
    /// path rendered slightly differently (quoting, host prefix) still compares equal.
    /// </summary>
    /// <param name="hostResource">The connection's <c>HostResource</c> array (may be null/empty).</param>
    /// <param name="switchId">The target switch's GUID (<c>Name</c>); empty ⇒ never a match (fail-open to
    /// "not already connected", so the caller proceeds to connect rather than wrongly skipping).</param>
    public static bool ConnectionTargetsSwitch(string?[]? hostResource, string? switchId)
    {
        if (string.IsNullOrEmpty(switchId)) return false;
        if (hostResource is null || hostResource.Length == 0) return false;
        var first = hostResource[0];
        return !string.IsNullOrEmpty(first) &&
               first.Contains(switchId, StringComparison.OrdinalIgnoreCase);
    }

    // ── Host-vNIC repair decision (RepairHostVNicAsync) ──────────────────────────

    /// <summary>The action <see cref="DecideHostVNicRepair"/> selects for a switch's management-OS state.</summary>
    public enum HostVNicRepair
    {
        /// <summary>Exactly one host vNIC (or an Internal/Private switch with none) — healthy, do nothing.</summary>
        None,
        /// <summary>More than one internal port allocation — remove the extras, keeping exactly one.</summary>
        RemoveExtraInternalPorts,
        /// <summary>An External switch whose management-OS sharing was turned off — add one internal port back.</summary>
        AddInternalPort,
    }

    /// <summary>
    /// Decides how to bring a switch back to exactly one host (management-OS) vNIC, from the count of its
    /// INTERNAL port allocations (each maps 1:1 to a <c>vEthernet (&lt;switch&gt;)</c> adapter) and whether
    /// the switch is External. This is the whole policy of <c>RepairHostVNicAsync</c>, pulled out so every
    /// branch is unit-testable without a host.
    ///
    /// <para><b>Safety contract:</b> the "&gt; 1 ⇒ remove extras" path only ever removes DOWN TO one, never
    /// to zero, so the host is never momentarily stranded (unlike the old <c>AllowManagementOS $false→$true</c>
    /// reset). Adding one back is reserved for an External switch that has lost sharing entirely (count 0);
    /// an Internal/Private switch legitimately has zero host vNICs and is left alone.</para>
    /// </summary>
    public static HostVNicRepair DecideHostVNicRepair(int internalPortCount, bool switchIsExternal)
    {
        if (internalPortCount > 1) return HostVNicRepair.RemoveExtraInternalPorts;
        if (switchIsExternal && internalPortCount == 0) return HostVNicRepair.AddInternalPort;
        return HostVNicRepair.None;
    }

    // ── External-adapter (Msvm_ExternalEthernetPort) matching ────────────────────

    /// <summary>
    /// True when an <c>Msvm_ExternalEthernetPort</c> endpoint denotes the wanted physical adapter, using the
    /// same precedence the binding path relies on: a hardware-MAC (<c>PermanentAddress</c>) match is
    /// authoritative; adapter description (<c>ElementName</c>) is the fallback for adapters whose WMI MAC is
    /// absent/renormalised. MACs are compared after stripping separators and case (via
    /// <see cref="AdapterMatcher.NormalizeMac"/>); a MAC that isn't a full 12 hex digits can't match, so
    /// only the description is considered then.
    /// </summary>
    /// <param name="candidateMac">The port's <c>PermanentAddress</c> (any separator form; may be null).</param>
    /// <param name="candidateDesc">The port's <c>ElementName</c> (may be null).</param>
    /// <param name="targetMac">The wanted adapter's MAC, already normalised (12 hex digits).</param>
    /// <param name="targetDesc">The wanted adapter's description (may be null ⇒ no description fallback).</param>
    public static bool ExternalPortMatchesAdapter(
        string? candidateMac, string? candidateDesc, string? targetMac, string? targetDesc)
    {
        if (!string.IsNullOrEmpty(targetMac) &&
            AdapterMatcher.NormalizeMac(candidateMac ?? "") == AdapterMatcher.NormalizeMac(targetMac))
            return true;
        return !string.IsNullOrEmpty(targetDesc) &&
               string.Equals(candidateDesc, targetDesc, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Eq(string? a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
