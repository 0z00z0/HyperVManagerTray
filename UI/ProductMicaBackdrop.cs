using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HyperVManagerTray.UI;

/// <summary>
/// Mica Alt with its active tint on this application's own colour (issue #146). The tint is what Mica
/// shows while the window has input; the controller's fallback, which it uses when the window has no
/// input and when transparency or effects are off, is left untouched, so the window still turns flat
/// grey when it is not in use.
///
/// <para>The stock <c>MicaBackdrop</c> element takes a <c>Kind</c> and nothing else, so reaching the
/// tint means driving a <see cref="MicaController"/> directly. The configuration comes from
/// <see cref="SystemBackdrop.GetDefaultSystemBackdropConfiguration"/>, which the framework keeps
/// current for theme, input activation and high contrast — the behaviour that would otherwise have to
/// be reproduced here.</para>
/// </summary>
internal sealed class ProductMicaBackdrop : SystemBackdrop
{
    // The two grounds, each derived on the product colour's hue at the lightness its own theme
    // already had, so only the hue changes and nothing drawn on them loses contrast. Measured:
    // white body text 15.85:1 on a card over the dark tone; headings 7.03:1 on the dark tone and
    // 6.70:1 on the light one.
    private static readonly Color DarkTint  = Color.FromArgb(0xFF, 0x05, 0x19, 0x20);
    private static readonly Color LightTint = Color.FromArgb(0xFF, 0xD1, 0xE1, 0xE8);

    private MicaController? _controller;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);

        // Where Mica cannot run, leaving the window without a backdrop is what MicaBackdrop does too.
        if (!MicaController.IsSupported()) return;

        var config = GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot);
        _controller = new MicaController { Kind = MicaKind.BaseAlt };
        ApplyTint(config);
        _controller.SetSystemBackdropConfiguration(config);
        _controller.AddSystemBackdropTarget(connectedTarget);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);

        if (_controller is null) return;
        _controller.RemoveSystemBackdropTarget(disconnectedTarget);
        _controller.Dispose();
        _controller = null;
    }

    /// <summary>Windows can change theme while the window is open, and the controller recomputes its
    /// own colours from the configuration when it does, so the tint is re-applied here.</summary>
    protected override void OnDefaultSystemBackdropConfigurationChanged(
        ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnDefaultSystemBackdropConfigurationChanged(target, xamlRoot);

        if (_controller is null) return;
        ApplyTint(GetDefaultSystemBackdropConfiguration(target, xamlRoot));
    }

    private void ApplyTint(SystemBackdropConfiguration config)
    {
        if (_controller is null) return;

        // High contrast exists so the user's own colours outrank every design decision; the
        // controller's own values stand there, as they do in the shared brand dictionary.
        if (config.IsHighContrast) return;

        _controller.TintColor = config.Theme == SystemBackdropTheme.Light ? LightTint : DarkTint;
    }
}
