# NUC Milestone 2 build report — 2026-11-14

Status: blocked; no Xbox deployment or uploads performed.

## Environment and validation

- Milestone 2 catalog source, portable project/tests, UWP integration and Python arcade sync source/tests are present. This transferred directory has no Git metadata; Git status cannot establish uncommitted changes.
- Visual Studio Community 2026 18.12.12405.243; MSBuild at `C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe`. UWP XAML v18.0 targets exist. Offline .NET Native compiler packages 1.7.6 and 2.2.12-rel-31116-00 exist; their presence does not establish a functioning Release build.
- Only Windows SDK/UAP 10.0.26100.0 was found. The transferred project already targets 10.0.26100.0, minimum 10.0.17763.0. Required prior baseline 10.0.18362.0 was supplied as an MSBuild command-line property; no project retargeting was performed. Install/provide that SDK before the baseline build can be completed.
- Existing dependencies retained: Microsoft.NETCore.UniversalWindowsPlatform 6.2.14 and Microsoft.Toolkit.Uwp.UI.Controls 7.1.3.
- URI tests: PASS, 256 assertions against production code.
- Catalog tests: attempted, blocked by NuGet NU1301 (network socket access denied). Not a test failure or pass.
- Python's WindowsApps alias cannot execute; no working interpreter found in checked standard Python locations or Visual Studio. The requested 45 mocked tests could not run.
- NuGet solution restore: FAILED NU1301, api.nuget.org network access denied.
- Debug x64: FAILED NU1301 resolving LaunchPass.Catalog dependencies, before compilation validation. Full diagnostic log and binlog: `build-logs/nuc-debug-x64.log` and `.binlog`; restore equivalents: `nuc-restore.log` and `.binlog`.
- Release x64/.NET Native: not attempted because Debug failed. No compilation blocker established; no source fixes made.
- .NET CLI first-run files were redirected to `build-logs/dotnet-home` after a denied profile write. Generated restore/build intermediates and logs may have changed; no intentional project/source/identity/version edits.

## Signing and packages

Signing is configured with `LaunchPass_TemporaryKey.pfx`, empty thumbprint and AppxPackageSigningEnabled=True. The PFX exists but loading with an empty password failed. CurrentUser/My and LocalMachine/My returned no certificates. PFX validity/private key availability therefore remains unverified. No certificate was created, imported or overwritten.

No new package was generated. A transferred Release artifact exists at `LaunchPass/AppPackages/LaunchPass_2.1.8.0_x64_Test/LaunchPass_2.1.8.0_x64.msix`; it is not evidence of a new Milestone 2 build. SignTool verify /pa /v reports **No signature found** (zero verified, one error). See `build-logs/nuc-existing-package-signature.log`. Do not deploy this unsigned artifact.

Existing Release x64 dependency files are in that package directory's `Dependencies/x64`: Microsoft.VCLibs.x64.14.00.appx, Microsoft.UI.Xaml.2.7.appx, Microsoft.NET.Native.Runtime.2.2.appx and Microsoft.NET.Native.Framework.2.2.appx. Recheck the newly generated package manifest/dependency folder after a successful build; this list describes transferred artifacts only.

## Xbox checklist — execute only after build/signing and deployment authorization

1. Preserve the installed RetroArch package, cores and ROMs. Back up LaunchPass LocalState's working `InternalStorageProbe.xml` and baseline logs.
2. After an approved fresh signed Release x64 installation, use Xbox Device Portal's app file explorer for **LaunchPass**, opening its **LocalState root**. Upload a copy of `Docs/InternalCatalog.example.json` as `InternalCatalog.json`, not into LocalCache or RetroArch's LocalState.
3. Retain contentRoot exactly as recorded by the working probe: `U:\Users\UserMgr0\AppData\Local\Packages\1e4cf179-f3c2-404f-b9f3-cb2070a5aad8_8ngdn9a6dx1ma\LocalState\roms`. JSON backslashes must be doubled. Keep `arcade/1941.zip` and default core `mame2003_plus_libretro.dll`; these resolve to the original probe's full ROM path. If package/user identity changed, obtain the actual working path instead of guessing.
4. Keep `arcade/1942.zip` only if that ROM is installed and compatible with that core. Replace/delete all replace-me entries. Genesis (`genesis_plus_gx_libretro.dll`), SNES (`snes9x_libretro.dll`) and arcade override (`fbneo_libretro.dll`) are examples, not verified installed cores. Use actual installed compatible ROM filenames and DLL basenames; retain stable unique game IDs.
5. Optionally upload artwork under LaunchPass LocalState's `Artwork` directory. Create/upload an empty file named `InternalCatalog.enabled` in the LocalState root.
6. In the deployed probe XML only, change `<Enabled>true</Enabled>` to `<Enabled>false</Enabled>`, or rename the XML while keeping a backup. Keep RomPath/CoreFileName unchanged. An enabled probe wins startup priority over the catalog marker. Fully terminate and reopen LaunchPass.
7. Disconnect external USB storage, browse internal platforms with the controller, launch 1941 and a second installed game, and verify the actual loaded game/core in RetroArch. Protocol activation success alone is insufficient.
8. Use RetroArch's normal Quit/Exit action so its existing `launchOnExit=LaunchPass:` callback executes. Confirm return to the current launcher navigation/selection, then launch another game. Repeat at least ten launch/exit cycles, including the controller Menu shortcut and a suspension/relaunch cycle. Forced termination does not exercise normal exit callback behavior. Inspect LaunchPass LocalState `InternalCatalog.log` and RetroArch logs.
9. Exercise default and per-game cores, valid relative/absolute ROM paths, artwork fallback and malformed/missing catalog recovery. Fix metadata and select Reload catalog from the platform selector, or restart.
10. To restore the probe, restore the original enabled XML and restart. To restore external mode, disable the probe, remove `InternalCatalog.enabled`, and restart with the existing LaunchBox storage.

## Resume requirements

Provide a Git checkout/history if status review is required, a working Python interpreter, the preserved Windows SDK 10.0.18362.0 and accessible NuGet sources or a complete offline cache. Resolve the existing development PFX password/usable matching-publisher certificate without creating a new certificate. Then rerun tests/restore, Debug x64, Release x64 with UseDotNetNativeToolchain=true, generate the signed sideload package without auto-incrementing identity/version, and verify its signature before any deployment.
