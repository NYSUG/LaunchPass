#!/usr/bin/env python3
"""Plan/synchronize a platform-organized ROM library and the existing InternalCatalog v1."""
import argparse
import copy
from dataclasses import dataclass, field
import hashlib
from http.client import HTTPException
from http.cookies import SimpleCookie
import json
import math
import ntpath
import os
from pathlib import Path
import re
import secrets
import stat
import sys
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode
from urllib.request import Request

from xbox_diagnostics import DiagnosticError, Portal, read_secret

PACKAGES = '/api/app/packagemanager/packages'
FILES = '/api/filesystem/apps/files'
FILE = '/api/filesystem/apps/file'
CATALOG = 'InternalCatalog.json'
# Identity Name in the current fork's Package.appxmanifest; no UWP build/runtime dependency.
LAUNCHPASS_IDENTITY = 'f3a28ab5-9cd3-480a-86e5-58ecbeba4015'
MAX_CATALOG_BYTES = 8 * 1024 * 1024
DEFAULT_EXTENSIONS = '.zip,.7z,.md,.gen,.smd,.sfc,.smc,.nes,.gb,.gbc,.gba,.chd,.iso,.cue,.bin'


class SyncError(Exception):
    """Messages contain local validation context only, never server bodies or secrets."""


def require(condition, message):
    if not condition:
        raise SyncError(message)


def json_load(data):
    def pairs(items):
        result = {}
        for key, value in items:
            require(key not in result, 'Duplicate JSON object key.')
            result[key] = value
        return result
    def invalid_constant(_):
        raise SyncError('Invalid JSON number.')
    try:
        return json.loads(data, object_pairs_hook=pairs, parse_constant=invalid_constant)
    except (ValueError, UnicodeError, RecursionError):
        raise SyncError('Invalid JSON document; contents suppressed.') from None


def read_json(path):
    return json_load(Path(path).read_text(encoding='utf-8-sig'))


def identifier(value):
    require(isinstance(value, str) and re.fullmatch(r'[A-Za-z0-9_-]+', value), 'Invalid catalog ID.')
    return value


def core_name(value):
    require(isinstance(value, str) and re.fullmatch(r'[A-Za-z0-9_-][A-Za-z0-9_.-]*\.dll', value, re.I),
            'Core must be a DLL filename without paths or arguments.')
    return value


def text(value):
    require(isinstance(value, str) and value.strip() and not any(ord(c) < 32 or 127 <= ord(c) <= 159 for c in value),
            'Catalog titles must be nonempty text without control characters.')


def relative(value):
    require(isinstance(value, str) and value.strip(), 'Expected a relative Windows path.')
    value = value.replace('/', '\\')
    for part in value.split('\\'):
        require(part.strip() and part not in ('.', '..') and not part.endswith(('.', ' ')) and
                not re.search(r'[<>:"|?*\x00-\x1f\x7f-\x9f]', part), 'Unsafe Windows path.')
    return value


def absolute(value):
    require(isinstance(value, str), 'Expected an absolute Windows drive path.')
    value = value.replace('/', '\\')
    require(re.match(r'^[A-Za-z]:\\', value), 'Expected an absolute Windows drive path.')
    if len(value) > 3:
        relative(value[3:])
    return value


def root_path(value):
    require(isinstance(value, str), 'Catalog contentRoot is required.')
    value = value.replace('/', '\\')
    return absolute(value if len(value) == 3 else value.rstrip('\\'))


def rom_path(root, rom):
    require(isinstance(rom, str), 'ROM must be a path string.')
    path = absolute(rom) if re.match(r'^[A-Za-z]:[/\\]', rom) else root.rstrip('\\') + '\\' + relative(rom)
    require(len(path) > 3, 'ROM must name a file.')
    return path.casefold()


def validate_catalog(doc):
    require(isinstance(doc, dict) and type(doc.get('version')) is int and doc['version'] == 1,
            'Expected InternalCatalog schema version 1.')
    root = root_path(doc.get('contentRoot'))
    platforms = doc.get('platforms')
    games = doc.get('games')
    require(isinstance(platforms, list) and platforms and isinstance(games, list) and games,
            'Catalog needs nonempty platforms and games arrays.')
    platform_ids, game_ids, paths = set(), set(), set()
    for platform in platforms:
        require(isinstance(platform, dict), 'Invalid platform entry.')
        pid = identifier(platform.get('id')).casefold()
        require(pid not in platform_ids, 'Duplicate platform ID.')
        platform_ids.add(pid)
        text(platform.get('title'))
        core_name(platform.get('defaultCore'))
    for game in games:
        require(isinstance(game, dict), 'Invalid game entry.')
        gid = identifier(game.get('id')).casefold()
        require(gid not in game_ids, 'Duplicate game ID.')
        game_ids.add(gid)
        require(identifier(game.get('platform')).casefold() in platform_ids, 'Unknown game platform.')
        text(game.get('title'))
        key = rom_path(root, game.get('rom'))
        # Stricter than the app: ambiguous same-ROM entries cannot be safely regenerated.
        require(key not in paths, 'Multiple catalog entries refer to the same ROM path.')
        paths.add(key)
        if game.get('core') is not None:
            core_name(game['core'])
        if game.get('artwork') is not None:
            relative(game['artwork'])
    return doc


def merge_fields(left, right, context):
    result = copy.deepcopy(left)
    for key, value in right.items():
        require(key not in result or json.dumps(result[key], sort_keys=True) == json.dumps(value, sort_keys=True),
                'Conflicting manually edited metadata in ' + context + '; reconcile the local seed and Xbox catalog first.')
        if key not in result:
            result[key] = copy.deepcopy(value)
    return result


def merge_catalogs(local, remote):
    validate_catalog(local)
    if remote is None:
        return copy.deepcopy(local)
    validate_catalog(remote)
    result = merge_fields({k: v for k, v in local.items() if k not in ('platforms', 'games')},
                          {k: v for k, v in remote.items() if k not in ('platforms', 'games')}, 'catalog')
    result['platforms'] = copy.deepcopy(local['platforms'])
    by_platform = {p['id'].casefold(): p for p in result['platforms']}
    for platform in remote['platforms']:
        key = platform['id'].casefold()
        if key in by_platform:
            merged = merge_fields(by_platform[key], platform, 'platform ' + key)
            by_platform[key].update(merged)
        else:
            item = copy.deepcopy(platform)
            result['platforms'].append(item)
            by_platform[key] = item
    result['games'] = copy.deepcopy(local['games'])
    by_id = {g['id'].casefold(): g for g in result['games']}
    for game in remote['games']:
        key = game['id'].casefold()
        if key in by_id:
            by_id[key].update(merge_fields(by_id[key], game, 'game ' + key))
        else:
            item = copy.deepcopy(game)
            result['games'].append(item)
            by_id[key] = item
    return validate_catalog(result)


def strict_listing(payload):
    require(isinstance(payload, dict) and isinstance(payload.get('Items'), list), 'Malformed Portal directory listing.')
    entries = {}
    for item in payload['Items']:
        require(isinstance(item, dict), 'Malformed Portal directory entry.')
        name = relative(item.get('Name'))
        require('\\' not in name, 'Portal entry name must be a single filename.')
        kind = item.get('Type')
        require(type(kind) is int and 0 <= kind <= 0xffffffff, 'Unknown Portal file attribute Type.')
        require(not kind & 0x400, 'Portal reparse points are not supported.')
        is_dir = bool(kind & 0x10)
        size = item.get('FileSize')
        require(is_dir or (type(size) is int and size >= 0), 'Missing/invalid Portal FileSize.')
        key = name.casefold()
        require(key not in entries, 'Case-insensitive duplicate Portal filenames.')
        entries[key] = {'name': name, 'directory': is_dir, 'size': None if is_dir else size}
    return entries


def installed(payload, full_name):
    items = payload.get('InstalledPackages') if isinstance(payload, dict) else None
    require(isinstance(items, list) and all(isinstance(p, dict) for p in items), 'Malformed installed-package response.')
    matches = [p for p in items if p.get('PackageFullName') == full_name]
    require(len(matches) == 1, 'Requested package is not installed or is ambiguous.')
    family = matches[0].get('PackageFamilyName')
    require(isinstance(family, str) and relative(family) == family and '\\' not in family,
            'Installed package has no valid PackageFamilyName.')
    return matches[0]


def package_content_path(content_root, family):
    root = root_path(content_root)
    match = re.fullmatch(r'[A-Za-z]:\\Users\\[^\\]+\\AppData\\Local\\Packages\\([^\\]+)\\(LocalState(?:\\.*)?)', root, re.I)
    require(match and match[1].casefold() == family.casefold(),
            'contentRoot must be inside the selected installed RetroArch package LocalState (exact package family).')
    return match[2]


def params(package, directory):
    directory = relative(directory)
    require(directory == 'LocalState' or directory.startswith('LocalState\\'), 'Destination must be inside LocalState.')
    return {'knownfolderid': 'LocalAppData', 'packagefullname': package, 'path': '\\' + directory}


class SyncPortal(Portal):
    """Reuse diagnostic TLS/auth/cookie isolation; add only file GET and explicitly gated POST."""
    def __init__(self, *args, apply=False, **kwargs):
        super().__init__(*args, **kwargs)
        self.apply_enabled = apply

    def _exchange(self, method, endpoint, query=None, data=None, headers=None):
        require((method == 'GET' and endpoint in ('/api/os/info', FILE)) or
                (method == 'POST' and endpoint == FILE and self.apply_enabled), 'Portal operation is not authorized.')
        request_headers = {'Accept': 'application/json', **(headers or {})}
        if self.authorization:
            request_headers['Authorization'] = self.authorization
        request = Request(self.origin + endpoint + ('?' + urlencode(query) if query else ''),
                          data=data, headers=request_headers, method=method)
        self.cookies.add_cookie_header(request)
        cookies = SimpleCookie()
        cookies.load(request.get_header('Cookie', ''))
        if 'CSRF-Token' in cookies:
            request.add_header('X-CSRF-Token', cookies['CSRF-Token'].value)
        require(method != 'POST' or 'CSRF-Token' in cookies, 'No applicable CSRF token; transfer refused.')
        try:
            with self.opener.open(request, timeout=self.timeout) as response:
                require(response.status in ((200, 201) if method == 'POST' else (200,)), 'Unexpected Portal response status.')
                body = response.read(MAX_CATALOG_BYTES + 1)
                require(len(body) <= MAX_CATALOG_BYTES, 'Portal response exceeds metadata size limit.')
                return body
        except HTTPError as error:
            code = error.code
            error.close()
            raise DiagnosticError('Portal HTTP status ' + str(code)) from None
        except (URLError, OSError, ValueError, HTTPException):
            raise SyncError('Portal transfer/connection interrupted; state may be partial. Re-run dry-run before retrying.') from None

    def bootstrap(self):
        # Matches the inspected upload.sh (the read-only diagnostic uses GET / instead).
        self._exchange('GET', '/api/os/info')

    def list_files(self, package, directory):
        return self.get_json(FILES, params(package, directory))

    def read_catalog(self, package):
        query = dict(params(package, 'LocalState'), filename=CATALOG)
        try:
            return self._exchange('GET', FILE, query)
        except DiagnosticError as error:
            if str(error) != 'Portal HTTP status 403':
                raise
            self.bootstrap()
            return self._exchange('GET', FILE, query)

    def upload(self, package, directory, name, source, size):
        # Known Content-Length + bounded chunks, never read an archive into memory or extract it.
        require(relative(name) == name and '\\' not in name, 'Unsafe upload filename.')
        boundary = 'LaunchPassSync' + secrets.token_hex(16)
        prefix = ('--' + boundary + '\r\nContent-Disposition: form-data; name="file"; filename="' + name +
                  '"\r\nContent-Type: application/octet-stream\r\n\r\n').encode('utf-8')
        suffix = ('\r\n--' + boundary + '--\r\n').encode('ascii')
        def chunks():
            yield prefix
            remaining = size
            while remaining:
                block = source.read(min(1024 * 1024, remaining))
                require(block, 'Source file truncated during upload.')
                remaining -= len(block)
                yield block
            require(not source.read(1), 'Source file grew during upload.')
            yield suffix
        query = dict(params(package, directory), extract='false')
        # Never retry a POST automatically: the server may already have written bytes.
        self._exchange('POST', FILE, query, chunks(),
                       {'Content-Type': 'multipart/form-data; boundary=' + boundary,
                        'Content-Length': str(len(prefix) + size + len(suffix))})


class SnapshotPortal:
    """Offline fixture transport. Applying a snapshot is always forbidden."""
    def __init__(self, document):
        require(isinstance(document, dict), "Snapshot must be a JSON object.")
        self.document = document
        self.apply_enabled = False
    def bootstrap(self):
        pass
    def get_json(self, endpoint):
        require(endpoint == PACKAGES, 'Unsupported snapshot endpoint.')
        return self.document.get('packages')
    def list_files(self, package, directory):
        packages = self.document.get('directories')
        require(isinstance(packages, dict) and isinstance(packages.get(package), dict), 'Malformed snapshot directories.')
        directories = packages[package]
        require(directory in directories, 'Snapshot lacks a requested directory (include explicit null to simulate 404).')
        result = directories[directory]
        if result is None:
            raise DiagnosticError('Portal HTTP status 404')
        return result
    def read_catalog(self, package):
        require(self.document.get('catalog') is not None, 'Snapshot is missing the listed remote catalog.')
        return encode_catalog(self.document['catalog'])


def encode_catalog(doc):
    return (json.dumps(doc, ensure_ascii=True, indent=2, allow_nan=False) + '\n').encode('utf-8')


def pairs_arg(values, validator):
    result = {}
    for value in values:
        key, sep, data = value.partition('=')
        require(sep and key not in result, 'Expected unique KEY=VALUE arguments.')
        result[key] = validator(data)
    return result


def fingerprint(path):
    info = path.lstat()
    require(stat.S_ISREG(info.st_mode) and not path.is_symlink(), 'ROM source must be a regular file, not a symlink.')
    return (info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns)


@dataclass
class LocalRom:
    source: Path
    rom: str
    platform: str
    stamp: tuple
    @property
    def size(self):
        return self.stamp[2]


def scan_library(root, platform_dirs, extensions):
    require(root.is_dir(), 'ROM library root is missing or is not a directory.')
    found, keys = [], set()
    for pid, folder in sorted(platform_dirs.items()):
        relative(folder)
        base = root.joinpath(*folder.replace('\\', '/').split('/'))
        require(base.is_dir() and not base.is_symlink(), 'Configured platform directory is missing or is a symlink: ' + pid)
        require(base.resolve().is_relative_to(root.resolve()), 'Platform directory escapes library root.')
        def walk_error(_):
            raise SyncError("Cannot read a ROM library directory; refusing a partial scan.")
        for directory, subdirs, files in os.walk(base, followlinks=False, onerror=walk_error):
            subdirs.sort()
            for subdir in subdirs:
                require(not Path(directory, subdir).is_symlink(), 'Symlink directories are not supported in a ROM library.')
            subdirs[:] = [d for d in subdirs if not d.startswith('.')]
            for filename in sorted(files):
                if filename.startswith('.') or (extensions != {'*'} and Path(filename).suffix.casefold() not in extensions):
                    continue
                source = Path(directory, filename)
                suffix = source.relative_to(base).as_posix()
                rom = relative(pid + '/' + suffix).replace('\\', '/')
                require(rom.casefold() not in keys, 'Local ROMs collide on case-insensitive Windows paths.')
                keys.add(rom.casefold())
                found.append(LocalRom(source, rom, pid, fingerprint(source)))
    return found


def generate(seed, roms, defaults, overrides):
    doc = copy.deepcopy(seed)
    platforms = {p['id'].casefold(): p for p in doc['platforms']}
    for pid, core in defaults.items():
        identifier(pid)
        if pid.casefold() in platforms:
            require(platforms[pid.casefold()]['defaultCore'] == core, 'Refusing to replace an existing platform default core.')
        else:
            item = {'id': pid, 'title': pid, 'defaultCore': core}
            platforms[pid.casefold()] = item
            doc['platforms'].append(item)
    by_rom = {rom_path(root_path(doc['contentRoot']), g['rom']): g for g in doc['games']}
    ids = {g['id'].casefold() for g in doc['games']}
    used_overrides = set()
    for rom in roms:
        require(rom.platform.casefold() in platforms, 'Supply --core for new platform: ' + rom.platform)
        key = rom_path(root_path(doc['contentRoot']), rom.rom)
        game = by_rom.get(key)
        if game is None:
            gid = rom.platform + '-' + hashlib.sha256(key.encode('utf-8')).hexdigest()[:16]
            require(gid.casefold() not in ids, 'Generated game ID collision; resolve in seed catalog.')
            ids.add(gid.casefold())
            game = {'id': gid, 'platform': platforms[rom.platform.casefold()]['id'],
                    'title': rom.source.stem, 'rom': rom.rom}
            doc['games'].append(game)
            by_rom[key] = game
        require(game['platform'].casefold() == rom.platform.casefold(), 'Existing ROM has a different manually assigned platform.')
        if rom.rom.casefold() in overrides:
            core = overrides[rom.rom.casefold()]
            require(game.get('core') is None or game['core'] == core, 'Refusing to replace an existing per-game core override.')
            game['core'] = core
            used_overrides.add(rom.rom.casefold())
    require(used_overrides == set(overrides), 'A --game-core entry did not match a scanned ROM.')
    return validate_catalog(doc)


@dataclass
class Plan:
    catalog: dict
    remote_catalog_bytes: object
    roms: list
    uploads: list
    report: dict
    directories: dict = field(default_factory=dict)


def remote_catalog(portal, package):
    listing = strict_listing(portal.list_files(package, 'LocalState'))
    entry = listing.get(CATALOG.casefold())
    if entry is None:
        return None, None
    require(not entry['directory'] and entry['size'] <= MAX_CATALOG_BYTES, 'Invalid remote catalog file.')
    data = portal.read_catalog(package)
    require(len(data) == entry['size'], 'Remote catalog changed while reading; rerun dry-run.')
    return validate_catalog(json_load(data)), data


def build_plan(portal, seed, root, retroarch, launchpass, identity, defaults, overrides, platform_dirs, extensions):
    portal.bootstrap()
    payload = portal.get_json(PACKAGES)
    ra, lp = installed(payload, retroarch), installed(payload, launchpass)
    require(retroarch != launchpass and ra['PackageFamilyName'].casefold() != lp['PackageFamilyName'].casefold(),
            'RetroArch and LaunchPass must be different installed packages.')
    require(launchpass.startswith(identity + '_') and lp['PackageFamilyName'].startswith(identity + '_'),
            'Installed LaunchPass identity does not match --launchpass-identity.')
    content_dir = package_content_path(seed['contentRoot'], ra['PackageFamilyName'])
    remote, old_bytes = remote_catalog(portal, launchpass)
    merged = merge_catalogs(seed, remote)
    roms = scan_library(root, platform_dirs, extensions)
    catalog = generate(merged, roms, defaults, overrides)
    listing_cache, missing_dirs, uploads, unchanged = {}, [], [], []
    # Validate the configured ROM content root itself, even when no ROM is discovered.
    directories = {content_dir} | {content_dir + '\\' + ntpath.dirname(r.rom.replace('/', '\\')) for r in roms}
    for directory in sorted(directories):
        try:
            listing_cache[directory] = strict_listing(portal.list_files(retroarch, directory))
        except DiagnosticError as error:
            if str(error) != 'Portal HTTP status 404':
                raise
            listing_cache[directory] = {}
            missing_dirs.append(directory)
    for rom in roms:
        directory = content_dir + '\\' + ntpath.dirname(rom.rom.replace('/', '\\'))
        existing = listing_cache[directory].get(rom.source.name.casefold())
        require(existing is None or not existing['directory'], 'Remote ROM filename refers to a directory.')
        if existing is not None and existing['size'] == rom.size:
            unchanged.append(rom.rom)
        else:
            uploads.append({'rom': rom.rom, 'bytes': rom.size, 'reason': 'new' if existing is None else 'size_changed',
                            'previous_bytes': None if existing is None else existing['size'], 'directory': directory})
    original = {g['id']: g for g in merged['games']}
    additions = [g['id'] for g in catalog['games'] if g['id'] not in original]
    updates = [g['id'] for g in catalog['games'] if g['id'] in original and g != original[g['id']]]
    report = {'mode': 'dry-run', 'configured_platforms': sorted(platform_dirs), 'scanned_roms': len(roms),
              'upload_bytes': sum(u['bytes'] for u in uploads), 'uploads': uploads, 'unchanged_roms': unchanged,
              'missing_directories': missing_dirs,
              'catalog': {'destination': 'LaunchPass LocalState\\' + CATALOG,
                          'action': 'create' if remote is None else ('unchanged' if catalog == remote else 'update'),
                          'added_game_ids': additions, 'updated_game_ids': updates,
                          'retained_unscanned_entries': len(catalog['games']) - len(roms)},
              'remote_deletions': [], 'generated_catalog': catalog}
    return Plan(catalog, old_bytes, roms, uploads, report, listing_cache)


def apply_plan(portal, plan, options):
    require(getattr(portal, 'apply_enabled', False), 'Actual transfers require --apply and a live Portal connection.')
    require(not plan.report['missing_directories'], 'Create the reported ROM directories in Device Portal, then rerun dry-run. No writes performed.')
    # Re-read packages/catalog/listings and rescan files immediately before the first mutation.
    fresh = build_plan(portal, **options)
    require(fresh.report == plan.report and fresh.directories == plan.directories and
            fresh.remote_catalog_bytes == plan.remote_catalog_bytes and
            [(r.rom, r.stamp) for r in fresh.roms] == [(r.rom, r.stamp) for r in plan.roms],
            'Library or Xbox state changed after planning; rerun dry-run. No writes performed.')
    by_rom = {r.rom: r for r in plan.roms}
    for upload in plan.uploads:
        rom = by_rom[upload['rom']]
        require(fingerprint(rom.source) == rom.stamp, 'Local ROM changed after planning; transfer stopped.')
        # O_NOFOLLOW guards a source-file symlink swap; parent resolution is checked again too.
        require(rom.source.resolve().is_relative_to(options['root'].resolve()), 'ROM source escaped library root.')
        fd = os.open(rom.source, os.O_RDONLY | getattr(os, 'O_NOFOLLOW', 0))
        with os.fdopen(fd, 'rb') as stream:
            info = os.fstat(stream.fileno())
            require((info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns) == rom.stamp,
                    'ROM source changed while opening; transfer stopped.')
            portal.upload(options['retroarch'], upload['directory'], rom.source.name, stream, rom.size)
        require(fingerprint(rom.source) == rom.stamp, 'ROM source changed during upload; catalog was not published.')
        entries = strict_listing(portal.list_files(options['retroarch'], upload['directory']))
        item = entries.get(rom.source.name.casefold())
        require(item is not None and not item['directory'] and item['size'] == rom.size,
                'Post-upload filename/size verification failed; catalog was not published.')
    _, current_bytes = remote_catalog(portal, options['launchpass'])
    require(current_bytes == plan.remote_catalog_bytes, 'Xbox catalog changed during ROM transfer; refusing to overwrite metadata.')
    if plan.report['catalog']['action'] != 'unchanged':
        import io
        data = encode_catalog(plan.catalog)
        portal.upload(options['launchpass'], 'LocalState', CATALOG, io.BytesIO(data), len(data))
        after, _ = remote_catalog(portal, options['launchpass'])
        require(after == plan.catalog, 'Catalog read-back failed; inspect LocalState and rerun dry-run.')
    return dict(plan.report, mode='applied')


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--rom-root', required=True, type=Path)
    parser.add_argument('--catalog', required=True, type=Path, help='Existing v1 manifest seed; never overwritten')
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument('--url', help='Device Portal HTTPS origin; credentials are prompted, never arguments')
    source.add_argument('--snapshot', type=Path, help='Offline package/listing fixture; cannot be applied')
    parser.add_argument('--retroarch-package-full-name', required=True)
    parser.add_argument('--launchpass-package-full-name', required=True)
    parser.add_argument('--launchpass-identity', default=LAUNCHPASS_IDENTITY)
    parser.add_argument('--core', action='append', default=[], metavar='PLATFORM=DLL')
    parser.add_argument('--game-core', action='append', default=[], metavar='PLATFORM/ROM=DLL')
    parser.add_argument('--platform-dir', action='append', default=[], metavar='PLATFORM=RELATIVE_FOLDER')
    parser.add_argument('--extensions', default=DEFAULT_EXTENSIONS, help='Comma-separated suffixes, or *')
    parser.add_argument('--output', type=Path, help='Write generated manifest to a NEW local file (never clobber)')
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument('--apply', action='store_true', help='Actually upload planned ROMs and catalog')
    mode.add_argument('--dry-run', action='store_true', help='Plan only (the default); never POST')
    parser.add_argument('--auth', choices=('basic', 'cookie'), default='basic')
    tls = parser.add_mutually_exclusive_group()
    tls.add_argument('--ca-file')
    tls.add_argument('--insecure', action='store_true')
    parser.add_argument('--timeout', type=float, default=60)
    args = parser.parse_args(argv)
    try:
        require(not (args.apply and args.snapshot), '--apply is forbidden with an offline snapshot.')
        require(math.isfinite(args.timeout) and args.timeout > 0, 'Timeout must be positive and finite.')
        require(args.output is None or (not args.output.exists() and not args.output.is_symlink()), '--output must be a new local file.')
        seed = validate_catalog(read_json(args.catalog))
        defaults = pairs_arg(args.core, core_name)
        directories = {p['id']: p['id'] for p in seed['platforms']}
        for pid in defaults:
            identifier(pid)
            directories.setdefault(pid, pid)
        for pid, folder in pairs_arg(args.platform_dir, relative).items():
            require(pid in directories, '--platform-dir needs a known platform (configure --core for a new one).')
            directories[pid] = folder
        overrides = {}
        for key, value in pairs_arg(args.game_core, core_name).items():
            key = relative(key).replace('\\', '/').casefold()
            require(key not in overrides, 'Duplicate normalized --game-core ROM path.')
            overrides[key] = value
        extensions = {e.casefold().strip() for e in args.extensions.split(',')}
        require(extensions == {'*'} or all(re.fullmatch(r'\.[a-z0-9]+', e) for e in extensions), 'Invalid --extensions list.')
        if args.snapshot:
            portal = SnapshotPortal(read_json(args.snapshot))
        else:
            kwargs = dict(ca_file=args.ca_file, insecure=args.insecure, timeout=args.timeout, apply=args.apply)
            SyncPortal(args.url, **kwargs)  # Validate address/TLS before secret input.
            if args.auth == 'basic':
                user, password = os.environ.get('XBOX_USER'), os.environ.get('XBOX_PASS')
                portal = SyncPortal(args.url, username=user if user is not None else read_secret('Device Portal username (hidden): '),
                                    password=password if password is not None else read_secret('Device Portal password (hidden): '), **kwargs)
            else:
                portal = SyncPortal(args.url, cookie=read_secret('Device Portal Cookie header value (hidden): '), **kwargs)
        options = dict(seed=seed, root=args.rom_root, retroarch=args.retroarch_package_full_name,
                       launchpass=args.launchpass_package_full_name, identity=args.launchpass_identity,
                       defaults=defaults, overrides=overrides, platform_dirs=directories, extensions=extensions)
        plan = build_plan(portal, **options)
        report = apply_plan(portal, plan, options) if args.apply else plan.report
        if args.output:
            with args.output.open('x', encoding='utf-8') as output:
                output.write(encode_catalog(plan.catalog).decode('utf-8'))
        print(json.dumps(report, indent=2, ensure_ascii=True))
        return 0
    except (SyncError, DiagnosticError) as error:
        print('Sync stopped: ' + str(error), file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        print('Sync interrupted; remote files may be partial. Re-run dry-run before retrying.', file=sys.stderr)
        return 130
    except (OSError, ValueError, EOFError, RecursionError, HTTPException):
        print('Sync stopped: local IO, configuration, or secret input failure; details suppressed.', file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
