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

    /// <summary>The name Windows displays, used in prompts, balloons and the log.</summary>
    public static string DisplayName(HyperVServiceKind kind) => kind switch
    {
        HyperVServiceKind.VirtualMachineManagement => "Hyper-V Virtual Machine Management",
        HyperVServiceKind.HostCompute              => "Hyper-V Host Compute Service",
        _                                          => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The short row label on the dashboard, where the full display name does not fit.</summary>
    public static string ShortLabel(HyperVServiceKind kind) => kind switch
    {
        HyperVServiceKind.VirtualMachineManagement => "VM management",
        HyperVServiceKind.HostCompute              => "Host compute",
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
