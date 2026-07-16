# Display vocabulary — which channel says what

**One rule: ask with a modal, tell with a balloon.**

This app has exactly two ways to speak to the user, and the choice between them is decided by **what
kind of thing is being said** — never by which window the code happens to be running in.

| | Channel | Use it for |
|---|---|---|
| **Ask** | `NativeMethods.Confirm`, `TextPromptWindow`, `RenameAdapterWindow` | A **question that gates a side effect**. The user's answer changes what happens next, and the flow genuinely cannot continue without it. Blocking is the *point*. |
| **Tell** | the tray balloon (the `Action<string,string,bool> notify` delegate threaded through `TrayMenu`, `SettingsWindow`, `DashboardWindow`) | The **outcome of a command the user invoked** — success or failure, from any surface. |

Everything else follows from that.

## Why "the surface decides" is *not* the rule

It is the natural guess, and issue #51 proposed it, on the premise that a modal needs a `XamlRoot` that a
bare tray click cannot supply. **That premise is false in this codebase.** `NativeMethods.Info/Warn/
Error/Confirm` are `MessageBoxW` with `hWnd = IntPtr.Zero` and `MB_TOPMOST` — Win32, parentless,
callable from any thread and any surface, with no `XamlRoot` anywhere. Both channels work everywhere.

So the choice was never forced by a constraint, which means it has to be made on merit — and the merit
argument is the one the codebase had already made twice before anyone wrote it down:

- `ManagedVmActions` — *"Used from Settings too, deliberately: a second display vocabulary for the same
  outcome is the thing to avoid."*
- `SettingsWindow`'s `notify` parameter — *"the outcome of a re-check or an override reads identically
  wherever it was started from, and inventing a second display vocabulary for the same events is what
  issue #37 spent its effort undoing."*

A rule keyed on the surface would contradict both: it would make the *same* outcome read one way from
the tray and another from Settings, which is precisely the second vocabulary #37 removed. Keying on the
**kind of speech act** instead gives one vocabulary per act, everywhere.

## The corollaries

1. **A report the user's own click asked for is never suppressed.** Pass
   `suppressWhenDashboardVisible: false` whenever a balloon answers a button. The window that raised it
   is visible *by definition* — that is what issue #45 found on the dashboard's own Connect button, and
   the default would swallow exactly the reports that were most certainly wanted.
2. **Failures are always told** (issues #37/#40). Choosing the quieter channel must never become
   choosing silence. A failure reported nowhere is the bug both issues existed to fix.
3. **A report states only what was verified** (issue #37). "Nothing needed repairing" is honest;
   "your network is fine" is a claim the command never checked.
4. **Message text lives in a pure class** (`NetworkStatusUi`, `AdapterNameRules`), not at the call site.
   The claim a message makes is the testable part, and re-deriving it per surface is how two surfaces
   drift into two vocabularies in the first place.
5. **One action, one report.** A command that ends by delegating to another command lets *that* one
   speak — `AddCurrentAsBridgedAsync` finishes with `ReCheckNetworkAsync()` and adds no balloon of its
   own. See `FailureAnnouncer`'s rule 1 for the same principle on the automatic path.

## The one exception, and why it is bounded

`AdapterRenameFlow` shows its **single outcome** in a modal (`NativeMethods.Info/Warn`), not a balloon.

That is deliberate and it is not "because it runs from Settings". It is because that outcome is the
**counterpart of a device-mutating consent**: issue #40 collapsed a four-dialog consent stack down to
exactly *one* dialog plus exactly *one* verified outcome, and the two are a matched pair — the user
answered a blocking question that dropped their network link, and the verified answer to it belongs in
the same conversation, not in a corner notification that can be missed.

The test for whether a future flow qualifies is that pairing, not its location: **did a blocking consent
prompt immediately precede it, for a mutation the user cannot easily undo?** If not, it is a report, and
reports are balloons.

## History

| Issue | What it settled |
|---|---|
| #37 | Failures must be surfaced; the tray balloon becomes the app's report channel; no surface may infer success. |
| #40 | The rename's one-consent/one-verified-outcome pair — the bounded exception above. |
| #45 | A balloon answering a window's own button must not be suppressed by that window being visible. |
| #51 | The rule above, written down. `NetworkActions`' repair and add-rule commands moved from modals to balloons, ending the split where two adjacent buttons in one Settings row answered in two vocabularies. The `XamlRoot` constraint that seemed to force the split turned out not to exist. |
