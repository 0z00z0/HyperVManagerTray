using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using HyperVManagerTray.Helpers;

namespace HyperVManagerTray.UI;

/// <summary>
/// A small borderless prompt for a single line of text. WinUI has no built-in modal input box
/// (Win32 MessageBoxW has no edit-field variant, and ContentDialog needs an XamlRoot this tray
/// app's windows don't reliably have at the moment a tray-menu action fires) — same independent-
/// Window pattern as <see cref="DashboardWindow"/>, just resolving a
/// <see cref="TaskCompletionSource{TResult}"/> instead of firing events.
/// </summary>
public sealed partial class TextPromptWindow : Window
{
    private readonly TaskCompletionSource<string?> _result = new();

    private TextPromptWindow(string title, string message, string defaultValue)
    {
        InitializeComponent();
        ConfigureChrome();

        TitleText.Text   = title;
        MessageText.Text = message;
        InputBox.Text    = defaultValue;
        InputBox.SelectAll();

        OkBtn.Click      += (_, _) => Submit();
        CancelBtn.Click  += (_, _) => Complete(null);
        InputBox.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)  { Submit();       e.Handled = true; }
            if (e.Key == VirtualKey.Escape) { Complete(null); e.Handled = true; }
        };
        // Alt+F4 / system close without a button press — treat as Cancel. TrySetResult is a no-op
        // if Submit()/Complete() already resolved it.
        Closed += (_, _) => _result.TrySetResult(null);
    }

    /// <summary>Shows the prompt near the cursor. Returns the trimmed text, or null if cancelled.</summary>
    public static Task<string?> ShowAsync(string title, string message, string defaultValue)
    {
        var window = new TextPromptWindow(title, message, defaultValue);
        window.Activate();
        window.InputBox.Focus(FocusState.Programmatic);
        return window._result.Task;
    }

    private void Submit()
    {
        var text = InputBox.Text.Trim();
        if (text.Length == 0)
        {
            NativeMethods.Warn("Enter a name.", TitleText.Text);
            return;
        }
        Complete(text);
    }

    private void Complete(string? value)
    {
        _result.TrySetResult(value);
        Close();
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

        Root.Width = 300;
        Root.Measure(new Size(300, double.PositiveInfinity));

        var (work, scale) = NativeMethods.GetCursorMonitorMetrics();
        int cw = (int)Math.Round(300 * scale);
        int ch = (int)Math.Round((Root.DesiredSize.Height > 0 ? Root.DesiredSize.Height : 160) * scale);

        AppWindow.ResizeClient(new SizeInt32(cw, ch));
        var outer = AppWindow.Size;
        AppWindow.Move(new PointInt32(
            work.Left + (work.Right  - work.Left - outer.Width)  / 2,
            work.Top  + (work.Bottom - work.Top  - outer.Height) / 2));
    }
}
