# Milestone 3A — keyboard input investigation and opt-in diagnostics

This phase observes actual Xbox-delivered key events. It does not assign X-Arcade controls to navigation actions. Hardware mappings remain unknown until captured. Internal catalog activation, RetroArch launch/return, controller behavior and external LaunchBox support are preserved.

## Existing architecture

- `InternalCatalogPage.xaml.cs`: standard UWP Buttons in a programmatic StackPanel/ScrollViewer under the compiled XAML root. Reload focuses the first platform button. A platform button Click navigates to GameCollectionPage. There were no custom KeyDown/KeyUp handlers or keyboard remapping on this page.
- `GameCollectionPage.xaml`: GridView uses IsItemClickEnabled, IsTabStop, SingleSelectionFollowsFocus and XYFocusKeyboardNavigation=Enabled. UWP supplies normal focus/activation behavior; ItemClick opens the details ContentDialog.
- `GameCollectionPage.xaml.cs`: routed OnKeyDown handles GamepadX or keyboard X for play later, GamepadY or keyboard Y for search, and GamepadMenu for immediate launch. These depend on key events reaching the page. BackRequested calls Frame.GoBack and marks the Back request handled. There is no corresponding explicit arbitrary-key Back mapping.
- `GameDetailsPage`: standard Buttons/ContentDialog; its action panel enables XYFocusKeyboardNavigation. Play uses ButtonPlay_Click. Search similarly uses normal input controls and keyboard directional navigation. The commented-out CoreWindow handlers in existing code are not active handlers.
- `MainPage.IsBackOrEscapeKey` recognizes Back/Escape/GamepadB, but has no callers; it does not implement global keyboard Back.
- `App.RequiresPointerMode=WhenRequested` and the focus/navigation system provide controller navigation. UWP gamepad keys can be translated to keyboard-style keys for controls. Working Xbox controller navigation does not demonstrate that arbitrary keyboard-emulator keys have suitable navigation semantics.

Keyboard navigation is therefore **partially implemented**, not a complete X-Arcade mapping layer. Keyboard emulation may send keys other than arrows/Enter, and focused controls may consume routed keys before page overrides see them. Neither possibility proves what this device currently sends. RetroArch working with the device also does not prove these UWP UI event APIs receive the same input.

## Enable without using the X-Arcade

1. After a separately authorized install of this diagnostic build, use Device Portal to select **LaunchPass**, then its **LocalState root**.
2. Keep the working InternalCatalog.enabled and InternalCatalog.json unchanged.
3. Upload an empty file named exactly **InputDiagnostics.enabled** (not .txt). It must be a file, not a directory.
4. Fully terminate and reopen LaunchPass. A passive bottom panel on the internal platform selector says **Input diagnostics — listening (observation only)**. No controller or joystick action is needed to open it.
5. Even with no input, elapsed seconds, event counters, focused element, window visibility and a periodic log heartbeat remain available. Wait at least one second before retrieving logs. If diagnostics initialization fails, startup continues and Startup.log records the failure.

Remove InputDiagnostics.enabled and restart to disable. The marker is also rechecked whenever the selector page is loaded, including returning from a collection. It is not continuously polled while another page is displayed. Existing Input.log files are not deleted merely by disabling diagnostics.

## What is captured

- CoreWindow.KeyDown/KeyUp: window-level VirtualKey and physical status.
- CoreDispatcher.AcceleratorKeyActivated: KeyDown/KeyUp/SystemKeyDown/SystemKeyUp observations when delivered, useful for modifier/system-key distinctions.
- The root Frame's routed KeyDown/KeyUp using AddHandler(..., handledEventsToo: true): translated **Key**, raw **OriginalKey**, and the observed Handled value even when a child already handled the event. No handler sets Handled.
- Key names, decimal/hex values, scan code, extended-key flag, RepeatCount, WasKeyDown and IsKeyReleased, with focused-element type and XAML Name when available. Focus is sampled at observation time and also refreshed on the panel.
- Gamepad virtual-key names are labeled gamepad-VK; other keys are labeled keyboard-style-VK. This is a key-code distinction, **not physical device identification**. Generic Ctrl/Alt versus left/right variants and extended scan status should be recorded as delivered rather than inferred.
- Window activation/visibility transitions and ten-second heartbeats with counters, including zero-event sessions.

One physical press may appear in multiple event sources; this is intentional. Do not count those as separate physical presses. Routed handlers on the root Frame may not see events in a separate popup visual tree; CoreWindow/accelerator capture remains subscribed during collection/search/details navigation. OS-reserved or device input not delivered to these APIs cannot be reconstructed. Nothing captures RetroArch's input while that application owns the foreground.

## Panel and log bounds

The noninteractive panel is present only on the selector, in its own Auto-height row with a maximum height. It does not enter the tab order, take focus, or cover the library as an overlay. History keeps the latest ten lines, newest first; long visible lines are shortened while the log retains the fuller observation. The panel remains useful when every key count is zero.

Core/route/accelerator counters include observations suppressed by repeat throttling. Repeated down observations are limited to one per source/event/key every 150ms; release observations are not repeat-throttled. The pending queue is capped at 256 lines and old pending lines are dropped if overloaded. Suppression/drop counters are visible.

Event handlers only enqueue observations; file I/O runs on a worker task. A 500ms timer flushes at most 64 bounded lines per batch. `LocalState/Input.log` rotates before exceeding 256 KiB, retaining one previous file, `Input.previous.log` (normally at most another 256 KiB). Logging errors appear in panel status, are caught and do not prevent startup/navigation. The last unflushed batch can be lost on termination/suspension; wait briefly after releasing each test control before leaving the app. Rotation intentionally discards older diagnostic history.

Retrieve **both** Input.log and Input.previous.log from **LaunchPass LocalState** through Device Portal. If initialization failed, also retrieve Startup.log. No logging switch in LocalSettings is required. No raw text-box contents are sampled; individual key names/codes are logged while this opt-in capture is enabled.

## Hardware capture procedure

1. Confirm the panel is listening with zero or low counts; note its UTC session time. Leave LaunchPass foreground.
2. Without a controller, press/release each X-Arcade control separately and pause about one second between tests. Record the order in a text note: Player 1 Up, Down, Left, Right, then each labeled physical button, Start/Coin, followed by Player 2 controls. Use the labels on this actual cabinet; do not assume factory key assignments.
3. Hold one direction for two seconds, then release; confirm a release observation and repeat behavior. Test combinations only after single controls have been recorded.
4. If no X-Arcade events appear, press a standard USB keyboard's arrow/Enter keys and then an Xbox controller button. Record which sources count events. A working comparison device with zero X-Arcade events suggests a delivery/device-mode issue rather than a remapping rule. Zero observations from all devices requires investigating focus, foreground state, installed build and capture availability first.
5. Use the working controller to visit collection, details and search, then return to the selector. Capture logs to compare routed versus window events, focus and Handled values. Ensure normal controller navigation and RetroArch launch/exit/return still work with diagnostics enabled.
6. Send the two logs and the physical-control test order. The next phase can define mappings from observed key codes, handle modifiers/repeats and implement consistent navigation/selection/Back across all internal screens.

## Changed files and validation

- New InputDiagnostics.cs: marker, passive panel, window/routed observers, bounded asynchronous logging.
- InternalCatalogPage.xaml: adds layout rows; main UI remains row zero and diagnostic UI occupies row one only when enabled.
- InternalCatalogPage.xaml.cs: adds a separate, failure-isolated Loaded subscriber to check the marker/start diagnostics.
- LaunchPass.csproj: registers InputDiagnostics.cs.
- This document: investigation, test procedure and API references.

Protected-file hashes confirm App startup, catalog parsing/game adapter, existing collection/details/search handlers, probe, URI generator, source manifest and themes were not edited. No new dependencies, SDK/framework migration, signing, deployment or commits are included. Xbox input capture, rendering and focus/modal behavior still require hardware verification.

Build outputs/logs are under Milestone3A/Debug, Milestone3A/Release and build-logs/milestone3a-*. These are unsigned builds using the unchanged source version 2.1.8.0, not a versioned update of the installed app. Packaging/versioning/signing remains a separate step.

VS 2022 validation passed for final Debug x64 and Release x64/.NET Native: each had 12 existing warnings and zero errors. Full logs/binlogs: build-logs/milestone3a-debug-final.log/.binlog and milestone3a-release.log/.binlog. The generated Release InternalCatalogPage activator is retained. No keyboard hardware events have been captured or claimed as tested on the build machine.

Unsigned Release bundle: `C:\TRANSFER\Milestone3A\Release\LaunchPass_2.1.8.0_Test\LaunchPass_2.1.8.0_x64.msixbundle`.

The later versioned hardware-test rebuild is documented in [Milestone3A-Package.md](Milestone3A-Package.md). It supersedes the original unsigned build path above; source and generated package manifests now use 2.1.8.2. The input diagnostic implementation is unchanged.

## Microsoft API references

- [UWP gamepad/remote navigation, mappings and BackRequested](https://learn.microsoft.com/en-us/windows/uwp/ui-input/gamepad-and-remote-interactions)
- [UWP keyboard interaction and focus](https://learn.microsoft.com/en-us/windows/uwp/design/input/keyboard-interactions)
- [Key versus OriginalKey](https://learn.microsoft.com/en-us/uwp/api/windows.ui.xaml.input.keyroutedeventargs.key)
- [Routed events and handledEventsToo](https://learn.microsoft.com/en-us/uwp/api/windows.ui.xaml.uielement.addhandler)
- [CoreWindow keyboard events](https://learn.microsoft.com/en-us/uwp/api/windows.ui.core.corewindow)
- [CoreDispatcher accelerator events](https://learn.microsoft.com/en-us/uwp/api/windows.ui.core.coredispatcher.acceleratorkeyactivated)
