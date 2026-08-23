using HyperVManagerTray.Models;

namespace HyperVManagerTray.Services;

/// <summary>The picture the MQTT channels publish from: the last applied <see cref="MatchResult"/>, the
/// last per-VM status snapshot, and each VM's most recent power operation. Each slot is swapped whole,
/// so a reader on the publish thread always sees a consistent snapshot.</summary>
public sealed class MqttStateCache
{
    private static readonly IReadOnlyDictionary<string, VmStatus> NoVms =
        new Dictionary<string, VmStatus>(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, string> NoOperations =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // Serialises the read-modify-write in SetOperation only. Readers never take it.
    private readonly object _operationLock = new();

    private volatile MatchResult? _network;
    private volatile IReadOnlyDictionary<string, VmStatus> _vms = NoVms;
    private volatile IReadOnlyDictionary<string, string> _operations = NoOperations;

    /// <summary>The last applied network result, or null before the first pass completes.</summary>
    public MatchResult? Network => _network;

    public void SetNetwork(MatchResult result) => _network = result;

    /// <summary>The last known status of one VM, or null when it has not been seen yet.</summary>
    public VmStatus? Vm(string vmName) =>
        _vms.TryGetValue(vmName ?? string.Empty, out var status) ? status : null;

    public void SetVms(IReadOnlyList<VmStatus>? statuses)
    {
        var map = new Dictionary<string, VmStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var status in statuses ?? []) map[status.Name] = status;
        _vms = map;
    }

    /// <summary>The most recent power operation reported for one VM, or null when none has been.</summary>
    public string? Operation(string vmName) =>
        _operations.TryGetValue(vmName ?? string.Empty, out var text) ? text : null;

    /// <summary>Records one VM's latest operation. Locked, unlike the whole-slot writes above: it
    /// copies the live map and puts it back, and two VMs progressing at once — a rule's autostart runs
    /// a power action per VM, each on its own thread — would otherwise lose an entry.</summary>
    public void SetOperation(VmOperationProgress progress)
    {
        lock (_operationLock)
        {
            _operations = new Dictionary<string, string>(_operations, StringComparer.OrdinalIgnoreCase)
            {
                [progress.VmName ?? string.Empty] = DescribeOperation(progress),
            };
        }
    }

    /// <summary>One VM operation as a sensor state: the verb, its phase, and whatever the job said.</summary>
    public static string DescribeOperation(VmOperationProgress progress) =>
        string.IsNullOrWhiteSpace(progress.Message)
            ? $"{progress.Kind} — {progress.Phase}"
            : $"{progress.Kind} — {progress.Phase}: {progress.Message!.Trim()}";
}
