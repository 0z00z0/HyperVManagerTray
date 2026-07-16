# UI copy style

House style for every user-facing string in this app: British English, sentence case, plain language
(per `0z0-design/design-language.md`). Written for issue #42, whose whole point is that the app used
three names for the same object and twice said things that were not true.

This lives in `docs/` rather than `DEVELOPMENT_NOTES.md` only because that file was under concurrent
edit (#43) when #42 landed.

## Pinned vocabulary

Use these words, and only these words, in user-facing text.

| Term | Means | Never say |
|---|---|---|
| **adapter** | a host physical network adapter | "NIC" (the config field `nicName` stays — schema compatibility) |
| **VM network adapter** | the adapter inside the VM (`VmTarget.NicName`) | a bare "NIC" |
| **adapter description** | the renamable device string the rename feature writes | "name" (see the warning below) |
| **virtual switch** (then "switch") | a Hyper-V virtual switch | "network" when a switch is meant |
| **network** | the physical LAN/Wi-Fi a rule recognises | "switch" when the network is meant |
| **rule** | maps a network → a virtual switch | — |
| **managed VM** | a VM this app looks after (one in config.json) | "in config" as the user-facing phrase |
| **config.json** | the file, when pointing at the file itself | "the config" for the file |

### Why "description" is not negotiable

The adapter rename feature writes the device's **description** — the text Device Manager, Hyper-V
Manager and Windows Settings show. It does **not** touch the adapter's Windows **name**, which is the
connection alias `ncpa.cpl` shows. They are two different strings.

This is not pedantry. On 2026-07-16 a *working* rename was reported as broken, because the UI never
drew that distinction and the two strings disagreed — exactly as they should have. Any message that
says "name" where it means "description" recreates that bug in the user's head. Validation errors are
the worst offenders: they are read at the moment the user is already unsure what they are editing.

## Conventions

- **Verbs are verbs**: "Shut down", not "Shutdown". (The noun form, if ever needed, is one word.)
- **Sentence case** in dialog captions, tray items, buttons, section headers and prompts —
  "Add current network", not "Add Current Network".
- **One delay format** at every magnitude: `Immediate`, `45 s`, `1 min 30 s`, `5 min`, `1 h 30 min`
  (`SettingsOptions.FormatDelay`). Never mix a spaced style with a compact `1h 30m`.
- **The product is `AppInfo.Name`** ("Hyper-V Manager Tray"). Never title a window of ours
  "Hyper-V Manager" — that is exactly Microsoft's MMC snap-in, and claiming it is a lie about what
  the user is looking at. Referring to the real snap-in by that name is fine and correct.
- **Brand name**: exactly "ZeroZero Software".
- **British English**: "licence" as a noun, "-ise" endings.

## Say only what is true

The rule that catches the most bugs, and the one both false strings #42 found had broken:

- **Report what was verified, not what was attempted** (issues #37 / #40). The confirmed/unconfirmed
  message pairs in `VmConfigUi` exist for this; the caller picks between them by re-reading state.
- **Never describe UI internals in a setting's description.** "Off until the status loads" explains
  the window's async read, not the setting.
- **A string can be false because the code moved.** #34 removed VM power from the tray; the About
  description still promised "VM power and state directly from the system tray" long after it was
  untrue. When behaviour moves, grep the copy for claims about where things live.
- **A string can be false because the code is wrong.** If so, fix the code — do not paper over it
  with wording.

## Labels must not overstate the data

`VmStatus.VhdBytes` is the summed size of the VM's VHD **files on the host**
(`VmService.RefreshVhd` → `FileInfo.Length`). It is not the guest's disk usage and not free space.
It is labelled **"VHD"** for that reason; "Disk", sitting beside CPU and Mem, read as a guest metric.
Before labelling a meter, check what the number actually is.
