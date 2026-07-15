using System.Net;
using System.Net.NetworkInformation;
using HyperVManagerTray.Services;
using Xunit;

namespace HyperVManagerTray.Tests;

public class AdapterMatcherTests
{
    // ── IsPickerPhysicalAdapter (issue #25) ─────────────────────────────────────

    [Theory]
    // Real physical NICs that MUST still appear in the rename picker:
    [InlineData(NetworkInterfaceType.Ethernet, 6, "Ethernet", "Intel(R) Ethernet Connection I219-V", true)]
    [InlineData(NetworkInterfaceType.Ethernet, 6, "Ethernet 5", "Realtek USB GbE Family Controller #2", true)]   // USB dock
    [InlineData(NetworkInterfaceType.Ethernet, 6, "Ethernet 7", "Lenovo USB Ethernet", true)]                    // USB dock
    [InlineData(NetworkInterfaceType.Wireless80211, 6, "Wi-Fi", "Intel(R) Wi-Fi 6E AX211 160MHz", true)]
    // Finding 4: a real physical NIC whose USER-RENAMABLE alias happens to contain a software marker
    // ("Office-VPN", "Bluetooth desk") must NOT be hidden — only the immutable description decides.
    [InlineData(NetworkInterfaceType.Ethernet, 6, "Office-VPN", "Realtek USB GbE Family Controller #2", true)]
    [InlineData(NetworkInterfaceType.Ethernet, 6, "Bluetooth desk dock", "Intel(R) Ethernet Connection I219-V", true)]
    // Software / non-physical adapters that must be HIDDEN:
    [InlineData(NetworkInterfaceType.Ethernet, 6, "Bluetooth Network Connection", "Bluetooth Device (Personal Area Network)", false)]
    [InlineData(NetworkInterfaceType.Ethernet, 6, "Ethernet 3", "TAP-Windows Adapter V9", false)]
    [InlineData(NetworkInterfaceType.Ethernet, 6, "VirtualBox Host-Only Network", "VirtualBox Host-Only Ethernet Adapter", false)]
    [InlineData(NetworkInterfaceType.Ethernet, 6, "VMware Network Adapter VMnet8", "VMware Virtual Ethernet Adapter for VMnet8", false)]
    [InlineData(NetworkInterfaceType.Ethernet, 6, "WireGuard Tunnel", "Wintun Userspace Tunnel", false)]
    [InlineData(NetworkInterfaceType.Tunnel, 6, "Tunnel", "Teredo Tunneling Pseudo-Interface", false)]           // type Tunnel
    [InlineData(NetworkInterfaceType.Ppp, 6, "VPN Connection", "WAN Miniport (PPP)", false)]                     // type Ppp
    [InlineData(NetworkInterfaceType.Ethernet, 0, "Ethernet 9", "Some Adapter With No MAC", false)]              // no 6-byte MAC
    [InlineData(NetworkInterfaceType.Ethernet, 8, "Ethernet 9", "Infiniband-ish", false)]                       // non-48-bit MAC
    public void IsPickerPhysicalAdapter_FiltersToRealPhysicalNics(
        NetworkInterfaceType type, int macBytes, string name, string description, bool expected)
        => Assert.Equal(expected, AdapterMatcher.IsPickerPhysicalAdapter(type, macBytes, name, description));

    [Theory]
    [InlineData("Bluetooth Device (Personal Area Network)", true)]
    [InlineData("TAP-Windows Adapter V9", true)]
    [InlineData("VirtualBox Host-Only Ethernet Adapter", true)]
    [InlineData("VMware Virtual Ethernet Adapter", true)]
    [InlineData("Realtek USB GbE Family Controller", false)]
    [InlineData("Intel(R) Ethernet Connection I219-V", false)]
    [InlineData("", false)]
    public void HasSoftwareAdapterMarker_MatchesOnlySoftwareAdapters(string description, bool expected)
        => Assert.Equal(expected, AdapterMatcher.HasSoftwareAdapterMarker(description));


    [Theory]
    [InlineData("10.0.0.45",   "10.0.0.0/23", true)]
    [InlineData("10.0.1.200",  "10.0.0.0/23", true)]    // /23 spans .0 and .1
    [InlineData("10.0.2.1",    "10.0.0.0/23", false)]   // just outside the /23
    [InlineData("192.168.1.5", "192.168.1.0/24", true)]
    [InlineData("192.168.2.5", "192.168.1.0/24", false)]
    [InlineData("10.0.0.1",    "10.0.0.1/32", true)]
    [InlineData("10.0.0.2",    "10.0.0.1/32", false)]
    [InlineData("8.8.8.8",     "0.0.0.0/0", true)]      // /0 matches everything
    public void IsInCidr_Works(string ip, string cidr, bool expected)
        => Assert.Equal(expected, AdapterMatcher.IsInCidr(IPAddress.Parse(ip), cidr));

    [Theory]
    [InlineData("not-a-cidr")]
    [InlineData("10.0.0.0/")]
    [InlineData("10.0.0.0/abc")]
    public void IsInCidr_InvalidCidr_ReturnsFalse(string cidr)
        => Assert.False(AdapterMatcher.IsInCidr(IPAddress.Parse("10.0.0.1"), cidr));

    [Theory]
    [InlineData("aa:bb:cc:dd:ee:ff", "AABBCCDDEEFF")]
    [InlineData("AA-BB-CC-DD-EE-FF", "AABBCCDDEEFF")]
    [InlineData("AABBCCDDEEFF",      "AABBCCDDEEFF")]
    public void NormalizeMac_StripsSeparatorsAndUppercases(string input, string expected)
        => Assert.Equal(expected, AdapterMatcher.NormalizeMac(input));

    [Fact]
    public void NormalizeMac_EqualAcrossFormats()
        => Assert.Equal(AdapterMatcher.NormalizeMac("aa:bb:cc:dd:ee:ff"),
                        AdapterMatcher.NormalizeMac("AA-BB-CC-DD-EE-FF"));

    [Theory]
    [InlineData("AABBCCDDEEFF", "AA:BB:CC:DD:EE:FF")]
    [InlineData("001122334455", "00:11:22:33:44:55")]
    public void FormatMac_FormatsValidMac(string raw, string expected)
        => Assert.Equal(expected, AdapterMatcher.FormatMac(raw));

    [Theory]
    [InlineData("")]        // empty MAC (e.g. tunnel / WAN miniport)
    [InlineData("ABCDEF")]  // wrong length
    public void FormatMac_InvalidLength_ReturnsRawUnchanged(string raw)
        => Assert.Equal(raw, AdapterMatcher.FormatMac(raw));

    // ── IsBridgeableAdapterType (issue #29, finding 5) ──────────────────────────
    // Only wired adapters surface as Msvm_ExternalEthernetPort (the switch-binding target); Wi-Fi is a
    // Msvm_WiFiPort the bind path never queries, so a Wi-Fi rule could never take effect.

    [Theory]
    [InlineData(NetworkInterfaceType.Ethernet, true)]
    [InlineData(NetworkInterfaceType.GigabitEthernet, true)]
    [InlineData(NetworkInterfaceType.Wireless80211, false)]
    public void IsBridgeableAdapterType_RejectsWirelessOnly(NetworkInterfaceType type, bool expected)
        => Assert.Equal(expected, AdapterMatcher.IsBridgeableAdapterType(type));
}
