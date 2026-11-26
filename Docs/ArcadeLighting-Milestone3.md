# Milestone 3 — Xbox arcade lighting

LaunchPass speaks the existing ESP32 firmware 0.2.0 HTTP API on the local IPv4 subnet.
Use the firmware's [API contract](../firmware/arcade-lighting/README.md#http-api) and [implementation](../firmware/arcade-lighting/src/HttpApi.cpp) as the reference.

## Phase A — Network client

The UWP manifest declares `privateNetworkClientServer` for home and work network access and retains `internetClient`. The package capability is verified at build time. Xbox reachability also depends on both devices being on the ESP32's local IPv4 subnet. [Microsoft capability documentation](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations)

`ArcadeLightingClient` reuses a single `HttpClient` for status, zones, single-zone updates, scenes, and blackout. It permits one request at a time, applies the configured timeout to the response, accepts caller cancellation, and limits response buffering to 16 KiB. HTTP failures, unexpected JSON, and network errors produce a diagnostic and a null result or false for blackout. Each request is independent, so the next request can succeed after a network failure.

The portable client tests passed 21 assertions against a simulated firmware handler. They cover all five routes, JSON payloads, failure responses, cancellation, timeouts, and recovery. The simulated handler exercises the client on Windows. Xbox network and LED results are tracked separately below as Phase C proceeds.
