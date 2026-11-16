# Internal catalog cabinet navigation

## Manufacturer conventions versus actual keys

The [Xgaming Machine guide](https://support.xgaming.com/support/solutions/articles/5000545135-x-arcade-machine-setup-guide-manual-and-support) assigns menu roles to physical controls: joystick directions navigate, Start 1/Button 1 select, Button 4 returns, Buttons 2/5 jump letters, and Buttons 3/6 relate to favorites. Those are Maximus frontend actions, not Windows VirtualKey names. Its in-game exit chord is not implemented or changed by this frontend work.

The separate [Dual/Solo controller manual, Mode-1 diagram on printed page 8](https://xgameroom.com/service/ServiceFiles/X-Arcade%20Manual%20USA.pdf) documents keyboard assignments. Default candidates are Start 1=top-row 1; Player-1 Buttons 1–6=left Ctrl, left Alt, Space, left Shift, Z, X. Its joystick diagram uses numeric-keypad directions; the actual delivered keys depend on keyboard/NumLock behavior. [Xgaming's test guide](https://support.xgaming.com/support/solutions/articles/5000545137-test-your-x-arcade-first-button-and-joystick-diagnostics) recommends testing individual controls in the intended mode.

These factory candidates are not asserted as this cabinet's physical identities. The safe built-in profile does not activate them automatically. No firmware mode, NumLock setting or RetroArch control configuration was changed.

### Comparison with the provided Input.log

The supplied log covers 2026-11-15 19:11:01–19:11:49 UTC. It is preserved unchanged. Evidence summaries are under build-logs/cabinet-navigation-observed-keys.json and cabinet-navigation-input-evidence.json.

| Input | Actual evidence | Interpretation |
| --- | --- | --- |
| Up / Down / Left / Right | VK 38 / 40 / 37 / 39, scan 72 / 80 / 75 / 77, nonextended | Player-1 directions are user-confirmed. Use these delivered arrow codes, not inferred NumPad numbers. |
| Control, Menu, Shift | VK 17, 18, 16 | Actual generic modifier events exist; Menu is Windows Alt, not GamepadMenu and not a Maximus menu action. |
| Space, Z, X, C | VK 32, 90, 88, 67 | Delivered, but the log does not label their physical buttons. |
| Numeric keys | Number1–Number6 (VK 49–54) | Top-row virtual keys, distinct from NumberPad1–6. Start/Coin identities require a labeled test. |
| R, F, D, G and other letters | Delivered | Consistent with additional controls; no Player-2 navigation mapping is assumed. |
| Brackets | Numeric VK 219/221 | Delivered OEM codes, not assigned frontend roles. |

At 19:11:14, Down reaches the routed observer with Handled=True while focus is a Button; the 19:11:33 heartbeat also shows GridViewItem focus. This supports controlling focus before ScrollViewer/control class handling. Space subsequently activates a focused Play button and the window deactivates. Unassigned Space is now blocked as an implicit button activation outside text editing, so it is not silently treated as another Select key.

## Implemented behavior

The central UWP adapter is LaunchPass/CabinetInput.cs. Mapping, repeat rules and navigation arithmetic are in the portable LaunchPass.Catalog/CabinetInput.cs, shared with executable tests. No mappings are duplicated in page switch statements for cabinet keys.

| Action | Built-in key | Behavior |
| --- | --- | --- |
| PreviousGame / NextGame | Up / Down | Previous/next entry, clamped at list ends; on the platform selector, previous/next menu button. |
| PreviousCategory / NextCategory | Left / Right | Cycle populated platform categories. In a game collection this changes its playlist in place, so Back still returns to the selector rather than traversing a category history. With fewer than two categories, no-op. Session play-later is not a platform category. |
| Select | Enter | Activate the selected platform/menu button, or launch the highlighted game directly through the existing StartContent method. In details, invoke the focused button, defaulting to Play. |
| Back | Escape | Return from collection; close details/search; root selector stays put. Existing details overlays close through the existing dialog-closing logic before leaving the dialog. |
| Search | Y | Preserves the prior keyboard Y shortcut in a game collection. Gamepad Y still uses the original controller path. |
| PreviousLetter / NextLetter | Unassigned | Optional distinct first-letter jumps in collection/search results without changing catalog order; wrap alphabetically. Ignored on selector/details. |
| Favorites / AddFavorite | Unassigned | Reserved: explicitly configured keys are consumed without changing favorites or play-later. Existing Xbox GamepadX play-later behavior remains. |

Enter/Escape/Y are conventional keyboard fallbacks, not claims that those keys belong to cabinet Start/Back buttons. Unconfirmed physical cabinet keys are deliberately not bound by the default map. The old keyboard X play-later shortcut is not used by the centralized internal-keyboard path; explicitly configure/reserve it after confirmation.

Selected internal games have a blue selection background plus a visible index/title in the collection header. Navigation updates SelectedIndex, focuses the actual game button, scrolls the item into view and requests centered BringIntoView. Asynchronous artwork loading updates observable images without rebinding/resetting selection. Generation checks prevent stale category image loads from resetting the current category. Selection is remembered per live playlist, including normal return from RetroArch.

Mapped keyboard keys are handled in tunneling PreviewKeyDown/PreviewKeyUp before child controls can scroll. OriginalKey is used to exclude gamepad virtual keys even when UWP translates their Key value. Alt/system-key notifications also use the dispatcher, with the shared press gate avoiding duplicate action delivery. Keyboard handlers observe only the active internal page/top dialog, not the probe or external LaunchBox UI. Key state is reset on deactivation; a held Select after return cannot launch until released and pressed again.

Only directional actions repeat: initial delay 350ms, interval 140ms by default. Select, Back, letter jumps and Search require a fresh press. Key-up is consumed for assigned keys to avoid a second native button activation. Scope registrations are removed when pages/dialogs leave. Dialog cleanup also runs if showing a dialog fails.

Text entry remains native for arrows and printable keys. In search, Select while the text box has focus transfers focus to the first result; a fresh Select chooses that result. Back leaves search. A keyboard can still type normal search text, including spaces. Cabinet actions do not implement text entry or a frontend exit chord.

## Configuration

Optional file: **LaunchPass LocalState/CabinetInput.json**. Load once per process; restart after changing it. No additional marker is required. InputDiagnostics.enabled remains an independent opt-in for raw event capture.

- Docs/CabinetInput.example.json matches the safe built-in profile.
- Docs/CabinetInput.mode1-candidate.json adds factory-candidate Ctrl/1 Select, Shift Back, Alt/Z letter jumps, and reserves Space/X. **Use only after the short physical confirmation below; it is not installed automatically.**
- Copy the chosen profile as CabinetInput.json; keep InternalCatalog.json and ROM/core values unchanged.
- The bindings list replaces the default list. Preserve the arrow and desired fallback bindings when customizing.
- Required: version=1, bindings array; each binding has a key string and action name. Optional: enabled (default true), repeatDelayMs (150–2000), repeatIntervalMs (60–1000). enabled=false restores the existing native/legacy input path.
- Key strings accept Windows-style names, A–Z, F1–F24, Number0–9, NumberPad0–9, decimal virtual-key codes or 0x hexadecimal codes. Use "Number1" or "49" for the top-row 1 key; "1" is not the digit key binding. Duplicate keys, unknown actions, mouse codes and gamepad codes are rejected.
- Generic Control/Shift/Menu bindings also match left/right variants. An explicit variant overrides a generic binding when the API supplies that variant. Do not infer left/right identity when Input.log reports only the generic key.
- Favorites actions are reserved no-ops, not aliases for launching or opening play-later.
- Missing configuration uses safe defaults. Invalid configuration is rejected atomically and falls back to defaults, with the error in LocalState/Startup.log. Unknown metadata fields are ignored. This parser is separate from the unchanged catalog parser/serializer.

## Short physical confirmation

Use the existing diagnostic panel/log, one control at a time. Note the UTC time and physical label; do not rely on the order of earlier unlabeled presses.

1. Press/release Player-1 Start three times. Record OriginalKey; verify whether it is Number1 (49), not NumberPad1 (97) or another numeric key.
2. Press/release Button 1 three times. Confirm whether it is Control (17), LeftControl (162), or something else.
3. Press/release Button 4 three times. Confirm whether it is Shift (16), LeftShift (160), or another key.
4. Optionally confirm Button 2, Button 5, Button 3 and Button 6 before enabling letter/favorite bindings. Factory candidates are Menu (18), Z (90), Space (32), X (88), respectively.
5. Compare complete down/up pairs; the same physical press can produce accelerator, routed and CoreWindow observations. These are not multiple physical presses.

Update CabinetInput.json using the measured codes. No application rebuild is needed for mapping changes once this navigation build is installed. If a test disagrees with the factory profile, keep the measured mapping rather than changing firmware or guessing labels.

## Acceptance checks after separately authorized packaging/install

- Use at least two actual installed games, plus a list longer than one visible grid row. Confirm each Up/Down changes exactly one highlighted game and crosses rows correctly; confirm list ends, held-repeat timing and visibility of off-screen selections.
- With at least two populated categories, verify Left/Right, return to the previous category/selection, and Back directly to the selector. Verify one-category and empty session play-later cases.
- Verify confirmed Select launches once per press; hold it through launch/return and confirm no automatic relaunch. Verify 1941 still uses its known-working core/path and normal RetroArch exit returns to the same selection.
- Verify Back from details, overlays, search and collection; holding it must not cascade through several menus.
- Check typing/caret behavior in search, result selection, and keyboard Y/GamepadY. Confirm all working Xbox controller navigation, A/B, X/Y and Menu shortcuts remain functional.
- Exercise missing, valid, disabled, malformed and duplicate-key CabinetInput.json. Use Startup.log for configuration errors and the unchanged Input.log diagnostics for event/focus observations.
- Confirm reserved Space/X cannot launch or modify favorites under the candidate profile. No favorites feature is added in this change.

## Code and validation

Changed implementation: new portable CabinetInput.cs; new UWP CabinetInput.cs; selector code-behind; collection XAML/code-behind; details/search code-behind for active-dialog dispatch; one Compile registration in LaunchPass.csproj. New console tests and the two configuration examples accompany this document. The GameDetailsPage.StartContent method body, URI generator, diagnostic probe, catalog game model/parser, themes, startup/XAML activation code, input diagnostics, package identity/version and dependency versions were verified unchanged.

Portable tests cover observed key-code defaults, unconfirmed controls remaining unbound, modifiers and overrides, top-row versus keypad values, gamepad pass-through, malformed/duplicate/trailing configuration, repeated delivery, held Select after activation changes, list boundaries, 25-game sequential navigation, category cycles and unsorted alphabetical groups. Actual Xbox focus rendering, event routing and physical mappings remain hardware acceptance checks.

Final verification: **126 cabinet input assertions, 55 catalog assertions and 256 URI assertions passed**. VS 2022 Debug x64 passed in 21.84s and Release x64/.NET Native passed in 2m55.23s; each had 12 existing warnings and zero errors. Final tests used the existing cached NETStandard.Library 2.0.3/Microsoft.NETCore.Platforms 1.1.0 packages without changing project dependency versions. An initial offline-cache attempt was corrected to use the existing complete user cache.

Logs/binlogs: build-logs/cabinet-navigation-debug-final.log/.binlog and cabinet-navigation-release.log/.binlog. Test logs: cabinet-navigation-tests-final.log, cabinet-navigation-catalog-tests.log and cabinet-navigation-uri-tests.log. The generated Release InternalCatalogPage activator remains present.

Unsigned Release bundle: `C:\TRANSFER\Cabinet-Navigation\Release\LaunchPass_2.1.8.2_Test\LaunchPass_2.1.8.2_x64.msixbundle`.

Microsoft references: [PreviewKeyDown routing](https://learn.microsoft.com/en-us/uwp/api/windows.ui.xaml.uielement.previewkeydown), [raw versus mapped keys](https://learn.microsoft.com/en-us/uwp/api/windows.ui.xaml.input.keyroutedeventargs.key), [gamepad interaction and Back](https://learn.microsoft.com/en-us/windows/uwp/ui-input/gamepad-and-remote-interactions).

No signing, deployment, package version increment or commit is included. Outputs are unsigned under Cabinet-Navigation/Debug and Cabinet-Navigation/Release using the existing source version 2.1.8.2. A later update package needs a version higher than the installed build. Existing signing scripts are pinned to older payload hashes and must not be reused unchanged.
