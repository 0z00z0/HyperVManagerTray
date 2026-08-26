using HyperVManagerTray.Models;

namespace HyperVManagerTray.Services;

/// <summary>
/// What the app last observed, held for the publish thread to read (issue #75). Fed entirely from the
/// events the app already raises — <c>NetworkMonitor.SwitchApplied</c>, <c>VmService.StatusesChanged</c>
/// and <c>VmService.OperationProgress</c>. <b>Nothing here polls.</b>
/// </summary>
/// <remarks>
/// Each slot is swapped whole behind a volatile reference, so a reader on the publish thread always
/// sees a consistent snapshot rather than a half-updated dictionary. <see cref="SetOperation"/> is the
/// exception and takes a lock: it copies the live map and puts it back, and two VMs progressing at once
/// — a rule's autostart runs a power action per VM, each on its own thread — would otherwise lose one.
/// </remarks>
public sealed class MqttStateCache
{
    private volatile MatchResult? _network;
    private volatile IReadOnlyDictionary<string, VmStatus> _vms =
        new Dictionary<string, VmStatus>(StringComparer.OrdinalIgnoreCase);
    private volatile IReadOnlyDictionary<string, string> _operations =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly object _operationLock = new();

    /// <summary>The last applied network outcome, or null before the first evaluation.</summary>
    public MatchResult? Network => _network;

    public void SetNetwork(MatchResult? result) => _network = result;

    /// <summary>The last status for one VM, or null when none has been seen.</summary>
    public VmStatus? Vm(string vmName) =>
        vmName is not null && _vms.TryGetValue(vmName, out var status) ? status : null;

    public void SetVms(IReadOnlyList<VmStatus>? statuses)
    {
        var map = new Dictionary<string, VmStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var status in statuses ?? [])
            if (!string.IsNullOrEmpty(status?.Name)) map[status.Name] = status;
        _vms = map;
    }

    /// <summary>The last operation message for one VM, or null when none has been reported.</summary>
    public string? Operation(string vmName) =>
        vmName is not null && _operations.TryGetValue(vmName, out var text) ? text : null;

    public void SetOperation(VmOperationProgress progress)
    {
        if (string.IsNullOrEmpty(progress.VmName)) return;
        lock (_operationLock)
        {
            var map = new Dictionary<string, string>(_operations, StringComparer.OrdinalIgnoreCase)
            {
                [progress.VmName] = Describe(progress),
            };
            _operations = map;
        }
    }

    /// <summary>The operation as one line: the verb, its phase, and whatever the WMI job said.</summary>
    internal static string Describe(VmOperationProgress progress) =>
        string.IsNullOrWhiteSpace(progress.Message)
            ? $"{progress.Kind} {progress.Phase}"
            : $"{progress.Kind} {progress.Phase}: {progress.Message}";
}
