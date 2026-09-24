using HyperVManagerTray.Models;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// What a failed power operation says, in the two lengths every surface needs: a line short enough for
/// the card's state column, and the sentence behind it for the card's tooltip, the balloon and the
/// published entity.
/// </summary>
/// <param name="Card">At most <see cref="VmFailureText.CardLimit"/> characters, so the card can add its
/// dismiss mark and still fit.</param>
/// <param name="Full">One or two sentences: what was being done, what stopped it, and what can be done
/// about it. Any Hyper-V number lives at the end of this, never on the card.</param>
public readonly record struct VmFailure(string Card, string Full);

/// <summary>
/// Every failure a power operation can report, as words rather than a number. Pure text, so the wording
/// is testable without a Hyper-V host and cannot drift between the card, the balloon and the broker.
///
/// <para>Hyper-V answers a refused request with a vendor return value — 32775 for a machine that is not
/// in a state where the requested change is possible — and that number carries no meaning to anyone
/// reading a card. The number stays useful in vm-power.log and at the end of the tooltip; the sentence
/// is what a person is shown.</para>
/// </summary>
public static class VmFailureText
{
    /// <summary>The card's state column truncates past 30 characters, and a dismissible failure spends
    /// two of those on its mark, so every card line here is built to fit in what is left.</summary>
    public const int CardLimit = 28;

    /// <summary>
    /// The mark a failure carries on the card until it is cleared: a multiplication sign, the same
    /// one-character-plus-tooltip idiom the address column already uses (<see cref="GuestAddressUi"/>).
    /// U+00D7 rather than a heavier cross because the card is set in the brand mono face, whose fixed
    /// advance is what makes the dashboard's width arithmetic exact — a character the face lacks falls
    /// back to a wider one and under-measures the row.
    /// </summary>
    public const string DismissMark = "×";

    /// <summary>The words that make the mark mean something, appended to a failure's tooltip.</summary>
    public const string DismissHint = "Select the × to clear this.";

    // ── Operation words ──────────────────────────────────────────────────────────

    /// <summary>The operation as a noun, for the start of a card line.</summary>
    private static string Noun(VmOpKind kind) => kind switch
    {
        VmOpKind.Start    => "Start",
        VmOpKind.Resume   => "Resume",
        VmOpKind.Pause    => "Pause",
        VmOpKind.Save     => "Save",
        VmOpKind.Shutdown => "Shutdown",
        _                 => "",
    };

    /// <summary>The operation as a lower-case noun, for the middle of a sentence.</summary>
    private static string Thing(VmOpKind kind) => kind switch
    {
        VmOpKind.Start    => "start",
        VmOpKind.Resume   => "resume",
        VmOpKind.Pause    => "pause",
        VmOpKind.Save     => "save",
        VmOpKind.Shutdown => "shutdown",
        _                 => "operation",
    };

    /// <summary>What did not happen to the machine, as the opening of a sentence.</summary>
    private static string CouldNot(VmOpKind kind) => kind switch
    {
        VmOpKind.Start    => "The machine could not be started",
        VmOpKind.Resume   => "The machine could not be resumed",
        VmOpKind.Pause    => "The machine could not be paused",
        VmOpKind.Save     => "The machine could not be saved",
        VmOpKind.Shutdown => "The machine could not be shut down",
        _                 => "The machine could not be changed",
    };

    /// <summary>The card line for a failure that has nothing to add beyond where to look. The longest
    /// operation noun keeps this inside <see cref="CardLimit"/>.</summary>
    public static string NoReasonGiven(VmOpKind kind) =>
        Noun(kind) is { Length: > 0 } noun ? $"{noun} failed, see the log" : "Failed, see the log";

    // ── Hyper-V return values ────────────────────────────────────────────────────

    /// <summary>The card line, the clause explaining it and the clause saying what to do, for one
    /// Hyper-V return value. Shared by the state-change request and the guest shutdown request, which
    /// draw their return values from the same vendor range.</summary>
    private static (string Card, string Because, string Action) Meaning(uint returnValue) => returnValue switch
    {
        // Invalid state for this operation — the value a second request against a machine that is
        // already starting comes back with.
        5 or 4097 or 32775 => ("Not possible in this state",
            "the machine is not in a state where that change is possible",
            "Wait for the change already under way to finish, then try again."),
        32778 => ("Not enough memory",
            "the host could not set aside the memory the machine needs",
            "Free memory on the host, or lower the machine's memory, then try again."),
        32769 => ("Access denied",
            "Hyper-V refused the request as not permitted",
            "Administrator rights are needed for this."),
        32774 => ("The machine is in use",
            "the machine is held by another operation",
            "Wait for that operation to finish, then try again."),
        32777 => ("The machine is unavailable",
            "Hyper-V cannot reach the machine",
            "Check it still exists on this host."),
        3 or 32772 => ("Hyper-V timed out",
            "Hyper-V did not finish the request in the time it allows itself",
            "The machine's state will show whether it finished anyway."),
        4099 => ("Hyper-V is busy",
            "Hyper-V is occupied with other work",
            "Try again in a moment."),
        1 or 32770 => ("Not supported",
            "Hyper-V does not support that change for this machine",
            "Use a different action from the card."),
        4 or 32773 or 32776 => ("The request was rejected",
            "Hyper-V rejected the request as malformed",
            "What was sent is recorded in vm-power.log."),
        32771 => ("Outcome unknown",
            "Hyper-V could not say whether the request took effect",
            "The machine's state will show what happened."),
        _ => ("Hyper-V refused the request",
            "Hyper-V refused the request without saying why",
            "What happened is recorded in vm-power.log."),
    };

    /// <summary>A state-change request Hyper-V answered with a refusal rather than a job.</summary>
    public static VmFailure ForRequestRefusal(VmOpKind kind, uint returnValue)
    {
        var (card, because, action) = Meaning(returnValue);
        return new VmFailure(card, $"{CouldNot(kind)}: {because}. {action} (Hyper-V code {returnValue}.)");
    }

    /// <summary>
    /// A graceful shutdown the guest's own shutdown service declined. Worded apart from
    /// <see cref="ForRequestRefusal"/> because the request reached the guest and came back: the machine
    /// is untouched, and the way on is to save it or to shut it down from inside.
    /// </summary>
    public static VmFailure ForShutdownRefusal(uint returnValue)
    {
        var (card, because, _) = Meaning(returnValue);
        return new VmFailure(card,
            $"The guest declined the shutdown request: {because}. Nothing was changed — save the machine "
          + $"instead, or shut it down from inside the guest. (Hyper-V code {returnValue}.)");
    }

    // ── Job outcomes ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A Hyper-V job that ended in failure, carrying the host's own description of why. The description
    /// always reaches the tooltip whole; the card gets a recognised short form where the description is
    /// one this app knows, and the opening words of the description otherwise.
    /// </summary>
    public static VmFailure ForJobFailure(VmOpKind kind, string? errorDescription)
    {
        var detail = (errorDescription ?? "").Trim();
        if (detail.Length == 0)
            return new VmFailure(NoReasonGiven(kind),
                $"{CouldNot(kind)} and Hyper-V gave no reason. What happened is recorded in vm-power.log.");

        return new VmFailure(CardLineFor(detail), $"{CouldNot(kind)}: {EndWithStop(detail)}");
    }

    /// <summary>
    /// The card line for a description Hyper-V wrote. Two failures are recognised by the words Hyper-V
    /// uses for them, since both have a short form a person can act on; anything else keeps the opening
    /// of the description, cut at a word. The match is on Hyper-V's English text, so a host answering in
    /// another language falls through to the trim — the whole description still reaches the tooltip
    /// either way.
    /// </summary>
    private static string CardLineFor(string detail)
    {
        if (detail.Contains("memory", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("Insufficient system resources", StringComparison.OrdinalIgnoreCase))
            return "Not enough memory";
        if (detail.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
            return "Access denied";
        return TrimToCard(detail);
    }

    /// <summary>The opening of <paramref name="text"/>, cut at a word and marked as cut.</summary>
    private static string TrimToCard(string text)
    {
        if (text.Length <= CardLimit) return text;
        var cut = text[..(CardLimit - 1)];
        int space = cut.LastIndexOf(' ');
        if (space > 0) cut = cut[..space];
        return cut.TrimEnd(' ', ',', '.', ';', ':') + "…";
    }

    /// <summary>
    /// A job that never reported a terminal state inside the deadline. The deadline is a fallback, not a
    /// verdict, so the wording says the work is probably still running rather than that it failed.
    /// </summary>
    public static VmFailure ForNoResult(VmOpKind kind, TimeSpan waited)
    {
        var span = Duration(waited);
        return new VmFailure($"No result after {span}",
            $"Hyper-V had not reported the {Thing(kind)} finishing after {span}. It is probably still "
          + "running; the machine's state will show the outcome.");
    }

    /// <summary>Whole minutes where the wait is one, seconds otherwise.</summary>
    private static string Duration(TimeSpan waited)
    {
        int seconds = (int)Math.Round(waited.TotalSeconds);
        if (seconds >= 60 && seconds % 60 == 0)
        {
            int minutes = seconds / 60;
            return minutes == 1 ? "1 minute" : $"{minutes} minutes";
        }
        return seconds == 1 ? "1 second" : $"{seconds} seconds";
    }

    // ── Faults on the way ────────────────────────────────────────────────────────

    /// <summary>The request itself could not be made — the Hyper-V interface threw before answering.</summary>
    public static VmFailure ForRequestFault(VmOpKind kind) =>
        new("Request did not go through",
            $"{CouldNot(kind)}: the request to Hyper-V did not go through. What went wrong is recorded "
          + "in vm-power.log.");

    /// <summary>The request was accepted but the job behind it could not be followed.</summary>
    public static VmFailure ForTrackingFault(VmOpKind kind) =>
        new("Progress could not be read",
            $"The {Thing(kind)} was accepted, but its progress could not be followed. It may still "
          + "finish; the machine's state will show the outcome. What went wrong is recorded in "
          + "vm-power.log.");

    // ── Nothing was attempted ────────────────────────────────────────────────────

    /// <summary>Hyper-V's management service is down, so no request was made at all.</summary>
    public static VmFailure ServiceNotRunning(VmOpKind kind) =>
        new("Service not running",
            $"{CouldNot(kind)}: "
          + HyperVServiceNames.DisplayName(HyperVServiceKind.VirtualMachineManagement)
          + " is not running. Start it from the dashboard's services row, then try again.");

    /// <summary>The machine this app is configured to manage is not on the host.</summary>
    public static VmFailure MachineNotFound(VmOpKind kind) =>
        new("Machine not found",
            $"{CouldNot(kind)}: it is no longer on this host. Check it still exists in Hyper-V Manager, "
          + "or remove it from the managed machines.");

    /// <summary>The guest has no shutdown integration service to ask.</summary>
    public static VmFailure NoShutdownSupport() =>
        new("No shutdown support",
            "The guest cannot be asked to shut down: its shutdown integration service is not available. "
          + "Save the machine instead, or shut it down from inside the guest.");

    /// <summary>
    /// The host was not in a fit state to be asked, so a rule's automatic action was never attempted.
    /// Reported per machine rather than logged alone: nothing else would account for a machine the rule
    /// names staying where it is.
    /// </summary>
    /// <param name="reason">Why the host could not be asked, as a sentence.</param>
    public static VmFailure NotAttempted(VmOpKind kind, string reason) =>
        new("Host not ready",
            $"{CouldNot(kind)}: {EndWithStop(reason.Trim())} The {Thing(kind)} was not attempted. Start "
          + "the machine from the dashboard once the services row reports Hyper-V running.");

    /// <summary>Ends <paramref name="text"/> with a full stop where it has no closing punctuation.</summary>
    private static string EndWithStop(string text) =>
        text.Length > 0 && ".!?".Contains(text[^1]) ? text : text + ".";
}
