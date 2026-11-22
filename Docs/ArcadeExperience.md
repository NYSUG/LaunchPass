# Tim's Arcade Experience — Milestone 3C

The existing Frame, premium browser, selection persistence, local artwork cache,
catalog, and RetroArch URI generator remain in use. Removing
`LocalState/PremiumBrowser.enabled` and restarting still restores the original
internal-catalog page. These experience services initialize with the premium page;
they do not initialize the diagnostic probe or external LaunchBox frontend.

## Configuration

Copy `ArcadeExperience.example.json` to LaunchPass's **LocalState** as
`ArcadeExperience.json`. Restart LaunchPass after changing settings. The example
contains the defaults: music and attract mode off, branding and the app-controlled
loading image on. Each feature can be switched independently. Enable music first,
test ordinary track changes, then optionally enable crossfade. No physical button
is assigned to Next Track by default.

`version` must be 1. Volume is 0–1; mute silences both players. Crossfade duration
is 2–4 seconds, default 3. Attract idle time is 15–3600 seconds, default 180;
exhibit duration is 5–60 seconds, default 12. Missing optional fields use defaults.
Invalid JSON, unsupported versions, invalid ranges, or a file over 64 KiB cause
the whole document to fall back to safe defaults. The reason appears in
`LocalState/Startup.log`. Settings are optional and never a startup prerequisite.

## Music and copyright

Runtime tracks belong directly in `LocalState/Music/*.mp3`. No subfolder recursion
is performed. No tracks are copied automatically or included in the MSIX. Use
music you are authorized to use; keep it out of public artifacts and commits.
`/mp3/` and `/Music-Staging/` are ignored staging locations on the development
machine. `.gitignore` does not remove files already tracked in Git.

On Mac, prepare a private staging folder, for example:

```sh
mkdir -p Music-Staging
cp /path/to/authorized-music/*.mp3 Music-Staging/
```

On Windows PowerShell:

```powershell
New-Item -ItemType Directory .\Music-Staging -Force
Copy-Item .\mp3\*.mp3 .\Music-Staging\
```

Manually upload those files through Xbox Device Portal to the installed app's
LocalState/Music directory. Do not uninstall the app. No upload is performed by
this implementation. The library is rescanned on foreground return; restart is
also sufficient after adding/removing files.

Shuffle exhausts the current playlist before reshuffling, avoiding a repeat at
the boundary when there is more than one valid track. One track can repeat.
Unreadable tracks are skipped; empty/missing libraries remain silent. Opening
has a 15-second timeout. At most two MediaPlayer instances exist. Normal UI
navigation reuses the app-owned radio; background/suspension releases the decks
and retains an in-memory position bookmark. Launch fades for about 300 ms and
releases both decks **before** the existing RetroArch activation. Foreground
return resumes the bookmark. Process termination does not persist that bookmark.
Music continues during attract mode. There is no background-audio capability.

Optional crossfade stages the next track near the current track's end and mixes
volumes over three seconds by default. A staging/playback failure disables
crossfade for that session and retains sequential playback. Seamless playback is
an Xbox hardware test, not a guarantee; use crossfade.enabled=false if timing or
mixing is unreliable. A normal sequential gap is acceptable as the safe fallback.
Diagnostics use the existing startup logger.

To add a Next Track shortcut, append a binding to your **existing**
CabinetInput.json bindings, preserving all your configured controls:

```json
{ "key": "N", "action": "NextTrack" }
```

N is only a keyboard example, not an assertion about an X-Arcade button. Assign a
confirmed spare key. The action does not move selection or launch a game.

## Branding and splash

`Themes/TimsArcadeTheme.xaml` centralizes midnight blue, cyan focus/navigation,
amber action emphasis and soft white text. Wheel selection has a restrained
layered outline instead of a blur. Branding can be disabled independently of
splash and attract mode. High contrast disables the decorative outline glow.
The original TV-safe content margins remain.

The exact unpadded Docs/SplashScreen/TimsArcadeSplash.png is packaged separately
as Assets/Branding/TimsArcadeSplash.png. An app-controlled overlay uses
UniformToFill: it preserves aspect ratio and crops edges instead of letterboxing.
The premium view requests UseCoreWindow bounds while retaining safe content
margins; the previous bounds mode is restored when leaving it. The overlay is
removed as soon as catalog initialization completes, including errors. No
artificial display delay is added; on fast startup it may be very brief.

The native OS splash remains a distinct platform surface. UWP specifies a
centered 620×300 image at 1×; an application cannot make that native image into a
full-screen cover image. The full-screen treatment applies after XAML activation.
See [Microsoft's splash requirements](https://learn.microsoft.com/en-us/windows/uwp/launch-resume/add-a-splash-screen)
and [custom splash guidance](https://learn.microsoft.com/en-us/windows/uwp/launch-resume/create-a-customized-splash-screen).

## Neon Marquee Museum

Attract mode requires the premium page to be foreground, loaded, idle, free of
dialogs and not launching a game or under high memory pressure. The independent
engine emits local selection/attract events; no networking is implemented.
The presentation uses the existing logo resolver/cache, a faint entrance image,
a bounded 20-line cyan perspective grid, amber accents, moving authentic marquees
and occasional Tim's Arcade branding. Text is a fallback when a logo is missing.
There is no persistent HUD. OS reduced-motion/high-contrast settings suppress
the museum's decorative animation.

Keyboard, joystick-emulated keys and Xbox gamepad activity wake the browser.
Wake input is intercepted centrally before configured actions. Key repeat and
release events are consumed, with a 250 ms release quarantine. Raw controller
buttons/sticks/triggers are additionally sampled every 100 ms, including controls
without a navigation action. Held raw controls extend the guard. The first wake
press cannot launch a game; release it and press Select again. Selection and
category remain unchanged. Launch/background/navigation/memory-pressure paths
cancel museum artwork work and release its images. No external lighting or
marquee hardware integration is included.

## Hardware acceptance checks

1. Start with safe defaults; verify 99-game navigation, category memory, search,
   original-page fallback, game launch and return.
2. Enable music only. Test an empty directory, one track, several tracks, a corrupt
   file, end-of-track advance, mute, volume, and normal UI navigation. Confirm
   silence before RetroArch appears and one playback session after returning.
3. Enable crossfade and listen through several boundaries; disable it if mixing,
   seeking or GameMedia audio focus is unreliable. Microsoft supports multiple
   [MediaPlayer instances](https://learn.microsoft.com/en-us/windows/apps/develop/media-playback/play-audio-and-video-with-mediaplayer),
   but Xbox timing and resource usage remain device tests.
4. For faster testing use attract idleSeconds=15, then restore 180. Wake with
   joystick arrows, Select, Back, modifiers, held controls, Xbox A/B, sticks and
   triggers. Confirm wake never launches and a fresh Select does.
5. Test missing/corrupt logos, backgrounding, controller disconnect/reconnect,
   return from RetroArch, and repeated idle/wake cycles. Monitor memory using the
   existing performance diagnostics and Device Portal.
6. Check splash cropping, TV safe areas, high contrast and reduced motion. The
   museum reduces static content but is not a guarantee against display retention.

Portable tests cover settings/shuffle, media lifecycle/crossfade using fake decks,
session policy, NextTrack mapping, idle timing and wake suppression. WinRT codecs,
audio mixing, TV geometry and composition timing require Xbox validation.
