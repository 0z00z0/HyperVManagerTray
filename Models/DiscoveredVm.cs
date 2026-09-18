namespace HyperVManagerTray.Models;

/// <summary>
/// A VM discovered on the local Hyper-V host (may or may not be in config). <paramref name="Id"/> is the VM
/// ID Hyper-V assigns and is what identifies it; <paramref name="Name"/> is only shown. The NIC is the one a
/// newly managed VM is seeded with, by its adapter ID, or null when the VM has no network adapter.
/// </summary>
public sealed record DiscoveredVm(string Id, string Name, string? NicId, string NicName);
