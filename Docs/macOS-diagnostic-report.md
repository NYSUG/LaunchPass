# macOS diagnostic report

Date: November 14, 2026. This tooling change does not modify any LaunchPass application source or existing probe behavior. No commit was made.

## Validation results

| Item | Evidence/status |
|---|---|
| Read-only diagnostic CLI | `tools/xbox_diagnostics.py`, using Python's standard library and no UWP build dependencies. |
| Device Portal authentication | All 15 Python tests passed, exercising Basic auth, in-memory session cookies, CSRF mirroring, bounded refresh, redirects, redacted errors, hidden input, CLI output, and uploader environment compatibility. No real credentials are embedded or saved; fixtures are synthetic. |
| Working uploader authentication parity | **Source inspected:** `/Users/timothyjohnson/Roms/Mame/upload.sh` uses Basic auth, root GET cookie bootstrap, and CSRF cookie/header mirroring. The CLI follows that flow using an in-memory jar and accepts the same `XBOX`, `XBOX_USER`, `XBOX_PASS`, and `PKG` environment names. Uploader TLS verification is disabled; the CLI requires an explicit `--insecure` to match that setting. Live compatibility remains untested. |
| Package and directory response handling | Offline tests cover synthetic responses, version extraction, ambiguous/renamed packages, package-relative paths, safe output, and partial failures. |
| Existing URI generator | 256 assertions passed against the actual production generator and Platform source on macOS. Model/storage substitutes avoid UWP dependencies; no production logic was copied. |
| Existing generator limitations | Core text is not URI-escaped or validated by the generator. Tests characterize this unchanged behavior; the probe's core-filename validation remains necessary. |
| Xbox package/version and `1941.zip` presence | **Not measured during this task.** The uploader requires environment inputs; none of `XBOX`, `XBOX_USER`, `XBOX_PASS`, or `PKG` were inherited by this Codex session. Run the CLI from the terminal used for uploading, or supply the portal origin and enter credentials at hidden prompts. |
| Full UWP compilation | **Not performed.** Requires Windows tooling. |
| Successful game load | **Not established.** Requires the Xbox-executed probe and observation/logs from RetroArch. |

`git diff --check` also passed. The .NET runner restored/built successfully on macOS using only local framework references. That is a tooling build, not a UWP build.

## Official API investigation

The [Microsoft core REST reference](https://learn.microsoft.com/en-us/windows/uwp/debug-test-perf/device-portal-api-core) documents package enumeration and package-scoped file listing. The CLI uses GET `/api/app/packagemanager/packages` and GET `/api/filesystem/apps/files` with `knownfolderid=LocalAppData`, the measured `packagefullname`, and a package-relative `path`.

That reference documents POST `/api/taskmanager/app` as ordinary app startup using app/package identifiers. It specifies no arbitrary protocol URI argument. Passing a `retroarch:` string as an app identifier would not establish protocol activation semantics. The [official Xbox extension index](https://learn.microsoft.com/en-us/previous-versions/windows/uwp/xbox-apps/reference) also supplies no documented arbitrary-URI activation endpoint. Microsoft's [Device Portal wrapper](https://microsoft.github.io/WindowsDevicePortalWrapper/class_microsoft_1_1_tools_1_1_windows_device_portal_1_1_device_portal.html) exposes `LaunchApplication(appid, packageName)`, without a URI-activation overload in the reviewed reference.

**Finding:** no officially documented Xbox Device Portal API for arbitrary UWP protocol activation was identified in these sources. This is a documentation finding, not proof that no undocumented or firmware-specific mechanism exists. No guessed URI parameters or activation endpoints were called. Remote input or ordinary app startup is not evidence of `ActivationKind.Protocol` delivery.

Microsoft [documents cookie-to-CSRF-header handling](https://learn.microsoft.com/en-us/windows/advanced-settings/device-portal#csrf-protection-and-scripting). The client implements the same flow observed in the working uploader, in memory; successful authentication against this Xbox still needs a live run.

## What still needs the Xbox probe

1. Measure the installed package identity/version and directory entries with the CLI. This confirms privileged Device Portal visibility only, not cross-app storage permission.
2. Obtain the exact ROM runtime path and loaded core DLL filename from RetroArch. A package-relative Device Portal path does not establish the drive mapping used by RetroArch.
3. Build/deploy the UWP probe on Windows and verify that its generated URI activates the intended installed RetroArch package.
4. Observe `1941.zip` loading with the intended MAME core and confirm gameplay. A true `LaunchUriAsync` result confirms activation only.
5. Test cold start, normal quit/callback, repeated launch, an already-initialized receiver, and process restart. Use the [hardware checklist](InternalStorageProbe.md).

Local string encoding tests cannot prove receiver parsing, core resolution, ROM compatibility, callback ownership, focus behavior, or access to private content. No sandbox access bypass is attempted by this tooling.

## Run instructions

See [tools/README.md](../tools/README.md). Save a live CLI JSON report separately when available; keep its timestamp/package version with the eventual probe and RetroArch logs. Missing live results must remain marked pending rather than being filled with sample values.
