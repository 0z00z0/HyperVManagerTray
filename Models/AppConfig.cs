namespace HyperVNetworkSwitcher.Models;

public sealed class FallbackAction
{
    public string VirtualSwitch { get; set; } = "Default Switch";
    public List<string> TargetVms { get; set; } = [];
}

public sealed class AppConfig
{
    public List<VmTarget> VirtualMachines { get; set; } = [];
    public List<NetworkRule> Rules { get; set; } = [];
    public FallbackAction Fallback { get; set; } = new();
}
