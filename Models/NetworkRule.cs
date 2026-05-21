namespace HyperVNetworkSwitcher.Models;

public sealed class RuleConditions
{
    /// <summary>Physical host adapter MAC, e.g. "48:65:EE:18:86:EF"</summary>
    public string? AdapterMac { get; set; }

    /// <summary>CIDR the adapter's IP must fall within, e.g. "10.0.0.0/23"</summary>
    public string? IpCidr { get; set; }
}

public sealed class NetworkRule
{
    public string Name { get; set; } = string.Empty;
    public int Priority { get; set; } = 100;
    public RuleConditions Conditions { get; set; } = new();
    public string VirtualSwitch { get; set; } = string.Empty;
    public List<string> TargetVms { get; set; } = [];
}
