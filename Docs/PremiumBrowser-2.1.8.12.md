# Layout-cycle recovery — 2.1.8.12

Xbox Startup.log confirmed `Windows.UI.Xaml.LayoutCycleException` immediately
after `Premium browser: catalog ready; games=186`, including on restart.
Catalog loading and cabinet configuration completed before this exception.
Continuing audio does not prove the XAML UI thread is responsive.

The 2.1.8.11 fix introduced a dangerous feedback path: LayoutUpdated measured an
animated container and repeatedly changed scroll offset until the measured center
matched. That correction had no retry limit. The older forced UpdateLayout and
selection write-back from SelectionChanged also fed layout/selection callbacks.
These are removed; the on-device exception establishes a layout failure, although
portable tests cannot establish every framework step that led to it.

## Immediate recovery on the installed build

Close LaunchPass. In Xbox Device Portal rename LaunchPass LocalState's
`PremiumBrowser.enabled` to `PremiumBrowser.enabled.bak`, then restart. Leave
`InternalCatalog.enabled` in place. This selects the original internal catalog
frontend. Do not uninstall, clear app data, or remove the working catalog, core
overrides, music, or artwork. Renaming Startup.log or editing a catalog index does
not clear the selection: premium remembered IDs are in application LocalSettings.
They need not be cleared for the corrected build.

## Correction

- No LayoutUpdated handler, forced UpdateLayout, TransformToVisual feedback or
  ScrollIntoView retry loop in the wheel.
- No SelectedItem write-back from SelectionChanged. Cabinet/search/click input
  remains authoritative; input rendering and bounded restoration synchronize the
  native selection.
- Restoration runs at most eight times on a 50-ms DispatcherTimer. The finite
  budget is consumed even when layout never becomes ready. New game selection,
  departure, modal UI or navigation cancels stale work. Failure to restore cannot
  hold the startup cover visible or gate navigation.
- Offset uses the existing fixed 82-DIP row pitch and symmetric spacers, independent
  of animated visual geometry. Unchanged spacer heights are not assigned again.
- Existing per-category stable-ID memory, input bindings, game overrides, artwork
  cache and RetroArch launch/return remain intact.
- Startup.log now records category ID, game ID, selected index and row count, plus
  restoration completion/termination with attempt count and scroll extent/offset.

The present selection model includes categories with game entries only. A platform
declared in the catalog with zero game entries is not a navigable category. Missing
ROMs on disk are not treated as zero catalog entries. Hardware observations of
Genesis/SNES showing "0" must distinguish zero entries from a single game's index
0; the new category diagnostics make that distinction explicit.

## Validation and hardware procedure

All seven portable suites passed (1,650 assertions), including retry exhaustion
when layout never settles, stale selection/category cancellation, empty selection,
navigation cancellation and fresh activation. The long Arcade / one-game Genesis /
one-game SNES selection regression remains covered. Real XAML layout needs Xbox
validation; these tests do not reproduce the Xbox framework exception.

Debug x64 and Release x64/.NET Native both passed with zero errors and 12 existing
warnings each. The unsigned Release bundle passed block-map integrity checks for
the application and all four scale-resource packages, identity/version checks,
compiled XAML/activation validation and original splash-asset comparison. Required
x64 dependencies are included in the generated Test/Dependencies/x64 directory.
The existing certificate signing script is prepared for this exact artifact at
Builds/2.1.8.12/Sign-Release.ps1; it was syntax checked, not executed.

After separate signing and deployment authorization, install the in-place update
without uninstalling. Retain LocalState. Rename the premium marker back to
`PremiumBrowser.enabled`, restart, and test:

1. The saved Arcade selection at index 1: Up/Down must work immediately.
2. Arcade -> Genesis -> SNES -> Arcade, repeatedly in both directions, then
   immediate Up/Down. Test index 0, index 1 and a game near the end of the list.
3. Rapid category changes and held direction input, followed by a pause. No
   scrolling/focus correction should continue indefinitely.
4. Restart with each selection, launch a known-working game and quit RetroArch.
5. Retrieve Startup.log even on success. Expect bounded `Premium wheel restore
   ended` records and no LayoutCycleException. If it recurs, stop testing and use
   the marker fallback above; retain the log and exact input sequence.

Unsigned package and logs: Builds/2.1.8.12. No signing, deployment or commit was
performed. The splash behavior is unchanged from 2.1.8.11.
