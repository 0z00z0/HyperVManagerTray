using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using Microsoft.Extensions.Logging;
using ZeroZero.Mqtt;
using ZeroZero.Mqtt.HomeAssistant;

namespace HyperVManagerTray.Services;

/// <summary>The live MQTT / Home Assistant integration: it owns the broker connection, keeps the entity
/// set in step with the config, and publishes from the app's own events. Never touches WinUI.
/// See <c>docs/mqtt-integration.md</c>.</summary>
internal sealed class MqttService : IDisposable
{
    /// <summary>How long clearing an abandoned identity's retained topics may take before the reconcile
    /// carries on without it.</summary>
    private static readonly TimeSpan ClearBudget = TimeSpan.FromSeconds(10);

    /// <summary>How long <see cref="Dispose"/> waits for the teardown it started. Above the
    /// connection's own ~3 s budget, so a synchronous dispose never truncates a live one.</summary>
    private static readonly TimeSpan DisposeBudget = TimeSpan.FromSeconds(5);

    private readonly ConfigManager  _config;
    private readonly NetworkMonitor _monitor;
    private readonly VmService      _vm;
    private readonly ILogger        _log;
    private readonly Func<CancellationToken, Task> _reCheckNetwork;
    private readonly Func<CancellationToken, Task> _repairHostNetworking;

    private readonly MqttStateCache _state = new();
    private readonly PlainTextMqttCredentialStore _credentials = new();
    private readonly MqttMetricsHold _metrics;
    private readonly string _version;

    // Field access only: nothing that blocks may run under it. The event pump takes it on WMI watcher
    // threads and the Settings panel takes it on the UI thread.
    private readonly object _lock = new();

    // One reconcile at a time. Each carries its own config snapshot, so two overlapping across the
    // clear-abandoned-identity await could land in either order and leave the older settings applied.
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private long _scheduled;

    private HaNode? _node;
    private MqttConnection? _connection;
    private string _identity = string.Empty;

    // The node id and discovery prefix the broker's retained topics are filed under. Null until the
    // first reconcile: nothing published, so nothing to strand.
    private MqttIdentity? _published;

    private MqttOptions? _applied;
    private string _appliedPassword = string.Empty;
    private IReadOnlyList<string> _vmNames = [];
    private IReadOnlyList<string> _ruleSwitches = [];
    private PublishCategories _categories;
    private bool _disposed;

    // Read by the entity payload providers on the publish thread; swapped whole on a reconcile.
    private volatile MqttSettings _settings = new();

    public MqttService(ConfigManager config, NetworkMonitor monitor, VmService vm, ILogger log,
                       string version,
                       Func<CancellationToken, Task> reCheckNetwork,
                       Func<CancellationToken, Task> repairHostNetworking)
    {
        _config  = config;
        _monitor = monitor;
        _vm      = vm;
        _log     = log;
        _version = version;
        _reCheckNetwork       = reCheckNetwork;
        _repairHostNetworking = repairHostNetworking;
        _metrics = new MqttMetricsHold(_vm.SubscribeMetrics, _vm.UnsubscribeMetrics);
    }

    /// <summary>Wires the app's events and brings the connection up to whatever the config asks for.
    /// Returns immediately — the first connect runs on the connection's own loop.</summary>
    public void Start()
    {
        _monitor.SwitchApplied  += OnSwitchApplied;
        _vm.StatusesChanged     += OnStatusesChanged;
        _vm.OperationProgress   += OnOperationProgress;
        _config.ConfigReloaded  += OnConfigReloaded;

        // Seed from what the app already knows, so a broker up at start-up need not wait for an event.
        if (_monitor.LastApplied is { } applied) _state.SetNetwork(applied);

        Schedule(_config.Current);
    }

    // ── What the Settings panel reads ───────────────────────────────────────────

    /// <summary>The broker password, behind the same store the connection authenticates from.</summary>
    public IMqttCredentialStore Credentials => _credentials;

    /// <summary>The live session's telemetry, or null when no connection has been built yet. A fresh
    /// connection carries a fresh instance, so a held reference stops reporting when one is rebuilt.</summary>
    public MqttActivity? Activity
    {
        get { lock (_lock) return _connection?.Activity; }
    }

    /// <summary>Whether the broker session is up right now.</summary>
    public bool IsConnected
    {
        get { lock (_lock) return _connection?.IsConnected == true; }
    }

    // ── App events → published state ────────────────────────────────────────────

    private void OnSwitchApplied(object? sender, MatchResult result)
    {
        _state.SetNetwork(result);
        Pump();
    }

    private void OnStatusesChanged(IReadOnlyList<VmStatus> statuses)
    {
        _state.SetVms(statuses);
        Pump();
    }

    private void OnOperationProgress(VmOperationProgress progress)
    {
        _state.SetOperation(progress);
        Pump();
    }

    private void OnConfigReloaded(object? sender, ConfigReloadedEventArgs e) => Schedule(e.Config);

    /// <summary>Tells the connection there is new state to publish, and re-checks whether the metrics
    /// subscription is still wanted. The connection raises no "connected" event, so liveness is
    /// sampled here rather than pushed.</summary>
    private void Pump()
    {
        MqttConnection? connection;
        lock (_lock) connection = _connection;
        if (connection is null) return;

        connection.SignalStateChanged();
        _metrics.Update(_settings.PublishVmMetrics, connection.IsConnected);
    }

    // ── Config → connection ─────────────────────────────────────────────────────

    /// <summary>Runs the reconcile off whatever thread raised the reload — it can be the UI thread, and
    /// a reconcile may dispose a connection. Ticketed, so a later reload supersedes an earlier
    /// one.</summary>
    private void Schedule(AppConfig config)
    {
        long ticket = Interlocked.Increment(ref _scheduled);
        _ = Task.Run(() => ReconcileAsync(config, ticket));
    }

    private async Task ReconcileAsync(AppConfig config, long ticket)
    {
        await _reconcileGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // A stale snapshot applied now would roll the newer settings back.
            if (MqttReconcile.Superseded(ticket, Interlocked.Read(ref _scheduled))) return;

            var settings = config.Mqtt ?? new MqttSettings();
            var vmNames  = config.VirtualMachines.Select(v => v.Name).ToList();
            var switches = config.RuleSwitches.ToList();
            string identity = Identity(settings);

            // Before anything republishes: whatever the old node id and discovery prefix own is
            // unreachable the moment either moves.
            await ClearAbandonedIdentityAsync(settings).ConfigureAwait(false);

            // Off the lock, because the teardown blocks for up to three seconds. Before the rebuild,
            // not after: the retiring session's retained "offline" shares an availability topic with
            // its replacement and must not land on top of that session's "online".
            Retire(TakeRetired(identity));

            MqttConnection? connection;
            lock (_lock)
            {
                if (_disposed) return;

                _settings = settings;
                _credentials.SetPassword(MqttSettings.CredentialReference, settings.Password);

                if (MqttReconcile.NeedsRecreate(_connection is not null, _identity, identity))
                {
                    Recreate(identity, settings, vmNames, switches);
                }
                else if (MqttReconcile.NeedsEntityRebuild(_vmNames, vmNames, _ruleSwitches, switches,
                                                          _categories, PublishCategories.Of(settings)))
                {
                    _vmNames      = vmNames;
                    _ruleSwitches = switches;
                    _categories   = PublishCategories.Of(settings);
                    // A category is otherwise only read on the next connect, so the entities a
                    // switched-off one owns would sit in Home Assistant until then.
                    _node!.SetEntities(BuildEntities());
                }

                var options = settings.ToOptions();
                if (MqttReconcile.NeedsApply(_applied, _appliedPassword, options, settings.Password))
                {
                    _applied         = options;
                    _appliedPassword = settings.Password ?? string.Empty;
                    _connection!.Apply(options);
                }
                _published  = IdentityOf(settings);
                connection  = _connection;
            }

            _metrics.Update(settings.PublishVmMetrics, connection?.IsConnected == true);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "MQTT: applying the configuration failed");
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    /// <summary>Detaches the connection a move to <paramref name="identity"/> retires, so it can be
    /// torn down off <see cref="_lock"/>. Null when the live one addresses the same identity.</summary>
    private MqttConnection? TakeRetired(string identity)
    {
        lock (_lock)
        {
            if (_disposed || _connection is null) return null;
            if (!MqttReconcile.NeedsRecreate(hasConnection: true, _identity, identity)) return null;

            var retired = _connection;
            _connection = null;
            _applied    = null;
            return retired;
        }
    }

    private void Retire(MqttConnection? connection)
    {
        if (connection is null) return;
        try { connection.Dispose(); }
        catch (Exception ex) { _log.LogWarning(ex, "MQTT: disposing the previous connection failed"); }
    }

    /// <summary>Empties the retained topics of an identity the configuration has just moved away from,
    /// on the connection that still addresses it. Best-effort: it runs against a live session or not at
    /// all. See <c>docs/mqtt-integration.md</c>.</summary>
    private async Task ClearAbandonedIdentityAsync(MqttSettings settings)
    {
        HaNode?         node;
        MqttConnection? connection;
        MqttIdentity?   previous;
        lock (_lock)
        {
            if (_disposed) return;
            node       = _node;
            connection = _connection;
            previous   = _published;
        }

        var topics = connection?.Topics;
        if (!MqttReconcile.CanClear(node is not null, connection is not null,
                                    connection?.IsConnected == true, topics is not null,
                                    previous, IdentityOf(settings)))
            return;

        try
        {
            using var cts = new CancellationTokenSource(ClearBudget);
            await node!.ClearIdentityAsync(connection!, topics!, cts.Token).ConfigureAwait(false);
            _log.LogInformation("MQTT: cleared the retained topics of the abandoned identity '{NodeId}'",
                                topics!.NodeId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "MQTT: clearing the abandoned identity '{NodeId}' failed — its retained "
                                + "topics are still on the broker", topics!.NodeId);
        }
    }

    /// <summary>The address these settings publish under. <see cref="Recreate"/> leaves the setup's
    /// machine name unset, so both derive the same node id from the same source.</summary>
    private static MqttIdentity IdentityOf(MqttSettings settings) =>
        MqttIdentity.For(settings, MqttEntitySet.TopicRoot, Environment.MachineName);

    /// <summary>The identity a live node is built for. Compared on the EFFECTIVE names, so writing a
    /// blank prefix out as Home Assistant's default does not read as a change.</summary>
    private static string Identity(MqttSettings settings) =>
        $"{MqttNaming.EffectiveDeviceName(settings)} {MqttNaming.EffectiveDiscoveryPrefix(settings)}";

    /// <summary>Builds a fresh node and connection. Caller holds <see cref="_lock"/>; whatever this
    /// replaces has already been retired by <see cref="TakeRetired"/> and torn down off the lock.</summary>
    private void Recreate(string identity, MqttSettings settings,
                          IReadOnlyList<string> vmNames, IReadOnlyList<string> ruleSwitches)
    {
        _connection   = null;
        _applied      = null;
        _identity     = identity;
        _vmNames      = vmNames;
        _ruleSwitches = ruleSwitches;
        _categories   = PublishCategories.Of(settings);

        var nodeOptions = new HaNodeOptions
        {
            TopicRoot = MqttEntitySet.TopicRoot,
            Device    = new HaDevice
            {
                Name             = MqttNaming.EffectiveDeviceName(settings),
                Model            = AppInfo.Name,
                SoftwareVersion  = _version,
                ConfigurationUrl = "https://github.com/0z00z0/HyperVManagerTray",
            },
            Origin = new HaOrigin
            {
                Name            = AppInfo.Name,
                SoftwareVersion = _version,
                SupportUrl      = "https://github.com/0z00z0/HyperVManagerTray/issues",
            },
            Logger = _log,
        };
        nodeOptions = nodeOptions with { DiscoveryPrefix = MqttNaming.EffectiveDiscoveryPrefix(settings) };

        _node = new HaNode(nodeOptions, BuildEntities());
        _connection = _node.CreateConnection(new MqttConnectionSetup
        {
            TopicRoot        = MqttEntitySet.TopicRoot,
            CredentialStore  = _credentials,
            RememberEndpoint = RememberEndpoint,
            // A disable empties the retained discovery, so the entities disappear rather than linger as
            // unavailable. Process exit runs no stop hook, so a restart finds its device where it was.
            OnStoppingAsync  = (publisher, topics, ct) => _node!.ClearIdentityAsync(publisher, topics, ct),
            Logger           = _log,
        });
    }

    private HaEntitySet BuildEntities() => MqttEntitySet.Build(new MqttEntitySpec
    {
        VmNames        = _vmNames,
        RuleSwitches   = _ruleSwitches,
        State          = _state,
        VmIp           = _vm.GetCachedVmIp,
        // Read per discovery pass, so a toggled category takes effect without rebuilding the set.
        PublishNetwork       = () => _settings.PublishNetwork,
        PublishVmState       = () => _settings.PublishVmState,
        PublishVmDiagnostics = () => _settings.PublishVmDiagnostics,
        PublishMetrics       = () => _settings.PublishVmMetrics,
        ReCheckNetwork = _reCheckNetwork,
        RepairHostNetworking = _repairHostNetworking,
        Power          = RunPowerAsync,
        OverrideSwitch = OverrideSwitchAsync,
        Refuse         = reason => _log.LogWarning("MQTT: command refused — {Reason}", reason),
    });

    // ── Commands ────────────────────────────────────────────────────────────────

    private Task RunPowerAsync(string vmName, VmOpKind kind, CancellationToken ct)
    {
        _log.LogInformation("MQTT: {Kind} requested for '{Vm}'", kind, vmName);
        _vm.BeginPowerAction(vmName, kind, VmOpOrigin.Mqtt);
        return Task.CompletedTask;
    }

    private async Task OverrideSwitchAsync(string vmName, string switchName, CancellationToken ct)
    {
        var outcome = await _monitor.ManualOverrideAsync(vmName, switchName).ConfigureAwait(false);
        _log.Log(outcome == NetworkMonitor.OverrideOutcome.Applied ? LogLevel.Information : LogLevel.Warning,
                 "MQTT: switch override '{Vm}' → '{Switch}' — {Outcome}", vmName, switchName, outcome);
    }

    private void RememberEndpoint(MqttEndpointMemory endpoint)
    {
        try { _config.RememberMqttEndpoint(endpoint); }
        catch (Exception ex) { _log.LogWarning(ex, "MQTT: could not remember the broker endpoint"); }
    }

    /// <summary>Detaches from the app's events synchronously — so the services this subscribes to may
    /// be disposed the moment it returns — and hands back the connection teardown as a task. That
    /// teardown blocks for up to three seconds, which is worth waiting for but not on the UI
    /// thread.</summary>
    public Task BeginDisposeAsync()
    {
        MqttConnection? connection;
        lock (_lock)
        {
            if (_disposed) return Task.CompletedTask;
            _disposed   = true;
            connection  = _connection;
            _connection = null;
        }

        _monitor.SwitchApplied -= OnSwitchApplied;
        _vm.StatusesChanged    -= OnStatusesChanged;
        _vm.OperationProgress  -= OnOperationProgress;
        _config.ConfigReloaded -= OnConfigReloaded;

        _metrics.Release();
        if (connection is null) return Task.CompletedTask;

        return Task.Run(() =>
        {
            try { connection.Dispose(); } catch { /* teardown is best-effort */ }
        });
    }

    public void Dispose() => BeginDisposeAsync().Wait(DisposeBudget);
}
