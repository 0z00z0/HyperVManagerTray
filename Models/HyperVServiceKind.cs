namespace HyperVManagerTray.Models;

/// <summary>The two Windows services the dashboard can start and stop (issue #114).</summary>
public enum HyperVServiceKind
{
    /// <summary>Hyper-V Virtual Machine Management (vmms). Every Hyper-V call the app makes goes through it.</summary>
    VirtualMachineManagement,
    /// <summary>Hyper-V Host Compute Service (vmcompute). WSL 2, Windows Sandbox and Docker depend on it.</summary>
    HostCompute,
}

/// <summary>A service's state as Windows reports it, reduced to what the dashboard shows.</summary>
public enum HyperVServiceState
{
    /// <summary>Not read yet, or the read failed.</summary>
    Unknown,
    Stopped,
    Starting,
    Running,
    Stopping,
    /// <summary>The service does not exist on this host (Hyper-V not installed, or the feature is off).</summary>
    NotInstalled,
}

/// <summary>Names and wording for <see cref="HyperVServiceKind"/>. Pure, so the guard and its tests share it.</summary>
public static class HyperVServiceNames
{
    public static readonly IReadOnlyList<HyperVServiceKind> All =
        [HyperVServiceKind.VirtualMachineManagement, HyperVServiceKind.HostCompute];

    /// <summary>The service name Windows knows it by.</summary>
    public static string ServiceName(HyperVServiceKind kind) => kind switch
    {
        HyperVServiceKind.VirtualMachineManagement => "vmms",
        HyperVServiceKind.HostCompute              => "vmcompute",
        _                                          => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The name Windows itself gives the service (its "Display name" in services.msc). Kept only
    /// for where the technical name must stay findable — currently the dashboard row's tooltip, next to
    /// <see cref="ServiceName"/>. Everywhere a person reads about the service, <see cref="DisplayName"/> is
    /// what is shown instead.</summary>
    public static string WindowsServiceName(HyperVServiceKind kind) => kind switch
    {
        HyperVServiceKind.VirtualMachineManagement => "Hyper-V Virtual Machine Management",
        HyperVServiceKind.HostCompute              => "Hyper-V Host Compute Service",
        _                                          => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The label used wherever a person reads about the service: the dashboard row, the stop
    /// confirmation, the tray tooltip, the MQTT entity name and the rule editor. Named for what the service
    /// is used for rather than for its Windows service name (<see cref="WindowsServiceName"/>).</summary>
    public static string DisplayName(HyperVServiceKind kind) => kind switch
    {
        HyperVServiceKind.VirtualMachineManagement => "VM management",
        HyperVServiceKind.HostCompute              => "WSL, Docker and Sandbox",
        _                                          => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// Whether a VM start must bring <paramref name="kind"/> up first: stopped or moving, and vmms also
    /// when it is missing. Unknown (not read yet) is not down, so the first second after start-up changes
    /// nothing.
    /// </summary>
    public static bool IsDown(HyperVServiceKind kind, HyperVServiceState state) => state switch
    {
        HyperVServiceState.Stopped or HyperVServiceState.Starting or HyperVServiceState.Stopping => true,
        HyperVServiceState.NotInstalled => kind == HyperVServiceKind.VirtualMachineManagement,
        _                               => false,
    };

    /// <summary>The state as a word on the dashboard.</summary>
    public static string StateText(HyperVServiceState state) => state switch
    {
        HyperVServiceState.Stopped      => "Stopped",
        HyperVServiceState.Starting     => "Starting",
        HyperVServiceState.Running      => "Running",
        HyperVServiceState.Stopping     => "Stopping",
        HyperVServiceState.NotInstalled => "Not installed",
        _                               => "Unknown",
    };
}
