# VS 2022 baseline build verification — November 14, 2026

Debug and Release x64 builds succeeded without source changes. This supersedes earlier missing-SDK build results.

## Environment

- Visual Studio Professional 2022 17.14.37906.3; MSBuild 17.14.60.43110 at `C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe` was explicitly used for restore and both builds.
- UWP workload/support, XAML v17.0 targets and .NET Native tools are installed. Release successfully ran .NET Native 2.2.12-rel-31116-00 from the offline UWPNuGetPackages toolchain.
- Installed Windows SDK/UAP versions: 10.0.18362.0 and 10.0.26100.0. Project now already specifies target 10.0.18362.0 and minimum 10.0.17763.0; no retargeting was performed in this session. Both builds explicitly selected Platform=x64.
- Dependencies unchanged: Microsoft.NETCore.UniversalWindowsPlatform 6.2.14, Microsoft.Toolkit.Uwp.UI.Controls 7.1.3; portable library remains netstandard2.0.
- Only VS 2022 was returned by vswhere in this session; VS 2026 was not registered in its output. This does not block the requested VS 2022 build.
- No required build tooling is missing for the tested configurations. This transfer still has no .git metadata, so Git status/diff cannot establish prior changes.

## Results

| Operation | Result |
| --- | --- |
| Solution Debug restore | Success, zero warnings/errors |
| Debug x64 | Success, 12 warnings, zero errors, 28.39 seconds |
| Release restore | Up to date |
| Release x64, UseDotNetNativeToolchain=true | Success, 12 warnings, zero errors, 2 minutes 40.28 seconds |
| Catalog tests | Pass, 55 assertions |
| URI tests | Pass, 256 assertions |

Each configuration emitted one WMC1506 binding-notification warning (SettingsPage.xaml:99), six CS4014 unawaited-call warnings, three CS0168 unused-variable warnings and two CS1998 async-without-await warnings. No fixes were made. Full locations and messages are in the diagnostic logs.

Portable tests used installed .NET SDK 9.0.321 and DOTNET_ROLL_FORWARD=Major to execute the unchanged net8.0 test projects. DOTNET_CLI_HOME was redirected to build-logs/dotnet-home. These tests do not validate Xbox activation or runtime behavior.

## Reproduction and logs

Use the MSBuild executable above with `LaunchPass\LaunchPass.sln /p:Platform=x64 /p:AppxPackageSigningEnabled=false /p:AppxAutoIncrementPackageRevision=false`.

- Debug: `/t:Restore` then `/t:Build /p:Configuration=Debug`.
- Release: `/t:Restore,Build /p:Configuration=Release /p:UseDotNetNativeToolchain=true`.

Signing and auto-increment overrides were command-line-only; project signing configuration and manifest identity/version were preserved.

Full diagnostic logs and binary logs: `build-logs/vs2022-restore.{log,binlog}`, `vs2022-debug-x64.{log,binlog}`, `vs2022-release-x64.{log,binlog}`. Console output and portable test logs use the same vs2022 prefix.

## Artifacts and signing

- Debug executable: `LaunchPass/bin/x64/Debug/LaunchPass.exe`.
- Debug bundle: `LaunchPass/AppPackages/LaunchPass_2.1.8.0_Debug_Test/LaunchPass_2.1.8.0_x64_Debug.msixbundle`.
- Release native payload: `LaunchPass/bin/x64/Release/ilc/`.
- Release x64 package: `LaunchPass/bin/x64/Release/LaunchPass_2.1.8.0_x64.msix`.
- Release bundle: `LaunchPass/AppPackages/LaunchPass_2.1.8.0_Test/LaunchPass_2.1.8.0_x64.msixbundle`.
- Release Dependencies/x64: Microsoft.NET.Native.Framework.2.2.appx, Microsoft.NET.Native.Runtime.2.2.appx, Microsoft.UI.Xaml.2.7.appx, Microsoft.VCLibs.x64.14.00.appx.

Packages are unsigned by design for compilation validation. SignTool verify /pa /v on the new Release bundle reports No signature found; see build-logs/vs2022-signature.log. This is not a deployment-ready signed artifact.

The configured LaunchPass_TemporaryKey.pfx still cannot be opened with an empty password. No matching signing certificate was found in CurrentUser/My or LocalMachine/My. To sign, make the existing valid PFX/private key usable with its password, or provide an existing valid code-signing certificate whose subject matches CN=LaunchPass. Certificate validity and signing suitability remain unverified. No signing certificate was created/imported/overwritten.

Manifest remains identity f3a28ab5-9cd3-480a-86e5-58ecbeba4015, publisher CN=LaunchPass, version 2.1.8.0. No intentional source/project/dependency/RetroArch integration changes, commits, uploads or deployments were performed. Generated build outputs and logs were written. Xbox runtime acceptance remains outstanding.
