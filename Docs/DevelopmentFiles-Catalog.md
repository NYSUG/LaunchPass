# Offline DevelopmentFiles catalog generation

The user confirmed 1941 gameplay from DevelopmentFiles on D: and explicitly
supplied `D:\DevelopmentFiles\roms\arcade\` as the ROM directory. This tool uses
that explicit input; it never infers a runtime mount from an SMB share name.
The expected 1941 path is `D:\DevelopmentFiles\roms\arcade\1941.zip`.
No console log containing the literal test path is present in this checkout;
compare that expected path with the actual RetroArch content path before uploading
the catalog. The offline tool cannot verify Xbox file existence or permissions.

No application, URI generator, package, artwork resolver or music changes are
needed to generate this catalog. Shared artwork/music access remains a separate
hardware gate. Existing LocalState artwork and metadata paths stay unchanged.

## Mac workflow

Use Python 3.9 or later. Catalog generation requires only the standard library,
no Device Portal, network, RetroArch package name, credentials or ROM upload.

First put an exported copy of the **current curated** LaunchPass LocalState
InternalCatalog.json at:

```text
/Users/timothyjohnson/Dev/LaunchPass/Builds/catalog/InternalCatalog.current.json
```

Create the staging directory if necessary. Do not use the multi-platform example
catalog as a substitute for the current 99-game seed. Keep the backup unchanged.

Run the tests from the repository root:

```sh
cd /Users/timothyjohnson/Dev/LaunchPass
python3 -m unittest discover -s tools -p 'test_arcade*sync.py' -v
python3 -m unittest discover -s tools -p 'test_arcade_catalog_only.py' -v
```

The existing sync suite and new offline/publication suite use synthetic files and
mocked Portal transports. Neither contacts the Xbox. To include diagnostics and
artwork regressions, use `python3 -m unittest discover -s tools -p 'test_*.py' -v`;
the artwork tests need Pillow from tools/arcade-artwork-requirements.txt.

Generate a new local catalog and review report (no ROM or Xbox writes):

```sh
cd /Users/timothyjohnson/Dev/LaunchPass
mkdir -p Builds/catalog
STAMP=$(date +%Y%m%d-%H%M%S)
OUT="$PWD/Builds/catalog/InternalCatalog.developmentfiles.$STAMP.json"
REPORT="$PWD/Builds/catalog/catalog-report.$STAMP.json"

python3 tools/arcade_sync.py \
  --catalog-only \
  --rom-root /Users/timothyjohnson/Roms/Arcade \
  --catalog "$PWD/Builds/catalog/InternalCatalog.current.json" \
  --xbox-rom-root 'D:\DevelopmentFiles\roms\arcade' \
  --platform arcade \
  --output "$OUT" > "$REPORT"
```

This scans the flat arcade directory recursively. It does **not** append another
arcade directory to the Xbox root. A local 1941.zip becomes an absolute
`D:\DevelopmentFiles\roms\arcade\1941.zip` in the generated game entry.
Nested ZIP paths remain nested. Defaults are .zip, .7z and .chd; CHDs are reported
as dependencies, not promoted to games. A deliberately curated CHD entry remains.
For a parent ROM directory instead, explicitly use `--platform-dir arcade=Arcade`.
For a seed platform named mame, use `--platform mame`.

Without --output, the same command prints only its plan/catalog JSON and creates
no files. --output is an explicit opt-in to writing **one new local file**, even
in dry-run. It must be outside the ROM collection and cannot already exist.
--apply and --url are rejected with --catalog-only. ROM files are never opened
for writing, copied, moved, deleted, unpacked or uploaded by this branch.

Review:

```sh
python3 -m json.tool "$OUT" > /dev/null
python3 - "$REPORT" "$OUT" <<'PY'
import json, sys
with open(sys.argv[1], encoding='utf-8') as f:
    report = json.load(f)
print(json.dumps(report['summary'], indent=2))
for key in ('excluded', 'ambiguous', 'retained_unscanned', 'dependencies_not_in_local_scan'):
    print(key, json.dumps(report[key], indent=2))
with open(sys.argv[2], encoding='utf-8') as f:
    catalog = json.load(f)
for game in catalog['games']:
    if game['id'] == 'arcade-1941' or game['rom'].lower().endswith('\\1941.zip'):
        print('1941:', json.dumps(game, indent=2))
PY
diff -u Builds/catalog/InternalCatalog.current.json "$OUT"
```

`diff` returning 1 simply means the catalogs differ. A zero generator exit code
and a valid JSON output must be confirmed before reviewing/publishing. Existing
IDs, titles, platform assignments, core defaults, per-game overrides, artwork
references and unknown manually curated metadata are retained. Only ROM paths and
the content root are migrated. New IDs depend on relative ROM names, not drive
letters, so changing the root does not rename them. New games use exact local
metadata titles when available, otherwise their ROM short names.

Unscanned curated entries remain with their old absolute path and are prominently
reported. Other-platform entries also retain their old absolute meaning. This
prevents changing contentRoot from silently redirecting them to a nonexistent D:
file. Resolve those entries manually after review; the tool does not delete them.
Unique basename fallback can preserve a curated ID when an old absolute path is
outside the old root; that match method is reported. Ambiguous matches stop
generation rather than guess. Unsafe Windows paths, traversal, symlinked ROM
files/directories, reserved filenames and case-insensitive collisions are rejected.

## BIOS, device and clone evidence

No filename blacklist is used. Unknown entries, including neogeo.zip without
reliable evidence, stay visible and appear under ambiguous. To exclude known BIOS
and device sets, add either or both of these options to the generation command:

```text
--mame-listxml /actual/path/to/mame-listxml.xml
--metadata-report /actual/path/to/ArcadeItalia/reports/existing-report.json
```

The paths above are placeholders for existing local files. No download occurs.
Use suitable MAME -listxml data for the ROM/core collection. XML `isbios="yes"`
and `isdevice="yes"` on exact short-name matches are reliable exclusion evidence.
Exact cached arcade_artwork.py matches with genre System / BIOS are also accepted.
An explicit mapping to a different set is not evidence about the original set.
Conflicting metadata stays ambiguous and is retained. Non-runnable sets without
an explicit BIOS/device classification also stay ambiguous.

Add `--friendly-titles` to replace archive-stem titles and previously imported
raw titles with friendly names from exact local metadata. The offline generator
removes recognized MAME region and revision qualifiers, preserves curated
titles, and lists the changes under `title_changes`.

Only visibility in the **new catalog** changes. BIOS/device archives, parent ZIPs,
clone ZIPs and CHD directories are never modified. cloneof/romof relationships and
dependency sets absent from the local scan are reported; this is not a ROM-set
audit or proof that the corresponding files are absent on Xbox. Existing curated
relationship fields remain unchanged. No schema extension is required by the app.
See [MAME's own listxml consumer](https://github.com/mamedev/mame/blob/master/scripts/minimaws/lib/lxparse.py)
for the device, runnable, cloneof and romof attributes.

## Optional catalog-only publication — separate from generation

Do not run this until the generated catalog and literal 1941 path have been
reviewed. It requires only LaunchPass's actual installed PackageFullName, not
RetroArch's. Replace the URL, certificate path and package placeholder below.
Device Portal credentials are prompted without echo and are never CLI arguments.
No publication was performed during implementation.

GET-only preview:

```sh
python3 tools/arcade_sync.py \
  --upload-catalog-only \
  --catalog "$OUT" \
  --url 'https://YOUR-XBOX-IP:11443' \
  --ca-file /actual/path/to/xbox-device-portal.cer \
  --launchpass-package-full-name 'ACTUAL_INSTALLED_LAUNCHPASS_PACKAGE_FULL_NAME'
```

Only after reviewing that plan, an explicit future apply would be:

```sh
python3 tools/arcade_sync.py \
  --upload-catalog-only \
  --catalog "$OUT" \
  --url 'https://YOUR-XBOX-IP:11443' \
  --ca-file /actual/path/to/xbox-device-portal.cer \
  --launchpass-package-full-name 'ACTUAL_INSTALLED_LAUNCHPASS_PACKAGE_FULL_NAME' \
  --catalog-backup "$PWD/Builds/catalog/InternalCatalog.before-upload.$STAMP.json" \
  --apply
```

The CA file here is the Device Portal TLS certificate, not the LaunchPass signing
certificate. Existing --auth cookie and explicit --insecure options remain available
with their existing meaning. No secret is written to reports or backups.

The publisher verifies LaunchPass identity, preserves the existing catalog bytes
in a new local .json backup before replacement, rechecks for concurrent edits,
uploads only LocalState/InternalCatalog.json, and reads it back. Existing backup
files are never overwritten. A changed remote catalog aborts publication. Device
Portal has no transaction here, so the final check cannot eliminate the race
between GET and POST; avoid concurrent edits. After a failed POST, inspect the
console and keep the backup instead of retrying blindly.

The original Device Portal ROM synchronization invocation remains supported.
It deliberately retains its RetroArch LocalState restrictions. Use --catalog-only
for the D: migration; do not pass the D: catalog to the old ROM-upload branch.

## Restore Premium Browser artwork and presentation

After publishing the catalog, use the same catalog path and LaunchPass package
arguments with `--upload-presentation-only`. Pass `--enrichment-dir` with your
artwork staging directory and optionally `--enrichment-python .venv/bin/python3`
if Pillow is installed in a different interpreter. Start with `--dry-run`,
then use `--apply` after reviewing the report. This mode checks that the
published catalog matches and sends missing logos, backgrounds, screenshots
and presentation fields to LaunchPass LocalState. It does not replace the
catalog or transfer ROM files.

## Genesis and SNES

Export the working Xbox catalog after adding one game to the platform manually.
That supplies the existing platform ID and tested core. For Genesis, run from
the repository root:

```sh
.venv/bin/python3 tools/console_library.py \
  --platform genesis \
  --catalog Builds/catalog/InternalCatalog.current-console.json \
  --rom-root /Users/timothyjohnson/Roms/Genesis \
  --xbox-rom-root 'D:\DevelopmentFiles\roms\genesis' \
  --enrichment-dir /Users/timothyjohnson/Roms/LaunchPass/Artwork
```

Use the Xbox folder from the working game. Review the output, then add
`--stage-artwork --output Builds/catalog/InternalCatalog.genesis.json`
to stage assets and save the new catalog. Use Python with Pillow. Libretro
No-Intro matches use the cartridge CRC and size. Unmatched content keeps its
filename title and is listed in the report. The run keeps the platform's curated
entries and settings and preserves the other platforms as exported.

Copy the ROMs to the matching mounted DevelopmentFiles directory and verify that
the new catalog paths exist on the share. Use `--upload-catalog-only` to publish
the catalog. Then use `--upload-presentation-only --staged-platform genesis`
with the same catalog path and `--enrichment-dir`. This publishes the local
box art, screenshots, year, publisher and genre without contacting ArcadeItalia
or transferring ROMs. For SNES, use `--platform snes`, the working SNES path,
and `--staged-platform snes`.

## Validation status on the Windows implementation machine

The new tests cover flat macOS-style paths, D: paths, curation, root migration,
classification, clones/parents, CHDs, no-clobber behavior, source safety and a mocked
catalog-only publisher. This machine currently has no usable Python interpreter:
its python/python3 commands are Store aliases, and the sandbox blocked downloading
an official portable interpreter. These tests have **not been executed here**.
Run the commands above on the Mac before using the generated catalog.
The actual Mac collection and exported current catalog are not present here, so
no claim is made about the final collection's counts or metadata coverage.

The four-file change is also exported as Builds/Milestone3D/catalog-only.patch
for a Mac handoff without committing or pushing. Copy that patch from the Windows
workspace to the Mac, then in the Mac repository run `git apply --check` on it
before `git apply`. Do not force it over conflicting local work. The patch was
reverse-checked successfully against the Windows implementation tree; that is a
patch consistency check, not a Python test result.
