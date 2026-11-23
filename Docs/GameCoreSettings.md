# Per-game core configuration — 2.1.8.10

## Cabinet workflow

Highlight a game in the premium browser and open **Game Settings** using Xbox
controller **View**, or a key explicitly bound to the new `GameSettings` cabinet
action. No physical X-Arcade key is assigned by default. No long press, delay or
change to existing Select, Back, Coin, Start, Search or play-later bindings is
introduced. The actual Xbox CabinetInput.json and a confirmed spare cabinet key
have been requested; they were not available during implementation.

To configure a confirmed spare button once, append a binding to the existing
CabinetInput.json rather than replacing its bindings. This is a **syntax example**,
not a claim that F is free on this cabinet or in RetroArch:

```json
{ "key": "F", "action": "GameSettings" }
```

Do not assign it until that key's current role is checked. Restart after editing
CabinetInput.json. Per-game changes after this initial setup require no JSON
editing or uploading.

The themed modal shows the game logo/title, effective core and its source,
catalog default, friendly core name, exact DLL filename and previous attempts.

- Up/Down moves through Choose Core, Save, Save and Test Launch, Reset, History,
  and Cancel. Left/Right changes the candidate core without saving; on the History
  row it pages previous attempts instead. Select activates the highlighted row.
- Save writes the selected override immediately and leaves the modal open.
- Save and Test Launch saves first, closes the modal, and uses the existing
  full-screen loading cover and internal launch adapter exactly once.
- Reset removes the effective override, falling back to the catalog per-game
  core or platform default. It does not launch.
- Back/Cancel discards unsaved choices. Already saved changes remain saved. Back
  during a save queues closing and cancels any pending Test Launch; an in-progress
  successful save itself is retained.
- The modal owns configured keyboard actions and gamepad events. Repeated Select
  or the held opening/closing key does not launch an additional game. Closing or
  returning from RetroArch restores the selected browser game and focus.

## Core discovery and registry

LaunchPass and RetroArch remain separate UWP applications. No direct enumeration
of RetroArch's private LocalState or installed core folder is assumed or attempted.
No filesystem capability, ACL, certificate trust or package identity is changed.
Access to D: ROMs does not establish access to packaged core DLLs. Microsoft
documents [app filesystem permissions](https://learn.microsoft.com/en-us/windows/apps/develop/files/file-access-permissions)
and [capability restrictions](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations).

Supply an optional **LocalState/CoreRegistry.json**, generated from the exact
RetroArch package used on Xbox. [CoreRegistry.example.json](CoreRegistry.example.json)
is a schema reference, not a universal list or an assertion about installed cores.
No core names are hardcoded into the app. The registry contains `version: 1`, an
optional `source`, and `cores` entries with `file`, `name`, and optional `available`.
An explicit `available: false` excludes that core and prevents using a saved
override/default for it. Removing an unlisted saved core produces an actionable
message instead of silently selecting a different core.

Cores already configured by the catalog are included if the registry has not
explicitly disabled them. These are labelled **catalog configured**, not claimed
to have been independently discovered. Missing/corrupt registries fall back to
these configured cores. Thus existing catalog launches remain operational without
uploading a registry. Additional saved cores require their registry to remain
available. A configured entry is not proof that its DLL is actually installed.
The menu and launch adapter reload settings, so registry updates are rechecked
before saving and launching; reopen the menu after an external registry edit.

### Offline Mac registry generator

The standard-library-only helper reads an explicit local APPX/MSIX or MSIX bundle,
selects the x64 application declaring the retroarch protocol, and inventories
`cores/*.dll`. It reads display_name from matching `info/*.info` where packaged,
otherwise derives a readable name. It never extracts or executes the archive,
contacts the Xbox, downloads cores, or touches ROMs. It preserves filename case.

```sh
cd /Users/timothyjohnson/Dev/LaunchPass
mkdir -p Builds/core-registry

python3 tools/retroarch_core_registry.py \
  --package '/actual/path/to/the-installed-RetroArch-package.msixbundle' \
  --installed-version 'ACTUAL_VERSION_SHOWN_IN_DEVICE_PORTAL' \
  --output Builds/core-registry/CoreRegistry.json
```

Replace both placeholders with the package and observed version. The version
check rejects mismatches; it is still a user-supplied assertion, not a remote
inspection. --output must be a new file. Without it, the helper prints JSON only.
Upload only the resulting CoreRegistry.json to LaunchPass LocalState when ready.
Do not upload DLLs into LaunchPass or move RetroArch's core directory.

## Override persistence

**LocalState/GameOverrides.json** is app-owned. Normal use creates it on the first
Save/Reset. Do not upload the example over a working overrides file.
[GameOverrides.example.json](GameOverrides.example.json) shows the format:

```json
{
  "version": 1,
  "overrides": { "arcade-1941": { "core": "mame2003_plus_libretro.dll" } }
}
```

Resolution is exactly: GameOverrides core, then catalog game core, then platform
default. Stable catalog IDs, not game titles or ROM paths, key overrides. Catalog
regeneration with the same IDs preserves them even if titles or ROM roots change.
The sync/artwork tools do not publish or delete this file. The catalog is never
rewritten by the menu. A different generated ID deliberately does not inherit an
unrelated game's override; old/orphan entries are retained.

Writes are serialized within the process, staged in a unique same-folder temporary
file, flushed, checked against the expected previous content, and committed with
MoveAndReplaceAsync (or a fail-if-existing rename for a new file). No delete-first
replacement is used. External edits abort saving when detected. There is no OS
transaction with a separate editor; avoid simultaneous external edits while saving.
Actual WinRT replacement and interruption behavior remain hardware checks.

Unknown future fields are retained where DataContract extension data permits.
Reset stores a null core while retaining other per-game fields. Missing files use
catalog defaults. Corrupt JSON cannot crash startup; the menu warns that explicit
Save/Reset backs up the corrupt source as GameOverrides.corrupt.<uuid>.json before
starting a fresh map. A newer schema version is never automatically downgraded.
Unreadable/oversized files can block saving with a visible error; they are not
deleted or overwritten blindly. File reads/writes are capped at 1 MiB and 5,000
override entries.

## Troubleshooting history

**LocalState/GameLaunchHistory.json** records stable game ID, exact core, UTC
attempt time, nullable handedOff, observed departure/resume, and available
exception HRESULT/message. A pending attempt can survive process termination
and receive a later callback observation. Native foreground return after a
recorded departure is labelled separately from the LaunchPass protocol callback.
An unrelated startup with no recorded departure is not recorded as a return.

The History row pages one retained attempt at a time; the previously-tried list
summarizes distinct cores for this game. Retention is at most 20 attempts/game,
1,000 total and 256 KiB, whichever bound is reached first. Older observations
are evicted. Setting top-level `enabled: false` in an existing history document
disables new recording without disabling overrides. Corrupt/newer history is
preserved and logging is skipped; a logging failure does not block game launch.

**Accepted handoff is not successful gameplay. Resume/callback is not evidence
of a crash.** A guide/dashboard switch may produce a foreground observation.
If suspension prevents a write, the handoff/outcome can remain unknown. HRESULTs
are from the actual LaunchPass/API exception captured; managed validation codes
are not RetroArch core error codes. A false LaunchUriAsync result exposes no
native failure code. RetroArch's private logs are not automatically read.

The upstream receiver [accepts cmd before initialization and preserves launchOnExit](https://github.com/libretro/RetroArch/blob/master/os/uwp/uwp_main.cpp#L692).
Use normal Quit RetroArch between attempts; merely switching to LaunchPass while
RetroArch remains initialized does not guarantee that a new core/content request
will be consumed. The protocol, quoting, escaping and callback are unchanged.
No automatic retries, core cycling, ROM modifications or replacement downloads
are performed.

Failures can involve core/ROM-set version mismatch, missing parent ZIPs, BIOS ZIPs,
CHDs or core-specific requirements. These observations help track experiments;
they are not a full compatibility diagnosis.

## Validation and deployment checklist

Version: **2.1.8.10**, with unchanged package family, Publisher, signing certificate,
SDK target/minimum and dependencies. Builds and validation logs are under
Builds/2.1.8.10. No signing, deployment or commit is performed by this milestone.
Use a signed Release in-place update later; do not uninstall the existing app.
Back up LocalState first, including the new override/history files after use.

The seven .NET suites passed **1,605 assertions**: CoreSettings 61,
ArcadeExperience 275, ArtworkPerformance 490, PremiumBrowser 342, Catalog 55,
CabinetInput 126, and UriGenerator 256. Debug x64 and Release x64/.NET Native
compile with zero errors and the 12 existing legacy warnings. The generated
Release metadata contains the settings-dialog activator and serialization
readers/writers for the new contracts. Package validation checks all five embedded
package block maps and compares the compiled settings XBF with its packaged PRI
resource. These checks do not establish Xbox behavior.

Unsigned Release bundle:

```text
C:\TRANSFER\Builds\2.1.8.10\Release\LaunchPass_2.1.8.10_Test\LaunchPass_2.1.8.10_x64.msixbundle
```

Portable tests cover precedence, reset, case-insensitive stable IDs, regeneration,
URI path/callback preservation, save/reload/restart, unknown fields, corrupt-source
backup, unsupported versions, failed commits/concurrent edits, registry exclusions,
history bounds and cold/warm resume observations, and input repeat/cancellation.
WinRT file replacement, popup focus, native controller routing and actual gameplay
remain Xbox tests. Python helper tests are separately provided:

```sh
python3 -m unittest discover -s tools -p 'test_retroarch_core_registry.py' -v
```

They were not run on the Windows machine: only unusable Store Python aliases are
available there. The seven .NET portable suites are runnable locally and their
actual results are recorded in Builds/2.1.8.10/Validation/*-tests.log.

After explicit signing/deployment approval:

1. Install as an in-place update, retain InternalCatalog.enabled,
   PremiumBrowser.enabled, catalog, CabinetInput.json, artwork, music and logs.
2. Open settings using Xbox View first. Confirm title/logo and effective core.
   Configure a verified spare cabinet key once; confirm it does not launch a game.
3. Select a core known to be installed, Save, restart LaunchPass and verify the
   effective override remains. Confirm InternalCatalog.json is unchanged.
4. Save and Test Launch a known-working game. Quit RetroArch normally; verify
   callback, same highlighted game, focus and a neutral history observation.
5. Test Reset to Default, Cancel without saving, repeated/held buttons, Back during
   save and attract wake before opening settings. None should cause double launch.
6. Test unavailable configured cores by marking a registry entry unavailable in a
   backup/test setup. It must not silently launch a different core. Restore the
   registry after testing. Do not delete a working RetroArch DLL for this test.
7. Back up overrides, run catalog/artwork synchronization, and verify overrides,
   history and selected game remain. Test a ROM-set compatibility case separately;
   record the core/result without labelling a quick return as a crash.

## Files

Added: LaunchPass.Catalog/GameCoreSettings.cs; LaunchPass/GameCoreService.cs;
LaunchPass/GameCoreSettingsDialog.xaml and .xaml.cs; tools/CoreSettings.Tests;
tools/retroarch_core_registry.py; tools/test_retroarch_core_registry.py; this guide
and the two schema examples. Integration changes are in the catalog entry adapter,
internal launch adapter, both catalog pages, App callback observation, CabinetInput
action enum and project registration. Earlier 2.1.8.9 wheel/cover changes in the
working tree were retained.
