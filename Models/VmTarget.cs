using System.Text.Json.Serialization;

namespace HyperVManagerTray.Models;

/// <summary>A Hyper-V virtual machine this app manages, plus the network adapter to reconnect.</summary>
/// <remarks>
/// Identified by <see cref="Id"/> alone. <see cref="Name"/> and <see cref="NicName"/> are labels: shown
/// where the VM is not currently readable, and the input a settings document written before identifiers
/// existed is migrated from. Nothing finds a VM or an adapter by them.
/// </remarks>
public sealed class VmTarget
{
    /// <summary>The VM ID Hyper-V assigns (a GUID). Empty while an older entry has not been identified
    /// yet — such an entry needs attention and is acted on by nothing.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The VM's name as last seen. Shown only.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The ID of the VM's network adapter to reconnect. Null means the VM's only adapter, and
    /// fails when it has several.</summary>
    public string? NicId { get; set; }

    /// <summary>The adapter's name as last seen. Shown only, and the migration input for an entry
    /// written before <see cref="NicId"/> existed.</summary>
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

    /// <summary>A reference to this VM for display and for the calls that act on it. Derived, so never
    /// written to the file.</summary>
    [JsonIgnore]
    public VmRef Ref => new(Id, Name);
}

/// <summary>A VM as the app passes it around: the ID that identifies it, and the name that is shown.</summary>
public sealed record VmRef(string Id, string Name)
{
    /// <summary>The name, or the ID when the name is blank, so a message never names nothing.</summary>
    public string Shown => string.IsNullOrWhiteSpace(Name) ? Id : Name;
}
