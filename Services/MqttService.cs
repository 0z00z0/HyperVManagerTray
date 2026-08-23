using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using Microsoft.Extensions.Logging;
using ZeroZero.Mqtt;
using ZeroZero.Mqtt.HomeAssistant;

namespace HyperVManagerTray.Services;

/// <summary>
/// The live MQTT / Home Assistant integration (issue #75): it owns the broker connection, keeps the
/// entity set in step with the config, and feeds the published state from the five events the app
/// already raises.
///
/// <para><b>Push-driven, never polled.</b> <c>NetworkMonitor.SwitchApplied</c>,
/// <c>VmService.StatusesChanged</c> and <c>VmService.OperationProgress</c> update
/// <see cref="MqttStateCache"/> and signal the connection; nothing here runs a timer.</para>
///
/// <para><b>Never on the UI thread.</b> The three status events already arrive on background threads;
/// <c>ConfigReloaded</c> can arrive on the UI thread, so the reconcile it triggers is dispatched to
/// the thread pool. No method of this class touches WinUI.</para>
/// </summary>
internal sealed class MqttService : IDisposable
{
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

    // Guards the node/connection swap and the "has anything worth re-applying changed?" bookkeeping.
    private readonly object _lock = new();
    private HaNode? _node;
    private MqttConnection? _connection;
    private string _identity = string.Empty;
    private MqttOptions? _applied;
    private string _appliedPassword = string.Empty;
    private IReadOnlyList<string> _vmNames = [];
    private IReadOnlyList<string> _ruleSwitches = [];
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

        // Seed from what the app already knows, so a broker that is up at start-up gets the current
        // picture rather than waiting for the next event.
        if (_monitor.LastApplied is { } applied) _state.SetNetwork(applied);

        Schedule(_config.Current);
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
    /// subscription is still wanted. Cheap: the connection coalesces signals, and the hold is a compare.
    ///
    /// <para>The connection raises no "connected" event, so the session's liveness is sampled here
    /// rather than pushed. <c>VmService.StatusesChanged</c> fires at least once a minute (App's
    /// safety-net refresh), so the hold follows a connect or a drop within that — and errs towards not
    /// holding, which is the safe direction for a subscription that costs a WMI poll.</para></summary>
    private void Pump()
    {
        MqttConnection? connection;
        lock (_lock) connection = _connection;
        if (connection is null) return;

        connection.SignalStateChanged();
        _metrics.Update(_settings.PublishVmMetrics, connection.IsConnected);
    }

    // ── Config → connection ─────────────────────────────────────────────────────

    /// <summary>Runs the reconcile off whatever thread raised the reload — it can be the UI thread,
    /// and a reconcile may dispose a connection.</summary>
    private void Schedule(AppConfig config) => _ = Task.Run(() => Reconcile(config));

    private void Reconcile(AppConfig config)
    {
        try
        {
            var settings = config.Mqtt ?? new MqttSettings();
            var vmNames  = config.VirtualMachines.Select(v => v.Name).ToList();
            var switches = config.RuleSwitches.ToList();
            string identity = Identity(settings);

            MqttConnection? connection;
            lock (_lock)
            {
                if (_disposed) return;

                _settings = settings;
                _credentials.SetPassword(MqttSettings.CredentialReference, settings.Password);

                if (_connection is null || !string.Equals(identity, _identity, StringComparison.Ordinal))
                {
                    Recreate(identity, settings, vmNames, switches);
                }
                else if (!vmNames.SequenceEqual(_vmNames, StringComparer.Ordinal)
                         || !switches.SequenceEqual(_ruleSwitches, StringComparer.Ordinal))
                {
                    _vmNames      = vmNames;
                    _ruleSwitches = switches;
                    _node!.SetEntities(BuildEntities());
                }

                var options = settings.ToOptions();
                if (NeedsApply(options, settings.Password))
                {
                    _applied         = options;
                    _appliedPassword = settings.Password ?? string.Empty;
                    _connection!.Apply(options);
                }
                connection = _connection;
            }

            _metrics.Update(settings.PublishVmMetrics, connection?.IsConnected == true);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "MQTT: applying the configuration failed");
        }
    }

    /// <summary>Whether the connection has to be re-applied. A remembered endpoint moving is not a
    /// reason: <c>Apply</c> drops the live session to rebuild it, and the endpoint is written back
    /// BY that session, so re-applying for it would reconnect once per successful connect.</summary>
    private bool NeedsApply(MqttOptions options, string? password) =>
        _applied is not { } previous
        || previous with { LastGoodEndpoint = null } != options with { LastGoodEndpoint = null }
        || !string.Equals(_appliedPassword, password ?? string.Empty, StringComparison.Ordinal);

    /// <summary>The identity a live node is built for. Both halves are fixed at construction, so a
    /// change to either needs a fresh node and connection.</summary>
    private static string Identity(MqttSettings settings) =>
        $"{DeviceName(settings)} {settings.DiscoveryPrefix}";

    private static string DeviceName(MqttSettings settings) =>
        string.IsNullOrWhiteSpace(settings.DeviceName)
            ? $"{AppInfo.Name} ({Environment.MachineName})"
            : settings.DeviceName.Trim();

    /// <summary>Builds a fresh node and connection. Caller holds <see cref="_lock"/>.</summary>
    private void Recreate(string identity, MqttSettings settings,
                          IReadOnlyList<string> vmNames, IReadOnlyList<string> ruleSwitches)
    {
        var previous = _connection;
        _connection = null;
        _applied    = null;
        try { previous?.Dispose(); } catch (Exception ex) { _log.LogWarning(ex, "MQTT: disposing the previous connection failed"); }

        _identity     = identity;
        _vmNames      = vmNames;
        _ruleSwitches = ruleSwitches;

        var nodeOptions = new HaNodeOptions
        {
            TopicRoot = MqttEntitySet.TopicRoot,
            Device    = new HaDevice
            {
                Name             = DeviceName(settings),
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
        if (!string.IsNullOrWhiteSpace(settings.DiscoveryPrefix))
            nodeOptions = nodeOptions with { DiscoveryPrefix = settings.DiscoveryPrefix.Trim() };

        _node = new HaNode(nodeOptions, BuildEntities());
        _connection = _node.CreateConnection(new MqttConnectionSetup
        {
            TopicRoot        = MqttEntitySet.TopicRoot,
            CredentialStore  = _credentials,
            RememberEndpoint = RememberEndpoint,
            // A disable — or a config that stops naming a broker — empties the retained discovery, so
            // the entities disappear from Home Assistant instead of lingering as unavailable. Process
            // exit runs no stop hook, so a restart finds its device where it left it.
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
        PublishMetrics = () => _settings.PublishVmMetrics,
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

    public void Dispose()
    {
        MqttConnection? connection;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed  = true;
            connection = _connection;
            _connection = null;
        }

        _monitor.SwitchApplied -= OnSwitchApplied;
        _vm.StatusesChanged    -= OnStatusesChanged;
        _vm.OperationProgress  -= OnOperationProgress;
        _config.ConfigReloaded -= OnConfigReloaded;

        _metrics.Release();
        try { connection?.Dispose(); } catch { /* teardown is best-effort */ }
    }
}
