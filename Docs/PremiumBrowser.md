# Milestone 3B.1 — Static premium browser

This opt-in presentation uses the existing internal catalog parser, game adapters, cabinet input configuration and GameDetailsPage.StartContent launch path. It adds no video, downloading, attract mode or custom animation. The existing library and external LaunchBox flow remain available.

## Enable and recover

In **LaunchPass's LocalState**, retain the working `InternalCatalog.enabled`, `InternalCatalog.json` and `CabinetInput.json`. Add an empty file named **PremiumBrowser.enabled**. Stop and restart LaunchPass. The premium marker alone does not enable internal mode; the probe retains its existing startup priority and must remain disabled for normal catalog use.

Removing or renaming PremiumBrowser.enabled and restarting restores the original internal-catalog startup page. Marker selection happens only for an empty startup frame, so adding/removing it while LaunchPass is running does not replace the active page or disrupt return activation.

Back/B opens the existing library, with a new conditional **Return to premium browser** button. Cabinet Back on that selector also returns to premium. This button is absent when the original library starts normally. The original game browsing/search/details, Reload and diagnostic probe remain there. An invalid/missing catalog automatically navigates to this existing recovery page; it never falls through to USB discovery. Correct the catalog there, then return to premium or restart. Premium rereads a failed catalog; an already-loaded premium session requires restart to adopt catalog/metadata edits.

Startup.log records marker choice, constructor/XAML initialization, catalog load, recovery, metadata rejection and unexpected artwork/action errors. InputDiagnostics.enabled retains its existing independent opt-in panel and Input.log. Neither new assets nor Presentation.json are required to start or launch games.

## Artwork

All paths below are relative to **LaunchPass LocalState**, not RetroArch LocalState and not the catalog's ROM contentRoot:

```text
Artwork/
  games/
    arcade-1941/
      logo.png
      background.jpg
      screenshot.png
  platforms/
    arcade/
      logo.png
      background.jpg
Presentation.json              optional metadata
```

Game and platform directory names use catalog `id`/`platform` identifiers, not display titles. Ordinary catalog IDs map directly. Windows device names (CON, PRN, AUX, NUL, COM0–9, LPT0–9, ignoring case) use `~` plus uppercase UTF-8 hex of the lowercase ID: CON becomes `~636F6E`. This avoids device-name aliases without colliding with valid catalog IDs. Paths cannot use IDs containing separators or traversal; existing catalog validation is unchanged.

For each role, filenames are tried in order: `.png`, `.jpg`, `.jpeg`. Game-specific logo/background assets precede platform equivalents. Screenshot falls back to the catalog's existing optional `artwork` path after game-specific candidates. Existing artwork is not treated as a transparent logo. Missing or corrupt candidates continue through the fallback list. Missing logos show a large text title; missing screenshots show a styled READY TO PLAY panel; missing backgrounds show a dark gradient. Assets can be supplied independently.

Recommended source assets:

| Role | Recommendation | Display |
| --- | --- | --- |
| Logo | Transparent PNG, up to 1200 × 400 | Uniform fit, never cropped |
| Background | JPEG, 1920 × 1080 | Uniform fill/crop; decorative only |
| Screenshot/flyer | PNG/JPEG; portrait or landscape | Uniform fit; original aspect ratio preserved |

Use a portrait flyer as screenshot if it must remain entirely visible; backgrounds intentionally crop. Keep important artwork away from outer edges. Files over 30 MiB are skipped. Header dimensions determine a bounded proportional decode: background at most 1920 × 1080, hero logo 1000 × 400, screenshot 1000 × 800, wheel logo 600 × 160. Small sources are not upscaled during decode. Only the selected game's hero images and up to five neighboring wheel logos are retained. Requests are serialized and generation-checked; obsolete results cannot replace the current game. These are presentation bounds, not a console performance guarantee.

## Optional metadata

Copy Presentation.example.json as **LocalState/Presentation.json**, matching actual game IDs. It uses an independent version-1 presentation schema:

```json
{"version":1,"games":[{"id":"arcade-1941","year":"1990","manufacturer":"Capcom","genre":"Vertical shooter"}]}
```

Year, manufacturer and genre are optional strings. Missing metadata is hidden. A missing/invalid sidecar is ignored with a Startup.log entry and does not block browsing. Unknown game IDs have no effect. This does not extend, rewrite or migrate InternalCatalog.json.

## Controls and selection

- Cabinet PreviousGame/NextGame (normally Up/Down): move one game, clamped at ends.
- PreviousCategory/NextCategory (normally Left/Right): cycle populated categories, restoring each category's selected game.
- Select: launch immediately through the existing game adapter. Back: existing library. Existing CabinetInput.json mappings are reused unchanged.
- PreviousLetter/NextLetter: existing configurable letter-jump behavior. Search: existing search dialog; choosing a result highlights it in the premium browser, then a fresh Select launches.
- Xbox D-pad/left stick: same selection/category state as cabinet input. A/Menu: launch; B: library; Y: search. X retains session play-later marking; the count is shown in the status line. Premium's session play-later list is separate from the existing selector's session list.
- Favorites/AddFavorite remain reserved through the existing cabinet adapter. No new physical key assumptions are introduced.

The wheel is a virtualized ListView with explicit selection, larger selected logo/text, neon outline, focus on the selected container and centered BringIntoView requests. Preview handling consumes mapped cabinet/gamepad directions before native scrolling. Controller OriginalKey is used to avoid confusing translated gamepad keys with keyboard input. Directional repeat is bounded; Select/Back require a fresh press. Xbox controller repeat uses the existing gate with safe default timings; cabinet repeat still comes from CabinetInput.json.

Selection is held in one portable PremiumSelection object. Category selections and the current game ID are also saved best-effort to LocalSettings under PremiumBrowser.Category.<platform-id> and PremiumBrowser.SelectedGame. A restarted process restores valid IDs against the current catalog, ignoring removed games. Normal RetroArch activation retains the existing page. Settings-write failures only log; they cannot block launching.

The page uses adaptive star-sized columns and 48 horizontal / 27 vertical effective-pixel margins. At the usual Xbox 200% scaling these correspond to 96 / 54 output pixels at 1080p. The system may apply additional visible-bound insets; the actual cabinet needs overscan/readability validation. Artwork is noninteractive; essential labels and controls stay inside the content margins. The diagnostic panel occupies a separate row when enabled.

## Hardware acceptance checklist

1. Without the premium marker, verify the working library is unchanged. Enable the marker and restart: verify the XAML page opens and all five game titles are visible/reachable even without artwork.
2. Alternate X-Arcade and controller navigation. Confirm the same highlighted game changes once per press, held directions repeat, ends clamp, selection stays in view and native scrolling cannot leave it behind. Repeat with at least 25 games and long titles.
3. With two populated categories, move between them and verify separate remembered selections; verify one-category no-op behavior.
4. Launch each working game using the confirmed Select mapping and controller A. Quit RetroArch and confirm the same game/category returns. Hold Select through the transition and verify no relaunch. Repeat after closing/terminating LaunchPass to exercise saved IDs.
5. Exercise B/Back, return-to-premium, search, old library details and probe access. Confirm mapped Alt/modifier buttons still work and dialogs retain controller navigation.
6. Test no art, logo-only, full art, portrait flyer, corrupt images and malformed Presentation.json. Verify no stretching and that selecting a game remains responsive while images load.
7. Remove InternalCatalog.json temporarily: verify the existing recovery page, fix it, Reload and return. Restore the original file. Do not modify ROM paths/core names or RetroArch configuration.
8. Check all edges on the cabinet TV at 1080p, including diagnostic-panel layout, long titles, wheel focus/highlight and category/position readability.
9. Browse rapidly for an extended session and repeat launch/return cycles. Inspect memory usage and Startup.log/Input.log; actual Xbox IO latency, virtualization/focus, decode cost and suspension behavior require hardware verification.

## Implementation boundaries

New: LaunchPass/PremiumBrowserPage.xaml(.cs), LaunchPass.Catalog/PremiumPresentation.cs, tools/PremiumBrowser.Tests, this guide and Presentation.example.json.
Changed: App.xaml.cs (opt-in initial page selection), LaunchPass.csproj (compiled XAML/code-behind registration), InternalCatalogPage.xaml.cs (conditional route back to premium).
Catalog parsing, launch adapters, URI generation, CabinetInput, probe, existing diagnostics, package identity/version, SDKs and dependencies are unchanged. No signing, deployment or commit is included. Packages remain at source version 2.1.8.2; a later update over a different installed 2.1.8.2 build will need a separately authorized higher version.

## Portable validation

PremiumBrowser.Tests covers mixed-input selection, category restoration and wrapping, empty/single/100-game lists, stable launch requests, safe relative artwork candidates, Windows device-name IDs, screenshot fallback priority, and optional metadata rejection/recovery boundaries. It does not run XAML or Xbox storage/activation APIs.

Run the portable suite from C:\TRANSFER:

```powershell
dotnet build tools\PremiumBrowser.Tests\PremiumBrowser.Tests.csproj --no-restore /p:UseAppHost=false
dotnet tools\PremiumBrowser.Tests\bin\Debug\net8.0\PremiumBrowser.Tests.dll
dotnet run --project tools\Catalog.Tests\Catalog.Tests.csproj --no-restore
dotnet run --project tools\CabinetInput.Tests\CabinetInput.Tests.csproj --no-restore
dotnet run --project tools\UriGenerator.Tests\UriGenerator.Tests.csproj --no-restore
```

The new test project requires its initial restore (existing cached dependencies suffice). The harness uses modern desktop .NET like the other portable tests; the Xbox application remains legacy UWP/.NET Native.

Test logs: build-logs/premium-browser-tests.log, premium-browser-Catalog-tests.log, premium-browser-CabinetInput-tests.log, premium-browser-UriGenerator-tests.log. Full build output and binlogs: build-logs/premium-browser-debug.log/.binlog and premium-browser-release.log/.binlog. The baseline preservation report is build-logs/premium-browser-preservation.json.

Final validation on this NUC:

| Check | Result |
| --- | --- |
| Premium selection/artwork/metadata | 318 assertions passed |
| Existing catalog | 55 assertions passed |
| Existing cabinet input | 126 assertions passed |
| Existing URI generator | 256 assertions passed |
| VS 2022 Debug x64 | Passed; 12 existing warnings, 0 errors |
| VS 2022 Release x64/.NET Native | Passed; 12 existing warnings, 0 errors |

Release XamlTypeInfo.g.cs contains `Activate_0_PremiumBrowserPage()` constructing the page, and the type's Activator assignment, consistent with existing compiled pages. Debug uses the existing reflection metadata provider; both configurations generate PremiumBrowserPage.g.cs and compiled wheel bindings. MakePRI inspection of the packaged resources.pri confirms Files/PremiumBrowserPage.xbf. No generated files were edited.

Unsigned Release bundle: C:\TRANSFER\Premium-Browser\Release\LaunchPass_2.1.8.2_Test\LaunchPass_2.1.8.2_x64.msixbundle. Debug output is under Premium-Browser/Debug. No Xbox runtime behavior has been tested for this presentation yet.
