# Milestone 3 — Xbox arcade lighting

LaunchPass speaks the existing ESP32 firmware 0.2.0 HTTP API on the local IPv4 subnet.
Use the firmware's [API contract](../firmware/arcade-lighting/README.md#http-api) and [implementation](../firmware/arcade-lighting/src/HttpApi.cpp) as the reference.

## Phase A — Network client

The UWP manifest declares `privateNetworkClientServer` for home and work network access and retains `internetClient`. The package capability is verified at build time. Xbox reachability also depends on both devices being on the ESP32's local IPv4 subnet. [Microsoft capability documentation](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations)

`ArcadeLightingClient` reuses a single `HttpClient` for status, zones, single-zone updates, scenes, and blackout. It permits one request at a time, applies the configured timeout to the response, accepts caller cancellation, and limits response buffering to 16 KiB. HTTP failures, unexpected JSON, and network errors produce a diagnostic and a null result or false for blackout. Each request is independent, so the next request can succeed after a network failure.

The portable client tests passed 21 assertions against a simulated firmware handler. They cover all five routes, JSON payloads, failure responses, cancellation, timeouts, and recovery. The simulated handler exercises the client on Windows. Xbox network and LED results are tracked separately below as Phase C proceeds.

## Phase B — Cabinet lighting

Upload a copy of [ArcadeLighting.example.json](ArcadeLighting.example.json) as `ArcadeLighting.json` in LaunchPass LocalState. Set `enabled` to `true` for the bench test. The URL must be an HTTP IPv4 origin with an optional port. Set `timeoutMs` between 250 and 3000, or omit it for a 1000 ms timeout. Restart LaunchPass after editing the file. A missing, disabled, or malformed configuration leaves lighting inactive.

The existing premium browser session sends a synchronized green breathing scene on startup, then queries ESP32 status. The first restored selection preserves that scene. Subsequent games alternate red/blue and cyan/amber on the left and right. At most one selection is pending and one request is in flight. The director dispatches selection scenes at least 250 ms apart and keeps the latest selection during a burst.

Press **RB** on the controller or **F9** on the keyboard from the premium browser to send a blackout. A pending selection that predates the blackout is discarded. The next actual selection change can turn the strips on again. The director uses the existing premium browser startup, selection, and session timer. It has no separate navigation or activation handlers.

The 32 local assertions cover all five routes, synchronized startup, status diagnostics, coalescing, palette changes, blackout priority, timeouts, and recovery. The x64 Release build compiled the premium browser integration successfully. Physical validation is part of Phase C.

## Phase C — Build, deploy, and validate

Build the x64 Release bundle with VS 2022 MSBuild and the installed UWP toolchain. Set `Platform=x64`, `Configuration=Release`, `UseDotNetNativeToolchain=true`, `AppxBundle=Always`, `AppxBundlePlatforms=x64`, `UapAppxPackageBuildMode=SideloadOnly`, `AppxPackageSigningEnabled=false`, and `AppxAutoIncrementPackageRevision=false`.

From the repository root, sign a freshly built 2.1.8.16 bundle with the prepared script. The script checks the unsigned hash, signs a copy with the existing development signer, and verifies the signed bundle, embedded packages, and dependency signatures.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Builds\2.1.8.16\Sign-Release.ps1
```

On the Xbox One X, open Device Portal from Dev Home. From its Home tab, install the signed bundle as an update with the packages from `Builds\2.1.8.16\Signed\Dependencies\x64`. Use the existing LaunchPass installation so the internal catalog and settings remain available. In File Explorer, select LaunchPass and upload the enabled `ArcadeLighting.json` to LocalState. Restart LaunchPass and look for the `Arcade lighting:` lines in LocalState's `Startup.log` or its LocalCache fallback. [Microsoft Xbox sideloading procedure](https://learn.microsoft.com/en-us/windows/uwp/apps-for-xbox/development-options#manual-side-loading)

### Validation on November 26, 2026

| Check | Result |
| --- | --- |
| Build and signature | Release x64 built with zero errors. Package identity, publisher, dependencies, protocol registration, private-network capability, embedded block maps, and signatures verified. |
| In-place deployment | Device Portal reported LaunchPass 2.1.8.16, and the enabled configuration was uploaded and read back from its LocalState. |
| Launcher startup with ESP32 available | Xbox log reports catalog window activation and the premium browser loading 1,815 games. |
| Status from the Xbox | Xbox log reports `GET /api/status` HTTP 200, firmware 0.2.0, Wi-Fi connected, and two enabled zones. |
| Startup scene | Xbox log reports `POST /api/scene` HTTP 202. The ESP32 zone snapshot shows both zones running green `breathe` at 1600 ms. Both physical strips were visually confirmed breathing green. |
| Slow and rapid joystick navigation | Joystick-driven physical color changes were confirmed. The Xbox log recorded successive accepted scenes. Rapid-navigation responsiveness confirmation is pending. |
| ESP32 disconnect and reconnect | Workbench confirmation pending. Portable tests passed timeout and later success. |
| Explicit blackout | Workbench confirmation pending. Portable tests passed the empty-body POST and blackout precedence. |
| Disable integration | Xbox verification pending. Portable tests passed disabled configuration with zero requests. |

For the workbench check, watch both strips at startup, then navigate slowly and rapidly and check that the last selection changes the colors when the joystick stops. Disconnect the ESP32, navigate again, reconnect it, and move to another game. Confirm colors resume, then press RB and confirm both strips turn off. Set `enabled` to `false`, restart LaunchPass, and verify the lighting hint disappears and navigation still works. The firmware can keep displaying its last scene, so use blackout before the disable check if the strips should start dark.
