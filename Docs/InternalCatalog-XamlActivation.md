# InternalCatalogPage XAML activation fix — November 15, 2026

The Xbox log stopped at Frame.Navigate(typeof(InternalCatalogPage)) before the constructor-body log. The prior generated Release XamlTypeInfo.g.cs had no activator for that code-only page. This change gives the page a compiled XAML definition and generated activator while retaining its existing navigation and UI logic. Xbox confirmation remains outstanding.

## Modified source files

- `LaunchPass/InternalCatalogPage.xaml` (new): minimal UWP Page, x:Class `RetroPass.InternalCatalogPage`, standard UWP/XAML namespaces, and one named Grid `CatalogRoot`.
- `LaunchPass/InternalCatalogPage.xaml.cs` (new): page implementation moved from InternalCatalog.cs, declared `sealed partial`. Constructor logs entry, calls InitializeComponent, logs XAML initialization, then creates the existing UI. The existing ScrollViewer/panel is added once to CatalogRoot.Children; Page.Content is not replaced. Reload, missing-catalog recovery, play-later and probe-button behavior are unchanged.
- `LaunchPass/InternalCatalog.cs`: retains the unchanged InternalCatalogGame adapter; page implementation moved to its code-behind file. There was no separate InternalCatalogPage.cs in the checkout.
- `LaunchPass/LaunchPass.csproj`: adds Compile/DependentUpon for InternalCatalogPage.xaml.cs and a Page entry for InternalCatalogPage.xaml using MSBuild:Compile.

This report is the only added documentation file. Generated outputs, diagnostic logs, baseline copies and verification evidence are under build-logs/xaml-activation* and Xaml-Activation-Fix.

## Validation

Used Visual Studio 2022 MSBuild explicitly:

`C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe`

Both configurations ran `/t:Clean,Build`, Platform=x64, signing disabled and revision auto-increment disabled through command-line properties. Release explicitly used UseDotNetNativeToolchain=true. No source SDK/dependency/signing/version properties were changed.

| Configuration | Result | Warnings | Errors | Duration |
| --- | --- | --- | --- | --- |
| Debug x64 | Success | 12 | 0 | 23.53 seconds |
| Release x64/.NET Native | Success | 12 | 0 | 2 minutes 50.51 seconds |

Warnings are the existing WMC1506, CS4014, CS0168 and CS1998 warnings; none came from the new files.

Release `LaunchPass/obj/x64/Release/XamlTypeInfo.g.cs` now contains:

```csharp
_typeNameTable[0] = "RetroPass.InternalCatalogPage";
_typeTable[0] = typeof(global::RetroPass.InternalCatalogPage);
private object Activate_0_InternalCatalogPage() { return new global::RetroPass.InternalCatalogPage(); }
// In the generated type switch:
userType.Activator = Activate_0_InternalCatalogPage;
```

The activator is at line 313 and assignment at line 344, consistent with other generated page activators. Evidence is copied to build-logs/xaml-activation-baseline/release-activator-evidence.txt. Generated code was inspected, not manually edited.

The compiled InternalCatalogPage.xbf resource is present in the Release package's resources.pri. SDK MakePri dump of the actual packaged PRI confirms its Files/InternalCatalogPage.xbf entry; it is not a loose ZIP entry. Evidence: build-logs/xaml-activation-baseline/packaged-resources.xml.

Structural comparisons confirmed the moved page body differs only in partial declaration, InitializeComponent/XAML diagnostic stage, and root attachment. Hash comparisons confirmed App.xaml.cs (including Frame.Navigate and StartupLog), the diagnostic probe, URI generator, theme implementation, catalog parsing/serialization and package manifest were unchanged. XML comparison confirmed project edits are limited to the two new file registrations.

Full diagnostic MSBuild logs/binlogs: build-logs/xaml-activation-debug.log/.binlog and xaml-activation-release.log/.binlog.

## Unsigned outputs

Current AppxBundle=Auto settings generated full x64 MSIX packages for these clean builds:

- Debug: `C:\TRANSFER\Xaml-Activation-Fix\Debug\LaunchPass_2.1.8.0_x64_Debug_Test\LaunchPass_2.1.8.0_x64_Debug.msix`
- Release: `C:\TRANSFER\Xaml-Activation-Fix\Release\LaunchPass_2.1.8.0_x64_Test\LaunchPass_2.1.8.0_x64.msix`

Output folders are separate from earlier diagnostic/signed artifacts. The Release package has no AppxSignature.p7x; nothing was signed. Existing signing scripts remain pinned to the prior diagnostic bundle and must not be used unchanged for this new artifact.

## Remaining hardware verification

No architectural change was required. The generated activator discrepancy is corrected, but compilation does not prove the Xbox crash is resolved. After separate signing/deployment authorization, first test with InternalCatalog.enabled present and InternalCatalog.json absent: expect the constructor/XAML/load stages in Startup.log and the missing-catalog recovery screen. Then restore the valid catalog, verify platform/game browsing and Back navigation, and test normal RetroArch exit/return.

The independent no-marker/external-mode null-theme startup issue is unchanged and outside this fix. Native failures elsewhere may still bypass managed exception handling. No commits, certificate generation, signing or deployment occurred.
