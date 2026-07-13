using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using HyperVManagerTray.Helpers;

namespace HyperVManagerTray.UI;

/// <summary>Which button resolved the <see cref="RenameAdapterWindow"/>.</summary>
public enum RenameDialogChoice
{
    /// <summary>User confirmed a new, validated, unique name (in <see cref="RenameDialogResult.NewName"/>).</summary>
    Rename,

    /// <summary>User asked to restore the saved original name.</summary>
    Reset,
}

/// <summary>Result of the rename dialog; <c>NewName</c> is set only for <see cref="RenameDialogChoice.Rename"/>.</summary>
public sealed record RenameDialogResult(RenameDialogChoice Choice, string? NewName);

/// <summary>
/// The "rename network adapter" dialog (issue #15): shows the current description, a warning about
/// the system-wide effect and the brief link drop, an input for the new name, and Reset / Cancel /
/// Rename buttons.  Same borderless-Mica pattern as <see cref="TextPromptWindow"/>, but validates and
/// enforces uniqueness (via <see cref="AdapterNameRules"/>) in-place so the user can correct without
/// reopening.  Resolves a <see cref="RenameDialogResult"/> (null = cancelled).  This window only
/// gathers intent — the caller performs the actual, consent-gated device write.
/// </summary>
public sealed partial class RenameAdapterWindow : Window
{
    private readonly TaskCompletionSource<RenameDialogResult?> _result = new();
    private readonly string _currentDescription;
    private readonly IReadOnlyList<string> _otherDescriptions;

    private RenameAdapterWindow(
        string currentDescription,
        IReadOnlyList<string> otherDescriptions,
        string? savedOriginalName,
        bool canReset)
    {
        InitializeComponent();

        _currentDescription = currentDescription;
        _otherDescriptions  = otherDescriptions;

        WarningText.Text =
            "Renaming changes this adapter's description system-wide — Device Manager, Hyper-V Manager " +
            "and Windows Settings all use it. To take effect everywhere the adapter may need to be " +
            "disabled/enabled or the PC restarted, which will briefly drop this adapter's network connection.";
        CurrentText.Text = currentDescription;
        InputBox.Text    = currentDescription;
        InputBox.SelectAll();

        ResetBtn.IsEnabled = canReset;
        if (canReset && !string.IsNullOrEmpty(savedOriginalName))
            ToolTipService.SetToolTip(ResetBtn, $"Restore the original name \"{savedOriginalName}\"");

        RenameBtn.Click += (_, _) => Submit();
        ResetBtn.Click  += (_, _) => Complete(new RenameDialogResult(RenameDialogChoice.Reset, null));
        CancelBtn.Click += (_, _) => Complete(null);
        InputBox.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)  { Submit();       e.Handled = true; }
            if (e.Key == VirtualKey.Escape) { Complete(null); e.Handled = true; }
        };
        // Alt+F4 / system close without a button — treat as Cancel. TrySetResult is a no-op if a
        // button already resolved it.
        Closed += (_, _) => _result.TrySetResult(null);

        ConfigureChrome();
    }

    /// <summary>
    /// Shows the dialog centered on the cursor's monitor and returns the user's choice, or null if
    /// cancelled.  <paramref name="otherDescriptions"/> is every *other* adapter's description (for the
    /// uniqueness check); <paramref name="savedOriginalName"/>/<paramref name="canReset"/> enable Reset.
    /// </summary>
    public static Task<RenameDialogResult?> ShowAsync(
        string currentDescription,
        IReadOnlyList<string> otherDescriptions,
        string? savedOriginalName,
        bool canReset)
    {
        var window = new RenameAdapterWindow(currentDescription, otherDescriptions, savedOriginalName, canReset);
        window.Activate();
        window.InputBox.Focus(FocusState.Programmatic);
        return window._result.Task;
    }

    private void Submit()
    {
        var validation = AdapterNameRules.ValidateName(InputBox.Text);
        if (!validation.IsValid)
        {
            NativeMethods.Warn(validation.Error ?? "Enter a valid name.", TitleText.Text);
            return;
        }

        if (string.Equals(validation.Sanitized, _currentDescription, StringComparison.Ordinal))
        {
            NativeMethods.Info("That is already the adapter's name — nothing to change.", TitleText.Text);
            return;
        }

        if (!AdapterNameRules.IsNameUnique(validation.Sanitized, _otherDescriptions))
        {
            NativeMethods.Warn(
                "Another network adapter already has that description. Choose a unique name.",
                TitleText.Text);
            return;
        }

        Complete(new RenameDialogResult(RenameDialogChoice.Rename, validation.Sanitized));
    }

    private void Complete(RenameDialogResult? value)
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

        const double width = 360;
        Root.Width = width;
        Root.Measure(new Size(width, double.PositiveInfinity));

        var (work, scale) = NativeMethods.GetCursorMonitorMetrics();
        int cw = (int)Math.Round(width * scale);
        int ch = (int)Math.Round((Root.DesiredSize.Height > 0 ? Root.DesiredSize.Height : 260) * scale);

        AppWindow.ResizeClient(new SizeInt32(cw, ch));
        var outer = AppWindow.Size;
        AppWindow.Move(new PointInt32(
            work.Left + (work.Right  - work.Left - outer.Width)  / 2,
            work.Top  + (work.Bottom - work.Top  - outer.Height) / 2));
    }
}
