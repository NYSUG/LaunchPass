# Xbox startup diagnostics (no debugger required)

The original build stayed on its splash screen without InternalCatalog.enabled. With the marker present, it returned to Dashboard even with the catalog renamed. No persistent logs were found. The exact cause remains unconfirmed; this build adds evidence rather than changing catalog activation, serialization, themes, or the RetroArch protocol.

## Capture one attempt

1. Sign the diagnostic **Release x64** bundle using the signing workflow that worked on this NUC. The generated diagnostic bundle is unsigned. Its version and publisher remain unchanged. Do not use an old signed bundle from another output directory.
2. Before updating, back up LaunchPass LocalState, especially InternalCatalog.json, InternalCatalog.enabled and any probe configuration. Prefer updating the installed app; uninstalling loses its application data. If installation requires uninstalling, restore these files afterward. No deployment was performed by this task.
3. Keep an empty file named exactly InternalCatalog.enabled in LaunchPass LocalState. For the first attempt, keep the catalog renamed to InternalCatalog.json.bak so the expected result is the missing-catalog recovery page. Ensure the enabled probe is not overriding catalog selection.
4. Fully terminate LaunchPass, launch once, and note whether it returns to Dashboard or displays a startup error. The startup error screen shows the exception type, HRESULT and message if the exception can be caught and UI remains usable.
5. In Xbox Device Portal, select **LaunchPass**, open its **LocalState** and download **Startup.log**. If absent, check LaunchPass **LocalCache/Startup.log**. These are not RetroArch files. Refresh the app file view after the attempt.
6. Share the complete log from that launch, including any exception/inner-exception stack traces. Logs append across launches; each attempt includes App constructor and OnLaunched entries with UTC timestamps. Back up/delete old diagnostic logs manually if they become large.
7. Then restore the exact catalog filename InternalCatalog.json and repeat. The spelling is JSON, not JAON; a mistaken filename should produce a recovery message, not be silently corrected by code.

## Recorded stages

- App constructor before/after XAML resource initialization and after settings initialization.
- Launch/activation entry, probe selection, marker lookup and selected mode.
- Internal theme/frame setup and navigation begin/result/failure.
- Catalog page construction, including panel and session playlist initialization.
- Catalog file lookup, text read, parse begin/result and platform-control creation.
- Page load completion and window activation.
- Exceptions from launch/activation, catalog load and the existing application UnhandledException event.

Each write closes the file before startup continues, independent of the probe and optional legacy logging switches. LocalState write failure falls back to LocalCache; if both fail, the error screen includes the latest write error when possible. Catalog content and credentials are not deliberately logged (exception messages may contain paths).

If the app still terminates, the last complete stage narrows the failure boundary. It does not itself prove a particular cause. Native failures, OS termination and failures before App construction can bypass managed exception handlers. Absence of both logs in this instrumented build requires checking the installed build, file access and a system-level crash report; it does not prove a JSON problem.

## Scope

Only App.xaml.cs and InternalCatalog.cs implement the diagnostics. The probe source, production URI generator, SDK/dependency configuration and package manifest are unchanged. Existing startup priority and external LaunchBox flow remain in place; caught launch/activation exceptions now display a diagnostic screen. No serialization/runtime-directive changes were made.

Build logs: build-logs/startup-diagnostics-debug.log and startup-diagnostics-release.log, with matching binary logs. Package output is isolated under Startup-Diagnostics/Debug and Startup-Diagnostics/Release so earlier AppPackages bundles are preserved. Signing and Xbox runtime validation are separate steps.

## Build validation — November 15, 2026

VS 2022 MSBuild successfully built Debug x64 and Release x64 with .NET Native enabled. Each build had 12 existing warnings and zero errors. Catalog tests passed 55 assertions; URI tests passed 256 assertions. Hash checks confirmed that InternalStorageProbe.cs, UrlSchemeGenerator.cs, LaunchPass.csproj and Package.appxmanifest were unchanged.

Unsigned diagnostic Release bundle: `C:\TRANSFER\Startup-Diagnostics\Release\LaunchPass_2.1.8.0_Test\LaunchPass_2.1.8.0_x64.msixbundle`. The corresponding Dependencies/x64 folder is alongside it. Nothing has been signed, deployed or committed in this diagnostic change. These build results do not establish that the original Xbox crash is fixed.
