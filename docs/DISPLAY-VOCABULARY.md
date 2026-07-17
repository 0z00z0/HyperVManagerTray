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

## Why the "tell" channel is still a legacy `Shell_NotifyIcon` balloon

Windows 11 renders these through the legacy-balloon compatibility path, and #53 proposed replacing them
with Windows App SDK `AppNotifications` (`AppNotificationManager`) — modern toasts, Action Center
presence, and survival of Focus Assist. **That migration is not available to this app**, for a reason
that has nothing to do with effort:

> **"Apps running with administrator privileges (elevated) cannot send or receive app notifications."**
> — [App notifications overview](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/), *Limitations*

`app.manifest` requests `requireAdministrator`, and **not optionally**: the Hyper-V WMI calls this app
exists to make need it. So the app is elevated 100 % of the time, and elevated is precisely the case
Microsoft excludes. The quickstart is explicit about the failure mode: `Show` **"will fail silently and
no notification will be displayed."**

**Silence is what makes this disqualifying rather than merely unsupported.** The app's report channel
carries every failure it knows about (#37). A migration whose failure mode is *no exception, no error,
no toast* would turn every one of those reports back into the silence #37 spent its effort eliminating —
and would look, from the code, exactly like success.

The obvious guard does not close the hole. `AppNotificationManager.IsSupported()` tests for the
[Singleton package](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps#dependencies-on-additional-msix-packages)
— which this app also lacks, since it publishes `WindowsAppSDKSelfContained=true` — but it says nothing
about elevation. On a machine where the Singleton package happens to be present (any framework-dependent
WinUI 3 app installs it), `IsSupported()` returns **true**, `Show()` is called, and it fails silently
anyway. The guard would pass and the toast would still never appear. **There is no delivery signal to
test against**, so a fallback cannot detect the failure it exists to catch.

This is not a version gap to wait out: [microsoft/WindowsAppSDK#3595](https://github.com/microsoft/WindowsAppSDK/issues/3595)
has been open since April 2023 and is labelled **`feature proposal`**, not `bug` — elevated support is a
request, not a fix in flight.

So the legacy balloon stays, on merit: it is the only channel that **actually delivers** for an elevated
process. Its known costs are real and accepted — ~1–2 s latency, and Focus Assist drops it. Balloons and
modern toasts *both* lose to Focus Assist here; the modern one would merely lose more quietly. If this
is ever revisited, the only real path is architectural (a non-elevated helper process owning the
notification surface), which is a much larger change than swapping an API — not a re-try of #53.

## History

| Issue | What it settled |
|---|---|
| #37 | Failures must be surfaced; the tray balloon becomes the app's report channel; no surface may infer success. |
| #40 | The rename's one-consent/one-verified-outcome pair — the bounded exception above. |
| #45 | A balloon answering a window's own button must not be suppressed by that window being visible. |
| #51 | The rule above, written down. `NetworkActions`' repair and add-rule commands moved from modals to balloons, ending the split where two adjacent buttons in one Settings row answered in two vocabularies. The `XamlRoot` constraint that seemed to force the split turned out not to exist. |
| #53 | The "tell" channel stays a legacy balloon. Windows App SDK `AppNotifications` is unavailable to an elevated process and fails **silently** — the one failure mode this app's report channel cannot accept. See the section above. |
