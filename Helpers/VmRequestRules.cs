using HyperVManagerTray.Models;

namespace HyperVManagerTray.Helpers;

/// <summary>Where a power request stands against the machine's current state.</summary>
public enum VmRequestStanding
{
    /// <summary>The machine is not at, and not moving towards, the state the request asks for.</summary>
    Needed,

    /// <summary>The machine is already in the state the request asks for; there is nothing to do.</summary>
    AlreadyThere,

    /// <summary>The machine is already moving towards the state the request asks for. Issuing a second
    /// request now is refused by Hyper-V with return value 32775 and changes nothing.</summary>
    AlreadyUnderWay,
}

/// <summary>
/// Whether a power request needs to be issued at all, decided from the machine's mapped state name
/// alone. Pure and host-free, so every combination can be tested without a Hyper-V host and without a
/// machine that can be persuaded to sit in a transient state.
///
/// <para>A rule's autostart can run several times while a dock settles, and a machine that is already
/// <c>Starting</c> looked no different from one that was <c>Off</c> to a guard that recognised only the
/// finished state. Each extra request was refused, and the refusal replaced the running start's
/// progress on the card with a code.</para>
/// </summary>
public static class VmRequestRules
{
    /// <summary>The finished state a request drives the machine to.</summary>
    private static string Target(VmOpKind kind) => kind switch
    {
        VmOpKind.Pause    => "Paused",
        VmOpKind.Save     => "Saved",
        VmOpKind.Shutdown => "Off",
        _                 => "Running",   // Start / Resume
    };

    /// <summary>The transient states that are already on the way to <see cref="Target"/>. Both names
    /// <see cref="WmiVmMapper.MapState"/> can produce for a machine coming up are listed for the start
    /// direction: a cold boot reads Starting, a resume-from-Saved reads Resuming.</summary>
    private static string[] OnTheWay(VmOpKind kind) => kind switch
    {
        VmOpKind.Pause    => ["Pausing"],
        VmOpKind.Save     => ["Saving"],
        VmOpKind.Shutdown => ["Stopping"],
        _                 => ["Starting", "Resuming"],
    };

    /// <summary>
    /// Where a request stands against <paramref name="state"/>, a state name as
    /// <see cref="WmiVmMapper.MapState"/> produces it. An unknown, empty or unrelated state answers
    /// <see cref="VmRequestStanding.Needed"/>: nothing has been established that would let the request
    /// be skipped.
    /// </summary>
    public static VmRequestStanding Standing(VmOpKind kind, string? state)
    {
        if (string.IsNullOrWhiteSpace(state)) return VmRequestStanding.Needed;
        var name = state.Trim();

        if (name.Equals(Target(kind), StringComparison.OrdinalIgnoreCase))
            return VmRequestStanding.AlreadyThere;

        foreach (var moving in OnTheWay(kind))
            if (name.Equals(moving, StringComparison.OrdinalIgnoreCase))
                return VmRequestStanding.AlreadyUnderWay;

        return VmRequestStanding.Needed;
    }

    /// <summary>True when the request would change nothing, for either reason.</summary>
    public static bool AlreadySatisfied(VmOpKind kind, string? state) =>
        Standing(kind, state) != VmRequestStanding.Needed;
}
