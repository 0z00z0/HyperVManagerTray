using HyperVManagerTray.Models;

namespace HyperVManagerTray.Services;

/// <summary>
/// The picture the MQTT channels publish from (issue #75): the last <see cref="MatchResult"/> the
/// network monitor applied, the last per-VM status snapshot, and each VM's most recent power
/// operation. Written from the app's existing events, read by the entity payload providers.
///
/// <para>Nothing here polls. Each slot is swapped whole (copy-on-write dictionaries, volatile
/// references) so a reader on the publish thread always sees a consistent snapshot rather than a
/// half-updated one.</para>
/// </summary>
public sealed class MqttStateCache
{
    private static readonly IReadOnlyDictionary<string, VmStatus> NoVms =
        new Dictionary<string, VmStatus>(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, string> NoOperations =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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

    public void SetOperation(VmOperationProgress progress)
    {
        var updated = new Dictionary<string, string>(_operations, StringComparer.OrdinalIgnoreCase)
        {
            [progress.VmName ?? string.Empty] = DescribeOperation(progress),
        };
        _operations = updated;
    }

    /// <summary>One VM operation as a sensor state: the verb, its phase, and whatever the job said.</summary>
    public static string DescribeOperation(VmOperationProgress progress) =>
        string.IsNullOrWhiteSpace(progress.Message)
            ? $"{progress.Kind} — {progress.Phase}"
            : $"{progress.Kind} — {progress.Phase}: {progress.Message!.Trim()}";
}
