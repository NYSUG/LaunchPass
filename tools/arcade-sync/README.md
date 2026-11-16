# Portable arcade library synchronization

`../arcade_sync.py` scans local platform folders, merges the existing InternalCatalog v1 manifest, and compares ROM filenames/sizes with Xbox Device Portal. It is a standalone Python 3.9+ CLI using the standard library. No UWP build, .NET SDK, pip packages, running LaunchPass process, or Windows machine is required.

**Default behavior is dry-run.** Live dry-runs make authenticated GET requests; they never upload. `--snapshot` plans entirely offline and cannot be combined with `--apply`. No live Xbox requests or uploads were performed while implementing this tool. Milestone 2 remains uncompiled and untested on Xbox.

## Inputs and catalog format

Use a copy of [the existing example](../../Docs/InternalCatalog.example.json) or a catalog exported from LaunchPass LocalState as `--catalog`. The seed must already be valid, with at least one game and platform. The seed is never rewritten. Remove unwanted placeholder entries manually before use; regeneration retains entries whose files are absent locally.

This tool follows [the actual portable model](../../LaunchPass.Catalog/Catalog.cs), rather than introducing another catalog format:

| Object | Required fields | Optional fields used by the app |
| --- | --- | --- |
| Catalog | `version: 1`, `contentRoot`, `platforms`, `games` | None |
| Platform | `id`, `title`, `defaultCore` | None |
| Game | `id`, `platform`, `title`, `rom` | `core`, `artwork` |

Unknown metadata fields at all three levels are retained as JSON values; the current app ignores those extensions. Artwork references are preserved; this tool does not transfer artwork. JSON whitespace/formatting is regenerated, but existing field values, titles, IDs and overrides are retained.

ROM paths are generated relative to the configured Windows `contentRoot`, with `/` separators accepted by the existing parser. Existing absolute ROM paths are retained and matched using Windows path rules, independently of macOS. The tool checks the content root's package family against the selected installed RetroArch package. It does **not** infer or prove the Xbox drive/user prefix: keep the root derived from the hardware-validated probe.

Library layout defaults to one folder per configured platform ID, recursively scanned:

```text
/MyLibrary/
  arcade/
    1941.zip
    1942.zip
    subfolder/another.zip
  genesis/
    My Game.md
  snes/
    My Game.sfc
```

Every configured platform folder must exist, even if empty. Only configured platform folders are scanned; the report lists them explicitly. Add a platform with `--core genesis=genesis_plus_gx_libretro.dll`, or put its definition in the seed. Map another local folder name with `--platform-dir arcade=MAME`; remote ROMs still go under `roms/arcade`. Nested local paths are preserved beneath that platform's remote directory. Hidden files/folders are skipped; symlinks, case-insensitive path collisions, unreadable folders and unsafe Windows paths stop the plan.

Default extensions are `.zip,.7z,.md,.gen,.smd,.sfc,.smc,.nes,.gb,.gbc,.gba,.chd,.iso,.cue,.bin`. Set `--extensions .zip,.chd` or `--extensions '*'` as appropriate. ZIP and other archives are always uploaded as files, never unpacked. Multi-file content requires including every necessary companion file; CUE/M3U references and core compatibility are not analyzed.

## Offline example (no Xbox or credentials)

From the repository root, choose a **new** temporary folder:

```sh
python3 tools/arcade-sync/examples/create_demo_library.py /tmp/launchpass-sync-example
python3 tools/arcade_sync.py \
  --rom-root /tmp/launchpass-sync-example \
  --catalog tools/arcade-sync/examples/seed.json \
  --snapshot tools/arcade-sync/examples/portal-snapshot.json \
  --retroarch-package-full-name RetroArch_1.0.0.0_x64__test \
  --launchpass-package-full-name f3a28ab5-9cd3-480a-86e5-58ecbeba4015_2.1.8.0_x64__test \
  --game-core arcade/1942.zip=fbneo_libretro.dll
```

The helper creates inert synthetic bytes, **not playable ROMs**. The snapshot contains only synthetic installed packages, listings and a catalog. The real CLI produces [this complete sample dry-run output](examples/dry-run-output.json):

```text
mode: dry-run
configured platforms: arcade, genesis, snes
scanned ROMs: 4
planned uploads: 3 (48 bytes)
  arcade/1942.zip       size_changed: 3 -> 17 bytes
  genesis/example.md   new: 17 bytes
  snes/example.sfc     new: 14 bytes
unchanged: arcade/1941.zip
catalog: update LaunchPass LocalState\InternalCatalog.json; add 3 games
remote deletions: none
```

The actual stdout is JSON and includes `generated_catalog`, ready for review. To save just the generated manifest, add `--output /tmp/InternalCatalog.generated.json`; this must be a **new file**. Existing output files and symlinks are refused, including the seed itself.

The offline snapshot envelope is a test/planning transport, not another game schema: `packages` holds the raw installed-package response, `directories` maps package full name → package-relative directory → raw `{"Items": [...]}` response, and `catalog` holds the remote v1 document when listed. Every requested directory must be present in the snapshot; an explicit `null` simulates HTTP 404. Snapshots never contain credentials, headers, cookies or tokens. Do not save authenticated HTTP traces as snapshots.

## Live read-only plan

Find both exact **installed package full names** in Device Portal. LaunchPass has a GUID identity in this fork; do not guess the full package name from its display name. Replace the placeholders below with the installed values. No credentials belong in these arguments:

```sh
python3 tools/arcade_sync.py \
  --rom-root /path/to/MyLibrary \
  --catalog /path/to/my/InternalCatalog.json \
  --url https://XBOX-IP:PORT \
  --retroarch-package-full-name 'EXACT_RETROARCH_PACKAGE_FULL_NAME' \
  --launchpass-package-full-name 'EXACT_LAUNCHPASS_PACKAGE_FULL_NAME' \
  --dry-run
```

Omitting `--dry-run` has the same effect. TLS certificate verification is on by default. Use `--ca-file /path/to/trusted-portal-ca.pem` or explicitly choose `--insecure` to match the existing uploader's `curl -k` configuration. Proxies are disabled and redirects are rejected so authentication is not forwarded elsewhere.

Basic-auth credentials are read through hidden prompts or the already exported `XBOX_USER` / `XBOX_PASS` environment variables used by the existing uploader. Cookie sessions are supported with `--auth cookie` and a hidden Cookie-header-value prompt. Never put credentials in a URL, command argument, shell history, seed, snapshot, saved config or repository file. No cookie jar or credentials file is created. Missing secure terminal input is an error; secrets are never echoed as a fallback. Authentication headers, cookies, CSRF tokens and server error bodies are omitted from reports and errors.

The new transport reuses `xbox_diagnostics.Portal` for origin validation, TLS, credentials, memory-only cookies and redirect isolation. Inspection of `/Users/timothyjohnson/Roms/Mame/upload.sh` confirmed **GET `/api/os/info`** to obtain cookies (the diagnostics client's bootstrap uses GET `/`), followed by multipart POST to `/api/filesystem/apps/file` with `extract=false` and the CSRF cookie mirrored to `X-CSRF-Token`. This tool follows that uploader flow without sourcing or executing the shell script. Catalog reads use the [documented file GET endpoint](https://learn.microsoft.com/en-us/windows/uwp/debug-test-perf/device-portal-api-core#files) with `filename=InternalCatalog.json`, `knownfolderid=LocalAppData`, the installed package full name and `path=\LocalState`.

## Core selection and metadata protection

The seed's platform defaults are used for new entries. An existing game's `core` takes precedence. Add an override for a new game or one without an override using, for example:

```text
--game-core arcade/1942.zip=fbneo_libretro.dll
```

An attempt to change an existing non-null override or an existing platform default through CLI flags is rejected. Edit the catalog deliberately instead. Override keys use the generated `platform/relative-ROM` path, case-insensitively; an unmatched key is an error.

Existing games are matched by their full resolved Windows ROM path. New IDs are deterministic `platform-<path hash>` values and become persistent once saved in the seed/catalog. Changing an existing title does not change its ID. New games receive the filename stem as their initial title.

If Xbox already has `InternalCatalog.json`, the tool downloads and validates it **before** scanning/planning transfers. Local-only and remote-only fields/entries are merged. Any conflicting value for the same metadata field stops the operation before uploads; there is no force/overwrite option. To resolve a conflict, export the Xbox catalog and reconcile your local seed while preserving the edits you want. Duplicate IDs/ROM paths, duplicate JSON keys, malformed remote catalogs and listed-but-unreadable catalogs also stop the operation. A 404 while downloading a catalog that was listed is not treated as an empty catalog.

Deleting a local file does not remove remote content or catalog entries. There is no DELETE endpoint, metadata-reset option or automatic cleanup operation.

## Applying a reviewed plan

**Live transfers require explicit approval before running with `--apply`.** No live apply has been attempted in this development session. After reviewing a live dry-run and obtaining approval, use the same live command with `--apply` instead of `--dry-run`. Offline snapshots can never authorize an apply.

Before allowing writes, the CLI requires:

- Both exact packages are installed, uniquely selected, and distinct.
- The selected LaunchPass full name and family match the fork's identity, `f3a28ab5-9cd3-480a-86e5-58ecbeba4015`. A deliberately renamed fork must explicitly provide its new `--launchpass-identity`.
- The configured RetroArch root is under that installed package family's `LocalState`.
- LaunchPass's fixed `LocalState` directory can be listed and its existing catalog read/validated, if present. There is no configurable catalog upload path.
- All needed RetroArch ROM directories exist and return valid listings. Missing folders appear in `missing_directories` during dry-run and block apply. Create them manually in Device Portal; the tool does not guess directory-creation endpoints.
- A fresh read of packages, catalog and listings, plus a fresh local scan, still matches the plan immediately before writes.

New filenames and files with different byte sizes are streamed as multipart uploads with a known Content-Length and `extract=false`. Each ROM upload is followed by a filename/size listing check. The catalog is uploaded **last**, only after every planned ROM upload passes verification and the remote catalog has been checked again for concurrent edits. Catalog upload always targets LaunchPass's own `LocalState\InternalCatalog.json` and is followed by a download/validation check.

The tool never writes the probe XML, `InternalCatalog.enabled`, LaunchPass settings, artwork, or RetroArch configuration. It does not install, start or stop applications. Enabling internal mode remains a separate deployment step in [the Milestone 2 instructions](../../Docs/InternalCatalog.md).

There are no automatic POST retries, including for 403. On interruption or ambiguous transfer failure, some ROMs or the catalog itself may be partially written. The command exits nonzero; rerun a dry-run and inspect/restore files as necessary before another approved apply. A failed ROM transfer prevents catalog publication. No automatic remote rollback/deletion is attempted.

## Verification and remaining limits

Run the independent Python tests from the repository root:

```sh
python3 -m unittest discover -s tools -p 'test_*.py' -v
```

[Local test results](TEST_RESULTS.md): **45 passing tests** (30 synchronization tests, 15 unchanged diagnostics tests), including mocked authentication failures, missing files/directories, raw multipart transfers, Unicode filenames, interrupted uploads, malformed listings, catalog read-back, identity/path checks, metadata preservation/conflicts and source/remote state changes. The offline example was run through the actual CLI. No Xbox requests, UWP builds, deployments or commits were performed.

Filename/size equality is the comparison contract, **not content hashing**. Same-size replacements or corruption cannot be detected from directory listings. Post-upload verification also checks size rather than a ROM checksum. Device Portal does not provide a transaction or conditional catalog update in this implementation: the final check reduces concurrent-edit risk but cannot close the race before POST. Avoid editing metadata or syncing from another process during apply. HTTP success does not prove durable storage or emulator compatibility.

Hardware checks still needed after explicit approval:

- Verify installed package/full-family responses, numeric `Type` attributes and file sizes on this Xbox build.
- Run a live GET-only plan; confirm actual directory paths and catalog download behavior.
- Approve a small raw ZIP upload, then verify filename/size, byte integrity independently and `extract=false` behavior on hardware.
- Verify catalog POST and read-back occur only in LaunchPass LocalState; test a second sync as an unchanged no-op.
- Exercise an interrupted transfer and inspect recovery, including possible partial catalog writes.
- Test Unicode filenames and CSRF/auth expiry with the actual Portal.
- Separately compile Milestone 2 on Windows and test catalog browsing, cores, launching and returning on Xbox. Milestone 1's hardware validation does not validate this CLI or the new catalog UI.
