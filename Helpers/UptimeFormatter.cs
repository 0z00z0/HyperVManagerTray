using System.Globalization;
using HyperVManagerTray.Models;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// Pure formatting helpers for VM uptime display.  Extracted from DashboardWindow so
/// this logic can be exercised by unit tests without a WinUI runtime dependency.
/// </summary>
public static class UptimeFormatter
{
    /// <summary>
    /// Formats the VM uptime for display on the dashboard card header.
    /// Returns an empty string when the VM is not running or the uptime string is unavailable.
    /// Examples: "47m", "3h 14m", "1d 3h".
    /// </summary>
    public static string Format(VmStatus? s)
    {
        if (s is null || !s.IsRunning || string.IsNullOrWhiteSpace(s.Uptime))
            return string.Empty;

        // Invariant: Uptime is machine text — WmiVmMapper.UptimeString wrote it with
        // TimeSpan.ToString(), whose "c" form is culture-independent, so it must be read back the
        // same way rather than through whatever separators the machine's locale uses.
        if (!TimeSpan.TryParse(s.Uptime, CultureInfo.InvariantCulture, out var ts) || ts < TimeSpan.Zero)
            return string.Empty;

        // Invariant on the way out too: this string is not only the dashboard card header, it is also
        // the payload of the vm_<slug>_uptime sensor, and a payload is a protocol value.
        if (ts.TotalDays >= 1)
            return string.Create(CultureInfo.InvariantCulture, $"{(int)ts.TotalDays}d {ts.Hours}h");
        if (ts.TotalHours >= 1)
            return string.Create(CultureInfo.InvariantCulture, $"{(int)ts.TotalHours}h {ts.Minutes}m");
        return string.Create(CultureInfo.InvariantCulture, $"{(int)ts.TotalMinutes}m");
    }
}
