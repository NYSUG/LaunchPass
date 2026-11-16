# Initial architecture findings

Investigation date: November 14, 2026. Scope: architecture investigation only. No source files were modified during the investigation. Implementation remains subject to approval.

An internal-storage library looks feasible without giving LaunchPass access to RetroArch’s private files. The smallest approach is to store a catalog in LaunchPass’s own storage and send ROM paths to RetroArch through its existing URI protocol. The installed RetroArch build must prove that works on the Xbox before implementation proceeds.

## Project context

- Target: dedicated arcade cabinet powered by an Xbox One X in Developer Mode.
- RetroArch has a working MAME core.
- Controller: original two-player X-Arcade USB keyboard encoder.
- ROMs are in RetroArch’s private LocalState directory under `roms/arcade`, with future Genesis, SNES, and other directories planned.
- ROM transfers through the Xbox Device Portal API already work.
- Goal: remove the external USB drive and LaunchBox-generated library requirements while preserving existing LaunchPass functionality.

## Repository architecture

The solution contains one UWP application, mostly under the `RetroPass` namespace despite the LaunchPass branding.

| Responsibility | Implementation |
|---|---|
| Discover library locations | [`DataSourceManager.GetConfigurationFile()`](LaunchPass/DataSourceManager.cs#L105) searches removable-device roots and LaunchPass’s own `LocalCacheFolder` for `LaunchPass.xml`. |
| Select library provider | [`GetDataSourceFromConfigurationFile()`](LaunchPass/DataSourceManager.cs#L207) resolves the configured library root. Only `DataSourceLaunchBox` is implemented. |
| Read LaunchBox metadata | [`DataSourceLaunchBox`](LaunchPass/DataSourceLaunchBox.cs#L161) reads `Data/Emulators.xml`, `Data/Platforms.xml`, platform game XML files, and optional playlist XML files. |
| Represent the library | [`DataSource`](LaunchPass/DataSource.cs#L96), `Platform`, `Game`, `Playlist`, and `PlaylistItem`. These currently mix library data with UWP storage, settings, and image types. |
| Display the library | [`MainPage`](LaunchPass/MainPage.xaml.cs#L119) loads the provider and builds playlist rows; [`GameCollectionPage`](LaunchPass/GameCollectionPage.xaml.cs#L49) binds the game grid. `SearchPage` searches loaded playlists; `GameDetailsPage` shows metadata and media. |
| Load artwork/media | `Game` performs storage queries; `ThumbnailCache` caches images; `LPMediaElement` displays backgrounds. |
| Launch games | [`GameDetailsPage.StartContent()`](LaunchPass/GameDetailsPage.xaml.cs#L224) checks the ROM, builds a URI through `UrlSchemeGenerator`, and calls `Launcher.LaunchUriAsync`. |
| Initialize themes | [`PreparelaunchPassFolder()`](LaunchPass/DataSourceManager.cs#L460) finds a removable drive containing `LaunchBox`, then prepares the neighboring `LaunchPass` theme folder. |

Two details matter for removing USB:

- **Local library support already exists**, but it remains LaunchBox-based. Its copy routine copies metadata and media, not ROMs. The Settings import and cancel handlers are currently commented out.
- **Startup still depends on USB theme discovery.** [`App.OnLaunched()`](LaunchPass/App.xaml.cs#L110) calls that preparation routine and then dereferences `CurrentThemeSettings`. There is no complete no-drive initialization path.

## Exact RetroArch launch flow

1. `LoadEmulatorsXml()` keeps LaunchBox emulator-platform entries marked `Default`, then joins their emulator IDs to emulator executable paths.
2. `Platform.SetEmulatorType()` infers the URI scheme from that executable path; its fallback is `retroarch`.
3. `LoadLaunchBoxPlatform()` obtains the platform core from its LaunchBox `CommandLine`. A game’s own `CommandLine` overrides it.
4. `ParseCommandLine()` simply takes `Path.GetFileName(commandLine)` and removes quotes. It is not a general command-line parser.
5. [`GameLaunchBox.ApplicationPathFull`](LaunchPass/DataSourceLaunchBox.cs#L35) computes:

   ```csharp
   Path.GetFullPath(Path.Combine(DataRootFolder, ApplicationPath))
   ```

6. `StartContent()` first calls `StorageFile.GetFileFromPathAsync()` on that path.
7. [`GetUrlRetroarch()`](LaunchPass/UrlSchemeGenerator.cs#L108) generates this shape:

   ```text
   retroarch:?cmd=retroarch -L cores\<CoreName> "<escaped-ROM-path>"&launchOnExit=LaunchPass:
   ```

   Only the ROM path is explicitly escaped with `Uri.EscapeDataString`.

8. LaunchPass constructs a `Uri` and invokes `Launcher.LaunchUriAsync()` without awaiting or checking its result.

This sends text arguments, not ROM bytes, a file handle, or a storage permission token. The `romPath` and `dirMappings` configuration fields exist but are not applied by the current launch flow.

Upstream RetroArch UWP confirms this protocol: it decodes the query, tokenizes `cmd` into arguments, and passes them to `rarch_main`. It handles `launchOnExit` and ignores new `cmd` arguments once initialized. This verifies upstream behavior, **not the installed package’s version or behavior**. [RetroArch UWP source](https://github.com/libretro/RetroArch/blob/master/uwp/uwp_main.cpp#L692)

## Storage feasibility

Microsoft documents UWP application-data folders as private and inaccessible to other apps. Xbox also does not support `broadFileSystemAccess`. LaunchPass therefore cannot be designed around enumerating RetroArch’s `LocalState`, even if its physical path is known. Successful Device Portal transfers do not establish access for another UWP app. [UWP file access](https://learn.microsoft.com/en-us/windows/uwp/get-started/fileio-learning-track), [Xbox limitations](https://learn.microsoft.com/en-us/uwp/extension-sdks/uwp-limitations-on-xbox)

| Operation | Assessment |
|---|---|
| LaunchPass scans RetroArch’s private `roms/arcade` | No documented access basis; do not assume it works. |
| LaunchPass stores titles, artwork, core names, and ROM path strings locally | Supported architectural approach. |
| LaunchPass sends a ROM path through protocol activation | Existing mechanism supports carrying the string. |
| RetroArch opens that ROM from its own private storage | Plausible; must validate the installed build and exact path on Xbox. |
| Moving ROMs into LaunchPass’s private storage | Creates the reverse access problem for RetroArch. |

**The current implementation cannot support this unchanged:** its mandatory caller-side ROM check blocks inaccessible paths. However, the URI transport itself may need no replacement.

Use the exact absolute path reported by RetroArch or its logs for the first experiment. Do not assume a Device Portal API path is a runtime filesystem path, that a drive letter is stable, or that `roms/arcade/...` resolves relative to `LocalState`.

If the existing protocol fails, the next option is a targeted RetroArch change that resolves a relative content identifier against its own `LocalFolder`. That would be a new receiver feature, not an API available today.

## Smallest proposed changes

1. Add a catalog provider beside `DataSourceLaunchBox`, using the existing `DataSource` abstraction and UI models.
2. Store its catalog and artwork in LaunchPass’s `LocalFolder`. Include stable game ID, title, platform, core filename, and emulator-side content path.
3. Generate that catalog on macOS from the same ROM inventory used for Device Portal transfers. LaunchPass should read the catalog rather than scan RetroArch’s private directory.
4. Separate the metadata root from the emulator content root. For emulator-owned content, validate the configured launch request without trying to open the ROM in LaunchPass. Retain the existing file check for existing accessible libraries.
5. Await protocol launching and report activation failures accurately. A successful activation still does not prove the ROM loaded. [Launcher API](https://learn.microsoft.com/en-us/uwp/api/windows.system.launcher.launchuriasync)
6. Initialize local theme defaults without USB, update theme settings to use the selected theme root, and handle absent artwork safely.
7. Preserve content location information through Play Later serialization; its current reload logic reconstructs paths relative to the library root.

## macOS development boundary

The full application requires Windows build tooling. [`LaunchPass.csproj`](LaunchPass/LaunchPass.csproj) specifies:

- Legacy UAP/AppContainer project format and Windows SDK `10.0.20348.0`.
- Windows XAML compiler targets.
- `Microsoft.NETCore.UniversalWindowsPlatform` 6.2.14.
- `Microsoft.Toolkit.Uwp.UI.Controls` 7.1.3.
- .NET Native for Release, plus AppX packaging and signing.

A macOS .NET SDK cannot replace that toolchain. Keep Windows for full builds and Xbox deployment.

A small **.NET Standard 2.0** library would be a reasonable extraction target for UWP compatibility, with a modern .NET test project running on macOS. Extract:

- Catalog models, serialization, validation, sorting, and search.
- LaunchBox XML parsing and core selection.
- Launch-request and URI generation.
- Catalog generation from ROM inventories.
- Stable game identity and recent/favorite selection logic.

Keep `StorageFile`, `ApplicationData`, XAML, bitmap/media handling, and `Launcher` in UWP adapters. Treat Xbox Windows paths explicitly: macOS `System.IO.Path` has different separator and rooted-path behavior.

## Milestone-based implementation plan

| Milestone | Completion criterion |
|---|---|
| **1. Prove launching on Xbox** | After approval, use a minimal launcher probe with one known-working ROM and exact MAME core filename. Verify cold launch, exit callback, and a second game launch. Record installed RetroArch identity/version and runtime paths. |
| **2. Extract portable logic** | Catalog and launch-request library builds/tests on macOS, covering Windows paths, URI escaping, core overrides, and invalid entries. |
| **3. Add internal catalog provider** | Catalog deployed to LaunchPass’s own storage displays Arcade without LaunchBox exports. Existing provider remains available. |
| **4. Remove startup USB dependency** | Fresh launch, themes, missing-art fallback, search, and Play Later work with USB disconnected. |
| **5. Cabinet acceptance** | X-Arcade navigation, select/back actions, two-player input, exit/return, reboot persistence, and future platform mappings work on hardware. Existing external-library launching passes regression checks. |

## Three biggest risks or unknowns

1. **Installed RetroArch protocol behavior:** package registration, core location, runtime path format, and handling of an already-running instance may differ from upstream.
2. **Catalog synchronization and path stability:** LaunchPass cannot independently verify private ROM files; transfers, deletions, or RetroArch reinstallations can leave stale entries.
3. **Cabinet input and lifecycle:** existing handlers emphasize gamepad controls with limited keyboard shortcuts. X-Arcade mappings, focus restoration, and repeated launch/exit cycles need physical testing.

Recommendation: approve milestone 1 first; its result determines whether the remaining work stays entirely within LaunchPass.
