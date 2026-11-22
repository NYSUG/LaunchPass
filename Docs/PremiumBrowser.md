# Premium browser — Milestones 3B.1–3B.3

This opt-in presentation uses the existing internal catalog parser, game adapters, cabinet input configuration and GameDetailsPage.StartContent launch path. Milestone 3B.3 adds bounded local artwork caching and compositor motion; video, downloading and attract mode remain outside this frontend. The existing library and external LaunchBox flow remain available.

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

Use a portrait flyer as screenshot if it must remain entirely visible; backgrounds intentionally crop. Keep important artwork away from outer edges. Files over 30 MiB are skipped. Header dimensions determine a bounded proportional decode: background at most 1920 × 1080, shared hero/wheel logo 1000 × 400 and screenshot 1000 × 800. Small sources are not upscaled during decode. The visible working set is the selected game and up to three wheel neighbors on each side; reusable decoded images are held in bounded LRU caches. Two prioritized worker slots and generation checks prevent obsolete results replacing current artwork. These are presentation bounds, not a console performance guarantee.

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

The wheel is a virtualized ListView with explicit selection, larger selected logo/text, neon outline, focus on the selected container and a centered viewport. Fixed-height rows use compositor translation/scale/opacity; native BringIntoView requests are suppressed in favor of explicit centering. Preview handling consumes mapped cabinet/gamepad directions before native scrolling. Controller OriginalKey is used to avoid confusing translated gamepad keys with keyboard input. Directional repeat is bounded; Select/Back require a fresh press. Xbox controller repeat uses the existing gate with safe default timings; cabinet repeat still comes from CabinetInput.json.

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

Milestone 3B.1 validation on this NUC (historical):

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

## Milestone 3B.3 — Performance and motion

### Investigation

The session baseline is saved under Builds/Milestone3B.3/Validation. The previous implementation constructed a new BitmapImage and decoded the same artwork on every selection. Hero and wheel logos used different decode sizes. It cleared all visible images before replacements were ready, which could expose the plain background. Neighbor loading ran serially after selected logo/background/screenshot loading; it was a small retained row window rather than a reusable cache. Generation checks already rejected stale results, but an in-progress obsolete request could delay current work. Every selection changed row heights and refreshed all row properties, forced UpdateLayout, and snapped ScrollIntoView. Category changes and page reactivation rebuilt wheel models; ordinary selections did not recreate the entire wheel.

### Current loading policy

ArtworkPerformance.cs in LaunchPass.Catalog contains the tested weighted LRU, latest-demand priority queue and interruption math. PremiumArtworkCache.cs owns UI-thread BitmapImage creation and physical-file decoding. PremiumBrowserMotion.cs connects those services to the existing page and input actions; it does not generate launch URIs or change catalog/schema logic.

| Cache | Normal budget / entry cap | Under pressure |
| --- | --- | --- |
| Shared decoded hero/wheel logos | 12 MiB / 24 | 4 MiB / 8 |
| Backgrounds | 18 MiB / 3 | 9 MiB / 1 |
| Screenshots | 8 MiB / 4 | 4 MiB / 1 |

The normal **38 MiB** and reduced **17 MiB** figures are decoded-pixel estimates (width × height × 4), not total process/GPU memory limits. Entry caps and byte caps both apply. Full 1080p backgrounds consume about 7.9 MiB each, so the normal background byte budget holds two at that maximum size. Old/new backgrounds during a crossfade, active decode buffers, UI-held images and framework/GPU allocations can exceed the reported cache bytes. Actual app usage/limit are included in diagnostics.

The selected logo is requested immediately. Up to three logos before and after selection are prefetched nearest-first. Heavy visible assets start after a 90 ms dwell, except existing in-flight look-ahead is promoted immediately rather than canceled/restarted. Existing cached visible artwork is assigned immediately. After visible work and neighboring marquees, one likely next game in the most recent movement direction gets background/screenshot look-ahead; fetching six heavyweight neighbors would churn the tighter budgets. No full-catalog preload occurs.

There are at most two worker slots. New demand cancels obsolete queued/active requests. Visible work preempts speculative decodes; still-wanted prefetch resumes afterward and retains its consumers. Concurrent equal requests share a task. Different requests resolving to the same physical file and decode profile also share the decoded object/active decode via consumer leases. One consumer's cancellation does not abort another consumer's still-needed decode. Missing/corrupt paths have a bounded 30-second negative cache. Every UI assignment checks the current generation and foreground/page state.

Activation return keeps the existing selected-game state. Navigation away, launch, deactivation, background entry, suspension or a reduced memory limit cancels work and clears cached/visible image references. Static memory/lifecycle handlers are detached when leaving the page. High/over-limit memory pressure reduces budgets, suppresses all look-ahead and uses immediate background replacement rather than holding a second fade image. Normal limits/prefetch resume when memory is low or foreground activation permits it. No forced garbage collection or memory-limit increase is used.

### Motion and focus

Wheel rows now have a fixed 82-effective-pixel pitch. Header/footer spacing permits centered selection at the ends. Scroll offsets snap internally, while an independent compositor Translation compensates the position and eases it to zero over **150 ms**. The interruption model carries the current visual offset into a new transition; there is no animation queue. Large jumps are capped to two row pitches. Selected marquee scale/opacity is 1/1; neighboring entries progressively reduce to 0.86/0.82, 0.75/0.63 and 0.68/0.45. No Width/Height/layout animation or per-input UpdateLayout is used.

The existing ListView, shared selected-game state, cabinet adapter and gamepad action paths remain. Selected containers retain real focus; unrealized containers receive focus after layout rather than forcing layout synchronously. Images are decorative and add no focus targets. Category changes reuse models when the category is unchanged, and reset motion when replacing the category.

Two background layers crossfade over **230 ms**. The prior background remains until a current result is ready. A running fade completes without replacing its source midway; at most one latest replacement is retained, and a newer selection discards any older pending replacement. Screenshots load independently. Missing images retain the existing text/gradient fallbacks. There is no blur, video or attract loop.

### Opt-in performance capture

Create an empty **LaunchPass LocalState/PerformanceDiagnostics.enabled** file, then restart or re-enter the premium page. A small noninteractive panel appears below the existing input diagnostics. Retrieve **LocalState/Performance.log** and **Performance.previous.log** through Device Portal File Explorer. Remove the marker and restart to disable. No diagnostic marker is required for caching/motion itself.

Sampling is every two seconds; disk appends run off the UI thread. Logs rotate at approximately 256 KiB, with one previous file. Errors in the diagnostic path do not block navigation/startup. Fields include current game/foreground state, last release reason, cache lookup hits/misses by role, entry/eviction counts, approximate bytes/budget, completed load/decode average and maximum decode duration, queue occupancy, deduplicated/canceled/preempted jobs, prefetch requests/failures, ignored stale results and actual MemoryManager usage/limit.

Logo/background/screenshot latency is measured from selection to the **first current image assignment or fade start**, not a GPU-present timestamp. `-1` means no real image was assigned for that selection. Cache hits may measure 0 ms. Prefetch counts are logical requests (including cache hits/coalesced phases); decode count is physical completed decodes. H/M counters are cache lookups, including candidate/fallback lookups. Missing paths are not classified as corrupt decode failures. Canceled/ignored counters can rise during normal rapid navigation and should not be interpreted as crashes.

### Hardware measurement checklist

1. Cold-start with the 99-game catalog and diagnostics enabled. Wait for the first game to settle, then step down slowly, pause, reverse and compare cache H/M, decode count, artwork assignment latency and actual memory usage. Repeat the same range to measure warm behavior.
2. Hold Down/Up and reverse direction rapidly. Confirm uninterrupted 150 ms wheel motion, stable selected outline/focus and latest matching logo/title/screenshot. Release the joystick; the last selected game must settle and neighbors should finish prefetching.
3. Pause, then advance one game in the last movement direction: background/screenshot look-ahead should produce hits when completed. Browse all 99 games; cache bytes and entry counts must remain within caps rather than grow with catalog size. Run for 10–15 minutes and inspect actual process memory for a plateau; cache estimates alone are insufficient.
4. Observe cold/missing/corrupt artwork and category switches, long titles, viewport ends, search/B/Back, controller D-pad/stick and the established X-Arcade mappings. Check no blank flash or late wrong-game assignment. Verify initial/restored off-screen selection is centered and focused after virtualization.
5. Launch several games immediately while artwork is loading, hold Select through the transition, quit RetroArch, and verify correct selection/return without relaunch. Check logs after return show cleared/repopulated caches and renewed prefetch, not a permanently reduced background policy.
6. Where feasible observe memory-pressure/reduced-limit behavior. Confirm foreground frames still respond, cache references are released when leaving, and normal prefetch resumes on return. Review the optional diagnostics panel's TV-safe layout with InputDiagnostics enabled/disabled.

The host tests cannot measure Xbox GPU upload/present time, disk latency, compositor scheduling, actual memory pressure or focus virtualization. These remain hardware acceptance work; no Xbox performance numbers are claimed by a successful desktop build.

Current outputs/logs are under **Builds/Milestone3B.3/Debug**, **Builds/Milestone3B.3/Release**, and **Builds/Milestone3B.3/Validation**. Source package version stays 2.1.8.4 for this implementation task; a future update over the existing different 2.1.8.4 package needs a separately prepared higher revision. Nothing in this milestone is signed, deployed or committed.

Final Milestone 3B.3 host validation:

| Check | Result |
| --- | --- |
| New cache/priority/cancellation/motion suite | 490 assertions passed |
| Existing presentation suite | 326 passed |
| Existing catalog suite | 55 passed |
| Existing cabinet input suite | 126 passed |
| Existing URI generator suite | 256 passed |
| Debug x64 | Passed; 12 existing warnings, 0 errors; 23.39s |
| Release x64/.NET Native | Passed; 12 existing warnings, 0 errors; 3m09.13s |

Release constructor activation metadata and packaged PremiumBrowserPage.xbf were confirmed. The unsigned bundle and all five embedded packages passed block-map hash checks. Existing catalog/URI/input/probe/startup/manifest sources match the session baseline; the previous uncommitted splash/alphabetical-navigation changes were preserved. Application sources were frozen and checked against the final build snapshot. Detailed logs/binlogs, baseline/final hashes, final test outputs and the preservation/results record are in Validation.

New source files for 3B.3: LaunchPass.Catalog/ArtworkPerformance.cs; LaunchPass/PremiumArtworkCache.cs; LaunchPass/PremiumBrowserMotion.cs; LaunchPass/PremiumPerformanceDiagnostics.cs; tools/ArtworkPerformance.Tests/Program.cs and its project. Existing files changed for this milestone: PremiumBrowserPage.xaml, its code-behind, LaunchPass.csproj and this guide. No other application behavior or source was intentionally changed.
