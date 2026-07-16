using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// Device-MUTATING I/O for the "rename network adapter" feature (issue #15): writes the device
/// <c>FriendlyName</c> — the string Windows surfaces as the adapter *description* in Device Manager,
/// Hyper-V Manager, Windows Settings and the NDIS <c>InterfaceDescription</c> — and restarts the
/// device so the change propagates.
///
/// <para><b>NOT NetworkInterface.Description (issue #32).</b> An earlier version of this summary also
/// claimed <c>FriendlyName</c> drives .NET's <c>NetworkInterface.Description</c>. It does not: that
/// property comes from the IP Helper API and is derived from the driver's <c>DeviceDesc</c> plus a
/// <c>#N</c> dedupe suffix, so a FriendlyName write never changes it. Because the app displayed that
/// property, renames worked yet stayed invisible in the app's own UI. Display sites now resolve the
/// FriendlyName through <c>AdapterMatcher</c>'s resolver, built on
/// <see cref="AdapterDeviceRegistry.ReadFriendlyName"/>.</para>
///
/// <para><b>Read-only lookups live elsewhere.</b> Device resolution and the FriendlyName read moved to
/// <see cref="AdapterDeviceRegistry"/> (issue #32) so the display path — and the test assembly that
/// links it — can use them without this device-mutating file coming along.</para>
///
/// <para><b>SAFETY.</b> <see cref="WriteFriendlyName"/> and <see cref="RestartDevice"/> mutate a real
/// network device. The write is a parameterized <see cref="Microsoft.Win32.Registry"/> <c>REG_SZ</c>
/// set and the restart goes through parameterized SetupAPI calls — <b>never</b> a shell/PowerShell
/// command string — so a crafted name cannot inject code into this elevated process (investigation
/// §3, §5.3). The write targets exactly one value (<c>FriendlyName</c>) on one resolved device's Enum
/// key; it never touches <c>DeviceDesc</c> or anything under the Class key. Callers MUST gate both
/// mutating methods behind explicit user consent. Every member of this type mutates the device.</para>
///
/// <para><b>#15 revert fix.</b> The original write used
/// <c>SetupDiSetDeviceRegistryProperty(SPDRP_FRIENDLYNAME)</c> declared without
/// <c>CharSet.Unicode</c>, so the P/Invoke bound to the <b>ANSI</b> entry point
/// (<c>…PropertyA</c>) while being fed a UTF-16 byte buffer — the value never landed correctly, yet
/// the API returned success and the app reported success. The write is now a direct, verified
/// registry set: it re-reads the value from disk and throws unless it matches, so a silent no-op can
/// never again be reported as success.</para>
/// </summary>
internal static class AdapterRenamer
{
    // Network adapter setup class (investigation §1/§2).
    private static readonly Guid NetClassGuid = new("4d36e972-e325-11ce-bfc1-08002be10318");

    // ── Device-mutating operations (gate behind explicit consent) ────────────────

    /// <summary>
    /// ★ DEVICE-MUTATING ★ Writes the <c>FriendlyName</c> <c>REG_SZ</c> on the resolved device's Enum
    /// key — the single value that changes the adapter description system-wide. This is a direct,
    /// parameterized <see cref="Microsoft.Win32.Registry"/> write (Administrators have FullControl on
    /// this key and the app is elevated — investigation §2); it builds no shell string, so a crafted
    /// name cannot inject code. After writing it <b>re-reads the value from disk and throws unless it
    /// matches</b>, so the #15 "silent no-op reported as success" failure can never recur. Throws on
    /// any failure. Must never be called without explicit user consent.
    /// </summary>
    internal static void WriteFriendlyName(string deviceInstanceId, string friendlyName)
    {
        var keyPath = AdapterDeviceRegistry.EnumKeyPrefix + deviceInstanceId;

        // OpenSubKey(writable:true) throws SecurityException/UnauthorizedAccessException if the ACL
        // denies write, and returns null if the device key is missing — both surface as a visible
        // error to the caller rather than a false success.
        using (var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: true))
        {
            if (key is null)
                throw new InvalidOperationException(
                    $"The device's registry key was not found (device not present?):\r\nHKLM\\{keyPath}");

            key.SetValue("FriendlyName", friendlyName, RegistryValueKind.String);
            key.Flush();
        }

        // Read-back verification: the write only counts as a success if the value is actually on disk.
        var (present, written) = AdapterDeviceRegistry.ReadFriendlyName(deviceInstanceId);
        if (!AdapterNameRules.FriendlyNameApplied(present, written, friendlyName))
            throw new InvalidOperationException(
                "The new adapter name did not persist to the registry. On disk the FriendlyName is " +
                (present ? $"\"{written}\"" : "still absent") +
                $" but \"{friendlyName}\" was expected. No change was applied.");

        AppInfo.AppendCrashLogLine("AdapterRename",
            $"Set FriendlyName of {deviceInstanceId} to \"{friendlyName}\" (verified on disk).");
    }

    /// <summary>
    /// ★ DEVICE-MUTATING ★ Disables then re-enables the device (<c>DIF_PROPERTYCHANGE</c> /
    /// <c>DICS_DISABLE</c> → <c>DICS_ENABLE</c>) so NDIS re-reads the FriendlyName and the new
    /// description appears everywhere. This briefly drops the adapter's link. Throws on failure.
    /// Must never be called without explicit user consent.
    /// </summary>
    internal static void RestartDevice(string deviceInstanceId)
    {
        var classGuid = NetClassGuid;
        var set = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
        if (set == InvalidHandle)
            throw new InvalidOperationException(
                $"SetupDiCreateDeviceInfoList failed (0x{Marshal.GetLastWin32Error():X8}).");

        try
        {
            var devInfo = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiOpenDeviceInfo(set, deviceInstanceId, IntPtr.Zero, 0, ref devInfo))
                throw new InvalidOperationException(
                    $"Device \"{deviceInstanceId}\" could not be opened (0x{Marshal.GetLastWin32Error():X8}).");

            ChangeState(set, ref devInfo, DICS_DISABLE);
            ChangeState(set, ref devInfo, DICS_ENABLE);
            AppInfo.AppendCrashLogLine("AdapterRename", $"Restarted device {deviceInstanceId}.");
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    private static void ChangeState(IntPtr set, ref SP_DEVINFO_DATA devInfo, uint stateChange)
    {
        var pcp = new SP_PROPCHANGE_PARAMS
        {
            ClassInstallHeader = new SP_CLASSINSTALL_HEADER
            {
                cbSize          = (uint)Marshal.SizeOf<SP_CLASSINSTALL_HEADER>(),
                InstallFunction = DIF_PROPERTYCHANGE,
            },
            StateChange = stateChange,
            Scope       = DICS_FLAG_GLOBAL,
            HwProfile   = 0,
        };

        if (!SetupDiSetClassInstallParams(set, ref devInfo, ref pcp, (uint)Marshal.SizeOf<SP_PROPCHANGE_PARAMS>()))
            throw new InvalidOperationException(
                $"SetupDiSetClassInstallParams failed (0x{Marshal.GetLastWin32Error():X8}).");

        if (!SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, set, ref devInfo))
            throw new InvalidOperationException(
                $"SetupDiCallClassInstaller failed (0x{Marshal.GetLastWin32Error():X8}).");
    }

    // ── SetupAPI P/Invoke ────────────────────────────────────────────────────────

    private static readonly IntPtr InvalidHandle = new(-1);

    private const uint DIF_PROPERTYCHANGE = 0x00000012;
    private const uint DICS_ENABLE        = 0x00000001;
    private const uint DICS_DISABLE       = 0x00000002;
    private const uint DICS_FLAG_GLOBAL   = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint   cbSize;
        public Guid   ClassGuid;
        public uint   DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_CLASSINSTALL_HEADER
    {
        public uint cbSize;
        public uint InstallFunction;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_PROPCHANGE_PARAMS
    {
        public SP_CLASSINSTALL_HEADER ClassInstallHeader;
        public uint StateChange;
        public uint Scope;
        public uint HwProfile;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid ClassGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiOpenDeviceInfo(
        IntPtr DeviceInfoSet, string DeviceInstanceId, IntPtr hwndParent, uint Flags,
        ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiSetClassInstallParams(
        IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
        ref SP_PROPCHANGE_PARAMS ClassInstallParams, uint ClassInstallParamsSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(
        uint InstallFunction, IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);
}
