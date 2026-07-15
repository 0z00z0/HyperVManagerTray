using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using HyperVManagerTray.Models;

namespace HyperVManagerTray.Services;

/// <summary>Result of evaluating config rules against the current host network state.</summary>
public sealed record MatchResult(string RuleName, string VirtualSwitch, IReadOnlyList<string> TargetVms)
{
    public string HostAdapterName          { get; init; } = "—";  // human-readable description
    public string HostAdapterInterfaceName { get; init; } = "—";  // OS interface alias used by Set-VMSwitch
    public string HostIp                   { get; init; } = "—";
    public string Gateway                  { get; init; } = "—";
    public IReadOnlyList<string> DnsServers { get; init; } = [];
}

/// <summary>Network details of the current primary host adapter, used by "Add current network" feature.</summary>
public sealed record CurrentNetworkInfo(
    string AdapterDescription,
    string Mac,          // colon-separated, e.g. "AA:BB:CC:DD:EE:FF"
    string Ip,           // e.g. "10.0.0.45"
    string IpCidr);      // e.g. "10.0.0.0/23"

/// <summary>
/// A physical NIC as offered in the "Rename network adapter" submenu (issue #15): its connection
/// alias, its raw device description (the FriendlyName-derived string being renamed, e.g.
/// "Realtek USB GbE Family Controller #2"), the InterfaceGuid used to resolve the PnP device, and MAC.
/// </summary>
public sealed record PhysicalAdapterInfo(
    string InterfaceAlias,   // NetworkInterface.Name, e.g. "Ethernet 5"
    string Description,      // NetworkInterface.Description (the string the rename changes)
    string InterfaceGuid,    // NetworkInterface.Id, e.g. "{BECDE8F3-...}"
    string Mac);             // colon-separated, or "—"

/// <summary>
/// Core rule-evaluation logic: inspects the host's live network adapters and decides which
/// virtual switch the VMs should use.  Handles the Hyper-V bridging quirk where an external
/// switch with AllowManagementOS=true moves the physical NIC's IP onto a virtual NIC, and
/// filters out WFP/NDIS filter-layer adapters that share a MAC/IP with the real NIC.
/// All members are pure/stateless.
/// </summary>
public static class AdapterMatcher
{
    /// <summary>Returns details for the current primary active adapter, or null if none found.</summary>
    public static CurrentNetworkInfo? GetCurrentNetworkInfo()
    {
        var (physical, virtual_) = SplitAdapters();

        var nic = PrimaryAdapter(physical, virtual_);
        if (nic is null) return null;

        try
        {
            // When the physical NIC is bridged its IP moves to the Hyper-V virtual NIC.
            // Try the physical NIC first, then fall back to any virtual NIC with an IPv4.
            UnicastIPAddressInformation? unicast = null;
            try
            {
                unicast = nic.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
            }
            catch { /* adapter removed after PrimaryAdapter() returned */ }

            if (unicast is null)
            {
                foreach (var v in virtual_)
                {
                    try
                    {
                        unicast = v.GetIPProperties().UnicastAddresses
                            .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                        if (unicast is not null) break;
                    }
                    catch { /* skip removed adapter */ }
                }
            }

            if (unicast is null) return null;

            string mac;
            try   { mac = FormatMac(nic.GetPhysicalAddress().ToString()); }
            catch { mac = "—"; }

            return new CurrentNetworkInfo(
                AdapterDescription: FriendlyAdapterName(nic),
                Mac:    mac,
                Ip:     unicast.Address.ToString(),
                IpCidr: CalculateCidr(unicast));
        }
        catch { return null; }
    }

    /// <summary>
    /// Evaluates the config rules (in priority order) against the current host network and
    /// returns the matching rule's switch/VMs, or the fallback if nothing matched.
    /// </summary>
    public static MatchResult Evaluate(AppConfig config)
    {
        var (physical, virtual_) = SplitAdapters();

        foreach (var rule in config.Rules)
        {
            var matched = MatchingNic(rule, physical, virtual_);
            if (matched is not null)
                return BuildResult(rule.Name, rule.VirtualSwitch, rule.TargetVms, matched, virtual_);
        }

        return BuildResult("Fallback", config.Fallback.VirtualSwitch, config.Fallback.TargetVms,
                           PrimaryAdapter(physical, virtual_), virtual_);
    }

    /// <summary>
    /// Returns the physical (non-Hyper-V-virtual, non-filter) NICs currently Up, for the
    /// "Rename network adapter" submenu.  Reuses the same <see cref="SplitAdapters"/> filtering the
    /// rule engine uses, so WFP/NDIS filter adapters and Hyper-V management vNICs are excluded and the
    /// menu only lists real devices whose description is worth renaming.
    /// </summary>
    public static IReadOnlyList<PhysicalAdapterInfo> GetPhysicalAdapters()
    {
        var (physical, _) = SplitAdapters();

        var list = new List<PhysicalAdapterInfo>(physical.Count);
        foreach (var nic in physical)
        {
            byte[] macBytes;
            try   { macBytes = nic.GetPhysicalAddress().GetAddressBytes(); }
            catch { macBytes = []; }

            // PICKER-specific gate (issue #25): SplitAdapters already dropped filter-layer / Hyper-V /
            // bridge adapters (that filtering must NOT change — the rule engine and PrimaryAdapter share
            // it), but Bluetooth PAN, TAP/VPN, VirtualBox/VMware host-only and similar software adapters
            // are still Up "physical" NICs the rule engine tolerates yet make no sense to rename. Only
            // the picker applies this tighter test, so USB-Ethernet docks (a real 6-byte MAC, no software
            // marker) still appear.
            if (!IsPickerPhysicalAdapter(nic.NetworkInterfaceType, macBytes.Length, nic.Name, nic.Description))
                continue;

            var mac = FormatMac(BitConverter.ToString(macBytes).Replace("-", ""));

            // Raw Description (not FriendlyAdapterName) — SplitAdapters already excluded the filter
            // adapters whose suffix FriendlyAdapterName strips, so this is the real device string and
            // exactly what the rename mutates.
            list.Add(new PhysicalAdapterInfo(nic.Name, nic.Description, nic.Id, mac));
        }
        return list;
    }

    /// <summary>
    /// PICKER-only test (issue #25) for whether an adapter is a real, renameable physical NIC.  Pure so
    /// it can be unit-tested without a live <see cref="NetworkInterface"/>: takes the adapter's
    /// <paramref name="type"/>, its MAC length in bytes, and its <paramref name="name"/>/<paramref name="description"/>.
    /// This is DELIBERATELY separate from <see cref="SplitAdapters"/>: tightening the shared split would
    /// change rule-matching / primary-adapter semantics, whereas hiding these adapters only trims the
    /// rename list.  Rules:
    /// <list type="bullet">
    ///   <item>Must carry a standard 48-bit (6-byte) MAC — excludes tunnel/WAN-miniport pseudo-NICs.</item>
    ///   <item>Excludes <see cref="NetworkInterfaceType.Tunnel"/> / <c>Ppp</c> / <c>Loopback</c>.</item>
    ///   <item>Excludes known software-adapter name/description markers (Bluetooth PAN, TAP/OpenVPN,
    ///   Wintun/WireGuard, VirtualBox, VMware, generic "virtual"/"pseudo"/"VPN").</item>
    /// </list>
    /// A USB-Ethernet dock ("Realtek USB GbE Family Controller", real MAC, Ethernet type) passes.
    /// </summary>
    internal static bool IsPickerPhysicalAdapter(NetworkInterfaceType type, int macByteLength, string name, string description)
    {
        if (macByteLength != 6) return false;

        switch (type)
        {
            case NetworkInterfaceType.Tunnel:
            case NetworkInterfaceType.Ppp:
            case NetworkInterfaceType.Loopback:
                return false;
        }

        return !HasSoftwareAdapterMarker(name) && !HasSoftwareAdapterMarker(description);
    }

    /// <summary>
    /// True when a NIC name/description contains a marker of a software/virtual adapter that should never
    /// be offered for rename (issue #25). Distinct from <see cref="IsFilterLayerAdapter"/>, which the
    /// shared split uses to drop WFP/NDIS/bridge layers; this set targets end-user virtual adapters that
    /// otherwise look physical (own MAC, Ethernet type). Chosen substrings are specific enough not to hit
    /// legitimate USB-Ethernet dock names.
    /// </summary>
    internal static bool HasSoftwareAdapterMarker(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var marker in SoftwareAdapterMarkers)
            if (s.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    private static readonly string[] SoftwareAdapterMarkers =
    [
        "Bluetooth",          // Bluetooth PAN / Personal Area Network
        "TAP-Windows",        // OpenVPN TAP adapter
        "TAP-Win32",
        "OpenVPN",
        "Wintun",             // WireGuard / modern tunnel driver
        "WireGuard",
        "VirtualBox",         // VirtualBox Host-Only Network
        "VMware",             // VMware Virtual Ethernet Adapter (VMnet1/VMnet8)
        "VMnet",
        "Hyper-V",            // belt-and-braces (SplitAdapters already drops these)
        "Virtual Adapter",
        "Virtual Ethernet",
        "Pseudo",
        "VPN",
        "GlobalProtect",      // Palo Alto PANGP
        "PANGP",
        "ZeroTier",
        "Tailscale",
        "Npcap Loopback",
    ];

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Splits all Up, non-loopback adapters into physical (non-Hyper-V-virtual) and
    /// virtual (Hyper-V management NICs created by AllowManagementOS=true).
    ///
    /// WFP/NDIS filter-layer adapters are excluded from both lists: Windows creates them
    /// on top of physical NICs, they share the same MAC and IP as the underlying adapter,
    /// but they are NOT valid targets for Set-VMSwitch -NetAdapterName and should not
    /// participate in rule matching or primary-adapter detection.
    /// </summary>
    private static (List<NetworkInterface> Physical, List<NetworkInterface> Virtual) SplitAdapters()
    {
        var all = NetworkInterface
            .GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                        && !IsFilterLayerAdapter(n))
            .ToList();

        return (all.Where(n => !IsHyperVVirtual(n)).ToList(),
                all.Where(n =>  IsHyperVVirtual(n)).ToList());
    }

    private static MatchResult BuildResult(
        string ruleName, string virtualSwitch, List<string> targetVms,
        NetworkInterface? nic, List<NetworkInterface> virtualAdapters)
    {
        IPInterfaceProperties? props = null;
        try { props = nic?.GetIPProperties(); }
        catch { /* adapter removed just after evaluation — treat as no IP */ }

        // When a physical NIC is bridged (AllowManagementOS=true), Windows moves the
        // IP/gateway/DNS to a Hyper-V virtual NIC.  If the physical NIC has no IPv4
        // address, source IP/gateway/DNS from the best available virtual NIC instead.
        bool hasIpv4 = props?.UnicastAddresses
            .Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork) == true;

        if (!hasIpv4 && virtualAdapters.Count > 0)
        {
            // Prefer the virtual NIC that also has a default gateway (bridges always do).
            NetworkInterface? vNic = null;
            foreach (var v in virtualAdapters)
            {
                try
                {
                    var vProps = v.GetIPProperties();
                    if (!vProps.UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                        continue;
                    bool hasGw = vProps.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
                    if (vNic is null || hasGw)
                    {
                        vNic  = v;
                        props = vProps;
                        if (hasGw) break; // gateway-bearing NIC wins immediately
                    }
                }
                catch { /* adapter removed mid-evaluation — skip */ }
            }
        }

        var ip = props?.UnicastAddresses
            .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            ?.Address.ToString() ?? "—";

        var gw = props?.GatewayAddresses
            .Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(g => g.Address.ToString())
            .FirstOrDefault() ?? "—";

        var dns = props?.DnsAddresses
            .Where(d => d.AddressFamily == AddressFamily.InterNetwork)
            .Select(d => d.ToString())
            .ToList() ?? [];

        return new MatchResult(ruleName, virtualSwitch, targetVms)
        {
            HostAdapterName          = nic is not null ? FriendlyAdapterName(nic) : "—",
            HostAdapterInterfaceName = nic?.Name ?? "—",
            HostIp                   = ip,
            Gateway                  = gw,
            DnsServers               = dns
        };
    }

    /// <summary>
    /// Returns the first physical NIC that satisfies all conditions of the rule, or null.
    ///
    /// When AllowManagementOS=true the physical NIC may lose its IPv4 address to a
    /// Hyper-V virtual NIC.  If the MAC matches but no IP is found on the physical NIC,
    /// we also check virtual NICs for the CIDR condition so the rule still fires.
    /// </summary>
    private static NetworkInterface? MatchingNic(
        NetworkRule rule,
        List<NetworkInterface> physicalAdapters,
        List<NetworkInterface> virtualAdapters)
    {
        foreach (var nic in physicalAdapters)
        {
            try
            {
                bool macOk = rule.Conditions.AdapterMac is null
                             || NormalizeMac(nic.GetPhysicalAddress().ToString()) ==
                                NormalizeMac(rule.Conditions.AdapterMac);

                if (!macOk) continue;
                if (rule.Conditions.IpCidr is null) return nic;

                // Check the physical NIC's own addresses.
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IsInCidr(addr.Address, rule.Conditions.IpCidr)) return nic;
                }

                // When bridged, the physical NIC has no IP — check virtual NICs for the CIDR.
                // If any virtual NIC carries an IP inside the rule's subnet, the rule is matched
                // and we return the physical NIC (its Name alias is what Set-VMSwitch needs).
                foreach (var vNic in virtualAdapters)
                {
                    try
                    {
                        foreach (var addr in vNic.GetIPProperties().UnicastAddresses)
                        {
                            if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                            if (IsInCidr(addr.Address, rule.Conditions.IpCidr)) return nic;
                        }
                    }
                    catch { /* virtual adapter removed mid-evaluation — skip */ }
                }
            }
            catch { /* physical adapter removed mid-evaluation — skip */ }
        }
        return null;
    }

    /// <summary>
    /// Returns the physical NIC that Windows currently routes traffic through.
    ///
    /// Uses GetBestInterface (Win32) for accuracy.  When the result is a Hyper-V virtual
    /// NIC (bridge active), we look for the physical NIC that has been stripped of its IP
    /// (it's the one driving the bridge) and return that instead, preferring wired over
    /// wireless when ambiguous.  Falls back to a gateway/speed heuristic if needed.
    /// </summary>
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

    private static NetworkInterface? PrimaryAdapter(
        List<NetworkInterface> physicalAdapters,
        List<NetworkInterface> virtualAdapters)
    {
        try
        {
            var bytes = IPAddress.Parse("8.8.8.8").GetAddressBytes();
            uint dest = BitConverter.ToUInt32(bytes, 0);

            if (GetBestInterface(dest, out uint bestIndex) == 0)
            {
                // Happy path: best interface is a physical adapter.
                var best = physicalAdapters.FirstOrDefault(n =>
                {
                    try   { return n.GetIPProperties().GetIPv4Properties().Index == (int)bestIndex; }
                    catch { return false; }
                });
                if (best is not null) return best;

                // GetBestInterface returned a Hyper-V virtual adapter (bridge is active).
                // The physical NIC driving the bridge has had its IP moved to that virtual
                // NIC; it is now Up but carries no IPv4 address.  Find it, preferring
                // wired over wireless so we don't accidentally pick an unrelated NIC.
                // Require a valid 6-byte MAC to exclude tunnel/WAN-miniport adapters that
                // also lack an IPv4 but are not real NICs.
                var bridged = physicalAdapters
                    .Where(n =>
                    {
                        try
                        {
                            return HasValidMac(n)
                                && !n.GetIPProperties().UnicastAddresses
                                    .Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                        }
                        catch { return false; }
                    })
                    .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 1 : 0)
                    .FirstOrDefault();

                if (bridged is not null) return bridged;
            }
        }
        catch { /* fall through to heuristic */ }

        // Heuristic fallback: prefer adapters with a default gateway, then wired over wireless,
        // then pick the fastest.  Wi-Fi 6E can report a higher Speed than Gigabit Ethernet, so
        // Speed alone would incorrectly prefer wireless when both share the same subnet.
        return physicalAdapters
            .Where(n =>
            {
                try
                {
                    return n.GetIPProperties().GatewayAddresses
                        .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
                }
                catch { return false; }
            })
            .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 1 : 0)
            .ThenByDescending(n => n.Speed)
            .FirstOrDefault()
            ?? physicalAdapters.FirstOrDefault();
    }

    // ── Small utilities ───────────────────────────────────────────────────────

    /// <summary>True only for adapters that carry a standard 48-bit (6-byte) MAC address.</summary>
    private static bool HasValidMac(NetworkInterface nic)
    {
        try   { return nic.GetPhysicalAddress().GetAddressBytes().Length == 6; }
        catch { return false; }
    }

    /// <summary>
    /// Returns a display-friendly adapter name by stripping the Windows filter-driver
    /// suffix that appears in <see cref="NetworkInterface.Description"/> for some adapters,
    /// e.g. "Lenovo USB Ethernet-WFP Native MAC Layer LightWeight Filter-0000" → "Lenovo USB Ethernet".
    /// </summary>
    private static string FriendlyAdapterName(NetworkInterface nic) => StripFilterSuffix(nic.Description);

    /// <summary>
    /// Strips the Windows filter-driver suffix from a raw adapter <c>Description</c> string, e.g.
    /// "Lenovo USB Ethernet-WFP Native MAC Layer LightWeight Filter-0000" → "Lenovo USB Ethernet".
    /// Pure string logic extracted from <see cref="FriendlyAdapterName"/> so it can be unit-tested
    /// without a live <see cref="NetworkInterface"/>. Returns the input unchanged when no marker is
    /// found, or when a marker occurs at index 0 (nothing meaningful to keep as the base name).
    /// </summary>
    internal static string StripFilterSuffix(string description)
    {
        foreach (var marker in new[] { "-WFP ", " - WFP ", "-NDIS ", " - NDIS " })
        {
            var idx = description.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx > 0) return description[..idx].Trim();
        }
        return description;
    }

    // Formats "AABBCCDDEEFF" → "AA:BB:CC:DD:EE:FF"
    internal static string FormatMac(string raw)
    {
        var clean = raw.Replace(":", "").Replace("-", "").ToUpperInvariant();
        if (clean.Length != 12) return raw; // not a standard 48-bit MAC — return as-is
        return string.Join(":", Enumerable.Range(0, 6).Select(i => clean.Substring(i * 2, 2)));
    }

    private static string CalculateCidr(UnicastIPAddressInformation unicast)
    {
        try
        {
            return CalculateCidrFromBytes(unicast.Address.GetAddressBytes(), unicast.IPv4Mask.GetAddressBytes());
        }
        catch
        {
            var parts = unicast.Address.ToString().Split('.');
            return $"{parts[0]}.{parts[1]}.{parts[2]}.0/24";
        }
    }

    /// <summary>
    /// Computes the "network/prefixLen" CIDR string from a raw IPv4 address and subnet mask (both
    /// 4-byte, network order). Pure byte-array logic extracted from <see cref="CalculateCidr"/> so it
    /// can be unit-tested without constructing a live <see cref="UnicastIPAddressInformation"/> (which
    /// has no public constructor). Counts mask bits regardless of contiguity — a non-standard,
    /// non-contiguous mask (e.g. 255.0.255.0) still yields a bit count, matching the production
    /// behaviour rather than validating mask well-formedness.
    /// </summary>
    internal static string CalculateCidrFromBytes(byte[] ipBytes, byte[] maskBytes)
    {
        var prefixLen = 0;
        foreach (var b in maskBytes) { var v = (int)b; while (v != 0) { prefixLen += v & 1; v >>= 1; } }

        var netBytes = ipBytes.Zip(maskBytes, (a, b) => (byte)(a & b)).ToArray();
        return $"{new IPAddress(netBytes)}/{prefixLen}";
    }

    /// <summary>
    /// Returns true for the Hyper-V management NICs that Windows creates on the host
    /// when AllowManagementOS=true is set on a virtual switch.  These adapters share the
    /// IP with the bridged physical NIC (which loses its own IP) but have a Microsoft-
    /// assigned MAC (00:15:5D prefix).  They are excluded from rule MAC matching and
    /// primary-adapter detection, but their IP/gateway/DNS is used when the paired
    /// physical NIC has no IPv4 address.
    /// </summary>
    private static bool IsHyperVVirtual(NetworkInterface nic) =>
        nic.Description.StartsWith("Hyper-V Virtual", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true for software/virtual adapters that should never be used as a
    /// <c>Set-VMSwitch -NetAdapterName</c> target or participate in rule matching.
    ///
    /// <list type="bullet">
    ///   <item><b>WFP / NDIS filter-driver adapters</b> — Windows creates these alongside the
    ///   real physical NIC (e.g. "Ethernet-WFP Native MAC Layer LightWeight Filter-0000").
    ///   They share the same MAC/IP but are not valid switch targets and cause WMI hangs.</item>
    ///   <item><b>Microsoft Network Adapter Multiplexor Driver</b> — the adapter created by
    ///   the Windows "Bridge Connections" (Network Bridge / ms_bridge) feature.  If selected
    ///   as the external NIC, <c>Set-VMSwitch</c> binds the Hyper-V switch to the Windows
    ///   bridge instead of the underlying physical NIC, which activates the MAC Bridge service
    ///   and causes the host to route through the wrong adapter.</item>
    /// </list>
    /// </summary>
    private static bool IsFilterLayerAdapter(NetworkInterface nic)
    {
        static bool HasMarker(string s) =>
            s.IndexOf("-WFP ",             StringComparison.OrdinalIgnoreCase) >= 0 ||
            s.IndexOf("LightWeight Filter", StringComparison.OrdinalIgnoreCase) >= 0 ||
            s.IndexOf("-NDIS ",            StringComparison.OrdinalIgnoreCase) >= 0 ||
            s.IndexOf("Multiplexor",       StringComparison.OrdinalIgnoreCase) >= 0;   // Windows Network Bridge
        return HasMarker(nic.Name) || HasMarker(nic.Description);
    }

    internal static bool IsInCidr(IPAddress address, string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[1], out int prefixLen)) return false;

        var network = IPAddress.Parse(parts[0]);
        uint mask = prefixLen == 0 ? 0u : ~((1u << (32 - prefixLen)) - 1u);
        return (ToUInt32(network) & mask) == (ToUInt32(address) & mask);
    }

    private static uint ToUInt32(IPAddress addr)
    {
        var bytes = addr.GetAddressBytes();
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return BitConverter.ToUInt32(bytes, 0);
    }

    /// <summary>Strips separators and upper-cases a MAC so two forms compare equal (e.g. "aa:bb…" == "AA-BB…").</summary>
    internal static string NormalizeMac(string mac) =>
        mac.Replace(":", "").Replace("-", "").ToUpperInvariant();
}
