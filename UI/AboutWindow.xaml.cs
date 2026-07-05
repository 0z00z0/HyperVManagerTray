using System.Reflection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.Graphics;
using HyperVManagerTray.Helpers;
using HyperVManagerTray.Services;

namespace HyperVManagerTray.UI;

public sealed partial class AboutWindow : Window
{
    private const string GitHubUrl = "https://github.com/0z00z0/HyperVManagerTray";
    private const string BmacUrl   = "https://buymeacoffee.com/ezpl";

    private readonly UpdateChecker _updateChecker;

    internal AboutWindow(UpdateChecker updateChecker)
    {
        _updateChecker = updateChecker;
        InitializeComponent();
        ConfigureChrome();

        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = ver is not null ? $"v{ver.ToString(3)}" : "v—";

        CloseBtn.Click  += (_, _) => Close();
        GitHubBtn.Click += (_, _) => Open(GitHubUrl);
        UpdateBtn.Click += (_, _) => _ = CheckForUpdatesAsync();
        BmacBtn.Click   += (_, _) => Open(BmacUrl);
    }

    private void ConfigureChrome()
    {
        AppWindow.IsShownInSwitchers = false;

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        presenter.IsResizable   = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);

        Root.Width = 320;
        Root.Measure(new Size(320, double.PositiveInfinity));

        var (work, scale) = NativeMethods.GetCursorMonitorMetrics();
        int cw = (int)Math.Round(320 * scale);
        int ch = (int)Math.Round((Root.DesiredSize.Height > 0 ? Root.DesiredSize.Height : 270) * scale);

        // ResizeClient sizes the CLIENT area (not the outer window), so the 320-DIP content fills
        // it exactly — sizing the outer window instead left the border eating into the client
        // area and clipping the right-hand buttons. Centre using the resulting outer size.
        AppWindow.ResizeClient(new SizeInt32(cw, ch));
        var outer = AppWindow.Size;
        AppWindow.Move(new PointInt32(
            work.Left + (work.Right  - work.Left - outer.Width)  / 2,
            work.Top  + (work.Bottom - work.Top  - outer.Height) / 2));
    }

    private Task CheckForUpdatesAsync()
    {
        // Capture the parent HWND first so the update dialog appears in front of this
        // always-on-top window. The shared flow must stay on the UI thread (comctl32 v6
        // activation context) — do not add ConfigureAwait(false) around this call.
        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        return UpdatePrompt.RunAsync(_updateChecker, hwnd);
    }

    private static void Open(string url) => Shell.Open(url);
}
