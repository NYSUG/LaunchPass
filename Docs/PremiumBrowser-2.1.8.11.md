# Category wheel restoration and splash bounds — 2.1.8.11

The selection model already remembers each category's game by stable ID. This
revision repairs presentation when returning from a short console category to a
long arcade wheel: reset motion before replacing items, realize the remembered
row, center using its measured position after layout, and reapply row transforms
after recycled containers have processed their new item bindings. Ignore visual
updates for containers that still belong to an old item.

The premium view requests full-screen mode and CoreWindow bounds independently
of the optional splash setting. Startup also paints the full Frame with the
original Tim's Arcade image while navigation constructs the premium page. The
existing extended startup and launch-cover images use UniformToFill, preserving
aspect ratio and cropping excess edges instead of letterboxing or stretching.
Browser controls retain their existing TV-safe margins. No image was edited.

Windows still owns the initial manifest splash: it displays a smaller centered
image over a background, and cannot stretch that image to the entire display.
This brief system phase can still have a border. The app-controlled splash is
full-screen once the premium window is activated. See Microsoft's documentation:
https://learn.microsoft.com/en-us/windows/uwp/launch-resume/add-a-splash-screen

Keep `splash.enabled` true in LocalState/ArcadeExperience.json to see the optional
app splash. No configuration or catalog migration is needed. Existing cabinet
mappings, selected IDs, core overrides and RetroArch URI/return behavior remain.

## Hardware checks

1. Choose a game well down the Arcade list. Switch to Genesis, then SNES, then
   Arcade. Confirm the left artwork, selected logo, highlight and wheel center
   all refer to the original game.
2. Browse several steps and repeat in both directions, including rapid category
   changes. Each category must restore its own game, with normal Up/Down input.
3. Restart and test again; launch and return from a known-working game.
4. Cold-launch with splash enabled. Distinguish the short OS splash from the
   app splash and launch cover. Check the latter two fill the screen with no
   distortion. No artificial startup delay was added.

Portable tests cover repeated three-category selection restoration with a long
Arcade list and single-game Genesis/SNES lists. Realized XAML geometry, Xbox
fullscreen behavior and TV display settings still require hardware validation.

All seven portable suites passed: PremiumBrowser 372, ArtworkPerformance 490,
CabinetInput 126, Catalog 55, UriGenerator 256, CoreSettings 61 and ArcadeExperience
275 (1,635 assertions total). Logs are in Builds/2.1.8.11/Validation.

Debug x64 and Release x64/.NET Native passed with zero errors and 11 existing
compiler warnings each in the final compilation passes. A packaging-only retry
restored resource packages omitted by the SDK incremental build; it passed with
zero warnings/errors. Final bundle validation checks all four scale packages,
application/resource block maps, identity/version, original splash bytes, compiled
XAML resources and activation metadata. The bundle is unsigned. The existing
certificate signing script is prepared with the final bundle's exact SHA-256
under Builds/2.1.8.11/Sign-Release.ps1, but has not been executed.

Unsigned build output and validation logs are under Builds/2.1.8.11. Signing,
installation, and commits require separate authorization.
