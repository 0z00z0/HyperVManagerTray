using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// How this process was started and how long it waited before application code ran (issue #90). The
/// parent separates a logon-task start from a start by hand, and the wait is the share of a slow start
/// no application code can account for.
/// </summary>
internal static class StartupOrigin
{
    /// <summary>The startup line. Never throws: a value that cannot be read is named as such, so a
    /// failed read costs one word of the line rather than the startup.</summary>
    /// <param name="reachedAt">When application startup code was entered, read as early as possible.</param>
    public static string Line(DateTime reachedAt)
    {
        string parent;
        try { parent = ParentName(); }
        catch { parent = "an unreadable parent"; }

        string wait;
        try
        {
            using var self = Process.GetCurrentProcess();
            wait = LatencyLog.FormatMs((reachedAt - self.StartTime).TotalMilliseconds);
        }
        catch { wait = "an unreadable time"; }

        return $"Startup: started by {parent}; application code reached {wait} after process creation.";
    }

    private static string ParentName()
    {
        using var self = Process.GetCurrentProcess();
        int status = NtQueryInformationProcess(
            self.Handle, ProcessBasicInformationClass, out var info,
            Marshal.SizeOf<ProcessBasicInformation>(), out _);
        if (status != 0) return "an unreadable parent";

        Process parent;
        try { parent = Process.GetProcessById((int)info.InheritedFromUniqueProcessId); }
        catch (ArgumentException) { return "a process that has already exited"; }

        using (parent)
        {
            // A parent's id is reused once it exits, so a process younger than this one is not the parent.
            try { if (parent.StartTime > self.StartTime) return "a process that has already exited"; }
            catch (Win32Exception) { /* start time withheld from this token; the name is still the best reading */ }
            return parent.ProcessName;
        }
    }

    private const int ProcessBasicInformationClass = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle, int processInformationClass, out ProcessBasicInformation processInformation,
        int processInformationLength, out int returnLength);
}
