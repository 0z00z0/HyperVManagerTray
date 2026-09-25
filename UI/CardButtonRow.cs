using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace HyperVManagerTray.UI;

/// <summary>
/// The action row on a dashboard card: one line of buttons that spans the card's width, with the widths
/// as close to equal as the captions allow.
///
/// <para>A caption that needs more than an equal share takes what it needs, and the remaining buttons
/// share what is left equally — so "Shut down" is wider than "Save", the row still reaches the right-hand
/// edge, and no caption is ever cut to force equality. A split button's chevron half is part of that
/// button's width, because the width shared out is the control's own desired width.</para>
///
/// <para>Where the captions together need more room than the row has, every button keeps its natural
/// width and the row reports that width: nothing is shrunk, and the card can be seen to be too narrow
/// rather than quietly losing a letter. <c>WindowPlacement.WrapStrip</c> takes the same line for the
/// settings window's control strips.</para>
/// </summary>
internal sealed class CardButtonRow : Panel
{
    /// <summary>Gap between adjacent buttons, in DIP.</summary>
    public double Spacing { get; set; }

    private double[] _widths = [];

    protected override Size MeasureOverride(Size available)
    {
        var children = Children;
        if (children.Count == 0)
        {
            _widths = [];
            return new Size(0, 0);
        }

        double gaps    = Spacing * (children.Count - 1);
        var    natural = new double[children.Count];

        for (int i = 0; i < children.Count; i++)
        {
            children[i].Measure(new Size(double.PositiveInfinity, available.Height));
            natural[i] = children[i].DesiredSize.Width;
        }

        _widths = Share(natural, available.Width - gaps);

        double width = gaps, height = 0;
        for (int i = 0; i < children.Count; i++)
        {
            children[i].Measure(new Size(_widths[i], available.Height));
            width  += _widths[i];
            height  = Math.Max(height, children[i].DesiredSize.Height);
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var children = Children;
        double x = 0;
        for (int i = 0; i < children.Count; i++)
        {
            double w = i < _widths.Length ? _widths[i] : children[i].DesiredSize.Width;
            children[i].Arrange(new Rect(x, 0, w, final.Height));
            x += w + Spacing;
        }
        return final;
    }

    /// <summary>
    /// Shares <paramref name="available"/> out between buttons whose natural widths are
    /// <paramref name="natural"/>: equal shares, except that a button needing more than its share keeps
    /// its natural width and the rest divide what is left. Returns the natural widths unchanged where
    /// there is no room to share out, or before the first real measure pass.
    /// </summary>
    private static double[] Share(double[] natural, double available)
    {
        var widths = (double[])natural.Clone();
        if (double.IsNaN(available) || double.IsInfinity(available) || available <= 0) return widths;

        double needed = 0;
        foreach (var w in natural) needed += w;
        if (needed >= available) return widths;

        var  settled   = new bool[natural.Length];
        int  remaining = natural.Length;
        double left    = available;

        while (remaining > 0)
        {
            double share = left / remaining;

            // The widest caption still over its share is settled first: settling any other one would
            // lower the share again and could leave this one short on the next pass.
            int widest = -1;
            for (int i = 0; i < natural.Length; i++)
                if (!settled[i] && natural[i] > share && (widest < 0 || natural[i] > natural[widest]))
                    widest = i;

            if (widest < 0)
            {
                for (int i = 0; i < natural.Length; i++)
                    if (!settled[i]) widths[i] = share;
                break;
            }

            settled[widest] = true;
            widths[widest]  = natural[widest];
            left           -= natural[widest];
            remaining--;
        }

        return widths;
    }
}
