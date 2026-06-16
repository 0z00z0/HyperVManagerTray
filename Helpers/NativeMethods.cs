using System.Runtime.InteropServices;

namespace HyperVManagerTray.Helpers;

/// <summary>Thin wrappers around Win32 APIs used for DPI-aware popup positioning.</summary>
internal static class NativeMethods
{
    private const uint SPI_GETWORKAREA          = 0x0030;
    private const uint MONITOR_DEFAULTTONEAREST  = 0x0002;
    private const int  MDT_EFFECTIVE_DPI         = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int  cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, out RECT output, uint winIni);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>
    /// Work area (taskbar-excluded desktop bounds, physical pixels) and DPI scale of the monitor
    /// under the cursor — the screen whose tray was just clicked.  Falls back to the primary
    /// monitor at 100 %.
    /// </summary>
    internal static (RECT WorkArea, double Scale) GetCursorMonitorMetrics()
    {
        if (GetCursorPos(out var cursor))
        {
            var monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
            var info    = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };

            if (GetMonitorInfo(monitor, ref info))
            {
                double scale = 1.0;
                if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX != 0)
                    scale = dpiX / 96.0;

                return (info.rcWork, scale);
            }
        }

        return (GetPrimaryWorkArea(), 1.0);
    }

    private static RECT GetPrimaryWorkArea()
    {
        if (SystemParametersInfo(SPI_GETWORKAREA, 0, out var rect, 0))
            return rect;

        return new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1040 };
    }

    // ── Native Win32 dark-mode support ─────────────────────────────────────────
    // uxtheme.dll exposes these only by ordinal (no named exports).
    // SetPreferredAppMode  = ordinal 135  (Win10 1903 / build 18362+)
    // RefreshImmersiveColorPolicyState = ordinal 104
    //
    // Calling SetPreferredAppMode(AllowDark=1) at process startup makes Windows
    // render native Win32 elements (menus, scrollbars) in dark mode whenever the
    // system theme is dark — the same mechanism Electron uses internally.
    // WinUI 3 controls are unaffected (they use their own XAML theming pipeline).
    //
    // Wrapped in try/catch: ordinal layout may differ on future Windows builds.

    [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = false)]
    private static extern int SetPreferredAppMode(int mode);   // 0=Default 1=AllowDark 2=ForceDark 3=ForceLight

    [DllImport("uxtheme.dll", EntryPoint = "#104", SetLastError = false)]
    private static extern void RefreshImmersiveColorPolicyState();

    /// <summary>
    /// Opts the process into Windows dark-mode rendering for native Win32 UI elements
    /// (context menus, etc.).  Call once, before any UI is shown.  No-ops safely on
    /// older Windows builds where the ordinals do not exist.
    /// </summary>
    internal static void EnableDarkModeForNativeUi()
    {
        try
        {
            SetPreferredAppMode(1); // AllowDark — follows the system preference
            RefreshImmersiveColorPolicyState();
        }
        catch { /* ordinal absent on old builds — non-fatal */ }
    }

    // ── Message boxes (WinUI has no built-in MessageBox) ────────────────────────
    private const uint MB_ICONERROR = 0x10, MB_ICONQUESTION = 0x20, MB_ICONWARNING = 0x30,
                       MB_ICONINFORMATION = 0x40, MB_YESNO = 0x4, MB_TOPMOST = 0x40000;
    private const int IDYES = 6;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>Returns the current foreground window HWND; useful for capturing a parent
    /// before ConfigureAwait(false) moves the continuation off the UI thread.</summary>
    internal static IntPtr CaptureHwnd() => GetForegroundWindow();

    /// <summary>Current cursor position in physical screen pixels, or (int.MinValue, int.MinValue) on failure.</summary>
    internal static (int X, int Y) GetCursorPosition()
        => GetCursorPos(out var p) ? (p.X, p.Y) : (int.MinValue, int.MinValue);

    internal static void Info(string text, string caption)
        => MessageBoxW(IntPtr.Zero, text, caption, MB_ICONINFORMATION | MB_TOPMOST);

    internal static void Warn(string text, string caption)
        => MessageBoxW(IntPtr.Zero, text, caption, MB_ICONWARNING | MB_TOPMOST);

    internal static void Error(string text, string caption)
        => MessageBoxW(IntPtr.Zero, text, caption, MB_ICONERROR | MB_TOPMOST);

    /// <summary>Shows a Yes/No prompt; returns true if the user chose Yes.</summary>
    internal static bool Confirm(string text, string caption)
        => MessageBoxW(IntPtr.Zero, text, caption, MB_YESNO | MB_ICONQUESTION | MB_TOPMOST) == IDYES;

    // ── Task Dialog — 3-button update prompt ────────────────────────────────────
    // TaskDialogIndirect supports fully custom button text and an expandable section,
    // making it ideal for the "update available" prompt with inline release notes.
    //
    // CRITICAL: TASKDIALOGCONFIG and TASKDIALOG_BUTTON are declared with 1-byte packing
    // in commctrl.h (they sit inside a #include <pshpack1.h> … <poppack.h> block), so the
    // x64 sizes are 160 and 12 — NOT the 176/16 you'd get from natural 8-byte alignment.
    // Pack=1 reproduces that. If the size/offsets are wrong, TaskDialogIndirect rejects the
    // call with E_INVALIDARG and silently shows nothing (no exception), so "Check for
    // updates" appears to do nothing. (See TaskDialogConfigSize regression test.)

    internal enum UpdateAction { Update, ShowReleases, Cancel }

    private const uint TDF_ALLOW_DIALOG_CANCELLATION = 0x0008;
    private const uint TDF_SIZE_TO_CONTENT           = 0x01000000;
    private const uint TDCBF_CANCEL_BUTTON           = 0x0008;

    // TD_INFORMATION_ICON = MAKEINTRESOURCEW(-3) = (WCHAR*)0xFFFD (resource ID, not HICON)
    private static readonly IntPtr TD_INFORMATION_ICON = new(65533);

    // Field order matches commctrl.h exactly; Pack=1 gives the byte-packed layout the API
    // expects.  Unused fields are left zero by the struct default.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TASKDIALOGCONFIG
    {
        public uint   cbSize;
        public IntPtr hwndParent;
        public IntPtr hInstance;
        public uint   dwFlags;
        public uint   dwCommonButtons;
        public IntPtr pszWindowTitle;
        public IntPtr hMainIcon;             // union: HICON or resource PCWSTR
        public IntPtr pszMainInstruction;
        public IntPtr pszContent;
        public uint   cButtons;
        public IntPtr pButtons;
        public int    nDefaultButton;
        public uint   cRadioButtons;
        public IntPtr pRadioButtons;
        public int    nDefaultRadioButton;
        public IntPtr pszVerificationText;
        public IntPtr pszExpandedInformation;
        public IntPtr pszExpandedControlText;
        public IntPtr pszCollapsedControlText;
        public IntPtr hFooterIcon;
        public IntPtr pszFooter;
        public IntPtr pfCallback;
        public IntPtr lpCallbackData;
        public uint   cxWidth;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TASKDIALOG_BUTTON
    {
        public int    nButtonID;
        public IntPtr pszButtonText;
    }

    /// <summary>x64 marshalled size of TASKDIALOGCONFIG (must be 160); exposed for a regression test.</summary>
    internal static int TaskDialogConfigSize => Marshal.SizeOf<TASKDIALOGCONFIG>();
    /// <summary>x64 marshalled size of TASKDIALOG_BUTTON (must be 12); exposed for a regression test.</summary>
    internal static int TaskDialogButtonSize => Marshal.SizeOf<TASKDIALOG_BUTTON>();

    [DllImport("comctl32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int TaskDialogIndirect(
        ref TASKDIALOGCONFIG pTaskConfig,
        out int              pnButton,
        IntPtr               pnRadioButton,
        IntPtr               pfVerificationFlagChecked);

    /// <summary>
    /// Shows the "update available" Task Dialog with up to three buttons
    /// (Update / Releases page / Cancel) and an expandable release-notes section.
    /// Blocks until the user responds.  Safe to call from any thread.
    /// </summary>
    /// <param name="canDownload">
    /// When <c>true</c> an "Update" button is shown; when <c>false</c> only
    /// "Releases page" is shown (direct download URL was not found in the release assets).
    /// </param>
    internal static UpdateAction ShowUpdateDialog(
        string latestVersion, string runningVersion,
        string releaseNotes,  string appName,
        bool   canDownload,   IntPtr hwndParent = default)
    {
        // Fall back to the current foreground window so the dialog is never orphaned
        // behind an always-on-top window when no explicit parent is supplied (e.g. tray menu).
        if (hwndParent == IntPtr.Zero)
            hwndParent = GetForegroundWindow();

        // Collect all unmanaged string allocations so we can free them in one pass.
        var strings = new List<IntPtr>(12);
        IntPtr Str(string? s)
        {
            if (s is null) return IntPtr.Zero;
            var p = Marshal.StringToHGlobalUni(s);
            strings.Add(p);
            return p;
        }

        const int BtnUpdate   = 100;
        const int BtnReleases = 101;
        int   btnCount  = canDownload ? 2 : 1;
        int   btnSize   = Marshal.SizeOf<TASKDIALOG_BUTTON>();
        var   pButtons  = Marshal.AllocHGlobal(btnSize * btnCount);
        try
        {
            if (canDownload)
            {
                Marshal.StructureToPtr(
                    new TASKDIALOG_BUTTON { nButtonID = BtnUpdate,   pszButtonText = Str("Update") },
                    pButtons, false);
                Marshal.StructureToPtr(
                    new TASKDIALOG_BUTTON { nButtonID = BtnReleases, pszButtonText = Str("Releases page") },
                    IntPtr.Add(pButtons, btnSize), false);
            }
            else
            {
                Marshal.StructureToPtr(
                    new TASKDIALOG_BUTTON { nButtonID = BtnReleases, pszButtonText = Str("Releases page") },
                    pButtons, false);
            }

            var hasNotes = !string.IsNullOrWhiteSpace(releaseNotes);
            var config   = new TASKDIALOGCONFIG
            {
                cbSize                  = (uint)Marshal.SizeOf<TASKDIALOGCONFIG>(),
                hwndParent              = hwndParent,
                dwFlags                 = TDF_ALLOW_DIALOG_CANCELLATION | TDF_SIZE_TO_CONTENT,
                dwCommonButtons         = TDCBF_CANCEL_BUTTON,
                pszWindowTitle          = Str(appName),
                hMainIcon               = TD_INFORMATION_ICON,
                pszMainInstruction      = Str($"Version {latestVersion} is available"),
                pszContent              = Str($"You are running version {runningVersion}."),
                cButtons                = (uint)btnCount,
                pButtons                = pButtons,
                nDefaultButton          = canDownload ? BtnUpdate : BtnReleases,
                pszExpandedInformation  = Str(hasNotes ? releaseNotes : "No release notes provided."),
                pszCollapsedControlText = Str("Show release notes"),
                pszExpandedControlText  = Str("Hide release notes"),
            };

            int hr = TaskDialogIndirect(ref config, out int nButton, IntPtr.Zero, IntPtr.Zero);
            if (hr != 0)   // S_OK = 0; any failure means no dialog was shown
            {
                // Degrade gracefully instead of silently doing nothing: a plain message box
                // still lets the user reach the download.
                var pick = MessageBoxW(hwndParent,
                    $"Version {latestVersion} is available (you have {runningVersion}).\n\n" +
                    "Open the releases page to download it?",
                    appName, MB_YESNO | MB_ICONINFORMATION | MB_TOPMOST);
                return pick == IDYES ? UpdateAction.ShowReleases : UpdateAction.Cancel;
            }

            return nButton switch
            {
                BtnUpdate   => UpdateAction.Update,
                BtnReleases => UpdateAction.ShowReleases,
                _           => UpdateAction.Cancel,
            };
        }
        finally
        {
            foreach (var p in strings) Marshal.FreeHGlobal(p);
            Marshal.FreeHGlobal(pButtons);
        }
    }
}
