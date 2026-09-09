using ZeroZero.Lifecycle;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// What the log says about the single-instance lock, one sentence per outcome. Pure text, so the
/// four outcomes can be pinned without a WinUI host.
///
/// <para>The two refusals are different facts about the machine — another copy of this app is
/// running, versus a name this process may not open at all — and one sentence for both hides a mutex
/// name the app can never take behind a message that reads like an ordinary second launch. The two
/// acquisitions differ the same way: a free name says nothing about the previous instance, while an
/// abandoned one says it died without releasing, which is the teardown this app is actually hit
/// by.</para>
/// </summary>
internal static class SingleInstanceLog
{
    public static string Message(SingleInstanceOutcome outcome) => outcome switch
    {
        SingleInstanceOutcome.TakenFree =>
            "The single-instance lock was free — this launch is the only instance.",

        SingleInstanceOutcome.TakenAbandoned =>
            "The single-instance lock was left behind by an instance that did not exit cleanly; "
          + "this launch has taken it.",

        SingleInstanceOutcome.RefusedHeld =>
            "Another instance already holds the single-instance lock — this launch is exiting.",

        SingleInstanceOutcome.RefusedDenied =>
            "The single-instance lock exists but this process may not open it — another session's "
          + "instance, or one holding it with rights this process does not have. This launch is exiting.",

        // Unreachable while the component reports four outcomes; a fifth must still say something
        // rather than write a blank line into the crash log.
        _ => $"The single-instance lock reported an unrecognised outcome ({outcome}).",
    };
}
