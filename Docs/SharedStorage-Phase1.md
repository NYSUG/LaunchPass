# Milestone 3D — Phase 1 storage/access investigation

Status: repository and public documentation investigated on 2026-11-22.
No Xbox connection, deployment, launcher changes, capability changes, builds or
content migration were performed. Existing uncommitted work and staged music
untracking were preserved.

## What is established

The user has mounted the console's DevelopmentFiles SMB share and uploaded
`roms/arcade/*.zip`. That establishes access from the Mac using the share's
credentials. It does not establish either UWP application's local filesystem
namespace or permissions. The mounted Mac path is not a RetroArch ROM path.

Microsoft's Device Portal wrapper explicitly supports DevelopmentFiles as a
known-folder ID, separately from application LocalAppData. Only LocalAppData
operations require a package full name. This supports an independent transport
for development content, but does not grant an installed application filesystem
access:

- [Microsoft Device Portal wrapper — file operations](https://microsoft.github.io/WindowsDevicePortalWrapper/md_XboxWDPDriver.html#autotoc_md10)
- [Microsoft UWP deployment documentation](https://learn.microsoft.com/en-us/visualstudio/debugger/deploy-windows-store-apps-from-visual-studio)

The exact absolute root visible to **this Xbox One X's installed RetroArch** and
to **this LaunchPass package** remains unverified. `D:\DevelopmentFiles` is a
candidate to test, not a path to bake into source or a migrated catalog. Do not
assume a fixed D:, S: or other drive mapping, or equate a deployment/package path
with the uploaded content root. RetroArch's upstream UWP frontend enumerates
logical drives for browsing; listing a drive alone is not proof a file is readable.

Microsoft explicitly documents broadFileSystemAccess as **unsupported on Xbox**.
The current LaunchPass manifest declares internetClient and removableStorage;
neither establishes arbitrary internal development-folder access. Adding music,
video or picture library capabilities would describe those libraries, not prove
access to this developer share. No manifest change is justified yet.

- [Microsoft capability declarations](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations)
- [Microsoft file access permissions](https://learn.microsoft.com/en-us/windows/apps/develop/files/file-access-permissions)

## What the actual launcher currently does

| Component | Current behavior |
|---|---|
| LaunchPass.Catalog/Catalog.cs | Schema v1 already resolves game.rom relative to an absolute Windows contentRoot; absolute per-game ROM paths also work syntactically. This is path validation, not an access check. |
| LaunchPass/UrlSchemeGenerator.cs | Quotes and escapes ApplicationPathFull, retains the core DLL basename and launchOnExit=LaunchPass:. No LocalState path restriction in the production URI generator. |
| LaunchPass/InternalCatalog.cs | InternalCatalogGame uses that generator and the catalog's validated URI; it does not require LaunchPass to open the ROM itself. |
| LaunchPass/PremiumArtworkCache.cs | DecodeAsync explicitly opens each image via ApplicationData.Current.LocalFolder.GetFileAsync. Shared images cannot be enabled just by changing the ROM root. Exceptions currently become cache failures/fallbacks. |
| LaunchPass.Catalog/PremiumPresentation.cs | Candidates use Artwork/games/<id>/logo, background and screenshot with PNG/JPG/JPEG extensions. No dedicated marquee or preview-video role today. |
| LaunchPass/PremiumBrowserPage.xaml.cs | InternalCatalog.json and Presentation.json are explicitly loaded from LocalState. |
| LaunchPass/ArcadeSessionCoordinator.cs and ArcadeRadioService.cs | Discovery and playback both explicitly use LocalState/Music. Both must use the future shared provider. |
| LaunchPass/StorageUtils.cs | Existing helper catches errors and returns null; insufficient for distinguishing permission denial from a missing file during this investigation. |
| tools/arcade_sync.py | Currently requires RetroArch/LaunchPass package full names and restricts the ROM destination to the selected RetroArch LocalState. It is not ready for an SMB destination. |
| tools/arcade_artwork.py | Currently writes Artwork/games and a root-level Presentation.json. A layout-aware export is needed for artwork/games and metadata/Presentation.json. |

Current upstream RetroArch UWP accepts cmd arguments containing a quoted absolute
content path and handles launchOnExit. There is no DevelopmentFiles-specific
restriction in that protocol parsing block. It only consumes new cmd arguments
before initialization, so cold-start tests are required. The installed Xbox
RetroArch version can differ from upstream. Protocol activation success does not
establish that a core opened the ROM.

- [RetroArch UWP protocol receiver](https://github.com/libretro/RetroArch/blob/master/os/uwp/uwp_main.cpp#L692)
- [RetroArch UWP drive browser](https://github.com/libretro/RetroArch/blob/master/frontend/drivers/platform_uwp.c#L176)

## One-game hardware gate without rebuilding

1. Preserve the installed working configuration. Download backups of
   InternalCatalog.json, Presentation.json, CabinetInput.json, ArcadeExperience.json
   and marker files as applicable. Do not uninstall or delete LocalState content.
2. In the already mounted SMB share, verify exactly roms/arcade/1941.zip and record
   its size and SHA-256. Use the actual Finder mount path, which may have a suffix;
   `/Volumes/DevelopmentFiles` is an example, not an assumed mount point:

   ```sh
   ls -l "/Volumes/DevelopmentFiles/roms/arcade/1941.zip"
   shasum -a 256 "/Volumes/DevelopmentFiles/roms/arcade/1941.zip"
   ```

3. Fully quit RetroArch. Open it directly, use Load Content and browse visible
   roots for the uploaded roms/arcade/1941.zip. D:\DevelopmentFiles is a candidate
   if that path is visible. Do not move the ROM into WindowsApps to make it work.
   Use the same known-working MAME core. Do not change core/system/BIOS directories.
4. Confirm gameplay. Capture the exact absolute content path from RetroArch's
   selected content/history entry or diagnostic log, plus its installed version,
   console OS version and current App/Game classification. Preserve the core name.
   A directory listing or successful protocol activation alone does not pass.
5. Back up the working LaunchPass catalog before the following manual test. Once
   the path is proven, change **only the 1941 entry's rom property** to that exact
   absolute path, escaped correctly in JSON. Leave contentRoot and every other
   game untouched. This override is already accepted by schema v1 and isolates
   the test from the rest of the collection. Do not substitute the SMB URL.
6. Fully quit RetroArch and restart LaunchPass. Launch 1941, verify gameplay, then
   Quit RetroArch and verify the real return callback and cabinet navigation.
   Restore the original catalog backup if it fails. This is a ROM/receiver test;
   it does not test LaunchPass reading shared artwork or music.

No commands in this document perform an upload or change the console. Steps 3–6
are manual hardware tests; their results have not yet been observed.

## LaunchPass read diagnostic needed next

An independent, opt-in, read-only probe is the smallest way to establish the
remaining access facts. It should accept an explicit measured root in LocalState,
run inside the actual LaunchPass package with its existing capabilities, and
write a bounded SharedStorageProbe.log in LocalState. Do not alter the original
InternalStorageProbe or run recursive drive discovery.

For each explicit relative test file, separately record root/folder enumeration,
StorageFile.GetFileFromPathAsync, OpenReadAsync and a small actual read. Capture
the resolved StorageFile.Path, size, elapsed time, exception type and HRESULT.
Where useful compare a System.IO read to detect an API-specific difference, but
the production Windows.Storage stream path must pass. Do not use File.Exists
alone: false does not distinguish absence from denied access.

Test the ROM read as informational (RetroArch owns ROM loading), a small real
logo with BitmapDecoder, metadata text, and optional authorized MP3/MP4 files
with MediaSource/MediaPlayer MediaOpened using mute and no playback. Decode/open
tests are distinct from file-byte access and need cancellation/timeouts. Test
missing files too. The probe must never create, rename, write or delete shared
content, modify ACLs, install certificates or launch a game.

A Mac or Device Portal read, an unrelated file manager, and RetroArch's successful
ROM read cannot stand in for a LaunchPass-context read test. Picker-granted access
in another app would not establish LaunchPass access either. If direct access is
denied, investigate supported folder-picker/token access on this console before
choosing a provider; do not add unsupported capabilities or permission workarounds.

## Conditional architecture after the gate

Use one logical content root configured in LocalState/StorageConfig.json and a
central storage provider. It should own safe relative paths, Windows path
normalization, stream opening, availability diagnostics and cancellation. Check
containment on path-component boundaries, reject traversal/absolute child paths,
and address reparse/symlink escapes rather than relying on string-prefix checks.
Whether both processes can use the same absolute root, or need an explicit alias
mapping for the same logical store, depends on the hardware results.

Keep configuration, catalog curation, cabinet mappings, logs, selection and
diagnostics in LaunchPass LocalState. Map content to the proposed roms/arcade,
artwork/games/<id>, videos/games/<id>, music and metadata/Presentation.json layout.
Retain the current LocalState provider and original UI as rollback. Preserve the
decoded cache, but include storage-root identity in cache keys/invalidation so
switching stores cannot return images from the old root. Video playback remains
an optional future adapter; the premium page currently has no preview player.

The Mac tool can copy to an existing authenticated mount without storing SMB
credentials or needing RetroArch's package full name. It must retain dry-run,
incremental comparison, curated entries and explicit deletion policy. ROM-copy
eligibility must be separate from catalog visibility: keep BIOS, devices, parents
and CHDs without promoting every ZIP to a selectable game.

Do not finalize a concrete StorageConfig root or run bulk migration until these
results are available: exact RetroArch path and gameplay, unchanged URI launch
and return, LaunchPass stream/image/media reads, and then BIOS-dependent gameplay.
No working local content should be removed during validation or rollback.
