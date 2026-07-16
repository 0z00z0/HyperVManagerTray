namespace HyperVManagerTray.Helpers;

/// <summary>
/// Decides whether a failure message should be announced to the user right now, and remembers what was
/// actually announced so a persisting failure isn't re-toasted on every network blip.
///
/// <para><b>Why this is its own class.</b> It is the state that <c>App.NotifyIfApplyFailed</c> used to
/// hold in a bare <c>_lastNotifiedFailure</c> field, lifted out so the two rules below are testable
/// without a WinUI host — the same reason <see cref="NetworkStatusUi"/>, <see cref="VmStateUi"/> and
/// <see cref="AdapterNameRules"/> are pure. It deliberately adds NO display vocabulary of its own: the
/// message it takes is produced by <see cref="NetworkStatusUi.FailureMessage"/>, and this class only
/// ever decides whether to say it.</para>
///
/// <para><b>Rule 1 — one action, one report.</b> A pass the user explicitly asked for (Re-check,
/// Override) is reported by the command that ran it, so the automatic balloon stands down. Both used to
/// fire: <c>ManualOverrideAsync</c> published <c>SwitchApplied</c> (balloon 1: "Could not connect
/// 'vDev'…") and then returned Failed to the caller (balloon 2: "Could not move vDev… Nothing was
/// changed."). Two toasts for one click, and balloon 2 asserted a fact balloon 1 contradicted. The
/// command owns the report because it knows things this path cannot — that it was an override at all,
/// and the "not a managed VM" outcome, which never reaches an apply pass.</para>
///
/// <para><b>Rule 2 — latch only what was announced.</b> <see cref="Next"/> does NOT record the message;
/// the caller records it via <see cref="MarkAnnounced"/> once the balloon has genuinely been shown. The
/// old code latched before calling ShowBalloon, which may suppress the toast when the dashboard is
/// visible — so a failure first seen with the dashboard open was marked "announced" though nothing was
/// ever shown, and once the dashboard closed the identical message short-circuited forever. The balloon
/// was never shown at all.</para>
/// </summary>
public sealed class FailureAnnouncer
{
    private readonly object _lock = new();
    private string? _lastAnnounced;

    /// <summary>
    /// The message to announce now, or null for "say nothing".
    ///
    /// <para><paramref name="failureMessage"/> is <see cref="NetworkStatusUi.FailureMessage"/>'s output:
    /// null means the pass was not a failure, which re-arms the latch so a genuinely new failure always
    /// announces itself. <paramref name="userInitiated"/> is
    /// <see cref="Services.MatchResult.UserInitiated"/> — see rule 1 above.</para>
    /// </summary>
    public string? Next(string? failureMessage, bool userInitiated)
    {
        lock (_lock)
        {
            if (failureMessage is null)
            {
                _lastAnnounced = null;   // recovered — re-arm for the next failure
                return null;
            }

            // Rule 1: the command reports this one. The latch is left untouched rather than armed: this
            // path announced nothing, and marking it announced would suppress the automatic balloon for
            // a failure that later persists on its own with nobody watching.
            if (userInitiated) return null;

            // Rule 2: offer it, but do not assume it lands. Only MarkAnnounced records it.
            return failureMessage == _lastAnnounced ? null : failureMessage;
        }
    }

    /// <summary>Records that <paramref name="message"/> was actually shown to the user, so the identical
    /// failure is not re-announced while it persists. Call this ONLY once the balloon has really been
    /// posted — never merely because one was requested.</summary>
    public void MarkAnnounced(string message)
    {
        lock (_lock) _lastAnnounced = message;
    }
}
