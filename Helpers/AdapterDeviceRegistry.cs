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
    /// <c>DeviceInstanceID</c>, or an abort reason, <b>always from a fresh walk of the Class key</b>.
    /// Read-only. See <see cref="AdapterNameRules.ResolveDeviceInstanceId"/> for the (pure, unit-tested)
    /// matching.
    ///
    /// <para><b>Use this for anything you will WRITE to; use <see cref="ClassEntryCache.Resolve"/> for
    /// display.</b> The two compute the identical <see cref="AdapterNameRules.DeviceResolution"/> and
    /// differ in exactly one respect — the cache may answer from a walk taken earlier — which is why the
    /// rule for choosing has to be written down rather than inferred. A cached hit can name a
    /// <c>DeviceInstanceID</c> the host has since stopped using (an adapter re-installed under a new Enum
    /// key), and the rename flow hands this result straight to
    /// <c>AdapterRenamer.WriteFriendlyName</c> — a device-mutating registry write. Stale is a cosmetic
    /// wobble for one; for the other it is a write aimed at the wrong device. So the walk here is not an
    /// oversight to be optimised away with "the cache already has this": it is the difference between the
    /// two, and it is the whole reason both exist.</para>
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

    /// <summary>
    /// Reads the raw <c>DeviceDesc</c> for a device instance (read-only). Unlike
    /// <see cref="ReadFriendlyName"/>, this value is written by the driver's INF and is NEVER touched
    /// by the rename — it is the factory description's ground truth (issue #33). The value may be in
    /// INF-indirect form (<c>@oem241.inf,%rtl8153.devicedesc%;Realtek USB GbE Family Controller</c>) or
    /// a plain literal; <see cref="AdapterNameRules.ParseFactoryDescription"/> does the (pure) parse.
    /// </summary>
    internal static (bool Present, string? Value) ReadDeviceDesc(string deviceInstanceId)
    {
        using var key = Registry.LocalMachine.OpenSubKey(EnumKeyPrefix + deviceInstanceId);
        if (key?.GetValue("DeviceDesc") is string s) return (true, s);
        return (false, null);
    }

    /// <summary>
    /// The device's factory description — <see cref="ReadDeviceDesc"/> parsed by
    /// <see cref="AdapterNameRules.ParseFactoryDescription"/> — or null when it is absent, unreadable,
    /// or not parseable into a usable literal. Read-only and total: an unreadable key (missing device,
    /// access denied, hive fault) yields null rather than an exception, so callers degrade to their own
    /// fallback instead of failing the rename (issue #33).
    /// </summary>
    internal static string? ReadFactoryDescription(string deviceInstanceId)
    {
        try
        {
            return AdapterNameRules.ParseFactoryDescription(ReadDeviceDesc(deviceInstanceId).Value);
        }
        catch
        {
            return null;   // unreadable → caller falls back; never let ground-truth lookup throw
        }
    }
}
