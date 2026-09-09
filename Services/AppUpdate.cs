using Microsoft.Extensions.Logging;
using HyperVManagerTray.Helpers;
using HyperVManagerTray.UI;
using ZeroZero.Update;
using ZeroZero.Update.Win32;

namespace HyperVManagerTray.Services;

/// <summary>
/// This app's self-update, over the shared update component.
///
/// <para>Two entry points, and the split between them is the point. <see cref="CheckAsync"/> is the
/// silent start-up check: it asks GitHub what the latest release is and nothing else, so the only
/// thing it can produce is the tray badge. <see cref="RunManualAsync"/> is the explicit check, and
/// the only path that may download and run an installer — and only after the user has said yes to a
/// dialog. Nothing here is scheduled: the component ships a check scheduler and it is deliberately
/// not started, because this app does not replace itself while nobody is looking.</para>
///
/// <para>A downloaded installer is checked twice before it runs — its SHA-256 against the hash the
/// release publishes, then its Authenticode signature and signer against
/// <see cref="ExpectedPublisher"/> — and again at the moment of launch, so the bytes that were
/// checked are the bytes that run or nothing runs. A refused file is deleted.</para>
/// </summary>
internal sealed class AppUpdate : IDisposable
{
    private readonly UpdateService _service;
    private readonly TrayUpdatePrompts _prompts = new();
    private readonly UpdateFlow _flow;

    /// <param name="runningVersion">The build to compare GitHub's latest against. Stated by the
    /// caller rather than read from the entry assembly, which is the component's fallback and would
    /// name whatever host happens to be running this code.</param>
    public AppUpdate(Version runningVersion, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(runningVersion);
        ArgumentNullException.ThrowIfNull(logger);

        var log = new UpdateLog(logger);
        _service = new UpdateService(AppUpdateOptions.For(runningVersion, log));
        _flow = new UpdateFlow(_service, _prompts, new UpdateFlowOptions
        {
            // The installer closes this app itself, so nothing exits here. The mark is still taken:
            // were the installer to close it cleanly, an unmarked exit is the one the relaunch hook
            // brings back — straight into the upgrade that is replacing it.
            Shutdown = AppLifecycle.MarkDeliberateExit,
            OpenReleasePage = page => Shell.Open(page.AbsoluteUri),
            Log = log,
        });
    }

    /// <summary>Removes download directories earlier runs left behind. Best-effort.</summary>
    public int SweepStaleDownloads() => _service.SweepStaleDownloads(AppUpdateOptions.StaleDownloadAge);

    /// <summary>The silent check. Finds the latest release and compares it; downloads nothing, runs
    /// nothing and shows nothing.</summary>
    public Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default) =>
        _service.CheckAsync(cancellationToken);

    /// <summary>
    /// The explicit check: ask, and on a yes download, verify and run the installer. Must be awaited
    /// on the UI thread — the dialogs need that thread's comctl32 version 6 activation context.
    /// </summary>
    /// <param name="owner">Parent window for the dialogs, captured by the caller before it awaits.</param>
    /// <remarks>One run at a time, across both the tray menu and the About window: a second request
    /// while one is on screen returns <see cref="UpdateFlowResult.AlreadyRunning"/> and shows
    /// nothing.</remarks>
    public Task<UpdateFlowResult> RunManualAsync(IntPtr owner)
    {
        _prompts.Owner = owner;
        return _flow.RunAsync(UpdateTrigger.Manual);
    }

    public void Dispose() => _service.Dispose();
}
