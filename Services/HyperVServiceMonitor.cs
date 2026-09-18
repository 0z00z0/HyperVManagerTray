using System.ServiceProcess;
using Microsoft.Extensions.Logging;
using HyperVManagerTray.Models;

namespace HyperVManagerTray.Services;

/// <summary>
/// Reads the two Hyper-V services' states from the Service Control Manager, and starts and stops them
/// (issue #114). Deliberately not through Hyper-V's WMI namespace: that namespace is served by vmms, so
/// it cannot report vmms being stopped.
///
/// <para>Every state change and every start or stop, with its origin, is written to vm-power.log, the
/// audit trail that already holds each VM power action. The service start type is never touched, so a
/// stop lasts until the next restart.</para>
/// </summary>
public sealed class HyperVServiceMonitor : IDisposable
{
    // The SCM answers a status query from memory, so a short poll costs next to nothing and keeps the
    // dashboard and VmService within a couple of seconds of reality without a native notification API.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);
    // vmms saves nothing on stop, but it waits for its WMI clients to let go, which can take a while.
    private static readonly TimeSpan StopTimeout  = TimeSpan.FromSeconds(120);

    private readonly ILogger _log;
    private readonly object _pollLock = new();
    private readonly Dictionary<HyperVServiceKind, HyperVServiceState> _states = new();
    private readonly Dictionary<HyperVServiceKind, ServiceController> _controllers = new();
    private CancellationTokenSource? _cts;

    /// <summary>Raised on a background thread when a service's state changes, including the first read.</summary>
    public event Action<HyperVServiceKind, HyperVServiceState>? StateChanged;

    public HyperVServiceMonitor(ILogger powerLog)
    {
        _log = powerLog;
        foreach (var kind in HyperVServiceNames.All) _states[kind] = HyperVServiceState.Unknown;
    }

    /// <summary>The last state read for <paramref name="kind"/>.</summary>
    public HyperVServiceState State(HyperVServiceKind kind)
    {
        lock (_pollLock) return _states[kind];
    }

    /// <summary>Starts polling. The first read runs at once on the thread pool.</summary>
    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                Poll();
                using var timer = new PeriodicTimer(PollInterval);
                while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) Poll();
            }
            catch (OperationCanceledException) { /* disposed */ }
            catch (Exception ex) { _log.LogWarning(ex, "Hyper-V service polling stopped"); }
        }, ct);
    }

    /// <summary>Reads both services now and raises <see cref="StateChanged"/> for each that moved. Never throws.</summary>
    public void Poll()
    {
        var changed = new List<(HyperVServiceKind, HyperVServiceState)>();
        lock (_pollLock)
        {
            foreach (var kind in HyperVServiceNames.All)
            {
                var state = Read(kind);
                if (state == _states[kind]) continue;
                _log.LogInformation("Service '{Service}': {Previous} -> {State}",
                    HyperVServiceNames.DisplayName(kind), _states[kind], state);
                _states[kind] = state;
                changed.Add((kind, state));
            }
        }
        // Raised outside the lock, so a handler that reads State() cannot deadlock against a poll.
        foreach (var (kind, state) in changed)
        {
            try { StateChanged?.Invoke(kind, state); }
            catch (Exception ex) { _log.LogWarning(ex, "A service state handler failed"); }
        }
    }

    /// <summary>Caller holds <see cref="_pollLock"/>.</summary>
    private HyperVServiceState Read(HyperVServiceKind kind)
    {
        try
        {
            if (!_controllers.TryGetValue(kind, out var sc))
                _controllers[kind] = sc = new ServiceController(HyperVServiceNames.ServiceName(kind));
            sc.Refresh();
            return sc.Status switch
            {
                ServiceControllerStatus.Running                                          => HyperVServiceState.Running,
                ServiceControllerStatus.Stopped                                          => HyperVServiceState.Stopped,
                ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending => HyperVServiceState.Starting,
                ServiceControllerStatus.StopPending or ServiceControllerStatus.PausePending     => HyperVServiceState.Stopping,
                _                                                                        => HyperVServiceState.Unknown,
            };
        }
        catch (InvalidOperationException ex) when (ex.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 1060 })
        {
            // ERROR_SERVICE_DOES_NOT_EXIST: Hyper-V is not installed or the feature is turned off.
            DropController(kind);
            return HyperVServiceState.NotInstalled;
        }
        catch (Exception)
        {
            // A handle gone bad is reopened on the next poll rather than read forever.
            DropController(kind);
            return HyperVServiceState.Unknown;
        }
    }

    private void DropController(HyperVServiceKind kind)
    {
        if (_controllers.Remove(kind, out var sc)) sc.Dispose();
    }

    /// <summary>
    /// Starts <paramref name="kind"/> and waits until it runs. Returns null on success (including when it
    /// was already running), or the reason it failed. Never throws.
    /// </summary>
    public Task<string?> StartServiceAsync(HyperVServiceKind kind, VmOpOrigin origin) => Task.Run(() =>
    {
        var name = HyperVServiceNames.DisplayName(kind);
        _log.LogInformation("BEGIN Start service '{Service}' (origin={Origin})", name, origin);
        try
        {
            using var sc = new ServiceController(HyperVServiceNames.ServiceName(kind));
            if (sc.Status == ServiceControllerStatus.Running)
            {
                _log.LogInformation("SUCCEEDED Start service '{Service}' (origin={Origin}): already running — no-op", name, origin);
                return null;
            }
            // A service still stopping refuses a start, so let the stop finish first.
            if (sc.Status == ServiceControllerStatus.StopPending)
                sc.WaitForStatus(ServiceControllerStatus.Stopped, StopTimeout);
            if (sc.Status != ServiceControllerStatus.StartPending)
                sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, StartTimeout);
            _log.LogInformation("SUCCEEDED Start service '{Service}' (origin={Origin})", name, origin);
            return null;
        }
        catch (Exception ex)
        {
            var reason = Reason(ex);
            _log.LogWarning(ex, "FAILED Start service '{Service}' (origin={Origin}): {Error}", name, origin, reason);
            return reason;
        }
        finally { Poll(); }
    });

    /// <summary>
    /// Stops <paramref name="kind"/> and waits until it has stopped. Returns null on success (including
    /// when it was already stopped), or the reason it failed. Never throws.
    ///
    /// <para>This is the bare stop. Callers go through <see cref="Helpers.ServiceStopFlow"/>, which refuses
    /// the stop while a managed VM is running.</para>
    /// </summary>
    public Task<string?> StopServiceAsync(HyperVServiceKind kind, VmOpOrigin origin) => Task.Run(() =>
    {
        var name = HyperVServiceNames.DisplayName(kind);
        _log.LogInformation("BEGIN Stop service '{Service}' (origin={Origin})", name, origin);
        try
        {
            using var sc = new ServiceController(HyperVServiceNames.ServiceName(kind));
            if (sc.Status == ServiceControllerStatus.Stopped)
            {
                _log.LogInformation("SUCCEEDED Stop service '{Service}' (origin={Origin}): already stopped — no-op", name, origin);
                return null;
            }
            if (sc.Status == ServiceControllerStatus.StartPending)
                sc.WaitForStatus(ServiceControllerStatus.Running, StartTimeout);
            if (sc.Status != ServiceControllerStatus.StopPending)
                sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, StopTimeout);
            _log.LogInformation("SUCCEEDED Stop service '{Service}' (origin={Origin})", name, origin);
            return null;
        }
        catch (Exception ex)
        {
            var reason = Reason(ex);
            _log.LogWarning(ex, "FAILED Stop service '{Service}' (origin={Origin}): {Error}", name, origin, reason);
            return reason;
        }
        finally { Poll(); }
    });

    // ServiceController wraps the Win32 error, whose text is the one that says why.
    private static string Reason(Exception ex) =>
        ex is System.ServiceProcess.TimeoutException
            ? "it did not finish in time."
            : ex.InnerException?.Message ?? ex.Message;

    public void Dispose()
    {
        try { _cts?.Cancel(); _cts?.Dispose(); } catch { }
        lock (_pollLock)
        {
            foreach (var sc in _controllers.Values) sc.Dispose();
            _controllers.Clear();
        }
    }
}
