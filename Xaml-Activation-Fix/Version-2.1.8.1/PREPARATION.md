# Release x64 update package 2.1.8.1 — signing blocked in sandbox

The fresh 2.1.8.1 package was repackaged from the approved unsigned XAML activation fix using SDK 18362 MakeAppx unpack/pack. No application compilation or source editing was required. The original 2.1.8.0 package is unchanged.

## Verification completed

- Identity Name remains f3a28ab5-9cd3-480a-86e5-58ecbeba4015, Publisher remains CN=LaunchPass, architecture remains x64.
- Only the packaged manifest's Identity.Version changed, from 2.1.8.0 to 2.1.8.1. All other manifest content is semantically identical, including dependencies, capabilities, protocol registration and SDK minimum/tested versions.
- All 73 non-manifest application/resource files match the original by SHA-256 and length. This includes the compiled native program and resources containing the XAML fix. File inventory is unchanged.
- Windows packaging APIs validated all 74 block-map files in the fresh unsigned package. The block map was regenerated for the versioned manifest. Content-type mappings are unchanged; MakeAppx reordered their declarations.
- All application source, XAML, project and source manifest hashes still match the baseline saved before this task. Source Package.appxmanifest remains at 2.1.8.0; this is a packaging-only diagnostic revision.
- Details: repack-validation.json, unpack.log and pack.log in this directory.

Original unsigned SHA-256: 7A28ADFCAB7DA75FD914D167338B1A9909D2C81B382C14D05DC008C1130CE0B0.

Fresh unsigned SHA-256: 2FB0BA353E88FA0E3771AA95E07D01C92B9AC350F8225001FAE94BE0DDF9432E.

Unsigned package: `C:\TRANSFER\Xaml-Activation-Fix\Version-2.1.8.1\Unsigned\LaunchPass_2.1.8.1_x64.msix`.

## Signing status and completion

Signing was attempted using only existing certificate CDCEBC7F97DEB3C2EAD40D734EE1C73B159DBC66. The sandbox sees no usable matching CurrentUser/My certificate, and opening the existing PFX fails in this execution profile. The attempt stopped before creating a Signed output directory or invoking SignTool. **No new signed 2.1.8.1 package exists yet.** No certificate/key was generated/imported, and no trust store was changed. The private-key password was not printed or persisted.

Run the prepared script from regular Windows PowerShell on this NUC:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\TRANSFER\build-logs\sign-xaml-activation-2.1.8.1.ps1"
```

It pins the expected unsigned package hash and existing certificate thumbprint, reuses the reviewed capture/redaction function from sign-startup-diagnostics.ps1, and signs a copy using SHA-256. It prefers the matching current-user certificate; otherwise it reads the existing PFX and existing encrypted password record. If the PFX was backed up elsewhere, supply -PfxPath. Use -PromptForPassword to enter its existing password without echoing or saving it.

The corrected validator now supports standalone MSIX as well as bundles. It runs SignTool trust verification, checks the pinned CMS signer/digest, uses Windows block-map validation, compares actual package payload files and validates any generated code-integrity catalog and PE members. This package is a standalone MSIX; it has no embedded MSIX packages. Existing bundle validation and recursive handling remain intact. A regression check against the earlier known-good signed standalone package passed.

Expected final output **after the script succeeds**:

- `C:\TRANSFER\Xaml-Activation-Fix\Version-2.1.8.1\Signed\LaunchPass_2.1.8.1_x64.msix`
- Same folder: LaunchPass-Development.cer, Dependencies/x64, sign.log and a unique validation directory containing result.json and verification logs.

The script refuses to overwrite existing output. If signing succeeds but a subsequent check stops, preserve all output and use the same command with -VerifyOnly; it will not re-sign. A failed validation is not deployment-ready. No timestamp is requested, so certificate expiry applies.

## Dependencies

Unchanged x64 dependencies are already prepared in `C:\TRANSFER\Xaml-Activation-Fix\Version-2.1.8.1\Dependencies\x64` and will also be copied beside the signed delivery after successful validation:

| Package file | Version |
| --- | --- |
| Microsoft.NET.Native.Framework.2.2.appx | 2.2.29512.0 |
| Microsoft.NET.Native.Runtime.2.2.appx | 2.2.28604.0 |
| Microsoft.UI.Xaml.2.7.appx | 7.2109.13004.0 |
| Microsoft.VCLibs.x64.14.00.appx | 14.0.33519.0 |

Existing public certificate: `C:\TRANSFER\Xaml-Activation-Fix\Version-2.1.8.1\LaunchPass-Development.cer`. Its thumbprint matches the existing signing certificate. Do not re-sign Microsoft dependencies or upload a PFX/password record.

## Later Xbox update procedure — not performed

1. Complete signing in regular PowerShell and require validation Status=PASS. Confirm the signed manifest is 2.1.8.1 and the expected certificate is used.
2. Back up LaunchPass LocalState, including marker, catalog, probe configuration and Startup.log. Fully terminate the running LaunchPass app.
3. In Xbox Device Portal, use the existing app installation/update flow to select the **signed** 2.1.8.1 x64 MSIX and the four x64 dependencies. Do not uninstall the installed 2.1.8.0 app; this is an attempt at an in-place update that retains application data. If the portal refuses the update, capture its exact deployment error rather than automatically uninstalling or changing identity/trust.
4. The same certificate was used previously. If the target reports a signing/trust error, inspect that error before making any trust changes; this task made none. Only the public .cer is suitable for the portal's certificate flow, never the PFX/password record.
5. After installation, confirm version 2.1.8.1. First test InternalCatalog.enabled present with the catalog temporarily renamed: expect a missing-catalog recovery page and constructor/XAML/load stages in LocalState/Startup.log. Then restore InternalCatalog.json and verify catalog startup and navigation.

A higher package version supports normal update selection, but successful Xbox installation and runtime operation are not verified here. References: [MakeAppx packaging](https://learn.microsoft.com/en-us/windows/msix/package/create-app-package-with-makeappx-tool), [MSIX updates](https://learn.microsoft.com/en-us/windows/msix/app-package-updates).

No deployment or commit was performed. No catalog, XAML implementation, startup diagnostics, URI protocol, probe, dependency versions or application source manifest were modified. Only packaging/signing tooling was added or extended.
