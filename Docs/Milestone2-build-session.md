# Milestone 2 build verification, November 14, 2026

This session supersedes the validation results in NUC-Milestone2-build-report.md. Stopped at a major toolchain blocker. No deployment, uploads, commits, certificate creation/import, or intentional source/project/identity/dependency changes were made. Build intermediates and new session logs were written.

## Results

- Catalog implementation, UWP integration, Python sync source and tests are present. No `.git` metadata exists in this transfer; Git is also unavailable on PATH. Uncommitted status cannot be established.
- Visual Studio Community 2026 18.12.12405.243, MSBuild Current/Bin/MSBuild.exe, UWP XAML v18.0 targets, UWP support and .NET Native installer components/offline packages are present. Release compiler operation remains unverified.
- Only SDK 10.0.26100.0 is installed. SDK 10.0.18362.0 References and UAP platform directories are absent. The transferred csproj already defaults to 26100; it was left unchanged. Builds explicitly used TargetPlatformVersion=10.0.18362.0 to preserve the requested baseline. Minimum remains 10.0.17763.0. UWP 6.2.14 and Toolkit 7.1.3 remain unchanged.
- Portable catalog: PASS, 55 assertions. URI: PASS, 256 assertions. Used DOTNET_CLI_HOME under build-logs and DOTNET_ROLL_FORWARD=Major because only .NET SDK 11.0.100 is installed; test target frameworks were unchanged.
- Python tests: unable to execute; WindowsApps python.exe alias fails to run. No working Python found in checked conventional locations. The requested 45 tests are not validated by this session.
- Solution NuGet restore: SUCCESS, zero warnings/errors.
- Debug x64: FAILED, 260 errors, beginning with missing Windows.ApplicationModel/Storage/XAML namespaces. The absent baseline SDK prevents meaningful application compilation validation. Do not patch application types to compensate for missing SDK references.
- Release x64/.NET Native and new packaging: not attempted because Debug failed.

Full diagnostic logs and binary logs are in ../build-logs/session-restore.log, session-restore.binlog, session-debug-x64.log and session-debug-x64.binlog. Portable test logs are session-catalog-tests.log and session-uri-tests.log. Console logs are retained alongside them.

## Signing

Signing is configured with LaunchPass_TemporaryKey.pfx and an empty thumbprint. The PFX exists but rejects an empty password; validity and usable private key could not be established. CurrentUser/My and LocalMachine/My returned no available certificates. No signing material was changed. Existing signing scripts were inspected, not executed.

No new package exists. The transferred LaunchPass/AppPackages/LaunchPass_2.1.8.0_x64_Test/LaunchPass_2.1.8.0_x64.msix fails SignTool verify /pa /v: No signature found. See session-signature.log. It must not be treated as a fresh, signed Milestone 2 artifact.

Transferred Dependencies/x64 contains Microsoft.VCLibs.x64.14.00.appx, Microsoft.UI.Xaml.2.7.appx, Microsoft.NET.Native.Runtime.2.2.appx and Microsoft.NET.Native.Framework.2.2.appx. Confirm dependencies against a newly built Release manifest before installation.

## Deployment checklist (preparation only)

1. Resolve SDK 18362 availability, provide working Python, and make an existing valid CN=LaunchPass signing certificate usable. Then rerun Python tests, Debug, Release with UseDotNetNativeToolchain=true, and signed sideload packaging with AppxAutoIncrementPackageRevision=false. Verify the new package signature before deployment.
2. Back up LaunchPass LocalState/InternalStorageProbe.xml and logs. Preserve the working RetroArch installation and content. In the deployed XML only, change Enabled from true to false, or rename the XML; leave RomPath and CoreFileName intact. An enabled probe overrides catalog startup.
3. Prepare Docs/InternalCatalog.example.json as InternalCatalog.json for the root of **LaunchPass LocalState**, using Xbox Device Portal's app file explorer after deployment authorization. Also place an empty InternalCatalog.enabled file there. Do not use LocalCache or RetroArch's LocalState. Artwork is optional under LaunchPass LocalState/Artwork.
4. Preserve contentRoot `U:\Users\UserMgr0\AppData\Local\Packages\1e4cf179-f3c2-404f-b9f3-cb2070a5aad8_8ngdn9a6dx1ma\LocalState\roms`, game ROM `arcade/1941.zip`, and arcade default core `mame2003_plus_libretro.dll`. Double backslashes in JSON. These resolve to the recorded probe path. If the installed package/user path has changed, obtain the actual working probe path first.
5. Keep 1942 only if arcade/1942.zip is already installed and compatible. Replace/delete all placeholder entries; use real installed ROMs and core DLL basenames. Genesis, SNES, and fbneo override entries are examples, not verified content. Keep unique stable IDs.
6. Fully terminate LaunchPass, unplug external USB storage, reopen it, select an internal platform/game and launch. Verify the actual game and core loaded in RetroArch. Launch 1941 and a second installed game with the default core, then a compatible per-game override if available.
7. Use RetroArch's normal Quit/Exit action to exercise launchOnExit=LaunchPass:. Confirm launcher selection/navigation survives, then launch a different game. Repeat ten cycles, including controller Menu and suspension cases. Inspect LaunchPass LocalState/InternalCatalog.log; protocol activation success alone does not prove game loading.
8. Test missing/invalid catalog recovery, Reload catalog from the platform selector, and artwork fallback. Restore the original enabled probe XML and restart for probe recovery. For external mode, disable the probe, remove InternalCatalog.enabled and restart with existing LaunchBox storage.

See InternalCatalog.md for the complete hardware acceptance checklist. No Xbox actions in this checklist were performed.
