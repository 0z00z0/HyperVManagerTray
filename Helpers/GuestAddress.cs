using System.Net;
using System.Net.Sockets;
using HyperVManagerTray.Services;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// Whether the address a guest reports belongs to the network its virtual switch leads to.
///
/// <para><b>Why this is an enum and not a bool.</b> Same reason as <see cref="SwitchUplinkState"/>:
/// "does not fit" and "could not be established" are different states of knowledge, and a surface
/// handed a bool would render the second as the first. Only <see cref="Foreign"/> is a finding.</para>
/// </summary>
public enum GuestAddressFit
{
    /// <summary>Nothing has been established — no comparand, no address, or a host that could not be
    /// read. The default (0), deliberately: an unstamped verdict must claim nothing.</summary>
    Unknown,

    /// <summary>The guest's address is inside the network its switch leads to.</summary>
    Fits,

    /// <summary>The guest's address belongs to a different network from the one its switch leads to —
    /// the state a guest is left in when it keeps an address across a move between switches.</summary>
    Foreign,
}

/// <summary>
/// The decision behind <see cref="GuestAddressFit"/>, pure and separate from the host readings that
/// feed it, so every answer can be tested without a Hyper-V host, a dock to unplug, or a guest whose
/// address client can be persuaded to misbehave.
///
/// <para><b>Where the comparand comes from.</b> The network a switch leads to is the network of the
/// host adapter that switch is bound to — the address the rule evaluation already reads off that
/// adapter (<c>AdapterMatcher.BuildResult</c>, which sources it from the Hyper-V vNIC when the
/// physical adapter is bridged and has therefore lost its own address). No separate traversal from a
/// switch to its management-OS vNIC exists here, and none is needed: the applied rule already names
/// both the switch and the adapter behind it.</para>
/// </summary>
public static class GuestAddressRules
{
    /// <summary>
    /// The verdict for one machine.
    ///
    /// <para>Every guard answers <see cref="GuestAddressFit.Unknown"/> rather than guessing, and each
    /// one covers a state that would otherwise be reported as a fault:</para>
    /// <list type="bullet">
    /// <item><description><paramref name="uplink"/> must be <see cref="SwitchUplinkState.Connected"/>.
    /// That single test carries three of the guards at once — an internal or private switch answers
    /// <see cref="SwitchUplinkState.NotExternal"/> and has no host adapter to compare against, a switch
    /// whose adapter is unplugged answers <see cref="SwitchUplinkState.NoUplink"/> and any address on it
    /// is already reported by the uplink surfaces, and a host nobody could read answers
    /// <see cref="SwitchUplinkState.Unknown"/>.</description></item>
    /// <item><description>The machine must be on the switch the applied rule names. On any other switch
    /// the app holds no reading of what that switch leads to, so it has nothing to compare.</description></item>
    /// <item><description>There must be a network to compare against, and an address to compare. A
    /// machine that has not been given an address yet is not wrong, it is early.</description></item>
    /// </list>
    /// </summary>
    /// <param name="guestIpv4">The address the guest reports, or null/empty when it reports none.</param>
    /// <param name="vmSwitchId">The switch the machine's adapter is on, by ID.</param>
    /// <param name="appliedSwitchId">The switch the applied rule put it on, by ID.</param>
    /// <param name="hostCidr">The network of the host adapter that switch is bound to, in
    /// <c>network/prefix</c> form, or null/empty when no adapter was resolved.</param>
    /// <param name="uplink">What that switch has for a way out.</param>
    public static GuestAddressFit For(
        string? guestIpv4, string? vmSwitchId, string? appliedSwitchId, string? hostCidr, SwitchUplinkState uplink)
    {
        if (uplink != SwitchUplinkState.Connected) return GuestAddressFit.Unknown;
        if (string.IsNullOrWhiteSpace(vmSwitchId) || string.IsNullOrWhiteSpace(appliedSwitchId))
            return GuestAddressFit.Unknown;
        if (!HostIdentity.Same(vmSwitchId, appliedSwitchId)) return GuestAddressFit.Unknown;
        if (string.IsNullOrWhiteSpace(hostCidr)) return GuestAddressFit.Unknown;
        if (!IPAddress.TryParse((guestIpv4 ?? "").Trim(), out var ip) ||
            ip.AddressFamily != AddressFamily.InterNetwork)
            return GuestAddressFit.Unknown;

        return AdapterMatcher.IsInCidr(ip, hostCidr) ? GuestAddressFit.Fits : GuestAddressFit.Foreign;
    }

    /// <summary>
    /// True only for the one move outcome that may ask a guest for a new address: the switch actually
    /// changed under it.
    ///
    /// <para>Asked through this method rather than compared inline, for the reason
    /// <see cref="SwitchUplinkRules.HasNoConnectionOut"/> exists: a call site that writes
    /// <c>outcome != SwitchMoveOutcome.Failed</c> folds in
    /// <see cref="SwitchMoveOutcome.AlreadyThere"/>, and the app starts bouncing the network of every
    /// machine that was already where it belongs on every evaluation — the exact no-op the skip guard in
    /// <c>HyperVManager.ApplySwitchCore</c> exists to prevent.</para>
    /// </summary>
    public static bool ShouldRenewAfter(SwitchMoveOutcome outcome) =>
        outcome == SwitchMoveOutcome.Moved;
}

/// <summary>
/// How <see cref="GuestAddressFit"/> is shown on a machine's card: one character in the address
/// column, with the words in its tooltip. Held here with the verdict rather than in
/// <c>NetworkStatusUi</c> so the mark and the rule it renders cannot drift apart.
///
/// <para><b>Why these two characters.</b> The card is set in the brand mono face, whose fixed advance
/// is what makes <see cref="DashboardSizing"/>'s width arithmetic exact rather than an estimate — a
/// character the face lacks falls back to a wider one and under-measures the row. Both marks below
/// were checked against the shipped <c>CascadiaMono.ttf</c> and are covered by it; the obvious
/// alternatives (a warning triangle, a circular-arrow refresh) are not, which is why they are not
/// used here.</para>
/// </summary>
public static class GuestAddressUi
{
    /// <summary>"Not equal to" — the address is not of this network. U+2260, covered by the brand face.</summary>
    public const string ForeignMark = "≠";

    /// <summary>An ellipsis — something is in flight. U+2026, covered by the brand face, and the same
    /// character the card's power overlay already uses for work in progress.</summary>
    public const string RenewingMark = "…";

    /// <summary>The mark for a machine's address column, or empty when there is nothing to say.
    /// A renewal in flight wins: it is the newer fact, and it explains the mark that preceded it.</summary>
    public static string MarkFor(GuestAddressFit fit, bool renewing) =>
        renewing                        ? RenewingMark
      : fit == GuestAddressFit.Foreign  ? ForeignMark
      :                                   "";

    /// <summary>The words behind the mark, or empty when there is no mark.</summary>
    public static string TooltipFor(GuestAddressFit fit, bool renewing) =>
        renewing                        ? "Being updated — the machine has been asked for a new address."
      : fit == GuestAddressFit.Foreign  ? "This address belongs to a different network from the one this switch leads to."
      :                                   "";

    /// <summary>The address column's text: the mark, a space, then the address. Either part may be
    /// empty — a machine with no address still shows the mark while a renewal is in flight.</summary>
    public static string AddressText(string? address, GuestAddressFit fit, bool renewing)
    {
        var mark = MarkFor(fit, renewing);
        var ip   = (address ?? "").Trim();
        if (mark.Length == 0) return ip;
        return ip.Length == 0 ? mark : $"{mark} {ip}";
    }
}
