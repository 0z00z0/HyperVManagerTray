using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using Microsoft.Extensions.Logging;
using ZeroZero.Mqtt;
using ZeroZero.Mqtt.Discovery;
using ZeroZero.Mqtt.WinUI;

namespace HyperVManagerTray.Services;

/// <summary>
/// The live MQTT session (issue #75): the shared module's settings store, publish groups, entity table,
/// discovery publisher and broker connection, wired to the five events this app already raises.
/// </summary>
/// <remarks>
/// <para><b>Deliberately not linked into the test assembly.</b> It holds a broker connection, so every
/// decision it takes lives in a pure file that is — <see cref="MqttConfigStore"/>,
/// <see cref="MqttEntityTable"/>, <see cref="MqttStateCache"/>, <see cref="MqttCommandGate"/>,
/// <see cref="MqttMetricsHold"/>, <see cref="MqttPublishGate"/> and <see cref="MqttWithdrawal"/>. A guard
/// added inline here is a guard nothing tests.</para>
/// <para><b>Nothing here touches the UI thread.</b> Every event it subscribes to arrives on a WMI,
/// timer or thread-pool thread, and every module call it makes is non-blocking; the one blocking call —
/// the connection's teardown — runs on the pool through <see cref="BeginDisposeAsync"/>.</para>
/// </remarks>
public sealed class MqttService : IDisposable
{
    /// <summary>The logger category App routes to mqtt.log.</summary>
    public const string LogCategory = "mqtt";

    /// <summary>Longest <see cref="Dispose"/> waits for the teardown. Above the connection's own ~3 s
    /// budget, so a healthy teardown is never truncated.</summary>
    public static readonly TimeSpan TeardownBudget = TimeSpan.FromSeconds(5);

    private readonly ConfigManager _config;
    private readonly NetworkMonitor _monitor;
    private readonly VmService _vm;
    private readonly ILogger _log;
    private readonly Func<CancellationToken, Task> _reCheckNetwork;
    private readonly Func<CancellationToken, Task> _repairHostNetworking;

    private readonly MqttStateCache _state = new();
    private readonly MqttConfigStore _store;
    private readonly PublishGroupSet _groups;
    private readonly DiscoveryLedgerFile _ledger;
    private readonly MqttMetricsHold _metrics;
    private readonly MqttPublishGate _publish;
    private readonly DiscoveryPublisher _publisher;
    private readonly MqttLog _moduleLog;
    private MqttConnection? _connection;

    // The broker settings as last applied. Publishing being switched off withdraws the device, and that
    // transition is only visible against what came before — the store's change notification carries no
    // previous value.
    private readonly object _appliedLock = new();
    private MqttSettings _applied;

    // One reconcile at a time. Never disposed: it is held for the life of the process, and a teardown
    // that disposed it would throw in whichever background reconcile was waiting on it.
    private readonly SemaphoreSlim _reconcile = new(1, 1);

    // What the announced table was built from: the VM names, and which shape the power verbs take. A
    // config write that leaves both alone — a log-level change, a saved window rect — must not rebuild
    // and re-announce the whole document.
    private string _tableSignature;
    private int _disposed;

    public MqttService(
        ConfigManager config,
        NetworkMonitor monitor,
        VmService vm,
        HyperVManager hyperV,
        ILogger log,
        string version,
        Func<CancellationToken, Task> reCheckNetwork,
        Func<CancellationToken, Task> repairHostNetworking,
        string dataDir)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(hyperV);

        _config = config;
        _monitor = monitor;
        _vm = vm;
        _log = log;
        _reCheckNetwork = reCheckNetwork;
        _repairHostNetworking = repairHostNetworking;

        _store = new MqttConfigStore(config);
        _groups = new PublishGroupSet(_store, MqttEntityTable.Groups);
        // Durable, not transient: an entity removed while the app was closed is never diffed against
        // anything, so without a record its retained topics stay on the broker for ever.
        _ledger = DiscoveryLedgerFile.In(dataDir);
        _metrics = new MqttMetricsHold(vm.SubscribeMetrics, vm.UnsubscribeMetrics);
        // Reads _connection, which is assigned below — the callback is deferred, so the order is fine.
        _publish = new MqttPublishGate(() => _connection?.RequestPublish());

        var moduleLog = _moduleLog = new MqttLog(log);
        _tableSignature = TableSignature(config.Current);
        _applied = _store.Read();

        _publisher = new DiscoveryPublisher(new DiscoveryPublisherSetup
        {
            IsConnected       = () => _connection?.IsConnected ?? false,
            TopicRoot         = MqttEntityTable.TopicRoot,
            Device            = new DiscoveryDevice("ZeroZero Software", AppInfo.Name, version,
                                    ConfigurationUrl: AppAbout.RepoUrl),
            Origin            = new DiscoveryOrigin(AppInfo.Name, version,
                                    SupportUrl: $"{AppAbout.RepoUrl}/issues"),
            Entities          = BuildEntities(config.Current),
            Ledger            = _ledger,
            Groups            = _groups,
            Migrating         = MqttEntityTable.Migrating,
            Retired           = MqttEntityTable.Retired,
            RetiredChannels   = MqttEntityTable.RetiredChannels,
            SetChannelsAsync  = (channels, ct) => _connection!.SetChannelsAsync(channels, ct),
            SetCommandTargets = targets => _connection!.SetCommandTargets(targets),
            Log               = moduleLog,
        });

        _connection = new MqttConnection(new MqttConnectionSetup
        {
            TopicRoot         = MqttEntityTable.TopicRoot,
            Channels          = _publisher.Channels(),
            CommandTargets    = _publisher.CommandTargets(),
            Subscriptions     = [_publisher.BirthMessage(DiscoveryPrefix())],
            Listener          = _publisher,
            DefaultDeviceName = machine => $"{AppInfo.Name} ({machine})",
            RecallEndpoint    = _store.RecallEndpoint,
            RememberEndpoint  = _store.RememberEndpoint,
            CommandRefused    = OnCommandRefused,
            Log               = moduleLog,
        });
    }

    /// <summary>Subscribes to the app's events and applies the stored broker settings. Separate from the
    /// constructor so nothing can fire into a half-built service.</summary>
    public void Start()
    {
        // Seed from what the app already knows, so a connection made now does not wait for the next
        // network change to have anything to say.
        if (_monitor.LastApplied is { } applied) _state.SetNetwork(applied);

        _monitor.SwitchApplied += OnSwitchApplied;
        _vm.StatusesChanged += OnVmStatuses;
        _vm.OperationProgress += OnVmOperation;
        _config.ConfigReloaded += OnConfigReloaded;
        _store.Changed += OnSettingsChanged;
        _groups.Changed += OnGroupsChanged;
        _connection!.StateChanged += OnConnectionState;

        Apply();
    }

    /// <summary>What the connection is doing, as a status line says it.</summary>
    public MqttConnectionState State => _connection?.State ?? MqttConnectionState.Disabled;

    /// <summary>The settings store, for the settings panel to commit through.</summary>
    public MqttConfigStore Settings => _store;

    /// <summary>
    /// Everything the shared settings panel needs, composed here rather than in the Settings window: the
    /// two change callbacks have to reach the connection's apply path, and this is the only object that
    /// holds it. The window supplies no MQTT vocabulary of its own.
    /// </summary>
    /// <remarks>Every callback is raised on the UI thread. None of them blocks it: the applies are
    /// fire-and-forget, the metrics reconcile takes one uncontended lock, and the publish is awaited by
    /// the panel rather than by us.</remarks>
    public MqttPanelSetup CreatePanelSetup() => new()
    {
        Settings        = _store,
        Groups          = _groups,
        TopicRoot       = MqttEntityTable.TopicRoot,
        Activity        = _connection!.Activity,
        ConnectionState = () => State,
        RecallEndpoint  = _store.RecallEndpoint,
        // Must be the expression the connection itself falls back to, or the placeholder promises a name
        // that is never published.
        DefaultDeviceName = $"{AppInfo.Name} ({Environment.MachineName})",

        PublishNow        = () => _connection?.PublishNowAsync() ?? Task.FromResult(false),
        ConnectionChanged = OnSettingsChanged,
        PublishSetChanged = OnPanelPublishSetChanged,
        CommandLabel      = id => _publisher.Entities.NameOf(id),

        // No consumer is named anywhere on this surface — see Services\MqttPanelStrings.cs, which takes
        // the one module string that named one out.
        PublishTitle       = "Publish to an MQTT broker",
        PublishDescription = "Announces this host's network state and its managed VMs to an MQTT broker, "
                           + "and accepts commands back on the same topics.",
        PublishInfo        = "Nothing leaves this machine until a broker host is set and this is on. The "
                           + "connection retries on its own and never holds the app up.",
        PublishGroupsInfo  = "Each group is announced as a set. Switching one off leaves its entities in "
                           + "the document, reading as unavailable, so nothing a receiver has already "
                           + "registered is lost.",
        DeviceIdConsequence = "Anything addressing this host's entities by their old ids stops working "
                            + "until it is repointed.",

        Strings = MqttPanelStrings.Instance,
        Log     = _moduleLog,
    };

    // ── App events → published state ────────────────────────────────────────────

    private void OnSwitchApplied(object? sender, MatchResult result)
    {
        _state.SetNetwork(result);
        SignalPublish();
    }

    private void OnVmStatuses(IReadOnlyList<VmStatus> statuses)
    {
        _state.SetVms(statuses);
        SignalPublish();
    }

    private void OnVmOperation(VmOperationProgress progress)
    {
        _state.SetOperation(progress);
        SignalPublish();
    }

    /// <summary>The state cache has moved. Through the gate rather than straight at the connection: these
    /// events start arriving before the socket does, and a signal made then is fifty failed publishes for
    /// values the connect republishes anyway (see <see cref="MqttPublishGate"/>).</summary>
    private void SignalPublish() => _publish.Signal(_connection?.IsConnected ?? false);

    private void OnConfigReloaded(object? sender, ConfigReloadedEventArgs e)
    {
        string signature = TableSignature(e.Config);
        if (signature == _tableSignature) return;
        _tableSignature = signature;

        // Replaced whole, never mutated: this rebuilds the channels, the command targets and the
        // document in one pass and empties the state topics of entities that have gone. That is also
        // what sheds the power shape just switched away from — the entities it replaced are absent from
        // the new set rather than withheld, so they are announced as removed rather than unavailable.
        _publisher.SetEntities(BuildEntities(e.Config));
    }

    private void OnSettingsChanged()
    {
        // The birth-message filter carries the discovery prefix, so a prefix change needs the
        // subscription rebuilt; it takes effect at the next connect, which the prefix change causes.
        _connection?.SetSubscriptions([_publisher.BirthMessage(DiscoveryPrefix())]);

        var next = _store.Read();
        MqttSettings previous;
        lock (_appliedLock) { (previous, _applied) = (_applied, next); }

        // The transition is claimed under the lock, so the panel's own raise and the store's — both of
        // which arrive for one toggle flip, on different threads — withdraw exactly once between them.
        _ = ReconcileAsync(withdraw: MqttWithdrawal.OnDisable(previous, next));
    }

    /// <summary>
    /// Brings the connection into line with the settings, withdrawing the device first when publishing
    /// has just been switched off.
    ///
    /// <para><b>Withdraw first, apply second, and never the other way round.</b> The apply for a
    /// switched-off configuration disconnects, and <see cref="MqttConnection.RemoveDeviceAsync"/> removes
    /// nothing without a live link — so the reversed order is a silent no-op.</para>
    ///
    /// <para><b>Serialised, and awaiting the real apply rather than the fire-and-forget one.</b> One
    /// toggle flip raises the settings change twice, on two threads: the panel's own, inline on the UI
    /// thread, and the store's, off the pool. An apply landing part-way through a removal publishes
    /// offline over the availability topic the removal has just emptied, which puts the device back as a
    /// permanently-offline ghost — exactly the state being removed.</para>
    /// </summary>
    private async Task ReconcileAsync(bool withdraw)
    {
        try
        {
            await _reconcile.WaitAsync().ConfigureAwait(false);
            try
            {
                if (withdraw) await RemoveDeviceAsync().ConfigureAwait(false);
                if (_connection is { } connection)
                    await connection.ApplyAsync(_store.Read().Connect()).ConfigureAwait(false);
            }
            finally { _reconcile.Release(); }

            ReconcileMetrics();
        }
        // Nothing awaits this task, so an escaping throw would be unobserved.
        catch (Exception ex) { _log.LogError(ex, "MQTT: reconciling the broker settings failed"); }
    }

    /// <summary>Only the metrics hold. The republish a group toggle needs is the publisher's own —
    /// its constructor subscribes to the same event — and asking for one here announces the whole
    /// document twice per toggle.</summary>
    private void OnGroupsChanged() => ReconcileMetrics();

    /// <summary>The panel's "what is announced has changed" raise. The republish it asks for has already
    /// been started by the time this runs: the panel writes the group through <see cref="PublishGroupSet"/>
    /// first, and the publisher subscribes to that event itself. Announcing again here is the double
    /// document per toggle <see cref="OnGroupsChanged"/> exists to avoid.</summary>
    private void OnPanelPublishSetChanged() => ReconcileMetrics();

    private void OnConnectionState(MqttConnectionState state)
    {
        _publish.OnState(state);
        ReconcileMetrics();
    }

    private void OnCommandRefused(MqttCommandRefusal refusal) =>
        _log.LogWarning("MQTT: {Entity} refused ({Outcome}). {Detail}",
                        refusal.EntityId, refusal.Outcome, refusal.Detail);

    // ── Withdrawal ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Takes the device off the broker and switches publishing off. What the Settings control runs, and
    /// the one entry point that leaves nothing behind: the removal disables the connection but not the
    /// setting, so an apply afterwards would announce the device it had just deleted.
    /// </summary>
    /// <remarks>Publishing is switched off only when the removal actually landed. Turning it off after a
    /// removal that never started would leave the device standing on the broker with the one thing that
    /// could still clear it switched off.</remarks>
    public async Task<MqttWithdrawal.Outcome> WithdrawDeviceAsync()
    {
        MqttWithdrawal.Outcome outcome;

        // Under the same gate as an apply, for the same reason — see ReconcileAsync. Released before the
        // write below, whose own reconcile arrives on another thread and would otherwise wait behind it.
        await _reconcile.WaitAsync().ConfigureAwait(false);
        try { outcome = await RemoveDeviceAsync().ConfigureAwait(false); }
        finally { _reconcile.Release(); }

        if (outcome != MqttWithdrawal.Outcome.Removed) return outcome;

        _store.Update(settings => settings.Enabled = false);
        return outcome;
    }

    /// <summary>
    /// The module's removal, classified. <see cref="MqttConnection.RemoveDeviceAsync"/> and not
    /// <c>DiscoveryPublisher</c>'s: the publisher's form withdraws the document and leaves the connection
    /// live, so the next announcement recreates the device it just deleted. The connection's form empties
    /// the command topics, the values and both availability topics too, and ends disabled.
    /// </summary>
    private async Task<MqttWithdrawal.Outcome> RemoveDeviceAsync()
    {
        if (_connection is not { } connection) return MqttWithdrawal.Outcome.NoConnection;

        try
        {
            if (!await connection.RemoveDeviceAsync().ConfigureAwait(false))
            {
                _log.LogInformation("MQTT: no live connection, so the published device was not removed.");
                return MqttWithdrawal.Outcome.NoConnection;
            }

            _log.LogInformation("MQTT: the published device was removed from the broker.");
            return MqttWithdrawal.Outcome.Removed;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "MQTT: removing the published device failed");
            return MqttWithdrawal.Outcome.Failed;
        }
    }

    // ── Composition ─────────────────────────────────────────────────────────────

    private MqttEntitySet BuildEntities(AppConfig config) =>
        MqttEntityTable.Build(new MqttEntitySpec
        {
            VmNames              = [.. config.VirtualMachines.Select(v => v.Name)],
            // Read per announcement pass, so a rule edit reaches the options with no rebuild.
            RuleSwitches         = () => [.. _config.Current.RuleSwitches],
            State                = _state,
            VmIp                 = _vm.GetCachedVmIp,
            ReCheckNetwork       = _reCheckNetwork,
            RepairHostNetworking = _repairHostNetworking,
            Power                = (name, kind, _) =>
            {
                // BeginPowerAction is the same entry point the dashboard's buttons use, and returns
                // as soon as the WMI job is requested; its outcome arrives on OperationProgress.
                _vm.BeginPowerAction(name, kind, VmOpOrigin.Mqtt);
                return Task.CompletedTask;
            },
            OverrideSwitch       = (name, switchName, _) => _monitor.ManualOverrideAsync(name, switchName),
            // Read once here, not per pass: the two shapes are different entities, so a flip has to
            // reach SetEntities for the shape being left behind to be evicted.
            PowerButtons         = config.Mqtt.PowerButtons,
        });

    /// <summary>Whether each VM's power verbs are published as one button per verb rather than as one
    /// select of them. Setting it writes config.json, whose reload rebuilds the entity table — see
    /// <see cref="OnConfigReloaded"/>, which is what evicts the shape being switched away from.</summary>
    public bool PowerButtons => _store.PowerButtons;

    /// <inheritdoc cref="PowerButtons"/>
    public void SetPowerButtons(bool on) => _store.SetPowerButtons(on);

    /// <summary>Applying is idempotent, so a settings write that changed nothing the connection reads
    /// leaves the projection identical and never bounces the socket.</summary>
    private void Apply() => _connection?.Apply(_store.Read().Connect());

    private void ReconcileMetrics() =>
        _metrics.Update(_groups.IsEnabled(MqttEntityTable.MetricsGroup),
                        State == MqttConnectionState.Connected);

    /// <summary>A blank stored prefix means the module's default, so the filter is composed from that
    /// rather than from an empty string.</summary>
    private string DiscoveryPrefix()
    {
        string stored = _store.Read().DiscoveryPrefix;
        return string.IsNullOrWhiteSpace(stored) ? MqttSettings.DefaultDiscoveryPrefix : stored;
    }

    /// <summary>What the entity table is composed from. Composed by <see cref="MqttEntityTable"/>, which
    /// is linked into the test assembly — a rule that decides whether the document is re-announced is
    /// not one to leave in this file, which nothing tests.</summary>
    private static string TableSignature(AppConfig config) =>
        MqttEntityTable.Signature(
            config.VirtualMachines.Select(v => v.Name), config.Mqtt.PowerButtons);

    // ── Lifecycle ───────────────────────────────────────────────────────────────

    /// <summary>Detaches synchronously — so no event reaches a torn-down connection — and starts the
    /// blocking teardown on the pool. The teardown publishes offline before the socket goes, and takes
    /// up to ~3 s, which is why exit overlaps it rather than waiting on it in line.</summary>
    public Task BeginDisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return Task.CompletedTask;

        _monitor.SwitchApplied -= OnSwitchApplied;
        _vm.StatusesChanged -= OnVmStatuses;
        _vm.OperationProgress -= OnVmOperation;
        _config.ConfigReloaded -= OnConfigReloaded;
        _store.Changed -= OnSettingsChanged;
        _groups.Changed -= OnGroupsChanged;
        if (_connection is not null) _connection.StateChanged -= OnConnectionState;
        _metrics.Release();

        return Task.Run(() =>
        {
            try
            {
                _connection?.Dispose();
                _publisher.Dispose();
                _store.Dispose();
            }
            catch (Exception ex) { _log.LogError(ex, "MQTT teardown failed"); }
        });
    }

    public void Dispose()
    {
        try { BeginDisposeAsync().Wait(TeardownBudget); }
        catch { /* never hold the exit on a teardown */ }
    }
}
