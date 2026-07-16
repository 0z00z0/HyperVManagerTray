using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using HyperVManagerTray.Helpers;

namespace HyperVManagerTray.UI;

/// <summary>
/// A horizontal run of controls that WRAPS onto further lines when the row is too narrow for it, used
/// as a settings row's control wherever that control is several controls (the Override row's two combos
/// + Apply, the Maintenance rows' buttons, a VM card's action + delay).
///
/// <para><b>What this replaces, and why it isn't a StackPanel.</b> Those rows each held a horizontal
/// <see cref="StackPanel"/>, which has no narrow behaviour at all: it measures its children unconstrained
/// and reports their SUM however little room it is given, and arranging it smaller simply pushes the
/// right-hand children outside the arranged rectangle. Inside <see cref="SettingRowPanel"/> that meant
/// the Override row's switch combo and Apply button were CLIPPED — invisibly and unreachably, because
/// <c>RootScroll</c> disables horizontal scrolling — at every window width from the enforced minimum up
/// to roughly 890 DIP. The override was unusable at the window's own minimum size.</para>
///
/// <para><b>Why this is the right layer to fix it.</b> Issue #31's design has two guards: a per-row
/// stack decision (<see cref="WindowPlacement.ShouldStackSettingRow"/>, measured, so it holds at any DPI
/// and font) and a minimum window width (a constant floor). The stack decision assumed a control can
/// shrink into the width it is handed — <see cref="SettingRowPanel"/> says as much: "a ComboBox
/// ellipsises its content". True of a ComboBox; false of a horizontal StackPanel, which is what these
/// rows actually pass it. Raising the width constant cannot rescue them either: covering the Maintenance
/// row's four buttons (~640 DIP natural) would force a ~1050 DIP minimum window. A control that genuinely
/// shrinks is what the design already assumed it had, so this supplies one — and it holds at any width,
/// which no constant does.</para>
///
/// <para>Consequently a row using this strip contributes only its WIDEST SINGLE CHILD to the window's
/// minimum-width derivation, not the sum of its children. See
/// <see cref="WindowPlacement.SettingRowControlMinimums"/>.</para>
///
/// <para>When everything fits on one line the layout is identical to the horizontal StackPanel this
/// replaces — same order, same spacing — so nothing changes at the default window size.</para>
/// </summary>
internal sealed class ControlStrip : Panel
{
    /// <summary>Gap between children, horizontally and between wrapped lines. Matches the 8 DIP the
    /// StackPanels these replace used.</summary>
    public double Spacing { get; set; } = 8;

    protected override Size MeasureOverride(Size availableSize)
    {
        // Every child is measured unconstrained: each one's natural size is what decides where the line
        // breaks fall, exactly as SettingRowPanel measures ITS control unconstrained to decide stacking.
        foreach (var child in Children)
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var lines = WindowPlacement.WrapStrip(
            [.. Children.Select(c => c.DesiredSize.Width)], availableSize.Width, Spacing);

        double width = 0, height = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            width  = Math.Max(width, LineWidth(line));
            height += LineHeight(line) + (i > 0 ? Spacing : 0);
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // Re-derive the line breaks against the FINAL width rather than reusing the measure pass's: a
        // panel can be arranged at a width it was not measured at, and a stale break would put a child
        // back outside the row — the very failure this class exists to prevent.
        var lines = WindowPlacement.WrapStrip(
            [.. Children.Select(c => c.DesiredSize.Width)], finalSize.Width, Spacing);

        double y = 0;
        foreach (var line in lines)
        {
            double x = 0;
            double lineHeight = LineHeight(line);
            foreach (int index in line)
            {
                var child = Children[index];
                double w = Math.Min(child.DesiredSize.Width, Math.Max(0, finalSize.Width - x));
                // Vertically centred within the line, matching the StackPanel rows' previous look.
                double h = child.DesiredSize.Height;
                child.Arrange(new Rect(x, y + Math.Max(0, (lineHeight - h) / 2), w, h));
                x += child.DesiredSize.Width + Spacing;
            }
            y += lineHeight + Spacing;
        }
        return finalSize;
    }

    private double LineWidth(IReadOnlyList<int> line)
    {
        double w = 0;
        foreach (int i in line) w += Children[i].DesiredSize.Width + Spacing;
        return Math.Max(0, w - Spacing);
    }

    private double LineHeight(IReadOnlyList<int> line)
    {
        double h = 0;
        foreach (int i in line) h = Math.Max(h, Children[i].DesiredSize.Height);
        return h;
    }
}
