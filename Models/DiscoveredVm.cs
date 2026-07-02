namespace HyperVManagerTray.Models;

/// <summary>A VM discovered on the local Hyper-V host (may or may not be in config).</summary>
public sealed record DiscoveredVm(string Name, string NicName);
