namespace HyperVNetworkSwitcher.Models;

public sealed class VmTarget
{
    public string Name { get; set; } = string.Empty;
    public string NicName { get; set; } = "Network Adapter";
    public string DefaultSwitch { get; set; } = "Default Switch";
}
