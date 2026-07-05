namespace HyperVManagerTray.Models;

/// <summary>
/// Live state + metrics for a Hyper-V VM, assembled by <see cref="Helpers.WmiVmMapper"/> from the
/// native WMI summary info (<c>root\virtualization\v2</c>). Memory/VHD are raw bytes; the computed
/// helpers convert for display.
/// </summary>
public sealed class VmStatus
{
    public string  Name        { get; set; } = "";
    public string  State       { get; set; } = "Unknown";  // Running, Off, Paused, Saved, …
    public string  Switch      { get; set; } = "";          // current virtual switch name
    public string  StatusDesc  { get; set; } = "";          // joined StatusDescriptions (e.g. "Saving, 47 %")
    public int     Cpu         { get; set; }                // % of host
    public long    MemAssigned { get; set; }                // bytes
    public long    MemMax      { get; set; }                // bytes
    public string? Uptime      { get; set; }
    public long    VhdBytes    { get; set; }                // filled by the separate VHD query

    public bool IsRunning => State.Equals("Running", StringComparison.OrdinalIgnoreCase);
    public bool IsPaused  => State.Equals("Paused",  StringComparison.OrdinalIgnoreCase);
    public bool IsSaved   => State.Equals("Saved",   StringComparison.OrdinalIgnoreCase);

    public double MemAssignedMb => MemAssigned / 1048576.0;
    public double VhdGb          => VhdBytes   / 1073741824.0;

    /// <summary>Assigned memory as a fraction (0–1) of the VM's maximum, for a meter.</summary>
    public double MemoryFraction => MemMax > 0 ? Math.Clamp((double)MemAssigned / MemMax, 0, 1) : 0;
}
