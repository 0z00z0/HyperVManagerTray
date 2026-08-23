# Home Assistant over MQTT

How this app publishes to a broker, what it accepts back, and the standing properties of the code that
does it. The shared protocol layer is documented in `0z0-shared/docs/zerozero-mqtt.md`; this file covers
only what is specific to HyperVManagerTray.

## Where it lives

| File | Responsibility |
|---|---|
| `Models/MqttSettings.cs` | The `mqtt` section of config.json, and its mapping to `MqttOptions` |
| `Services/MqttService.cs` | The live connection: reconciles config, feeds state, routes commands |
| `Services/MqttEntitySet.cs` | Every published entity, declared once |
| `Services/MqttStateCache.cs` | The picture the entity payload providers read |
| `Helpers/MqttReconcile.cs` | The reconcile decisions, lifted out so they are assertable |
| `Helpers/MqttIdentity.cs` | Node id and discovery prefix — the address retained topics are filed under |
| `Helpers/MqttCommandGate.cs` | What an inbound command is allowed to do |
| `Helpers/MqttMetricsHold.cs` | Whether the metrics WMI subscription is held |
| `Helpers/MqttPanelSeam.cs` | Settings-panel edits mapped onto the stored settings |

`MqttService` holds the live broker connection and is deliberately **not** linked into the test
assembly. Every decision it takes therefore lives in `MqttReconcile`, `MqttIdentity`,
`MqttCommandGate`, `MqttMetricsHold` and `MqttPanelSeam`, all of which are pure and all of which are
asserted directly. A guard added inline in `MqttService` is a guard nothing tests.

## Configuration

`AppConfig.Mqtt` is never null in a loaded config: `ConfigManager.Load` replaces a missing section, or
one hand-edited to `"mqtt": null`, with defaults. The defaults are inert — `Enabled` is false — so a
config that predates the section reads as "configured, disabled" rather than crashing every consumer.

Two rules govern writes to the section:

- **`ConfigManager.With(...)` must carry every field.** It builds the object serialised over config.json
  wholesale, so a field it omits is not left alone — it is written back as null and lost. Omitting
  `Mqtt` means every unrelated mutator blanks the whole MQTT section.
- **`mqtt` is on `NonNetworkProperties`.** A reload caused by an MQTT edit reports
  `ConfigReloadedEventArgs.AffectsNetwork` false. That list is an **exclusion list**: a property not
  named in it counts as network-affecting. A new MQTT field therefore needs no entry — the section name
  covers it — but a new *non*-MQTT settings field does, or editing a broker port moves a VM's switch.

`RememberMqttEndpoint` is its own mutator rather than part of the broker batch. It writes back a fact
the connection discovered, under `ConfigManager`'s own read-modify-write, so it cannot be rolled back by
a snapshot the Settings window has been holding since it opened.

### The broker password

`MqttSettings.Password` is plain text beside the rest of the configuration. At runtime it lives behind
`IMqttCredentialStore`, so an encrypted store is an implementation swap rather than a config change.
`MqttSettings.ToOptions()` deliberately omits it: `MqttOptions` carries a credential *reference*.

### Publish categories

Four groups, each announced through its entity's `Include` gate, so switching one off empties its
entities' retained configs rather than leaving them in Home Assistant as entities that have stopped
reporting.

| Category | Default | Cost |
|---|---|---|
| Host network | On | None — the app already holds the state |
| VM state and controls | On | None |
| VM diagnostics | On | None |
| VM metrics | **Off** | Holds a 2.5 s WMI poll |

VM metrics defaults off because CPU, memory and VHD figures only flow while something holds
`VmService.SubscribeMetrics()`, and an idle app otherwise runs no in-process WMI at all. The other three
publish what the app already knows.

## Config relocation

config.json lives in `AppInfo.DataDir` (`%AppData%\HyperVManagerTray`). `ConfigMigration` copies a
config left beside the executable by an earlier build into that location at startup.

**It is a copy, never a move.** An install rolled back to an earlier build must still find the file it
reads.

A **failed** copy suppresses the blank-slate default (`ConfigMigration.MayCreateDefault`). Writing the
blank slate would occupy the target, so the next start's `ConfigMigration.Run` would report `NotNeeded`
and the user's real config would stay beside the executable, unread, for good. The failure is reported
as a tray balloon and logged as an error; the copy is attempted again at the next start.

The relocation runs **before** the log-level read, so an upgrade's first start already honours the
user's own `logLevel`. It therefore also runs before any logger exists, which is why it reports through
an `Action<Exception>` and its outcome is logged afterwards.

config.json is not a `Content` item in the project and is excluded from the installer's `[Files]`, so no
publish folder can drop a blank config over one an older build left in `{app}`.

## The reconcile

A config reload, and the initial start, both go through `MqttService.ReconcileAsync`.

- **Ticketed.** Each run carries its own config snapshot. A later reload supersedes an earlier one still
  queued behind the gate, so an older snapshot landing last cannot roll newer settings back.
- **One at a time**, behind `_reconcileGate`. Two reconciles overlapping across the
  clear-abandoned-identity await could land in either order.
- **Off the reload thread.** `ConfigReloaded` can arrive on the UI thread, and a reconcile may dispose a
  connection, so the work is dispatched to the thread pool. No method of `MqttService` touches WinUI.
- **`_lock` is for field access only.** Nothing that blocks may run under it: the event pump takes it on
  WMI watcher threads and the Settings panel takes it on the UI thread.

**Retire before rebuild, off the lock.** A retiring session publishes a retained `offline` on an
availability topic its replacement usually shares. Tearing it down *after* the rebuild lands that
`offline` on top of the new session's `online`, leaving the device permanently unavailable in Home
Assistant. The teardown also blocks for up to three seconds, which is the second reason it does not run
under `_lock`.

### Abandoning an identity

The node id and the discovery prefix together are the address every retained topic is filed under: the
node id is the state, command and availability path; the prefix is where the discovery configs live.
They are compared as one value (`MqttIdentity`), because a change to either strands everything published
under the old pair.

Disabling MQTT needs no special handling — `OnStoppingAsync` clears the identity as the session stops. A
node id or prefix edited while publishing is **on** has no such moment: the connection is re-applied, or
rebuilt, against the new address, and the old one's configs, availability and state stay retained on the
broker for ever as entities Home Assistant shows as permanently unavailable.
`MqttService.ClearAbandonedIdentityAsync` empties them first.

That clear is best-effort by construction. It runs against a live session or not at all: with the broker
down there is nothing retained the process can reach, and blocking the reconcile on a broker that is not
answering would cost the reconnect the new settings are for. It is bounded by `ClearBudget` (10 s).

A device name edited on its own is not an abandonment — it republishes over the same topics.

## Publishing

Everything published comes from events the app already raises — `NetworkMonitor.SwitchApplied`,
`VmService.StatusesChanged` and `VmService.OperationProgress`. **Nothing here polls.**

`MqttStateCache` swaps each slot whole (copy-on-write dictionaries behind volatile references), so a
reader on the publish thread always sees a consistent snapshot. `SetOperation` is the exception and
takes a lock: it copies the live map and puts it back, and two VMs progressing at once — a rule's
autostart runs a power action per VM, each on its own thread — would otherwise lose one entry.

### The metrics hold

The metrics subscription is held only while the publish toggle is on **and** the broker session is live.
The connection raises no "connected" event, so liveness is **sampled** in `MqttService.Pump` rather than
pushed. `VmService.StatusesChanged` fires at least once a minute (App's safety-net refresh), so the hold
follows a connect or a drop within that window, and errs towards not holding — the safe direction for a
subscription that costs a WMI poll.

### Entity naming

Each VM owns a topic slug derived from its name. Two names that reduce to the same slug are separated by
a numeric suffix: the slug is both the state topic and the command topic, so a collision would route one
VM's commands to another.

A VM entry hand-edited to `"name": null` is tolerated. Without that guard it throws out of the slug
dictionary and takes the whole integration down, silently, until the file is fixed.

## Commands

Every VM verb passes through `MqttCommandGate.Power`, which asks `VmStateUi.AllowedVerbs` — the same
gate the dashboard's own buttons use. A remote write therefore reaches nothing the dashboard cannot.

A verb the current state does not allow is **refused outright**, not queued and not attempted. Hyper-V
answers a disallowed verb with 0x8007 in any case, and an attempt leaves a failure in the log that reads
as a fault rather than as a refusal.

A switch-override is refused unless the switch name is one this host actually announced, so a stale
option in Home Assistant cannot bind a switch no rule names.

The two host-network commands run through the same `NetworkActions` the tray and Settings use, so a
remote re-check or repair is the identical operation. Their outcome goes to `mqtt.log` rather than to a
tray balloon: the command was issued from Home Assistant, and the desktop has nobody waiting on it.

## The Settings panel

Settings → Home Assistant hosts the shared `MqttSettingsPanel`, embedded exactly as `BrandAboutControl`
is: a hostable `UserControl` that renders the settings and reports edits, owning no window chrome and no
connection of its own. Every edit lands in config.json through `ConfigManager` and returns to
`MqttService` as a reload; the window applies nothing itself.

`MqttPanelSeam` maps each reported facet — the master toggle, one publish category, the broker batch,
the node id — onto a copy of the stored settings. It is pure, so the effect of an edit is assertable
without WinUI. An unrecognised category key changes nothing: a dead toggle is visible, whereas a key
typo falling through to another field would not be.

`WithBroker` writes only the fields the broker batch owns. The master toggle, the node id and the
remembered endpoint each have their own commit path, and taking them from a snapshot the panel has held
since it opened would roll back whichever changed meanwhile.

The window is handed the service through an accessor rather than an instance: MQTT is composed after the
tray menu the window is opened from. Null is a supported answer — the category then edits the settings
with no session to report on, which is the state a never-configured broker is in.

`MqttActivity` belongs to one connection, so a different instance is how a rebuilt connection is
detected. `RefreshMqttStatus` rebuilds the whole options record only in that case: rebuilding on every
visit would throw away a half-typed broker edit.

The panel runs its own connection test and background endpoint search. Both are abandoned on window
close and on a panel rebuild, or a continuation touches a control that is going away.

## Build and install

**`InvariantGlobalization` is off, and must stay off.** Two reasons, both load-bearing:

1. `Microsoft.Win32.TaskScheduler.Trigger`'s static initialiser calls
   `CultureInfo.CreateSpecificCulture("en")`, which throws `CultureNotFoundException` in
   globalization-invariant mode. It is reached from `TaskRegistrationInfo.get_Date()` inside
   `TaskFolder.RegisterTaskDefinition`, so **every** Task Scheduler write throws — the "run at logon"
   toggle and the battery-flag self-heal alike.
2. Invariant mode pins `CultureInfo.CurrentCulture` to the invariant culture, so every number and date
   the UI shows is formatted invariantly instead of in the user's locale.

Machine-readable text — config.json, MQTT topics and payloads, log lines, the crash-stamp file — pins
`CultureInfo.InvariantCulture` at each call site instead. The app is framework-dependent win-x64, so ICU
comes from the shared runtime and no ICU payload is published.

`Tests/CultureBoundaryTests` guards that boundary by driving real production code under `nb-NO`, a
locale with a comma decimal separator and a U+2212 negative sign. `TheHarnessItselfIsHonest` exists so a
failure to enter `nb-NO` at all cannot let the rest pass vacuously.

Only the **formatting** pins are provable there. Deleting the pinned culture from
`MqttEntitySet.Number`, `LatencyLog` or the topic slug fails a test; deleting it from an integer, long
or `TimeSpan` **parse** does not, because none of those three is culture-sensitive for the inputs the
app hands them, across all 889 cultures this runtime carries. Those pins are stated intent at a
boundary, and the tests over them are ordinary regression pins rather than proof the pin is honoured.
The one genuine divergence is the negative sign — 57 Arabic-script cultures reject an ASCII `-1` — which
no value the app parses can reach.

Two pins are asserted by reading source text rather than by execution: `InvariantGlobalization` in the
build file (the test assembly has its own globalization mode) and `UI\AdapterRenameFlow.cs` (WinUI, not
linked into the test assembly). Both are coarse, and aimed at the format argument being dropped during
an edit.

**Project references.** `ZeroZero.Mqtt.WinUI` needs `UndefineProperties="WindowsAppSDKSelfContained"`,
as `ZeroZero.Brand.WinUI` does: it is a WinUI class library, and the propagated property errors it out.
`ZeroZero.Mqtt` and `ZeroZero.Mqtt.HomeAssistant` are plain `net10.0` with no Windows App SDK targets,
so the property propagates harmlessly and they need no exclusion.

**The installer's startup task is skipped on a silent run.** `RegisterStartupTask` runs `runas`, which is
a UAC prompt, and `/SUPPRESSMSGBOXES` does not suppress UAC. A silent run — the winget background upgrade
that the `autoupdate` task itself schedules — would raise an unexplained consent dialogue on the desktop
and block the installer waiting on it. Hence the `WizardSilent()` guard, for the same reason
`PrepareToInstall` and `LaunchApp` skip their own elevations.

A silent *upgrade* loses nothing: `runstartup` is selected there only because an earlier interactive
install selected it, so the task already exists, and `StartupManager.TryRepairPowerSettings` keeps its
battery settings correct. The case this gives up is a silent *fresh* install passing
`/MERGETASKS=runstartup`, which leaves the task uncreated; Settings → "Run on startup" registers it from
inside the already-elevated app, with no prompt. The task is never created behind the user's back: its
existence **is** the toggle's state (`StartupManager.IsEnabled`), so a self-heal that created one would
turn auto-start on for someone who never asked for it.

`RegisterAutoUpdateTask` is not elevated (no `/RL HIGHEST`), so it is safe to run silently.

## Logging

The integration logs to `mqtt.log` (`AppInfo.MqttLog`), beside the app's other logs in `AppInfo.DataDir`.
The log-category-to-file routing lives in `App.OnLaunched` and is gated by the live log-level switch.
