using HyperVManagerTray.Models;

namespace HyperVManagerTray.Helpers;

/// <summary>The machine operations in flight, reduced to the one ring the tray icon shows. UI thread only.</summary>
internal sealed class TrayProgress
{
    /// <summary>The ring moves in steps of this many percent, so a job's per-second updates repaint rarely.</summary>
    internal const int Step = 5;

    private readonly Dictionary<string, (VmOpKind Kind, VmOpPhase Phase, int? Percent, DateTime Since)> _ops =
        new(StringComparer.OrdinalIgnoreCase);

    public bool Active => _ops.Count > 0;

    /// <summary>The average of the operations that report a percentage, in whole steps; null when none does.</summary>
    public int? Percent
    {
        get
        {
            var known = _ops.Values.Where(o => o.Percent is not null).Select(o => Math.Clamp(o.Percent!.Value, 0, 100)).ToList();
            if (known.Count == 0) return null;
            return (int)(known.Average() / Step) * Step;
        }
    }

    public void Update(VmOperationProgress progress, DateTime now)
    {
        if (progress.Phase is VmOpPhase.Succeeded or VmOpPhase.Failed)
            _ops.Remove(progress.VmId);
        else
            _ops[progress.VmId] = (progress.Kind, progress.Phase, progress.Percent, now);
    }

    // A guest shutdown, and a request for a change already under way, never send an outcome.
    public void Settle(IEnumerable<VmStatus> statuses)
    {
        foreach (var s in statuses)
            if (_ops.TryGetValue(s.Id, out var op) && VmStateUi.ReachedTarget(op.Kind, s))
                _ops.Remove(s.Id);
    }

    public void Expire(DateTime now)
    {
        foreach (var id in _ops.Where(o => VmStateUi.IsOverlayExpired(o.Value.Kind, o.Value.Phase, now - o.Value.Since))
                               .Select(o => o.Key).ToList())
            _ops.Remove(id);
    }
}
