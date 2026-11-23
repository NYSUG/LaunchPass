# Premium Browser corrections — 2.1.8.9

## Startup selection

The previous SelectionChanged handler wrote the ListView's selection back into
the shared selected-game model. XAML realization/focus during startup could thus
change the model while the saved selection and virtualized wheel were being
restored. Focusing the ListView itself before its selected container existed could
also select a default row. The left/right category action happened to retry the
presentation after layout, consistent with the reported recovery behavior.

The model is now authoritative. Cabinet actions, search and ItemClick change the
selected game; wheel layout/focus events only reconcile the view to that game.
Category/activation restoration is deferred until the dispatcher can measure the
wheel, then centering, selection visuals and focus are reapplied. Focus waits for
the correct realized row instead of focusing the list and accepting its default
selection. The startup overlay remains until this initial reconciliation runs.
No full wheel reconstruction is added to individual Up/Down actions.

## Launch transition

A separate full-page cover displays the existing unpadded Tim's Arcade entrance
image, an amber LOADING label and the selected title. It is shown before artwork
release, with at most a 150 ms wait for a rendering opportunity. The opaque cover
also hides cleared browser assets while its image is decoding.

URI acceptance alone does not dismiss the cover. It remains during departure,
then clears on return. Declined activation, launch exceptions and page navigation
clear it too. If the app stays foreground for ten seconds without a departure,
the cover is dismissed and the browser is restored; the log records the unknown
handoff outcome. Input cannot select another game behind the cover. Music still
stops before the unchanged RetroArch launch call. The original internal-catalog
fallback page, launch URI, ROM paths, callback and mappings are unchanged.

## Attract mode setting

The submitted Xbox ArcadeExperience.json explicitly contained
`"attract": { "enabled": false, "idleSeconds": 180, "cycleSeconds": 12 }`.
That correctly disables the museum; it is not an idle-timer failure.

Keep all other fields, including the current music settings, and replace only
that attract object with:

```json
"attract": { "enabled": true, "idleSeconds": 180, "cycleSeconds": 12 }
```

For a quick test, use idleSeconds 15, then restore 180. Fully quit LaunchPass and
launch it again after changing LocalState/ArcadeExperience.json. Returning to the
Dashboard and resuming the same process does not reload these settings.

The app now logs effective settings once at startup and attract entry/exit:

```text
ArcadeExperience effective settings: music=True; attract=True; idleSeconds=180; ...
Arcade museum: attract-enter
Arcade museum: attract-exit
```

Retrieve LocalState/Startup.log through Device Portal. If LocalState logging
failed, the existing logger can use LocalCache/Startup.log. Malformed settings
still fall back to safe defaults and log the rejection. Attract mode requires a
ready, foreground premium browser with no dialog, launch or memory-pressure
condition; actual keyboard/controller input restarts the idle interval. Wake input
continues to be consumed centrally, so waking with Select must not launch a game.

## Validation and Xbox checklist

Portable suites: ArcadeExperience 275; ArtworkPerformance 490; PremiumBrowser
342; Catalog 55; CabinetInput 126; UriGenerator 256. Total 1,544 passed.
New coverage includes the reported disabled-attract settings, restoration at game
80 in a 99-game catalog and launch-cover acceptance/departure/return/failure/
timeout behavior. Portable tests cannot exercise actual ListView realization,
Xbox focus, compositor timing or RetroArch activation.

Debug x64 and Release x64/.NET Native both passed: zero errors and 12 existing
warnings each. Build logs and package checks are under Builds/2.1.8.9/Validation.
All five embedded packages passed SHA-256 block-map validation. Package identity,
version, XAML activation metadata and compiled resources passed; no MP3s are
packaged. The exact-hash signing script is Builds/2.1.8.9/Sign-Release.ps1 and has
been syntax-checked without execution. No signing, deployment, catalog upload or
commit is performed by this change.

1. Save a selection far down the wheel, fully quit, restart, and confirm the left
   details, selected right-side logo and wheel position agree immediately.
2. Press Up/Down immediately, browse rapidly, and verify selection/highlighting
   remains consistent. Left/Right with one category should preserve selection.
3. Launch 1941 and verify the full-screen loading cover hides asset unloading,
   RetroArch still starts, and normal Quit RetroArch returns to the same game.
4. Enable attract mode, restart, wait without input, and verify the museum appears.
   Wake with Select, arrows and Xbox controller input; release, then press Select
   again to launch. Confirm the wake press alone never launches.

## Modified files

```text
AGENTS.md
LaunchPass/Package.appxmanifest
LaunchPass/ArcadeSessionCoordinator.cs
LaunchPass/PremiumBrowserPage.xaml
LaunchPass/PremiumBrowserPage.xaml.cs
LaunchPass/PremiumBrowserMotion.cs
LaunchPass/PremiumBrowserExperience.cs
LaunchPass.Catalog/PremiumPresentation.cs
tools/PremiumBrowser.Tests/Program.cs
tools/ArcadeExperience.Tests/Program.cs
Docs/PremiumBrowser-2.1.8.9.md
```
