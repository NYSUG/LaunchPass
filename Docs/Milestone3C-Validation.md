# Milestone 3C implementation and validation

Version refresh: the subsequent hardware-test package is **2.1.8.6**, under
`Builds/2.1.8.6/Release/`. Use `Builds/2.1.8.6/Sign-Release.ps1` for that build.
The original 2.1.8.5 implementation-validation record below is retained.
Future hardware-test/release packages must increment the package version; this
requirement is recorded in the repository's AGENTS.md.

Validated on the Windows build machine, 2026-11-22. Xbox behavior remains a
hardware acceptance step. No signing, deployment, commit, push, certificate/trust
changes, history rewriting, or remote changes were performed.

## Implementation

The app retains its root Frame. A single app-owned coordinator owns music and
the idle engine. WinRT audio is an adapter around a portable, fake-deck-tested
radio controller. The museum is a separate XAML-backed control hosted by the
premium page; it shares the existing bounded artwork cache and does not change
the selected game. Wake suppression runs before cabinet action dispatch.

Music uses LocalState/Music only, shuffle without cycle-boundary repeats,
automatic advancement, optional three-second two-player crossfade, sequential
fallback, mute/volume, a normal-navigation session and position bookmarks on
backgrounding. It is stopped before the original RetroArch activation. The
unbound NextTrack action is available through CabinetInput.json.

The centralized theme adds restrained cyan selection and amber action emphasis.
The app loading overlay uses the exact original splash image with UniformToFill.
The museum uses authentic cached marquees, a subdued entrance image, perspective
grid, occasional branding and bounded compositor animation. It remains opt-in.
Full settings and device checks are in [ArcadeExperience.md](ArcadeExperience.md).

## Validation

| Suite | Result |
|---|---:|
| New experience settings, shuffle, playback, crossfade, session, input and idle policy | 274 passed |
| Artwork cache/priority/cancellation/motion | 490 passed |
| Premium presentation/selection | 326 passed |
| Catalog | 55 passed |
| Cabinet mappings/repeat/navigation | 126 passed |
| Production RetroArch URI generator | 256 passed |
| **Total** | **1,527 passed** |

Debug x64 and Release x64/.NET Native both succeeded using VS 2022 MSBuild,
SDK target 10.0.18362.0 and minimum 10.0.17763.0. Both reported 12 existing
warnings, zero errors. Warnings concern legacy unawaited calls, async methods
without awaits, unused exception variables and one SettingsPage binding. No
warning originated in the new components.

The package remains **2.1.8.5**, Publisher **CN=LaunchPass**, identity
**f3a28ab5-9cd3-480a-86e5-58ecbeba4015**. No version bump was requested.
The manifest, original probe, URI generator, catalog parser/schema and artwork
resolver/cache source are unchanged. The launch adapter only adds the wake guard
and music lifecycle boundary around the existing URI generator/activation.

Artifacts, intentionally unsigned:

```text
C:\TRANSFER\Builds\Milestone3C\Debug\LaunchPass_2.1.8.5_Debug_Test\LaunchPass_2.1.8.5_x64_Debug.msixbundle
C:\TRANSFER\Builds\Milestone3C\Release\LaunchPass_2.1.8.5_Test\LaunchPass_2.1.8.5_x64.msixbundle
C:\TRANSFER\Builds\Milestone3C\Validation\Debug.log
C:\TRANSFER\Builds\Milestone3C\Validation\Release.log
C:\TRANSFER\Builds\Milestone3C\Validation\Package-validation.json
```

SHA-256 block maps were checked for the bundle and all five embedded packages.
No MP3 exists in any embedded package. The packaged branding PNG matches the
original source hash. The theme, museum and existing page XBF resources are
embedded in resources.pri, rather than loose ZIP entries. SDK MakePRI successfully
dumps that PRI, and the embedded XBF bytes match generated build output.
Release XamlTypeInfo.g.cs contains explicit activators for ArcadeAttractPresenter,
PremiumBrowserPage and InternalCatalogPage. .NET Native serialization generation
includes the new settings contracts. This verifies packaging and metadata;
actual XAML/audio startup still needs Xbox testing.

Required x64 dependencies accompany the Release bundle under Dependencies/x64:
Microsoft.UI.Xaml.2.7, Microsoft.NET.Native.Framework.2.2,
Microsoft.NET.Native.Runtime.2.2 and Microsoft.VCLibs.x64.14.00.
No dependency version was changed. The machine-readable package receipt records
their exact minimum versions and the bundle hash.

## Music and Git safety

All 13 tracked MP3s were inventoried (104.5 MiB); all local SHA-256 hashes remain
unchanged. They are excluded from the MSIX. `/mp3/` and `/Music-Staging/` are now
in .gitignore. They were originally added by commit dcd41fe and are already
contained in the locally cached remote branch origin/feature/internal-arcade-library.
No network fetch was performed; this is evidence from remote-tracking history.

The Git index is read-only in this session. Untracking is **prepared, not staged**.
The preview of tools/Untrack-LocalMusic.ps1 passed for all 13 files. In a regular
PowerShell session, run from C:\TRANSFER:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Untrack-LocalMusic.ps1 -Apply
```

The script uses git rm --cached on the inspected MP3 paths and verifies every
local hash afterward. It deletes no local audio, commits nothing and does not
erase audio already in history. The execution-policy override affects only
that child process. Review the staged changes before making your own commit.

## Files added or modified

Existing files modified:

```text
.gitignore
LaunchPass.Catalog/CabinetInput.cs
LaunchPass/App.xaml.cs
LaunchPass/CabinetInput.cs
LaunchPass/InternalCatalog.cs
LaunchPass/LaunchPass.csproj
LaunchPass/PremiumBrowserMotion.cs
LaunchPass/PremiumBrowserPage.xaml
LaunchPass/PremiumBrowserPage.xaml.cs
```

Files added:

```text
Docs/ArcadeExperience.example.json
Docs/ArcadeExperience.md
Docs/Milestone3C-Validation.md
LaunchPass.Catalog/ArcadeExperience.cs
LaunchPass.Catalog/ArcadeRadio.cs
LaunchPass/ArcadeAttractPresenter.xaml
LaunchPass/ArcadeAttractPresenter.xaml.cs
LaunchPass/ArcadeRadioService.cs
LaunchPass/ArcadeSessionCoordinator.cs
LaunchPass/Assets/Branding/TimsArcadeSplash.png
LaunchPass/PremiumBrowserExperience.cs
LaunchPass/Themes/TimsArcadeTheme.xaml
tools/ArcadeExperience.Tests/ArcadeExperience.Tests.csproj
tools/ArcadeExperience.Tests/Program.cs
tools/Untrack-LocalMusic.ps1
```

Build outputs, validation scripts, music inventory and logs are confined to the
already ignored Builds directory.

## Outstanding hardware risks

Crossfade is off by default: codec timing, dual-player mixing, seek accuracy,
audio focus and clean RetroArch return need listening tests. Position bookmarks
survive the running UI session, not process termination. Raw gamepad activity
is sampled every 100 ms; held-input and duplicate event routing need device
confirmation. Verify many idle/wake cycles, memory pressure, missing artwork,
controller reconnects, TV safe areas and image cropping. Animated content is
bounded, but its GPU cost and effect on image retention are hardware concerns.
The OS-controlled centered splash remains a UWP limitation; only the subsequent
app-controlled loading surface can use the full-screen image.
