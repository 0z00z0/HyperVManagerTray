using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using HyperVManagerTray.Helpers;

namespace HyperVManagerTray.UI;

/// <summary>
/// The responsive layout of a single settings card: a text block (header + description) beside its
/// control, which drops to <i>beneath</i> the text as soon as the row is too narrow to give the text
/// its <see cref="WindowPlacement.SettingRowTextMinWidth"/> floor (issue #31 (3)).
///
/// <para><b>Why a custom panel and not a Grid.</b> The row used to be a 2-column Grid — column 0
/// <c>Star</c> (text), column 1 <c>Auto</c> (control). An <c>Auto</c> column always takes its natural
/// width, so as the window narrowed the <c>Star</c> column was starved toward zero and the description
/// wrapped one character per line — a vertical column of letters. No arrangement of Grid lengths fixes
/// that, because the decision the row actually needs ("is there room for both side by side?") depends
/// on the control's <i>measured</i> width, which only exists during a measure pass.</para>
///
/// <para><b>Why it can't regress.</b> The decision (<see cref="WindowPlacement.ShouldStackSettingRow"/>,
/// pure and unit-tested) is evaluated on every measure against real measured DIP widths. It therefore
/// holds at any DPI, under any font (issue #44's wider mono face included), and for any label length —
/// unlike a minimum window size, which only papers over the symptom at one scale and one font. The
/// minimum window size is a separate, complementary guard: it stops the window shrinking to where even
/// a stacked row would be unreadable.</para>
/// </summary>
internal sealed class SettingRowPanel : Panel
{
    // Set by MeasureOverride and consumed by the ArrangeOverride that always follows it — the standard
    // WinUI panel contract (arrange is never invoked without a preceding measure at the same size).
    private bool _stacked;

    /// <summary>Gap between the text and the control — horizontal when side by side, vertical when stacked.</summary>
    private const double Spacing = WindowPlacement.SettingRowColumnSpacing;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count != 2) return base.MeasureOverride(availableSize);

        var text    = Children[0];
        var control = Children[1];

        // The control's natural width — measured unconstrained, so it reflects whatever the control
        // really needs (its MinWidth, or wider content) rather than an assumption baked into a constant.
        control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double controlWidth = control.DesiredSize.Width;

        double available = availableSize.Width;
        _stacked = WindowPlacement.ShouldStackSettingRow(available, controlWidth);

        if (_stacked)
        {
            // Both get the full width. Re-measuring the control constrained lets it shrink into the row
            // (a ComboBox ellipsises its content) instead of overflowing the card.
            text.Measure(new Size(available, double.PositiveInfinity));
            control.Measure(new Size(available, double.PositiveInfinity));
            return new Size(
                Math.Max(text.DesiredSize.Width, control.DesiredSize.Width),
                text.DesiredSize.Height + Spacing + control.DesiredSize.Height);
        }

        // Side by side: the control keeps its natural width, the text gets the remainder — which
        // ShouldStackSettingRow has just guaranteed is at least SettingRowTextMinWidth.
        bool unbounded = double.IsInfinity(available);
        double textWidth = unbounded ? double.PositiveInfinity : Math.Max(0, available - Spacing - controlWidth);
        text.Measure(new Size(textWidth, double.PositiveInfinity));

        return new Size(
            // Fill the row when we know its width (what the old Star column did); fall back to the
            // natural total when measured unbounded, so we never return an infinite desired size.
            unbounded ? text.DesiredSize.Width + Spacing + controlWidth : available,
            Math.Max(text.DesiredSize.Height, control.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count != 2) return base.ArrangeOverride(finalSize);

        var text    = Children[0];
        var control = Children[1];

        if (_stacked)
        {
            double textHeight = text.DesiredSize.Height;
            text.Arrange(new Rect(0, 0, finalSize.Width, textHeight));
            control.Arrange(new Rect(
                0, textHeight + Spacing,
                Math.Min(control.DesiredSize.Width, finalSize.Width),
                control.DesiredSize.Height));
            return finalSize;
        }

        double controlWidth = Math.Min(control.DesiredSize.Width, finalSize.Width);
        double textWidth    = Math.Max(0, finalSize.Width - Spacing - controlWidth);

        // Vertically centred against each other, matching the row's previous look.
        text.Arrange(new Rect(0, Centred(finalSize.Height, text.DesiredSize.Height), textWidth, text.DesiredSize.Height));
        control.Arrange(new Rect(
            finalSize.Width - controlWidth, Centred(finalSize.Height, control.DesiredSize.Height),
            controlWidth, control.DesiredSize.Height));
        return finalSize;

        static double Centred(double outer, double inner) => Math.Max(0, (outer - inner) / 2);
    }
}
