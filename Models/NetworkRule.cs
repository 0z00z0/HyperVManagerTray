using HyperVManagerTray.Helpers;

namespace HyperVManagerTray.Models;

/// <summary>
/// Match conditions for a <see cref="NetworkRule"/>.  Both are optional; when both are set
/// the host adapter must satisfy both (logical AND).  A rule with no conditions matches the
/// current primary adapter unconditionally.
/// </summary>
public sealed class RuleConditions
{
    /// <summary>Physical host adapter MAC, e.g. "AA:BB:CC:DD:EE:FF". Null = don't match on MAC.</summary>
    public string? AdapterMac { get; set; }

    /// <summary>CIDR the adapter's IP must fall within, e.g. "10.0.0.0/23". Null = don't match on IP.</summary>
    public string? IpCidr { get; set; }
}

/// <summary>
/// Maps a recognised host network (by adapter MAC and/or IP subnet) to the Hyper-V virtual
/// switch the listed VMs should be connected to.
/// </summary>
public sealed class NetworkRule
{
    /// <summary>Human-readable rule name, shown in the tray status popup.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Evaluation order — lower numbers are checked first.</summary>
    public int Priority { get; set; } = 100;

    /// <summary>Conditions the active host adapter must satisfy for this rule to match.</summary>
    public RuleConditions Conditions { get; set; } = new();

    /// <summary>Hyper-V virtual switch to connect to when this rule matches.</summary>
    public string VirtualSwitch { get; set; } = string.Empty;

    /// <summary>Names of the VMs to reconnect to <see cref="VirtualSwitch"/>.</summary>
    public List<string> TargetVms { get; set; } = [];

    /// <summary>
    /// When true, the <see cref="TargetVms"/> are started (or resumed if paused) the moment this
    /// rule becomes active.  They are never auto-stopped when the rule deactivates.
    /// </summary>
    public bool AutoStart { get; set; } = false;

    /// <summary>
    /// What happens to Hyper-V Virtual Machine Management when this rule becomes active (issue #114).
    /// Null leaves the service alone, which is what every rule written before the setting existed reads as.
    /// </summary>
    public RuleServiceAction? VmManagementService { get; set; }

    /// <summary>
    /// What happens to the Hyper-V Host Compute Service when this rule becomes active: null leaves it alone,
    /// and Start starts it. Never Stop — that service is stopped only from the dashboard, because stopping
    /// it also stops WSL 2, Windows Sandbox and Docker. <c>ConfigManager.Load</c> reads a stored Stop as null.
    /// </summary>
    public RuleServiceAction? HostComputeService { get; set; }

    /// <summary>
    /// Seconds between this rule becoming active and a service stop it asks for. The stop is cancelled if
    /// another rule becomes active first, so a brief network flap saves no VMs. Same presets as a VM's
    /// on-bridge-lost delay.
    /// </summary>
    public int ServiceStopDelaySeconds { get; set; } = 30;

    /// <summary>The setting for <paramref name="kind"/>; <see cref="RuleServiceAction.None"/> when unset.</summary>
    public RuleServiceAction ServiceAction(HyperVServiceKind kind) =>
        (kind == HyperVServiceKind.VirtualMachineManagement ? VmManagementService : HostComputeService)
        ?? RuleServiceAction.None;

    /// <summary>
    /// The services to stop when this rule becomes active. Only Virtual Machine Management: a rule never
    /// stops the Host Compute Service, whatever a hand-edited file says.
    ///
    /// <para>Empty while the rule auto-starts VMs: those VMs need the services, and a stop would save the
    /// very VMs the rule has just started.</para>
    /// </summary>
    public IReadOnlyList<HyperVServiceKind> ServicesToStop() =>
        AutoStart && TargetVms.Count > 0
            ? []
            : [.. new[] { HyperVServiceKind.VirtualMachineManagement }
                  .Where(k => ServiceAction(k) == RuleServiceAction.Stop && ServiceStopGuard.MayStopUnattended(k))];

    /// <summary>The services to start when this rule becomes active, vmms first.</summary>
    public IReadOnlyList<HyperVServiceKind> ServicesToStart() =>
        [.. HyperVServiceNames.All.Where(k => ServiceAction(k) == RuleServiceAction.Start)];
}

/// <summary>What a network rule does to one Hyper-V service when it becomes active (issue #114).</summary>
public enum RuleServiceAction
{
    /// <summary>Left alone.</summary>
    None,
    Start,
    /// <summary>Stopped after the rule's delay, with every running VM saved first.</summary>
    Stop,
}
