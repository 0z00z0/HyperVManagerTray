using Microsoft.Win32;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// Self-registers Windows Error Reporting "LocalDumps" for this exe so that a NATIVE crash —
/// an access violation in GDI+, comctl32, the WinUI/Mica compositor, etc. — produces a usermode
/// minidump on disk.  Those faults bypass .NET's managed exception handlers entirely (nothing
/// reaches AppDomain.UnhandledException, so crash.log stays empty), which otherwise leaves a
/// vanished tray icon and no trace.  The dump names the faulting module, the thread stacks, and —
/// because it carries process memory — the managed exception object behind a stowed WinRT failure.
///
/// Writing under HKLM needs admin; the app is requireAdministrator, so this succeeds at startup.
/// Entirely best-effort — any failure (e.g. a non-elevated run) is swallowed.
/// Docs: https://learn.microsoft.com/windows/win32/wer/collecting-user-mode-dumps
/// </summary>
internal static class CrashDumps
{
    private const string ExeName = "HyperVManagerTray.exe";
    private const string LocalDumpsKey =
        @"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\" + ExeName;

    /// <summary>Registers a crash dump for this exe into <paramref name="dumpDir"/>. Never throws.</summary>
    internal static void TryRegisterLocalDumps(string dumpDir)
    {
        try
        {
            Directory.CreateDirectory(dumpDir);
            using var key = Registry.LocalMachine.CreateSubKey(LocalDumpsKey);
            if (key is null) return;
            key.SetValue("DumpFolder", dumpDir, RegistryValueKind.ExpandString);
            key.SetValue("DumpCount",  5, RegistryValueKind.DWord);
            // 2 = full, not 1 = mini: a mini carries thread stacks but no heap, so the type and
            // message of a stowed WinRT exception are simply absent from it and the crash cannot be
            // diagnosed. Trade-off: a full dump is an order of magnitude larger on disk, and
            // DumpCount above keeps five of them.
            key.SetValue("DumpType",   2, RegistryValueKind.DWord);
        }
        catch { /* needs admin / policy-restricted — best-effort only */ }
    }
}
