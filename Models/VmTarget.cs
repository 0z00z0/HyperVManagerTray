namespace HyperVManagerTray.Models;

/// <summary>A Hyper-V virtual machine this app manages, plus the NIC and fallback switch to use.</summary>
public sealed class VmTarget
{
    /// <summary>Exact Hyper-V VM name (as shown in Hyper-V Manager).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Name of the VM's network adapter to reconnect (default "Network Adapter").</summary>
    public string NicName { get; set; } = "Network Adapter";

    /// <summary>
    /// Action to perform when the bridged network is lost (switch goes to Fallback).
    /// Supported values: "pause", "save", "shutdown". Null or "none" = no action.
    /// </summary>
    public string? OnBridgeLostAction { get; set; }

    /// <summary>
    /// Seconds to wait after the bridge is lost before executing OnBridgeLostAction.
    /// The action is cancelled if the bridge is restored within this window.
    /// Recommended values: 5, 10, 30, 60. Default 30.
    /// </summary>
    public int OnBridgeLostDelaySeconds { get; set; } = 30;
}
