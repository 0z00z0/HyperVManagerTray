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

    // ── VM-NIC connection correlation (FindNicConnection) ────────────────────────

    /// <summary>
    /// True when an <c>Msvm_EthernetPortAllocationSettingData</c> (EPASD — a VM NIC's switch connection)
    /// belongs to the synthetic NIC whose <c>InstanceID</c> is <paramref name="nicInstanceId"/> (an
    /// <c>Msvm_SyntheticEthernetPortSettingData</c>, of the form
    /// <c>Microsoft:&lt;vmguid&gt;\&lt;portguid&gt;</c>).
    ///
    /// <para><b>Why not a raw <c>Parent</c> substring (the issue #17 bug).</b> The EPASD's <c>Parent</c>
    /// is a REF path that references the SEPSD by its InstanceID, but the embedded <c>\</c> between the VM
    /// and port GUIDs renders ESCAPED there — doubled to <c>\\</c>, sometimes also namespace-prefixed and
    /// quoted (<c>\\HOST\root\virtualization\v2:Msvm_SyntheticEthernetPortSettingData.InstanceID="…"</c>).
    /// A raw <c>Parent.Contains(nicInstanceId)</c> — where <paramref name="nicInstanceId"/> still has a
    /// single <c>\</c> — therefore MISSES, so the caller wrongly concludes the NIC is unconnected and tries
    /// to ADD a second Ethernet Connection to a NIC that already has one. That is the observed
    /// <c>InvalidOperationException: '…' failed to add device 'Ethernet Connection'</c> failure.</para>
    ///
    /// <para><b>Correlation used.</b> Compare on the shared <c>&lt;vmguid&gt;\&lt;portguid&gt;</c> identity
    /// after collapsing every backslash, so the level of escaping is irrelevant. Two independent signals
    /// both carry that identity and either is accepted: the EPASD's <c>Parent</c> (its authoritative
    /// reference to the SEPSD) and the EPASD's own <c>InstanceID</c> (which is the SEPSD InstanceID plus a
    /// trailing connection segment, e.g. <c>Microsoft:VM\PORT\CONN</c>). This mirrors how
    /// <see cref="VmService"/> correlates a port allocation to its VM by an InstanceID substring rather
    /// than by a REF path. The full port GUID is part of the compared identity, so a different NIC on the
    /// same VM (same vmguid, different portguid) never matches.</para>
    /// </summary>
    /// <param name="epasdInstanceId">The EPASD's own <c>InstanceID</c> (may be null/empty).</param>
    /// <param name="epasdParent">The EPASD's <c>Parent</c> REF path to its SEPSD (may be null/empty).</param>
    /// <param name="nicInstanceId">The synthetic NIC's (SEPSD) <c>InstanceID</c>; empty ⇒ never a match.</param>
    public static bool NicConnectionMatches(string? epasdInstanceId, string? epasdParent, string? nicInstanceId)
    {
        if (string.IsNullOrEmpty(nicInstanceId)) return false;
        var nicId = CollapseBackslashes(nicInstanceId);
        if (nicId.Length == 0) return false;

        return ContainsId(epasdParent, nicId) || ContainsId(epasdInstanceId, nicId);

        static bool ContainsId(string? candidate, string nicId) =>
            !string.IsNullOrEmpty(candidate) &&
            CollapseBackslashes(candidate).Contains(nicId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Removes every backslash from a WMI InstanceID / REF path so a value read from a raw property (a
    /// single <c>\</c> separator) and the same value embedded in an escaped REF path (a doubled <c>\\</c>,
    /// or more) reduce to the same separator-free identity. Backslashes are only ever the GUID separators
    /// here (the GUIDs themselves are hex + hyphens), so collapsing them is a lossless canonicalisation
    /// that is immune to however many layers of escaping a given host renders.
    /// </summary>
    public static string CollapseBackslashes(string raw) => raw.Replace(@"\", "");

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
