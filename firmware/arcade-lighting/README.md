# Arcade lighting firmware

The firmware tests WS2812B-compatible strips on an ELEGOO ESP32 DevKit V1
(30-pin, USB-C). It uses PlatformIO, the Arduino framework, and FastLED with
the `esp32dev` board target.

## Hardware

| Zone | ESP32 GPIO | SN74AHCT125N input → output | Enabled |
| --- | --- | --- | --- |
| left | GPIO25 | pin 2 (1A) → pin 3 (1Y) | Yes, 10 pixels |
| right | GPIO26 | pin 5 (2A) → pin 6 (2Y) | Yes, 10 pixels |
| coin_door | GPIO27 | pin 9 (3A) → pin 8 (3Y) | No |
| rear | GPIO33 | pin 12 (4A) → pin 11 (4Y) | No |
| marquee | GPIO32 | second buffer, channel 1 | No |
| monitor | GPIO18 | second buffer, channel 2 | No |
| reserved_1 | GPIO19 | second buffer, channel 3 | No |
| reserved_2 | GPIO23 | second buffer, channel 4 | No |

Power the ESP32 from USB-C. Connect the external regulated 5 V supply to
the strip's 5 V input and buffer pin 14 (VCC). Tie the supply return, strip
ground, buffer pin 7 (GND), and an ESP32 GND pin together. Keep the external
5 V rail separate from the USB-powered ESP32's power pins.

Ground pins 1 and 4 (active-low OE) to enable the first two channels. On the
breadboard, tie unused OE pins 10 and 13 to 5 V, tie unused input pins 9 and
12 to ground, and leave unused outputs open. Fit a 100 nF bypass capacitor
across pins 14 and 7 near the buffer. Connect pin 3 to left strip **DIN** and
pin 6 to right strip **DIN**. Check the polarity at both strips before power-up.
[TI SN74AHCT125 datasheet](https://www.ti.com/lit/ds/symlink/sn74ahct125.pdf)

Set each zone's name, pixel count, enabled state, and chase direction
(`reverse`) in [`include/LightingConfig.h`](include/LightingConfig.h).
Enabled zones require a positive count. The firmware initializes only enabled
GPIOs and addresses only the configured number of pixels. Both active strips
use WS2812B, GRB order, and a global brightness limit of 16/255.

## Build, upload, and monitor on macOS

From the repository root, check the available tools first.

```sh
command -v pio
pio --version
pio pkg list --global
```

If the CLI is missing and Homebrew is available, install it with
`brew install platformio`. The first build fetches any missing dependencies.

```sh
cd firmware/arcade-lighting
pio run
```

Plug in the ESP32 with a USB-C data cable and run `pio device list`. Look for
the USB-to-UART bridge. If several ports are listed, compare
`ls /dev/cu.*` before and after plugging in the board.

Replace `/dev/cu.usbserial-0001` with the detected port if it differs. Close
any serial monitor before uploading.

```sh
pio run -t upload --upload-port /dev/cu.usbserial-0001
pio device monitor --port /dev/cu.usbserial-0001 --baud 115200
```

If the uploader stalls at `Connecting...`, hold BOOT, tap EN, and release
BOOT once the upload starts. If no USB serial device appears, check the
cable and USB connection. Press EN with the monitor open to see the startup
phase. Use Ctrl+C to exit the monitor.

## Validate both strips

With both strips and the buffer powered, reset the ESP32. Match the first
10 pixels on each strip to the phases printed at 115200 baud.

1. Both zones start dark.
2. Left shows red while right shows blue.
3. One red pixel chases on left at 250 ms per step while one blue pixel
   chases on right at 400 ms per step.
4. Both zones switch colors together from green to blue to white.
5. Left goes dark while right stays white.
6. Both zones go dark, then the sequence repeats.

To run the Milestone 1 diagnostic, set `kDiagnostic` to
`Diagnostic::Milestone1` and rebuild and upload. Left shows red, green,
blue, and white for two seconds each, followed by a white pixel chase
and a one-second blackout. Right stays dark. Set the left count to five
to match the original bench test.

If the serial phases appear but the strip stays dark, check the external 5 V,
common ground, pin 1 enable, and DIN connection. If the colors appear in the
wrong order, check the strip's color order and adjust `kColorOrder`.

## Wi-Fi and network mode

The default `esp32dev` environment runs the hardware diagnostic. The
`esp32-network` environment starts both zones off and controls them over HTTP.
From this project directory, copy the credentials template if you don't already
have a local copy, then fill in your 2.4 GHz network's SSID and password.

```sh
cp -n include/WifiCredentials.example.h include/WifiCredentials.h
pio run -e esp32-network
pio run -e esp32-network -t upload --upload-port /dev/cu.usbserial-0001
pio device monitor --port /dev/cu.usbserial-0001 --baud 115200
```

The credentials header is ignored by Git. Network firmware binaries contain
the credentials, so keep those binaries private. With no credentials, the
network build leaves Wi-Fi disabled. Watch for `Wi-Fi IP:` on serial after
boot or reconnect. Press EN with the monitor open if you missed the address.

Use a trusted LAN. The server accepts API clients on the ESP32's local IPv4
subnet and uses plain HTTP without authentication. Keep the ESP32 off public
networks and leave router port forwarding disabled. The render loop runs
independently of the HTTP task. Effects continue running during a Wi-Fi outage.
Effect state is in memory and resets to off when the ESP32 restarts.

## HTTP API

| Method and path | Request | Successful response |
| --- | --- | --- |
| `GET /api/status` | Empty | `200` with `firmware_version`, `uptime_ms`, `ip_address`, `wifi_status`, and `enabled_zone_count` |
| `GET /api/zones` | Empty | `200` with a `zones` array |
| `POST /api/zones/{name}` | One effect object | `202` with `{"accepted":true,"revision":1}` |
| `POST /api/scene` | `{"zones":{"left":{...},"right":{...}}}` | `202` with `accepted` and the queued `revision` |
| `POST /api/blackout` | Empty | `200` with `{"blackout":true}` after the off frame is sent |

Status reports uptime as an integer in milliseconds, enabled zone count as
an integer, and the other fields as strings. Each zone entry includes
`name`, `gpio`, `led_count`, `enabled`, `reverse`, and `effect`.
Active effects report their applicable parameters using the request field
names below. Disabled zones are listed with `effect: "off"`.

An effect object requires an `effect` string.

| Effect | Optional parameters |
| --- | --- |
| `off` | None |
| `solid` | `color`, `brightness` |
| `breathe` | `color`, `brightness`, `speed_ms` for a full cycle (default 2000) |
| `chase` | `color`, `brightness`, `speed_ms` per pixel (default 250) |

`color` is an RGB array of three integers from 0 to 255, defaulting to
`[255,255,255]`. `brightness` is an integer from 0 to 255, defaulting to
255. It scales the color within the hardware cap of 16/255. `speed_ms`
is an integer from 20 to 60000. Each update replaces that zone's settings
and restarts the effect. Send JSON bodies with
`Content-Type: application/json`. The maximum body size is 2048 bytes.

A scene validates all entries before queuing any of them. Named zones begin
on the same render frame and share the same start time. Omitted zones keep
running. If updates to the same zone arrive within one frame, the latest
one takes effect. A blackout requests an immediate frame and waits briefly
for it to be sent.

API validation errors use `{"error":"message"}`. Status codes include
`400` for invalid JSON or effect parameters, `403` for a client outside the
local subnet, `404` for an unknown zone, `408` for a body read timeout,
`409` for a disabled zone, `413` for an oversized body, `415` for an
unsupported content type, and `503` if the blackout frame is still pending.

## Expanding to eight outputs

The pinned FastLED build uses the ESP32 RMT driver. The classic ESP32 has
eight RMT memory blocks, and the driver defaults to two blocks per
transmitter, so it can start up to four strips in parallel and schedules
additional strips as channels become available. Scene settings still latch
together in software, but eight physical strips can show inter-group skew.
Longer strips and Wi-Fi traffic increase the time and interrupt load per
frame. Validate timing and visible stability under Wi-Fi traffic at the
intended strip lengths before enabling all eight outputs. Size the external
5 V supply and distribution for the installed strips.
