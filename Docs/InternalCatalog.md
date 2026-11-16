# Milestone 2: internal game catalog

The catalog uses LaunchPass LocalState for metadata/artwork and passes configured Windows ROM paths to RetroArch. It never opens or enumerates RetroArch private storage. No ROMs or cores are included.

## Deploy metadata (after build/deployment approval)

1. Back up the working `InternalStorageProbe.xml` from LaunchPass LocalState, unchanged. Keep `Docs/InternalStorageProbe.xml` and the validated RetroArch package/content/core installation intact.
2. Copy `Docs/InternalCatalog.example.json` to **LaunchPass's** LocalState as `InternalCatalog.json` through Xbox Device Portal's app file explorer. Do not put it in LocalCache or RetroArch LocalState.
3. Verify `contentRoot` against the working probe's ROM path. The example takes the parent `roms` directory from the recorded probe configuration. Package identity or Xbox user changes require updating it. JSON backslashes must be doubled; relative ROMs can use `/`.
4. Replace/delete the clearly labeled placeholder games with actual ROM filenames and installed core DLL filenames. 1941 and 1942 share the arcade default; the third arcade entry demonstrates an override. Genesis and SNES are examples only. Core availability/ROM compatibility is verified by RetroArch, not by catalog validation.
5. Optionally upload artwork under LaunchPass LocalState, e.g. `Artwork/1941.png`. Artwork paths are relative to LaunchPass LocalState; missing, inaccessible, or corrupt images fall back to the packaged `Assets/empty.png`. Titles remain available in the collection UI.
6. Create an empty file called `InternalCatalog.enabled` in LaunchPass LocalState. Its presence opts into internal mode independently of catalog validity, so a missing/broken JSON file produces a recovery screen without scanning USB.
7. Preserve the original probe backup; set `Enabled` to `false` in the deployed probe XML (or rename that deployed XML). An enabled probe intentionally takes startup priority. Do not change its ROM path or core. Fully terminate and reopen LaunchPass.

Startup priority is enabled probe → internal-mode marker → existing LaunchBox/external mode. Removing the marker and restarting restores the old startup flow; restore the original probe XML to run the validated probe. No mode change is applied to a running internal session. The catalog recovery page includes Reload and a diagnostic-probe button; the diagnostic button enables its loaded configuration in memory without changing the XML, ROM path, core, or probe implementation. Restoring the original enabled XML and restarting also opens the probe directly.

To edit metadata, return to the platform selector and choose Reload catalog, or restart. Returning from RetroArch preserves the current frame/navigation state. Play later uses stable game IDs and is intentionally **session-only in internal mode**; it does not read or overwrite the external mode's PlayLater.xml. Recent-game persistence remains external-mode behavior.

## Schema version 1

Required top-level fields: `version: 1`, `contentRoot`, `platforms`, `games`. Each platform requires unique `id`, display `title`, and `defaultCore`. Each game requires globally unique `id`, known `platform` ID, `title`, and `rom`; optional `core` overrides the default and optional `artwork` references LaunchPass LocalState. IDs are case-insensitively unique and use letters, digits, `_`, or `-`. Rename titles without changing IDs to retain identity.

ROM paths can be relative to contentRoot or fully qualified Windows drive paths. Drive-relative paths (`C:game.zip`), root-relative paths, UNC/device paths, traversal, invalid Windows characters and trailing dots/spaces are rejected. Artwork must be relative and cannot traverse out of LocalState. DLL values are filenames only. Unknown JSON fields are ignored for forward-compatible metadata. Validation is atomic: one invalid entry rejects the whole document with a visible error, so no partially valid library is silently loaded.

The launch request retains the probe format:

```text
retroarch:?cmd=retroarch -L cores\<core.dll> "<Uri.EscapeDataString(fullWindowsRomPath)>"&launchOnExit=LaunchPass:
```

The app compares the portable request to the existing `UrlSchemeGenerator` output, then calls the same `Launcher.LaunchUriAsync(new Uri(...))`. There is no filesystem ROM preflight for the dedicated internal-game model. `true` means protocol activation succeeded; it does not prove game loading. Attempts/results are recorded in LaunchPass LocalState `InternalCatalog.log`. The probe retains its separate log and configuration.

## Portable checks

```sh
dotnet restore tools/Catalog.Tests/Catalog.Tests.csproj --configfile tools/UriGenerator.Tests/NuGet.Config
dotnet run --project tools/Catalog.Tests --no-restore
dotnet restore tools/UriGenerator.Tests/UriGenerator.Tests.csproj --configfile tools/UriGenerator.Tests/NuGet.Config
dotnet run --project tools/UriGenerator.Tests --no-restore
```

The empty-source NuGet config works with a cached NETStandard.Library 2.0 package. On a fresh machine, restore normally from your configured NuGet source first. Tests reference the actual netstandard2.0 project and link the production URI generator/Platform with minimal UWP model stubs. They cover multiple platforms, defaults/overrides, identity, optional artwork metadata, malformed/missing fields, duplicate IDs, Windows paths, escaping, and parity with the validated generator. Actual artwork IO, XAML, protocol activation, and return behavior require Windows/Xbox checks.

## Local verification results

On macOS, the .NET Standard 2.0 project builds with zero warnings/errors. The catalog suite and the existing 256-assertion URI suite pass. `git diff --check` passes. These results do not substitute for the Windows/.NET Native build or the Xbox hardware checklist below.

## Windows build checklist

- [ ] Review changes and approve build/deployment separately; nothing has been committed or deployed by this implementation.
- [ ] Open `LaunchPass/LaunchPass.sln` with the existing UWP workloads/SDK and restore packages. No UWP target SDK, framework, or package versions were upgraded.
- [ ] Build Debug x64 and confirm LaunchPass.Catalog is referenced and packaged.
- [ ] Build Release x64 with .NET Native. Exercise JSON deserialization on that build to validate runtime serialization/reflection metadata (existing application runtime directives cover application assemblies).
- [ ] Run both portable test executables.
- [ ] Verify no XAML/controller focus regressions in the existing collection/search/details screens.

## Xbox hardware checklist

- [ ] With the original enabled probe XML, confirm the same 1941 probe screen and launch; retain baseline logs.
- [ ] Disable the deployed probe, install marker/catalog, unplug USB, cold-start LaunchPass and browse Arcade, Genesis and SNES using the controller.
- [ ] Launch 1941 and a second installed arcade game with the same default core; verify actual loaded game/core in RetroArch.
- [ ] Launch a compatible arcade ROM with the per-game core override; verify the actual core.
- [ ] Replace Genesis/SNES placeholders with owned content and verify both cores launch.
- [ ] Verify a fully qualified absolute ROM override, a relative path, spaces, Unicode, `&`, `+`, `%` and `#` where practical; inspect log URI and RetroArch behavior.
- [ ] Launch/exit/return at least ten times, including different games and controller Menu shortcut; verify correct selection, no duplicate initialization, and successful relaunch after suspension. Test return after LaunchPass termination separately (cold reconstruction is expected).
- [ ] Test missing JSON, invalid JSON, missing required fields, duplicate IDs, unknown platform, bad core/path; each shows an actionable load error with no USB scan. Fix JSON and Reload.
- [ ] Test omitted artwork, missing file, corrupt file and valid image in both collection and details. All entries remain selectable and launchable.
- [ ] Test missing RetroArch protocol handler/activation failure and rapid repeated launch input; verify an error message and subsequent retry works.
- [ ] Confirm Play later retains the internal model/launch behavior during the session, including same-title games with distinct IDs.
- [ ] Restore enabled probe XML and confirm diagnostic fallback again.
- [ ] Disable probe, remove marker, restart with existing LaunchBox storage and verify import, themes, search, artwork, play later, file preflight and launches behave as before. Existing external PlayLater.xml must be unchanged by internal use.

## Changed files and risks

- `LaunchPass.Catalog/{LaunchPass.Catalog.csproj,Catalog.cs}`: portable JSON contracts/validation, stable IDs, explicit Windows paths, core resolution and launch requests.
- `LaunchPass/InternalCatalog.cs`: UWP LocalState IO, platform selector, internal Game adapter, activation/result logging and safe artwork fallback.
- `App.xaml.cs`: opt-in internal startup before USB preparation, after the preserved probe; retain navigation on protocol return.
- `Game.cs`, `GameDetailsPage.xaml.cs`: dispatch internal artwork/launch through the adapter; existing external branches remain in place.
- `LaunchPassThemeSettings.cs`: internal collections use the packaged visual resources without external theme preparation.
- `PlaylistPlayLater.cs`: internal IDs and session-only bookmarks preserving the internal model, without writing external XML.
- `LaunchPass.csproj`, `LaunchPass.sln`: add the portable reference/project and UWP source.
- `tools/Catalog.Tests/{Catalog.Tests.csproj,Program.cs}`, `Docs/InternalCatalog.example.json`, this document: tests, sample and operations guide.

Material risks: macOS cannot build/run UWP XAML or .NET Native, so the Windows builds and Xbox checklist remain release gates. URI parity protects the known format but cannot validate receiver decoding or core/ROM compatibility. Private package paths can change on reinstall. The catalog is trusted user-authored launch metadata, not a storage discovery mechanism. Internal mode deliberately uses a small platform selector and existing collection UI; full custom themes, persistent internal bookmarks/recent history and richer metadata are future work. Probe source/configuration and the production URI generator are unchanged by Milestone 2.
