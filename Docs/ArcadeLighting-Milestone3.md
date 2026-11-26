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
