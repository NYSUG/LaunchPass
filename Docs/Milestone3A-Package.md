# Milestone 3A hardware-test package — 2.1.8.2

Release x64/.NET Native was rebuilt successfully. Signing requires the user's regular Windows PowerShell session; no signed 2.1.8.2 bundle exists yet.

## Version and identity

The source Package.appxmanifest now uses version **2.1.8.2**, higher than both the original 2.1.8.0 build and the previously prepared 2.1.8.1 update. The Xbox's currently installed version was not queried; if it is already 2.1.8.2 or higher, do not attempt to force an update/downgrade.

Identity Name remains `f3a28ab5-9cd3-480a-86e5-58ecbeba4015`; Publisher remains `CN=LaunchPass`. These package-family inputs, the app identity, protocol registrations, capabilities and dependency requirements are unchanged. The .NET assembly version remains the existing 1.1.0.0; it is separate from the MSIX deployment version.

The generated bundle, application package and all four resource packages consistently use 2.1.8.2. Existing historical packages/manifests were not retargeted. MSBuild Rebuild cleaned its prior unsigned build outputs; the new versioned output directory is authoritative. Previously copied signed delivery artifacts were not changed.

## Build and unsigned validation

- VS 2022 MSBuild, Release x64, UseDotNetNativeToolchain=true, Rebuild: **success**, 12 existing warnings, zero errors, 2 minutes 55.42 seconds.
- AppxBundle=Always and AppxBundlePlatforms=x64 were supplied as command-line properties to guarantee an x64 bundle; revision auto-increment and signing were disabled for the build. SDK/dependencies/project settings were not upgraded.
- Windows packaging APIs validated the outer bundle block map and all five embedded package block maps: 46 application files plus 9 files in each resource package.
- The generated application manifest matches the preserved bundled diagnostic manifest after normalizing only Identity.Version. All implementation, XAML and project hashes match the pre-task baseline. Input diagnostics, catalog implementation, startup diagnostics, navigation, probe and URI generator were not edited.
- The generated InternalCatalogPage native XAML activator remains present.
- Four x64 dependency packages meet the generated manifest's publisher/version/architecture requirements, match the preserved known dependency files by SHA-256, and pass SignTool verify /pa /v (exit 0 each).

Unsigned bundle:

`C:\TRANSFER\Milestone3A\2.1.8.2\Unsigned\LaunchPass_2.1.8.2_Test\LaunchPass_2.1.8.2_x64.msixbundle`

Unsigned SHA-256: `FF32D45C8D4D0829FC072FC1B72385553C0159F547DEB554208160D5C1C4BDFD`.

Evidence: `Milestone3A/2.1.8.2/unsigned-inspection.json`, `DependencyVerification/results.json` and associated signature logs; full build log/binlog at `build-logs/milestone3a-2.1.8.2-release.log/.binlog`.

## Finish signing locally

No matching usable private-key certificate is visible in the sandbox's personal certificate stores. Opening the existing PFX failed in this execution profile before the script created a Signed directory or invoked SignTool. No certificate generation, import, trust-store change or password persistence occurred.

Run from **regular Windows PowerShell on this NUC**:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\TRANSFER\build-logs\sign-milestone3a-2.1.8.2.ps1"
```

The script is derived from the corrected sign-startup-diagnostics.ps1, with only the version, source/output paths and pinned unsigned hash updated. It requires certificate thumbprint `CDCEBC7F97DEB3C2EAD40D734EE1C73B159DBC66`, exact subject CN=LaunchPass, current validity, code-signing EKU and usable private key. It uses the current-user certificate or the existing password-protected PFX/encrypted password record. Passwords are not printed or newly persisted. If the PFX was relocated, supply `-PfxPath`; `-PromptForPassword` requests the existing password without echoing it.

Expected final signed bundle **after successful execution**:

`C:\TRANSFER\Milestone3A\2.1.8.2\Signed\LaunchPass_2.1.8.2_x64.msixbundle`

Alongside it: `LaunchPass-Development.cer`, `Dependencies/x64`, `sign.log`, and a unique validation directory containing `result.json` and verification logs. Keep the signing script, validate-startup-diagnostics.ps1 and AppxIntegrity.cs together in build-logs.

The script checks SignTool trust verification and pinned SHA-256 CMS signatures on the bundle and every embedded package, Windows block-map validation, actual application/resource file equality against the unsigned packages, exact allowed signing-metadata changes, and generated code-integrity catalog signatures/PE membership. It does not compare embedded container hashes across recursive signing. This proves signed payload/identity/version preservation when the script completes; **those signed checks are pending for this new bundle**.

The script refuses to overwrite existing output. If signing succeeds but a later check stops, preserve the files and rerun the same command with `-VerifyOnly`; this does not re-sign. Dependencies are copied only after validation, without overwriting differing files. No trust exception is accepted as a verification pass, and no trust store is changed. No timestamp is requested; certificate expiry applies.

## Required x64 dependencies

| File | Version |
| --- | --- |
| Microsoft.NET.Native.Framework.2.2.appx | 2.2.29512.0 |
| Microsoft.NET.Native.Runtime.2.2.appx | 2.2.28604.0 |
| Microsoft.UI.Xaml.2.7.appx | 7.2109.13004.0 |
| Microsoft.VCLibs.x64.14.00.appx | 14.0.33519.0 |

Already present in the unsigned build's Dependencies/x64 folder; the signing script copies exactly these to the final Signed/Dependencies/x64 folder. Do not re-sign them or substitute ARM/x86 dependencies.

## Later hardware test

After signing validation passes, back up LaunchPass LocalState and terminate the app. Attempt a Device Portal in-place update with the top-level signed bundle and its x64 dependencies; do not uninstall the working application. The unchanged Name/Publisher permit the normal package-family update path, but Xbox installation itself is not verified here. If update fails, record the exact Portal error before changing identity, trust or installed data.

Keep the working InternalCatalog.enabled and InternalCatalog.json. Add an empty InputDiagnostics.enabled marker, restart, and follow XArcadeInputDiagnostics.md to capture each physical control without assuming its mapping. Verify controller navigation and the existing 1941 launch/RetroArch return baseline as part of hardware acceptance. No uninstall, deployment or commit was performed by this task.
