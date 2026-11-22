# Milestone 3B.2 — ArcadeItalia artwork and metadata importer

Standalone tool: `tools/arcade_artwork.py`. Python 3.11+ is recommended; Pillow is the only external dependency. It does not import arcade_sync.py, connect to Xbox, examine/write ROM contents, or alter any LaunchPass source/configuration. Supply a local copy of the working catalog and a local staging directory shaped like LaunchPass LocalState.

## Provider documentation reviewed

[ArcadeItalia documentation](https://adb.arcadeitalia.net/service_scraper.php), reviewed 2026-11-15: June 2026 query_mame release 6 and query_mame_media release 2 require single-ROM requests; use_parent/resize scraper parameters were removed. This supersedes older batching advice.

The importer uses one connection, ≥1-second spacing, download_status quotas, and bounded Retry-After handling; 403/429 or exhausted quotas stop network access.

Notify the maintainer before first online use, as requested. The tool sends no messages. Keep its generated credits/source records with shared output; original artwork rights remain intact. History prose is not exported.

## Mac setup and mocked tests

From the repository root, using an installed Python 3.11+:

```sh
python3 -m venv .venv-artwork
source .venv-artwork/bin/activate
python -m pip install -r tools/arcade-artwork-requirements.txt
python -m unittest discover -s tools -p 'test_arcade_artwork.py' -v
```

The tests use synthetic catalog/API fixtures and temporary local files, not the real five-game catalog, ROMs, Xbox or internet. There are 42 test methods. Five image tests require Pillow; symlink/case-collision tests may skip when the filesystem does not support those cases. Check the test runner's actual counts/skips before recording a pass. Fixture data is deliberately not presented as live acceptance evidence.

Windows: use a working Python interpreter in a regular PowerShell session, create a venv with `python -m venv .venv-artwork`, activate `.venv-artwork\Scripts\Activate.ps1`, and run the same pip/unittest commands. The interpreter and Pillow have not been installed globally by this change.

## Five-game workflow

Use the **actual five-game catalog exported from Xbox**, not Docs/InternalCatalog.example.json (that file contains placeholder ROMs and non-arcade entries). Keep the original catalog backed up. Choose a staging directory, not a ROM directory or a live Xbox path.

Dry-run (default): queries metadata/media listings, reads existing cache, but downloads no image files and writes nothing. The JSON plan goes to stdout and compact per-game summaries go to stderr. Shell redirection below is an explicit user-requested report file, not an importer side effect:

```sh
python tools/arcade_artwork.py "$HOME/LaunchPass/InternalCatalog.json" \
  --output "$HOME/LaunchPass/Artwork-Staging" > artwork-plan.json
```

For a fully offline plan against an existing cache, add `--offline`. An empty cache reports misses; it never silently contacts the provider.

After inspecting matches and source notices, actually download/write:

```sh
python tools/arcade_artwork.py "$HOME/LaunchPass/InternalCatalog.json" \
  --output "$HOME/LaunchPass/Artwork-Staging" --apply > artwork-result.json
```

Run again with the same arguments for incremental completion. Existing files are preserved unless `--refresh` is explicitly supplied **and** their hashes still match the importer ownership ledger. A manual edit makes an asset custom, so refresh preserves it. Deleted state/cache never grants overwrite permission for existing artwork.

Refresh unchanged importer-owned data, or allow a clone's parent artwork explicitly:

```sh
python tools/arcade_artwork.py "$HOME/LaunchPass/InternalCatalog.json" \
  --output "$HOME/LaunchPass/Artwork-Staging" --apply --refresh

python tools/arcade_artwork.py "$HOME/LaunchPass/InternalCatalog.json" \
  --output "$HOME/LaunchPass/Artwork-Staging" --apply --parent-artwork
```

Default arcade platform IDs are arcade and mame (case-insensitive). For other IDs, supply repeated `--platform actual-id` options. Those options replace the defaults. Selection never infers platform or ROM identity from a display title. `.zip` and `.7z` filenames are accepted; CHDs/software lists/non-MAME paths are reported for manual review.

Legacy MAME names can differ from the latest database. An explicit `--mapping verified-roms.json` file can map existing game IDs to verified current short names:

```json
{"arcade-some-game":"verified_shortname"}
```

Unknown results expose suggestions but never choose a fuzzy match. Multiple exact matches are ambiguous. Clone metadata stays associated with the clone; optional parent images have a source-ROM/parent flag in the report. Parent fallback is limited to one level.

## Output and frontend compatibility

The actual resolver is `LaunchPass.Catalog/PremiumPresentation.cs`, and image loading is in `LaunchPass/PremiumBrowserPage.xaml.cs`. Its recognized per-game filenames are logo/background/screenshot with .png, .jpg, .jpeg priority. The importer emits PNGs:

```text
Artwork-Staging/
  Presentation.json
  Artwork/games/arcade-1941/
    logo.png
    background.png
    screenshot.png
    extras/
      marquee.png
      flyer.png
      title.png
      cabinet.png
      bezel.png
      cpanel.png
  ArcadeItalia/
    CREDITS.txt
    state.json
    .cache/...
    reports/<timestamp>-<unique-id>.json
```

Assets are emitted only when available/valid. Extras are retained for future use; the current browser ignores that directory. No videos, manuals or ROMs are downloaded.

Game-directory names use the **catalog game ID**, not the ROM short name. Windows device IDs use exactly the frontend's encoding: CON/con → `~636F6E`; ordinary `arcade-1941` remains unchanged. Existing case-insensitive names are honored even on case-sensitive Macs. Case collisions, symlink paths and aliases of the input catalog are rejected. Writes use temporary files, atomic replacement, expected-content hashes and an exclusive importer lock. Review a leftover ArcadeItalia/import.lock before manually removing it after an interrupted run; the tool never deletes an unrelated lock.

| Output | Source priority | Maximum dimensions |
| --- | --- | --- |
| logo.png | Logo, then marquee | 1200 × 400 |
| background.png | Flyer, artwork preview, then gameplay | 1920 × 1080 |
| screenshot.png | Gameplay, then title screen | 1000 × 800 |
| extras/marquee.png | Marquee | 1200 × 400 |
| extras/flyer.png | Flyer | 1200 × 1800 |
| extras/title.png | Title screen | 1280 × 960 |
| extras/cabinet.png | Cabinet photo | 1000 × 1400 |
| extras/bezel.png | Bezel | 1920 × 1080 |
| extras/cpanel.png | Control panel | 1600 × 800 |

All resizing preserves aspect ratio, never crops or upscales, applies EXIF orientation and preserves source transparency. A marquee does not become transparent merely because it is used as a logo. Sources without alpha remain opaque. Source files over 30 MiB or images over 40 million pixels are rejected. Image-memory cache is bounded to 64 MiB; persistent caches have a 30-day freshness window. Provider-supplied image URLs are used as returned, including any resize=0 in the **image URL**; removed parameters are never sent as scraper request inputs.

The browser itself Uniform-fills/crops backgrounds, so portrait flyer backgrounds are decorative crops; the complete flyer remains in extras. Gameplay images and logos Uniform-fit without distortion. Platform fallback artwork and legacy catalog artwork are untouched. Existing per-game logo/background/screenshot files in any recognized extension are preserved; if several variants exist, the frontend's existing PNG-first priority still applies. The importer does not delete/reorder them.

Presentation.json keeps `version:1` and merges only supported `id`, `year`, `manufacturer`, `genre` fields. Existing metadata, empty values, unknown fields and unrelated game entries remain intact. A refresh updates only fields still equal to their recorded importer-generated values. Malformed existing metadata stops the run before output writes. A no-change merge preserves the existing file bytes.

The standalone importer records full title, player count, clone relation, controls, languages, orientation and other fetched fields under `games[].metadata` in its report. The combined sync's `--enrich` mode can use a matched full title to replace a catalog title derived from the archive filename. PremiumBrowserPage gets the display title from the catalog. Per-asset URLs, parent origin, dimensions and hashes are under `managed_sources` and state.json.

## Reports and acceptance

Reports contain catalog SHA256, API release, exact ROM matches, unmatched/ambiguous suggestions, rich metadata, missing fields, candidate media URLs, generated/preserved/missing/failed assets, and source attribution. The `summary` object counts game and asset outcomes. `would-download` is a plan, not proof of a successful download. `not-processed` marks work interrupted by a game/service error; it is not reported as missing provider artwork.

Exit codes: 0 for completed runs (missing optional artwork is allowed), 2 for unresolved matches or per-game/download/service failures, 1 for invalid configuration/local errors. Each apply report gets a unique filename; prior reports are never overwritten. Partial successful work is retained so the next run can continue; no rollback deletes artwork.

Before any separately authorized Xbox upload, check:

1. All five real catalog games have exact matches or reviewed mappings; no placeholders were substituted.
2. Presentation.json parses as version 1 with unique IDs and string year/manufacturer/genre fields. IDs still match the input catalog.
3. Recognized filenames exist for successful assets and image dimensions/alpha look right. Review missing/corrupt roles and parent fallbacks in the report.
4. Input catalog SHA256 is unchanged; custom files and metadata remain intact.
5. Include Artwork, merged Presentation.json and ArcadeItalia attribution records in the future local library. Keep the cache/state on the development machine for subsequent imports; the Xbox needs no internet or Python.

## Results in this session

- Frontend resolver/schema and current provider documentation were inspected; no frontend source changes were made.
- Importer and 42 mocked/offline test methods were added, but **tests have not been executed**: this Windows environment exposes only unusable Python aliases, no real interpreter was found, and a workspace-local Python download could not connect through the restricted network.
- **Live five-game acceptance has not run.** Only Docs/InternalCatalog.example.json is present; it is not the working catalog. The current catalog path/contents and provider-notification status were requested from the user.
- No real artwork or fabricated acceptance assets were generated. No package rebuild, signing, deployment or commit occurred. Run the commands above in a Python-enabled Mac/Windows session once the actual catalog and provider notice are available.
