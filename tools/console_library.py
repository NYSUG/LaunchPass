#!/usr/bin/env python3
"""Generate a DevelopmentFiles console catalog and stage Libretro presentation assets."""
import argparse
from collections import Counter, defaultdict
from concurrent.futures import ThreadPoolExecutor
import copy
import hashlib
import json
import ntpath
from pathlib import Path
import re
import sys
from urllib.error import HTTPError, URLError
from urllib.parse import quote
from zipfile import BadZipFile, ZipFile
import zlib

import arcade_artwork as art
import arcade_sync as sync


SYSTEMS = {
    'genesis': ('Sega - Mega Drive - Genesis', {'.bin', '.gen', '.md', '.smd'}),
    'snes': ('Nintendo - Super Nintendo Entertainment System', {'.sfc', '.smc'}),
}
DATABASE = 'https://raw.githubusercontent.com/libretro/libretro-database/master/metadat/'
THUMBNAILS = 'https://raw.githubusercontent.com/libretro-thumbnails/'
FIELDS = {'releaseyear': 'year', 'publisher': 'manufacturer', 'genre': 'genre'}
ROLES = {'background': 'Named_Boxarts', 'screenshot': 'Named_Snaps'}


def dat_rows(data, field):
    rows = defaultdict(set)
    for block in re.split(r'^game\s*\(', data.decode('utf-8-sig'), flags=re.M)[1:]:
        value = re.search(r'^\s*' + field + r'\s+"([^"\r\n]+)"', block, re.M)
        if value is None:
            continue
        for rom in re.findall(r'\brom\s*\(((?:"[^"\r\n]*"|[^")])*)\)', block):
            crc = re.search(r'\bcrc\s+([0-9a-fA-F]{8})\b', rom)
            size = re.search(r'\bsize\s+(\d+)\b', rom)
            if crc and (field != 'name' or size):
                key = (crc[1].lower(), int(size[1])) if field == 'name' else crc[1].lower()
                rows[key].add(value[1])
    return {key: next(iter(values)) for key, values in rows.items() if len(values) == 1}


def fetch(opener, url, limit):
    try:
        with opener.open(url, timeout=25) as response:
            data = response.read(limit + 1)
    except HTTPError as error:
        status = error.code
        error.close()
        if status == 404 and url.startswith(THUMBNAILS):
            return None
        raise sync.SyncError(f'Libretro returned HTTP {status}.') from None
    except (URLError, OSError):
        raise sync.SyncError('Libretro connection failed. Check network access and certificate trust.') from None
    sync.require(len(data) <= limit, 'Libretro response exceeds its size limit.')
    return data


def load_metadata(opener, system):
    name = quote(system + '.dat', safe='')
    files = {'no-intro': 'name', **{field: field for field in FIELDS}}
    return {key: dat_rows(fetch(opener, DATABASE + key + '/' + name, art.MAX_JSON), field)
            for key, field in files.items()}


def rom_signature(path, formats):
    if path.suffix.casefold() == '.zip':
        try:
            with ZipFile(path) as archive:
                members = [m for m in archive.infolist() if not m.is_dir()
                           and Path(m.filename).suffix.casefold() in formats and not m.flag_bits & 1]
                if len(members) == 1:
                    return f'{members[0].CRC:08x}', members[0].file_size
        except BadZipFile:
            pass
        return None
    crc = size = 0
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            crc = zlib.crc32(block, crc)
            size += len(block)
    return f'{crc:08x}', size


def display_title(label):
    # No-Intro uses trailing parenthesized distribution tags after the geographic tag.
    label = re.sub(r'\s+\((?:USA|Europe|Japan|World|Asia|Brazil|Korea|Australia|Canada|Taiwan|China|Hong Kong)'
                   r'(?:, [A-Za-z ]+)*\)(?:\s+\([^()]+\))*$', '', label)
    return re.sub(r'^(.+), (The|A|An)$', r'\2 \1', label)


def thumbnail_url(system, role, label):
    name = re.sub(r'[&*/:`"<>?\\|]', '_', label) + '.png'
    repository = system.replace(' ', '_')
    return THUMBNAILS + '/'.join(quote(part, safe='') for part in (repository, 'master', ROLES[role], name))


def prepare(seed, root, xbox_root, platform, metadata, aliases=None):
    _, formats = SYSTEMS[platform]
    aliases = aliases or {}
    xbox_root = sync.root_path(xbox_root)
    destinations = {sync.rom_path(xbox_root, target): local for local, target in aliases.items()}
    sync.require(len(destinations) == len(aliases), 'ROM aliases must have unique Xbox paths.')
    selected = [copy.deepcopy(g) for g in seed['games'] if g['platform'].casefold() == platform]
    sync.require(selected, f'Seed must include a working {platform} game and core.')
    # Keep the catalog's existing root and all other platforms exactly as exported.
    subset = dict(seed, games=selected)
    for game in selected:
        local = destinations.get(sync.rom_path(seed['contentRoot'], game['rom']))
        if local is not None:
            game['rom'] = ntpath.join(xbox_root, local)
    plan = sync.catalog_only_plan(subset, root, xbox_root, platform=platform,
                                  extensions=formats | {'.zip'})
    sync.require(not plan['retained_unscanned'], f'Curated {platform} entries were not found in the local scan: '
                 + ', '.join(plan['retained_unscanned']))
    rebuilt = {g['id']: g for g in plan['generated_catalog']['games']}
    report = []
    used = set()
    for entry in plan['entries']:
        path = root.joinpath(*entry['local_relative'].split('/'))
        game = rebuilt[entry['id']]
        local = sync.relative(entry['local_relative']).casefold()
        if local in aliases:
            game['rom'] = ntpath.join(xbox_root, aliases[local])
            used.add(local)
        signature = rom_signature(path, formats)
        label = metadata['no-intro'].get(signature)
        item = {'id': entry['id'], 'rom': entry['local_relative'], 'xbox_path': game['rom'], 'status': 'unmatched'}
        if label is not None:
            if entry['match'] == 'new' or game['title'] == path.stem:
                game['title'] = display_title(label)
            item.update(status='matched', label=label, metadata={
                field: metadata[key][signature[0]] for key, field in FIELDS.items()
                if signature[0] in metadata[key]})
        report.append(item)
    sync.require(used == aliases.keys(), 'A ROM alias did not match the local scan.')
    catalog = copy.deepcopy(seed)
    catalog['games'] = [rebuilt.pop(g['id']) if g['platform'].casefold() == platform else g
                        for g in catalog['games']] + list(rebuilt.values())
    return sync.validate_catalog(catalog), report


def stage_artwork(opener, source, stage, platform, report, apply):
    system, _ = SYSTEMS[platform]
    files = art.LocalFiles(stage, source, apply)
    before = files.path('Presentation.json')
    previous = hashlib.sha256(before.read_bytes()).hexdigest() if before.exists() else None
    presentation = art.presentation_read(files)
    old = copy.deepcopy(presentation)
    rows = {row['id'].casefold(): row for row in presentation['games']}
    matched = [item for item in report if item['status'] == 'matched']
    pending = []
    for item in matched:
        identifier = item['id']
        if item['metadata']:
            key = identifier.casefold()
            if key not in rows:
                rows[key] = {'id': identifier}
                presentation['games'].append(rows[key])
            for key, value in item['metadata'].items():
                rows[identifier.casefold()].setdefault(key, value)
        directory = 'Artwork/games/' + art.folder(identifier)
        assets = item['assets'] = {}
        for role in ROLES:
            target = directory + '/' + role + '.png'
            if art.existing_role(files, directory, role):
                assets[role] = 'preserved'
            elif not apply:
                assets[role] = 'would-lookup'
            else:
                url = thumbnail_url(system, role, item['label'])
                item.setdefault('artwork_sources', {})[role] = url
                pending.append((assets, role, target, url))

    def download(task):
        _, role, _, url = task
        image = fetch(opener, url, art.MAX_IMAGE)
        if image is None:
            return 'missing', None
        try:
            return 'staged', art.resized(image, art.ROLES[role])[0]
        except art.ImportFailure:
            return 'invalid-image', None

    if pending:
        with ThreadPoolExecutor(max_workers=4) as pool:
            for index in range(0, len(pending), 16):
                print(f'Staging {platform} artwork: {index}/{len(pending)} lookups complete.',
                      file=sys.stderr, flush=True)
                batch = pending[index:index + 16]
                for (assets, role, target, _), (status, data) in zip(batch, pool.map(download, batch)):
                    if data is not None:
                        files.write(target, data)
                    assets[role] = status
    if apply and presentation != old:
        files.write('Presentation.json', art.json_bytes(presentation), previous)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--catalog', required=True, type=Path)
    parser.add_argument('--platform', required=True, choices=SYSTEMS)
    parser.add_argument('--rom-root', required=True, type=Path)
    parser.add_argument('--xbox-rom-root', required=True)
    parser.add_argument('--enrichment-dir', required=True, type=Path)
    parser.add_argument('--output', type=Path, help='New local catalog JSON file')
    parser.add_argument('--stage-artwork', action='store_true', help='Download and stage missing artwork locally')
    parser.add_argument('--rom-alias', action='append', default=[], metavar='LOCAL_ROM=XBOX_ROM',
                        help='Map a local filename to a verified Xbox filename under --xbox-rom-root')
    args = parser.parse_args(argv)
    try:
        sync.require(args.output is None or not args.output.resolve().is_relative_to(args.rom_root.resolve()),
                     'Catalog output must be outside the ROM library.')
        sync.require(args.output is None or (not args.output.exists() and not args.output.is_symlink()),
                     '--output must be a new catalog file.')
        sync.require(not args.enrichment_dir.resolve().is_relative_to(args.rom_root.resolve()),
                     'Artwork staging must be outside the ROM library.')
        opener = art.provider_opener()
        print(f'Loading Libretro metadata for {args.platform}.', file=sys.stderr, flush=True)
        seed = sync.validate_catalog(sync.read_json(args.catalog))
        aliases = {}
        for local, target in sync.pairs_arg(args.rom_alias, sync.relative).items():
            key = sync.relative(local).casefold()
            sync.require(key not in aliases, 'Duplicate normalized ROM alias.')
            aliases[key] = target
        catalog, report = prepare(seed, args.rom_root, args.xbox_rom_root, args.platform,
                                  load_metadata(opener, SYSTEMS[args.platform][0]), aliases)
        stage_artwork(opener, args.catalog, args.enrichment_dir, args.platform, report, args.stage_artwork)
        if args.output:
            with args.output.open('xb') as output:
                output.write(sync.encode_catalog(catalog))
        print(json.dumps({'metadata_source': DATABASE, 'artwork_source': THUMBNAILS,
                          'summary': dict(Counter(item['status'] for item in report)),
                          'games': report, 'generated_catalog': catalog}, indent=2))
        return 0
    except (sync.SyncError, art.ImportFailure, OSError, ValueError) as error:
        print(f'Console library stopped: {error}', file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
