# ToDo handling

How work is tracked in HyperVManagerTray. Short version: **GitHub Issues are the source of truth; a
local git-ignored [`TODO.md`](TODO.md) mirrors them for at-a-glance/offline work; the two are kept
in sync both ways.**

This mirrors the convention used in the sibling `ChargeKeeper` repo — same process, labels adapted
to this app's own areas.

## Two surfaces

| Surface | Role | Committed? |
|---|---|---|
| [GitHub Issues](https://github.com/0z00z0/HyperVManagerTray/issues) | **Source of truth.** Every real task is an issue, labelled per the taxonomy below. | n/a (GitHub) |
| [`TODO.md`](TODO.md) | **Local mirror** — a grouped, tagged snapshot for quick scanning and offline planning. | No — git-ignored, local-only. |
| `TODO-HANDLING.md` (this file) | The convention itself. | Yes. |

When the two disagree, **GitHub wins** — `TODO.md` is regenerated/corrected from it.

## The "always sync" rule

`TODO.md` and the issue tracker must never drift. In practice:

- **New work** → create a GitHub issue first (labelled), then add a line to `TODO.md`. Don't put a
  task only in `TODO.md`.
- **Status change** (start / finish / block / descope) → update GitHub (labels and/or open↔closed)
  **and** move the line to the matching `TODO.md` section, in the same session.
- **Done** → close the issue with a comment referencing the implementing commit(s), apply the `done`
  status label (kept on the closed issue for retroactive consistency — see the taxonomy below); move
  the line to `TODO.md` → _Recently done_.
- **Descoped / won't-do** → close with a comment saying why. No `done` label in that case — the close
  reason alone documents it.
- Refresh the `_Last synced_` date in `TODO.md` whenever you touch it.

An automated assistant working in this repo keeps both surfaces in sync as part of any task that
changes work status — it is not a manual afterthought.

## Ad-hoc requests from chat

Requests arrive mid-conversation — "Settings should store X", "the icons are wrong", a screenshot of
a broken layout. Capture them at **chat speed** and enrich them **in the background**, never the
other way round:

1. **File first, immediately.** Create the issue straight from what was asked, near-verbatim, plus
   whatever came with it (screenshot, log, config). No code reading, no root-causing, no design, no
   deliberation. The point is to have it recorded in seconds and keep the conversation moving.
2. **Label on a best guess.** One `type` + the most likely `area:`. A wrong label is cheap and gets
   corrected in step 3.
3. **Then hand it to an agent.** Spawn a subagent whose entire job is to read that issue and fill it
   out — reproduce or confirm the behaviour, root-cause it, name the affected files, sketch the
   implementation, add acceptance criteria — and write the result back into the issue body. It works
   while the conversation carries on.
4. **Sync `TODO.md`**, as with any new work.

Why: investigating before filing spends the user's turnaround on work a background agent does just as
well, and risks the request being half-remembered by the time it's written down. The issue is the
handoff — get it on paper, then deepen it.

## Label taxonomy

Every open issue carries **one `type`**, **one or more `area:`**, and optionally a **status**.

**Type** (what kind of work)
- `enhancement` — new user-facing capability.
- `bug` — defect / regression.
- `refactor` — internal cleanup / restructuring, no behaviour change.
- `performance` — efficiency / hot-path work.
- `ci` — CI, build, release, packaging.
- `documentation` — docs only.

**Area** (what part of the app)
- `area:tray` — tray menu / notify icon (`UI/TrayMenu.cs`).
- `area:dashboard` — dashboard window / VM status UI (`UI/DashboardWindow.xaml*`).
- `area:network` — network adapter monitoring / auto-switch rules (`Services/NetworkMonitor.cs`,
  `Services/AdapterMatcher.cs`).
- `area:vm-power` — Hyper-V VM power control, start/stop/pause/save (`Services/HyperVManager.cs`,
  `Services/VmService.cs`).
- `area:core` — app core / services: config, logging, startup, updates
  (`Services/ConfigManager.cs`, `Services/FileLogger.cs`, `Services/StartupManager.cs`,
  `Services/UpdateChecker.cs`, `Helpers/SelfHealWatchdog.cs`).
- `area:installer` — installer / winget / release packaging (`installer/`).
- `area:brand` — shared ZeroZero brand integration: the About box and the `0z0-shared` sibling
  dependency it pulls in.

**Status** (optional)
- `blocked` — waiting on an external dependency or an explicit go-ahead.
- `idea` — brainstorm backlog, not committed/scheduled work. Removed once an item is scheduled.
- `done` — completed. Applied alongside closing so a closed-and-shipped issue is visually
  distinguishable from one closed as won't-do/descoped. Not part of ChargeKeeper's original set —
  added here specifically to make the retroactive labeling pass legible; keep using it going forward
  for consistency.

## `TODO.md` format

Grouped by working status, one line per issue:

```
- [ ] **#<issue>** Title — `type` `area:*` `status` — optional one-line note
```

Sections, in order: **In progress · Scheduled / ready · Blocked · Backlog (ideas) · Recently done**.
Closed issues stay briefly under _Recently done_ for context, then age out.

## Commit convention

One commit per issue where practical, message referencing the issue number, so history maps to the
tracker. See the repo's commit history for examples.
