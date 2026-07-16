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

/// <summary>
/// Result of the rename dialog; <c>NewName</c> is set only for <see cref="RenameDialogChoice.Rename"/>.
/// </summary>
/// <param name="Choice">Which button resolved the dialog.</param>
/// <param name="NewName">The validated new description (Rename only; null for Reset).</param>
/// <param name="RestartNow">
/// Whether the user left the restart checkbox ticked — the consent for the device restart, captured in
/// the same click as the consent for the write (issue #40).
/// </param>
public sealed record RenameDialogResult(RenameDialogChoice Choice, string? NewName, bool RestartNow);

/// <summary>
/// The "rename adapter description" dialog (issue #15) — and, since issue #40, the flow's <b>single
/// consent point</b>.  Shows what is actually being changed (the adapter's DESCRIPTION, not its Windows
/// name), the system-wide-effect warning, the current description, an input for the new one, the
/// device-restart choice with its link-drop consequence, and Reset / Cancel / Rename buttons.
///
/// <para><b>Why everything is here (issue #40).</b> Renaming used to walk the user through up to four
/// stacked dialogs — this window, a From/To confirm, a restart prompt, then an outcome box. A repetitive
/// consent stack trains click-through, which is the opposite of what consent is for on a device-mutating
/// action. So this window now carries the full consequence and the restart decision, and clicking Rename
/// (or Reset) IS the consent: the caller performs the write with no further prompting. Validation errors
/// render inline for the same reason — a message box per rejected keystroke is still a message box.</para>
///
/// <para>Same borderless-Mica pattern as <see cref="TextPromptWindow"/>; validation and uniqueness go
/// through the pure <see cref="AdapterNameRules"/> so the user can correct in place without reopening.
/// Resolves a <see cref="RenameDialogResult"/> (null = cancelled).  This window only gathers intent —
/// the caller performs the actual, consent-gated device write.</para>
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

        // Say plainly WHAT is being renamed. The flow used to mix "description" and "name" mid-flow, and
        // that ambiguity is what made a working rename get reported as broken (#32): the user changed the
        // description and looked for it where Windows shows the connection alias. Name the distinction
        // here, in the vocabulary #42 pins.
        ScopeText.Text =
            "This changes the adapter's DESCRIPTION — the text Device Manager, Hyper-V Manager and " +
            "Windows Settings show for it. It does not change the adapter's Windows name (its connection " +
            "alias, such as \"Ethernet 2\").";

        WarningText.Text =
            "The description is used system-wide, so every one of those places will show the new text.";

        RestartHintText.Text =
            "Drops this adapter's network connection for a few seconds. Untick to apply it later — the " +
            "next adapter restart or PC reboot picks it up.";

        CurrentText.Text = currentDescription;
        InputBox.Text    = currentDescription;
        InputBox.SelectAll();

        ResetBtn.IsEnabled = canReset;
        if (canReset && !string.IsNullOrEmpty(savedOriginalName))
            ToolTipService.SetToolTip(ResetBtn, $"Restore the original description \"{savedOriginalName}\"");

        RenameBtn.Click += (_, _) => Submit();
        ResetBtn.Click  += (_, _) => Complete(new RenameDialogResult(RenameDialogChoice.Reset, null, RestartNow));
        CancelBtn.Click += (_, _) => Complete(null);
        InputBox.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)  { Submit();       e.Handled = true; }
            if (e.Key == VirtualKey.Escape) { Complete(null); e.Handled = true; }
        };
        // Clear a stale error as soon as the user starts correcting it; the verdict is re-computed on
        // Submit, so this only ever hides a message that no longer describes what is in the box.
        InputBox.TextChanged += (_, _) => ClearError();
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

    /// <summary>Whether the user wants the device restarted now (checkbox; defaults ticked).</summary>
    private bool RestartNow => RestartCheck.IsChecked == true;

    /// <summary>
    /// Validates what is in the box and, when it passes, resolves the dialog with the user's consent for
    /// both the write and (per the checkbox) the restart. A rejected name is shown inline — see the type
    /// summary for why this is not a message box.
    /// </summary>
    private void Submit()
    {
        var validation = AdapterNameRules.ValidateNewName(InputBox.Text, _currentDescription, _otherDescriptions);
        if (!validation.IsValid)
        {
            ShowError(validation.Error ?? "Enter a valid description.");
            return;
        }

        Complete(new RenameDialogResult(RenameDialogChoice.Rename, validation.Sanitized, RestartNow));
    }

    private void ShowError(string message)
    {
        ErrorText.Text       = message;
        ErrorText.Visibility = Visibility.Visible;
        FitHeightToContent();                      // the error line is new content — make room for it
        InputBox.Focus(FocusState.Programmatic);
    }

    private void ClearError()
    {
        if (ErrorText.Visibility == Visibility.Collapsed) return;
        ErrorText.Text       = string.Empty;
        ErrorText.Visibility = Visibility.Collapsed;
        FitHeightToContent();
    }

    private void Complete(RenameDialogResult? value)
    {
        _result.TrySetResult(value);
        Close();
    }

    private const double DialogWidth = 360;

    /// <summary>Scale of the monitor the dialog was placed on; captured once so a later resize does not
    /// re-read the CURSOR's monitor (the pointer may have moved to a different-DPI screen by then).</summary>
    private double _scale = 1.0;

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

        Root.Width = DialogWidth;

        var (work, scale) = NativeMethods.GetCursorMonitorMetrics();
        _scale = scale;

        FitHeightToContent();

        var outer = AppWindow.Size;
        AppWindow.Move(new PointInt32(
            work.Left + (work.Right  - work.Left - outer.Width)  / 2,
            work.Top  + (work.Bottom - work.Top  - outer.Height) / 2));
    }

    /// <summary>
    /// Re-measures the content and resizes the client area to fit it, keeping the window's position.
    /// The presenter is non-resizable and has no title bar, so the user cannot fix a clipped dialog
    /// themselves — showing or hiding the inline error must therefore drive the height itself.
    /// </summary>
    private void FitHeightToContent()
    {
        Root.Measure(new Size(DialogWidth, double.PositiveInfinity));

        int cw = (int)Math.Round(DialogWidth * _scale);
        int ch = (int)Math.Round((Root.DesiredSize.Height > 0 ? Root.DesiredSize.Height : 260) * _scale);

        AppWindow.ResizeClient(new SizeInt32(cw, ch));
    }
}
