using Microsoft.Win32;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// READ-ONLY registry lookups for a network adapter's underlying PnP device: the Class-key
/// <c>NetCfgInstanceId</c> → <c>DeviceInstanceID</c> chain, and the device's <c>FriendlyName</c>.
///
/// <para><b>Why this is its own file.</b> These members were extracted verbatim from
/// <see cref="AdapterRenamer"/> when the display path started needing them (issue #32):
/// <c>AdapterMatcher</c> now resolves an adapter's FriendlyName for display, and
/// <c>AdapterMatcher.cs</c> is linked into the test assembly. Keeping these reads here lets the tests
/// link the code they need while <see cref="AdapterRenamer"/> — which holds the device-MUTATING
/// FriendlyName write and the SetupAPI disable/enable — stays out of the test assembly entirely, as
/// <c>HyperVManagerTray.Tests.csproj</c> deliberately requires. The dependency is one-way:
/// <see cref="AdapterRenamer"/> uses this type; this type never mutates anything.</para>
///
/// <para><b>Safety.</b> Every member here opens keys read-only and needs no elevation. Nothing in this
/// file writes to the registry, touches a device, or builds a shell string.</para>
/// </summary>
internal static class AdapterDeviceRegistry
{
    // Network adapter setup class (investigation §1/§2).
    internal const string NetClassKeyPath =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
    internal const string EnumKeyPrefix = @"SYSTEM\CurrentControlSet\Enum\";

    /// <summary>
    /// Reads every network Class-key subkey and returns the <c>NetCfgInstanceId</c> →
    /// <c>DeviceInstanceID</c> pairs. Read-only; skips non-adapter subkeys (e.g. "Properties") that
    /// carry no <c>NetCfgInstanceId</c>.
    ///
    /// <para>This walks the whole Class key, so callers resolving several adapters must call it ONCE
    /// and reuse the result rather than per adapter (issue #32).</para>
    /// </summary>
    internal static List<AdapterNameRules.ClassAdapterEntry> ReadClassAdapterEntries()
    {
        var entries = new List<AdapterNameRules.ClassAdapterEntry>();

        using var classKey = Registry.LocalMachine.OpenSubKey(NetClassKeyPath);
        if (classKey is null) return entries;

        foreach (var subName in classKey.GetSubKeyNames())
        {
            try
            {
                using var sub = classKey.OpenSubKey(subName);
                if (sub?.GetValue("NetCfgInstanceId") is not string netCfg) continue;
                if (sub.GetValue("DeviceInstanceID")  is not string devInst) continue;
                if (!string.IsNullOrWhiteSpace(netCfg) && !string.IsNullOrWhiteSpace(devInst))
                    entries.Add(new AdapterNameRules.ClassAdapterEntry(netCfg, devInst));
            }
            catch { /* unreadable subkey — skip, never let one bad key abort resolution */ }
        }
        return entries;
    }

    /// <summary>
    /// Resolves a NIC's InterfaceGuid (from <c>NetworkInterface.Id</c>) to exactly one PnP
    /// <c>DeviceInstanceID</c>, or an abort reason. Read-only. See
    /// <see cref="AdapterNameRules.ResolveDeviceInstanceId"/> for the (pure, unit-tested) matching.
    /// </summary>
    internal static AdapterNameRules.DeviceResolution ResolveDevice(string interfaceGuid)
        => AdapterNameRules.ResolveDeviceInstanceId(interfaceGuid, ReadClassAdapterEntries());

    /// <summary>
    /// Reads the current <c>FriendlyName</c> for a device instance (read-only). Returns
    /// <c>Present=false</c> when the device has no explicit FriendlyName (its description then falls
    /// back to the base <c>DeviceDesc</c>) — the "absent" case the rename must save so Reset never
    /// deletes the value (investigation §5.4), and the case the display path falls back on (issue #32).
    /// </summary>
    internal static (bool Present, string? Value) ReadFriendlyName(string deviceInstanceId)
    {
        using var key = Registry.LocalMachine.OpenSubKey(EnumKeyPrefix + deviceInstanceId);
        if (key?.GetValue("FriendlyName") is string s) return (true, s);
        return (false, null);
    }
}
