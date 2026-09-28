using Microsoft.Extensions.Logging;
using HyperVManagerTray.Helpers;
using HyperVManagerTray.UI;
using ZeroZero.Update;
using ZeroZero.Update.Win32;

namespace HyperVManagerTray.Services;


/// <summary>
/// This app's self-update, over the shared update component.
///
/// <para>Three entry points. <see cref="CheckAsync"/> is the silent start-up check: it asks GitHub
/// what the latest release is and nothing else, so the only thing it can produce is the tray badge.
/// <see cref="RunManualAsync"/> is the explicit check, and the only path that shows a window — and it
/// downloads and runs an installer only after the user has said yes to it.
/// <see cref="SetAutomaticInstalling"/> is the third, off unless the settings row is switched on: it
/// hands the whole check-download-install decision to the shared component's
/// <see cref="UnattendedUpdatePolicy"/>, which shows nothing and starts an installer only while the
/// screen is locked or the machine has gone ten minutes untouched.</para>
///
/// <para>The policy owns its own flow, wired to prompts that answer themselves, so it cannot draw
/// over the window an explicit check puts on screen. This app's own flow is never wired to those
/// prompts: they answer "Install" unconditionally, so a flow carrying them would install whatever it
/// found with neither the machine-free rule nor the question below.</para>
///
/// <para>The one thing this app decides about a moment is <see cref="MayInstallNow"/>. The
/// ten-minute rule beneath it belongs to the component and cannot be loosened from here.</para>
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
    private readonly UpdateLog _log;
    private readonly Action _exitForInstaller;
    private readonly Func<bool> _bridgeActionPending;

    // The policy exists only while the setting is on, and the setting can be switched at any moment
    // from the Settings window. Guarded because the switch arrives on whatever thread raised the
    // config reload, while the policy's own ticks run on the thread pool.
    private readonly object _policyLock = new();
    private UnattendedUpdatePolicy? _policy;

    // The release the policy was about to install when it last asked, so the shutdown callback can
    // record what the update was for. Written only where the moment was accepted, which is the last
    // point before the installer starts.
    private string? _acceptedByPolicy;

    /// <param name="runningVersion">The build to compare GitHub's latest against. Stated by the
    /// caller rather than read from the entry assembly, which is the component's fallback and would
    /// name whatever host happens to be running this code.</param>
    /// <param name="exitForInstaller">Queues the app's ordinary exit. Called once the installer has
    /// started, so the files it replaces are released.</param>
    /// <param name="bridgeActionPending">Whether a machine is waiting out its bridge-lost delay. Read
    /// immediately before an unattended installer starts; see <see cref="MayInstallNow"/> for why that
    /// is the one moment this app refuses. Called rather than captured, because the network monitor
    /// does not exist yet when this is built.</param>
    public AppUpdate(Version runningVersion, ILogger logger, Action exitForInstaller,
                     Func<bool> bridgeActionPending)
    {
        ArgumentNullException.ThrowIfNull(runningVersion);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(exitForInstaller);
        ArgumentNullException.ThrowIfNull(bridgeActionPending);

        var log = new UpdateLog(logger);
        _log                 = log;
        _exitForInstaller    = exitForInstaller;
        _bridgeActionPending = bridgeActionPending;
        _service = new UpdateService(AppUpdateOptions.For(runningVersion, log));
        _flow = new UpdateFlow(_service, _prompts, new UpdateFlowOptions
        {
            // The installer waits for this exit before it ends anything, so the sooner the app goes
            // the sooner the upgrade proceeds. Marked first: an unmarked exit is the one the relaunch
            // hook brings back — straight into the upgrade that is replacing it.
            //
            // The record goes down before the exit. Setup installs unattended over this process's
            // files, so this process is gone before an outcome exists; the version that starts next
            // reports it from the record. Nothing here waits on Setup: the wait would hold the very
            // files it is about to replace.
            Shutdown = () =>
            {
                if (_prompts.AcceptedVersion is { } target)
                    UnattendedUpdate.Record(AppInfo.DataDir, target, DateTimeOffset.UtcNow, log);
                AppLifecycle.MarkDeliberateExit();
                exitForInstaller();
            },
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
    /// on the thread that owns this app's windows — the update window is shown on it, and the flow
    /// returns to it after every await.
    /// </summary>
    /// <remarks>One install at a time, across both the tray menu and the About window: a second
    /// request while an install is on screen returns <see cref="UpdateFlowResult.AlreadyRunning"/>
    /// and shows nothing. A second request arriving while the CHECK is still in flight joins that
    /// check and reads its result, rather than being refused. No owner window is passed: the update
    /// window centres itself on the monitor under the cursor and stays on top.</remarks>
    /// <returns>The outcome and, when one was found, the release it refers to. Both call sites
    /// discard it today — they are buttons whose reporting the flow does itself. A download the
    /// user stopped answers <see cref="UpdateFlowResult.DownloadCancelled"/>, and nothing is said
    /// about it: the person who pressed the button knows.</returns>
    public Task<UpdateFlowRun> RunManualAsync() => _flow.RunAsync(UpdateTrigger.Manual);

    /// <summary>
    /// Switches the automatic install on or off, to match the settings row. Off is the state an absent
    /// setting means, and in it nothing is checked either — the policy is the whole of that path, so
    /// there is no scheduler left running and the app behaves exactly as it does with the row untouched.
    /// </summary>
    /// <remarks>Called at start and again on every config reload, so a switch takes effect without a
    /// restart. Idempotent: asking for the state it is already in changes nothing, which matters
    /// because a reload raised by an unrelated save arrives here too. Switching it on again after off
    /// starts a new policy, so the first check comes 30 s later rather than immediately.</remarks>
    public void SetAutomaticInstalling(bool enabled)
    {
        lock (_policyLock)
        {
            if (enabled == (_policy is not null)) return;

            if (!enabled)
            {
                _policy?.Dispose();
                _policy = null;
                _log.Info("Automatic update installing is off; no check is scheduled.");
                return;
            }

            _policy = new UnattendedUpdatePolicy(_service, new UnattendedUpdateOptions
            {
                Enabled = true,
                // The cadence, the retry tick and the initial delay are the component's defaults — a
                // check a day, retried every ten minutes, starting 30 s after this. There is one
                // setting here, so there is nothing for a second one to carry.
                MayInstallNow = MayInstallNow,
                Shutdown      = ShutdownForPolicyInstaller,
                Log           = _log,
            });
            _policy.Start();
            _log.Info("Automatic update installing is on; a check runs daily and installs only while the machine is free.");
        }
    }

    /// <summary>
    /// Whether this moment suits an installer that is downloaded, verified and waiting. Asked by the
    /// policy immediately before it starts one, and only about that moment: a refusal stops the attempt
    /// and nothing else, and the next tick asks again.
    /// </summary>
    /// <remarks>
    /// One thing is refused, and it is the one thing a restart genuinely loses. A bridge-lost action —
    /// pausing, saving or shutting a machine down because its bridged network went away — is a timer in
    /// the network monitor's memory, and the first evaluation after a start deliberately never arms one,
    /// so an update landing during the delay drops that action altogether and nothing acts on the
    /// machine until the bridge is restored and lost again.
    ///
    /// <para>Nothing else is refused, because nothing else is lost. A machine starting, stopping,
    /// pausing or saving is Hyper-V's own job once it has been asked, and it finishes whether this
    /// process lives or not. An interrupted service stop is re-armed by the first evaluation after the
    /// restart, and an interrupted switch move or host vNIC repair is re-applied by it.</para>
    ///
    /// <para>The reason is the log's key, so it carries no delay, no machine name and no count: one
    /// wording means one line for as long as the cause lasts.</para>
    /// </remarks>
    private InstallMoment MayInstallNow(ReleaseInfo release)
    {
        ArgumentNullException.ThrowIfNull(release);

        if (_bridgeActionPending())
            return InstallMoment.NotNow("a machine is waiting out its bridge-lost delay");

        _acceptedByPolicy = release.VersionText;
        return InstallMoment.Now;
    }

    /// <summary>The policy's exit, once its installer is running. The same handover the explicit check
    /// leaves, so the version that starts next reports the outcome of an update nobody asked for the
    /// way it reports one that was.</summary>
    private void ShutdownForPolicyInstaller()
    {
        if (_acceptedByPolicy is { } target)
            UnattendedUpdate.Record(AppInfo.DataDir, target, DateTimeOffset.UtcNow, _log);
        AppLifecycle.MarkDeliberateExit();
        _exitForInstaller();
    }

    public void Dispose()
    {
        lock (_policyLock)
        {
            _policy?.Dispose();
            _policy = null;
        }
        _service.Dispose();
    }
}
