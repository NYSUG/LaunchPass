#!/usr/bin/env python3
"""ArcadeItalia -> local PremiumBrowser artwork. No Xbox or ROM writes.

Dry-run queries metadata (or reads cache) but writes/downloads no files.
--offline makes a dry-run entirely local. --apply enables writes/downloads.
"""
from __future__ import annotations

import argparse
import copy
import gzip
import hashlib
import io
import json
import math
import ntpath
import os
from pathlib import Path
import re
import ssl
import sys
import tempfile
import time
from datetime import datetime, timezone
from email.utils import parsedate_to_datetime
from http.client import HTTPException
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode, urljoin, urlsplit
from urllib.request import Request, build_opener, HTTPRedirectHandler, HTTPSHandler
import uuid
import warnings
import zlib

API = 'https://adb.arcadeitalia.net/service_scraper.php'
CREDIT = 'Arcade Database by motoschifo — https://adb.arcadeitalia.net'
RIGHTS = 'Artwork, names and trademarks belong to their respective copyright/trademark holders. MAME is a registered trademark of Nicola Salmoria.'
MAX_JSON = 8 * 1024 * 1024
MAX_IMAGE = 30 * 1024 * 1024
MACOS_CA_BUNDLE = Path('/etc/ssl/cert.pem')
ID = re.compile(r'[A-Za-z0-9_-]+\Z')
ROM = re.compile(r'[a-z0-9_]+\Z')
DEVICE = re.compile(r'(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])\Z', re.I)
ROLES = {
    'logo': (1200, 400), 'background': (1920, 1080), 'screenshot': (1000, 800),
    'extras/marquee': (1200, 400), 'extras/flyer': (1200, 1800),
    'extras/title': (1280, 960), 'extras/cabinet': (1000, 1400),
    'extras/bezel': (1920, 1080), 'extras/cpanel': (1600, 800),
}
FIELDS = ('title', 'short_title', 'year', 'manufacturer', 'genre', 'players',
          'nplayers', 'cloneof', 'status', 'languages', 'input_controls',
          'input_buttons', 'serie', 'screen_orientation', 'screen_resolution',
          'emulator_name', 'url', 'history_copyright_short', 'history_copyright_long')


class ImportFailure(Exception):
    pass


class UnsupportedRom(ImportFailure):
    pass


class ProviderCertificateError(Exception):
    """Fatal trust failure that applies to the run rather than an individual ROM."""


class ServiceStopped(ImportFailure):
    """Quota/maintenance/rate limit: stop all further network requests this run."""


def digest(data):
    return hashlib.sha256(data).hexdigest()


def json_bytes(value):
    return (json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False) + '\n').encode('utf-8')


def parse_json(data):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ImportFailure('Duplicate JSON key: ' + key)
            result[key] = value
        return result
    try:
        return json.loads(data.decode('utf-8-sig'), object_pairs_hook=unique,
                          parse_constant=lambda value: (_ for _ in ()).throw(ValueError(value)))
    except (ValueError, UnicodeError) as exc:
        raise ImportFailure('Invalid JSON: ' + str(exc)) from exc


def folder(identifier):
    if not isinstance(identifier, str) or not ID.fullmatch(identifier):
        raise ImportFailure('Invalid catalog identifier: ' + repr(identifier))
    return '~' + identifier.lower().encode().hex().upper() if DEVICE.fullmatch(identifier) else identifier


def shortname(rom):
    if not isinstance(rom, str):
        raise ImportFailure('ROM must be a path string')
    name, ext = ntpath.splitext(ntpath.basename(rom))
    name = name.lower()
    if ext.lower() not in ('.zip', '.7z') or not ROM.fullmatch(name):
        raise UnsupportedRom('Not a MAME ZIP/7z ROM short name: ' + rom)
    return name


def catalog_read(path):
    raw = path.read_bytes()
    if len(raw) > MAX_JSON:
        raise ImportFailure('Catalog exceeds 8 MiB')
    doc = parse_json(raw)
    if not isinstance(doc, dict) or type(doc.get('version')) is not int or doc['version'] != 1:
        raise ImportFailure('InternalCatalog version must be 1')
    if not isinstance(doc.get('contentRoot'), str) or not isinstance(doc.get('platforms'), list) or not isinstance(doc.get('games'), list):
        raise ImportFailure('Catalog requires contentRoot, platforms and games')
    platforms, ids = set(), set()
    for platform in doc['platforms']:
        if not isinstance(platform, dict):
            raise ImportFailure('Invalid platform')
        folder(platform.get('id'))
        key = platform['id'].casefold()
        if key in platforms:
            raise ImportFailure('Duplicate platform ID')
        platforms.add(key)
    for game in doc['games']:
        if not isinstance(game, dict):
            raise ImportFailure('Invalid game')
        folder(game.get('id')); folder(game.get('platform'))
        key = game['id'].casefold()
        if key in ids or game['platform'].casefold() not in platforms:
            raise ImportFailure('Duplicate game ID or unknown platform')
        if not isinstance(game.get('title'), str) or not isinstance(game.get('rom'), str):
            raise ImportFailure('Game needs title and rom strings')
        ids.add(key)
    return doc, digest(raw)


class LocalFiles:
    """All generated files stay under output. Refuse symlink traversal and input aliases."""
    def __init__(self, root, catalog, apply=False):
        self.root = Path(os.path.abspath(root))
        self.catalog = catalog.resolve()
        self.apply = apply
        self.path('.')

    def path(self, relative):
        rel = Path(relative)
        if rel.is_absolute() or '..' in rel.parts:
            raise ImportFailure('Unsafe output path')
        path = self.root
        # Match Windows' case-insensitive paths even when staging on macOS/Linux.
        for segment in rel.parts:
            if segment == '.':
                continue
            if path.is_dir():
                matches = [item for item in path.iterdir() if item.name.casefold() == segment.casefold()]
                if len(matches) > 1:
                    raise ImportFailure('Case-colliding output files: ' + str(path / segment))
                path = matches[0] if matches else path / segment
            else:
                path = path / segment
        for parent in (path, *path.parents):
            if parent.is_symlink():
                raise ImportFailure('Output path contains a symlink: ' + str(parent))
        if not path.resolve().is_relative_to(self.root.resolve()):
            raise ImportFailure('Output escaped root')
        if path.resolve() == self.catalog or (path.is_file() and self.catalog.exists()
                                              and os.path.samefile(path, self.catalog)):
            raise ImportFailure('Refusing to replace input catalog')
        return path

    def read(self, relative, default=None):
        path = self.path(relative)
        if not path.exists():
            return copy.deepcopy(default)
        raw = path.read_bytes()
        if len(raw) > MAX_JSON:
            raise ImportFailure('Local JSON exceeds 8 MiB: ' + relative)
        return parse_json(raw)

    def write(self, relative, data, expected=None):
        if not self.apply:
            raise ImportFailure('Writes require --apply')
        path = self.path(relative)
        path.parent.mkdir(parents=True, exist_ok=True)
        self.path(relative)
        actual = digest(path.read_bytes()) if path.exists() else None
        if actual != expected:
            raise ImportFailure('File changed or already exists; preserving: ' + relative)
        fd, temp = tempfile.mkstemp(prefix='.arcade-artwork-', dir=path.parent)
        try:
            with os.fdopen(fd, 'wb') as stream:
                stream.write(data); stream.flush(); os.fsync(stream.fileno())
            # Recheck immediately before replacement; only a held importer lock may write.
            if (digest(path.read_bytes()) if path.exists() else None) != expected:
                raise ImportFailure('File changed during import: ' + relative)
            os.replace(temp, path)
        finally:
            if os.path.exists(temp):
                os.unlink(temp)


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def allowed_url(url):
    try:
        parts = urlsplit(url)
        host = parts.hostname or ''
        port = parts.port
    except ValueError as exc:
        raise ImportFailure('Invalid media URL') from exc
    if (parts.scheme != 'https' or parts.username or parts.password or port not in (None, 443)
            or not (host == 'arcadeitalia.net' or host.endswith('.arcadeitalia.net'))):
        raise ImportFailure('Refusing non-ArcadeItalia HTTPS URL: ' + url)


def provider_opener():
    paths = ssl.get_default_verify_paths()
    if (sys.platform == 'darwin' and not paths.cafile and not paths.capath
            and paths.openssl_cafile_env not in os.environ and paths.openssl_capath_env not in os.environ
            and MACOS_CA_BUNDLE.is_file()):
        context = ssl.create_default_context(cafile=str(MACOS_CA_BUNDLE))
        return build_opener(NoRedirect(), HTTPSHandler(context=context))
    return build_opener(NoRedirect())


class Client:
    def __init__(self, files, interval=1.0, ttl_days=30, refresh=False, offline=False,
                 opener=None, sleep=time.sleep, clock=time.monotonic, now=time.time):
        self.files, self.interval = files, max(1.0, interval)
        self.ttl, self.refresh, self.offline = ttl_days * 86400, refresh, offline
        self.opener = opener if opener is not None else provider_opener()
        self.sleep, self.clock, self.now = sleep, clock, now
        self.last = None
        self.stopped = None
        self.memory = {}
        self.image_memory = {}
        self.requests = 0

    def _http(self, url, limit):
        if self.stopped:
            raise ServiceStopped(self.stopped)
        if self.offline:
            raise ImportFailure('Offline cache miss')
        allowed_url(url)
        redirects, retries = 0, 0
        while True:
            if self.last is not None:
                self.sleep(max(0, self.interval - (self.clock() - self.last)))
            self.last = self.clock()
            self.requests += 1
            request = Request(url, headers={'User-Agent': 'LaunchPass-ArtworkImporter/1.0 (local cabinet artwork)',
                                            'Accept-Language': 'en', 'Accept-Encoding': 'identity'})
            try:
                with self.opener.open(request, timeout=30) as response:
                    length = response.headers.get('Content-Length')
                    if length:
                        try:
                            size = int(length)
                        except ValueError as exc:
                            raise ImportFailure('Invalid response Content-Length') from exc
                        if size < 0 or size > limit:
                            raise ImportFailure('Response exceeds size/quota limit')
                    data = response.read(limit + 1)
                    if len(data) > limit:
                        raise ImportFailure('Response exceeds size/quota limit')
                    encoding = response.headers.get('Content-Encoding', '').strip().lower()
                    if encoding == 'gzip' or data.startswith(b'\x1f\x8b'):
                        try:
                            with gzip.GzipFile(fileobj=io.BytesIO(data)) as compressed:
                                data = compressed.read(limit + 1)
                        except (OSError, EOFError, zlib.error) as exc:
                            raise ImportFailure('Invalid gzip response from ArcadeItalia') from exc
                        if len(data) > limit:
                            raise ImportFailure('Decompressed response exceeds size limit')
                    return data
            except HTTPError as exc:
                if exc.code in (301, 302, 303, 307, 308):
                    location = exc.headers.get('Location')
                    exc.close()
                    if not location or redirects >= 5:
                        raise ImportFailure('Invalid/excessive image redirect')
                    url = urljoin(url, location); allowed_url(url); redirects += 1
                    continue
                if exc.code in (403, 429):
                    self.stopped = 'Provider HTTP ' + str(exc.code) + '; network stopped (no retry)'
                    exc.close(); raise ServiceStopped(self.stopped)
                retry = exc.headers.get('Retry-After')
                code = exc.code
                exc.close()
                if code in (500, 502, 503, 504):
                    delay = 2 ** (retries + 1)
                    if retry:
                        try:
                            delay = max(delay, float(retry))
                        except ValueError:
                            try:
                                delay = max(delay, parsedate_to_datetime(retry).timestamp() - self.now())
                            except (ValueError, TypeError, OverflowError):
                                pass
                    if retries >= 2 or delay > 60:
                        self.stopped = 'Provider unavailable/retry window; network stopped'
                        raise ServiceStopped(self.stopped)
                    retries += 1; self.sleep(delay); continue
                raise ImportFailure('Provider HTTP ' + str(code)) from None
            except (URLError, TimeoutError, OSError, HTTPException) as exc:
                cause = exc.reason if isinstance(exc, URLError) else exc
                if isinstance(cause, ssl.SSLCertVerificationError):
                    raise ProviderCertificateError(
                        f'ArcadeItalia TLS certificate verification failed for {urlsplit(url).hostname}. '
                        'Configure a trusted CA bundle '
                        'for the artwork Python with SSL_CERT_FILE.'
                    ) from None
                raise ImportFailure('Network request failed: ' + str(exc)) from exc

    def api(self, function, name=None, cache=True):
        query = {'ajax': function}
        if name is not None:
            if not ROM.fullmatch(name):
                raise ImportFailure('Invalid query short name')
            query['game_name'] = name
        # June 2026: one ROM, never use_parent or resize input parameters.
        url = API + '?' + urlencode(query)
        key = 'ArcadeItalia/.cache/' + digest(url.encode()) + '.json'
        if cache and url in self.memory:
            return copy.deepcopy(self.memory[url])
        if cache:
            cache_path = self.files.path(key)
            try:
                saved = parse_json(cache_path.read_bytes()) if cache_path.exists() and cache_path.stat().st_size <= MAX_JSON else None
            except ImportFailure:
                saved = None
            stamp = saved.get('time') if isinstance(saved, dict) else None
            fresh = isinstance(stamp, (int, float)) and math.isfinite(stamp) and 0 <= self.now() - stamp < self.ttl
            if isinstance(saved, dict) and saved.get('url') == url and (self.offline or (not self.refresh and fresh)):
                if isinstance(saved.get('data'), dict):
                    self.memory[url] = saved['data']; return copy.deepcopy(saved['data'])
        data = parse_json(self._http(url, MAX_JSON))
        if not isinstance(data, dict) or type(data.get('release')) is not int or 'result' not in data:
            raise ImportFailure('Unexpected ArcadeItalia response envelope')
        if cache:
            self.memory[url] = data
            if self.files.apply:
                old = self.files.path(key)
                self.files.write(key, json_bytes({'url': url, 'time': self.now(), 'data': data}), digest(old.read_bytes()) if old.exists() else None)
        return copy.deepcopy(data)

    def image(self, url):
        allowed_url(url)
        if url in self.image_memory:
            return self.image_memory[url]
        path = 'ArcadeItalia/.cache/' + digest(url.encode()) + '.image'
        local = self.files.path(path)
        if local.exists() and (self.offline or (not self.refresh and self.now() - local.stat().st_mtime < self.ttl)):
            data = local.read_bytes()
            if len(data) <= MAX_IMAGE:
                self._remember_image(url, data); return data
        if not self.files.apply:
            raise ImportFailure('Image downloads require --apply')
        quota = self.api('download_status', cache=False)['result']
        if isinstance(quota, list) and len(quota) == 1:
            quota = quota[0]
        if not isinstance(quota, dict):
            raise ImportFailure('Unexpected download_status shape; refusing download')
        files, size = quota.get('download_limit_files'), quota.get('download_limit_bytes')
        if type(files) is not int or type(size) is not int:
            raise ImportFailure('Invalid download quota; refusing download')
        if files <= 0 or size <= 0:
            self.stopped = 'ArcadeItalia download quota exhausted; network stopped'
            raise ServiceStopped(self.stopped)
        data = self._http(url, min(MAX_IMAGE, size))
        self._remember_image(url, data)
        self.files.write(path, data, digest(local.read_bytes()) if local.exists() else None)
        return data

    def _remember_image(self, url, data):
        self.image_memory[url] = data
        while sum(len(value) for value in self.image_memory.values()) > 64 * 1024 * 1024:
            self.image_memory.pop(next(iter(self.image_memory)))


def records(envelope):
    result = envelope.get('result')
    if not isinstance(result, list) or any(not isinstance(row, dict) for row in result):
        raise ImportFailure('Expected API result array')
    return result


def exact(rows, name):
    matches = [row for row in rows if isinstance(row.get('game_name'), str) and row['game_name'].casefold() == name.casefold()]
    return matches[0] if len(matches) == 1 else None


def resized(data, bounds):
    try:
        from PIL import Image, ImageOps
    except ImportError as exc:
        raise ImportFailure('Install Pillow: python3 -m pip install -r tools/arcade-artwork-requirements.txt') from exc
    try:
        with warnings.catch_warnings():
            warnings.simplefilter('error', Image.DecompressionBombWarning)
            with Image.open(io.BytesIO(data)) as source:
                if source.width * source.height > 40_000_000:
                    raise ImportFailure('Image exceeds 40 million pixels')
                image = ImageOps.exif_transpose(source)
                image = image.convert('RGBA' if image.mode in ('RGBA', 'LA') or 'transparency' in image.info else 'RGB')
                image.thumbnail(bounds, Image.Resampling.LANCZOS)
                result = io.BytesIO(); image.save(result, format='PNG')
                return result.getvalue(), {'width': image.width, 'height': image.height, 'mode': image.mode}
    except (OSError, ValueError, Image.DecompressionBombError, Image.DecompressionBombWarning) as exc:
        raise ImportFailure('Invalid/oversized artwork: ' + str(exc)) from exc


def asset_urls(media, parent=None):
    def candidates(keys):
        result = []
        for origin in (media, parent):
            if not origin:
                continue
            for key in keys:
                url = origin.get(key)
                if isinstance(url, str) and url and not any(item['url'] == url for item in result):
                    result.append({'url': url, 'rom': origin.get('game_name'), 'field': key,
                                   'parent': origin is parent})
        return result
    result = {
        'logo': candidates(('url_image_logo', 'url_image_marquee')),
        'background': candidates(('url_image_flyer', 'url_image_artwork_preview', 'url_image_ingame')),
        'screenshot': candidates(('url_image_ingame', 'url_image_title')),
    }
    for role in ROLES:
        if role.startswith('extras/'):
            result[role] = candidates(('url_image_' + role.split('/')[1],))
    return result


def presentation_read(files):
    doc = files.read('Presentation.json', {'version': 1, 'games': []})
    if not isinstance(doc, dict) or type(doc.get('version')) is not int or doc['version'] != 1 or not isinstance(doc.get('games'), list):
        raise ImportFailure('Existing Presentation.json is invalid; preserve and repair it before importing')
    ids = set()
    for game in doc['games']:
        if not isinstance(game, dict):
            raise ImportFailure('Invalid presentation game')
        folder(game.get('id'))
        if game['id'].casefold() in ids:
            raise ImportFailure('Duplicate presentation game ID')
        ids.add(game['id'].casefold())
        for key in ('year', 'manufacturer', 'genre'):
            if key in game and game[key] is not None and not isinstance(game[key], str):
                raise ImportFailure('Presentation metadata fields must be strings')
    return doc


def existing_role(files, directory, role):
    path = files.path(directory + '/' + role)
    if not path.parent.exists():
        return []
    return [files.path(str(item.relative_to(files.root))).relative_to(files.root).as_posix()
            for item in path.parent.iterdir() if item.name.casefold() in
            {path.name.casefold() + ext for ext in ('.png', '.jpg', '.jpeg')}]


def import_catalog(catalog, output, *, apply=False, offline=False, refresh=False,
                   parent_artwork=False, platforms=('arcade', 'mame'), mapping=None, client_factory=Client,
                   progress=None):
    catalog = Path(catalog)
    doc, original_hash = catalog_read(catalog)
    files = LocalFiles(output, catalog, apply)
    state = files.read('ArcadeItalia/state.json', {'version': 1, 'assets': {}, 'metadata': {}})
    if not isinstance(state, dict) or state.get('version') != 1 or any(not isinstance(state.get(key), dict) for key in ('assets', 'metadata')):
        raise ImportFailure('Invalid importer state; refusing to replace it')
    if any(not isinstance(value, dict) for key in ('assets', 'metadata') for value in state[key].values()):
        raise ImportFailure('Invalid importer ownership entries')
    presentation = presentation_read(files)
    presentation_before = copy.deepcopy(presentation)
    original_presentation = files.path('Presentation.json')
    presentation_hash = digest(original_presentation.read_bytes()) if original_presentation.exists() else None
    state_path = files.path('ArcadeItalia/state.json')
    state_hash = digest(state_path.read_bytes()) if state_path.exists() else None
    rows = {row['id'].casefold(): row for row in presentation['games']}
    mapping = mapping or {}
    known_ids = {game['id'].casefold() for game in doc['games']}
    if len({key.casefold() for key in mapping}) != len(mapping) or any(key.casefold() not in known_ids or not isinstance(value, str) or not ROM.fullmatch(value) for key, value in mapping.items()):
        raise ImportFailure('Overrides must map existing game IDs to lowercase ROM short names')
    overrides = {key.casefold(): value for key, value in mapping.items()}
    allowed = {item.casefold() for item in platforms}
    report = {'version': 1, 'mode': 'apply' if apply else 'dry-run', 'source': CREDIT, 'copyright': RIGHTS,
              'catalog_sha256': original_hash, 'games': [], 'warnings': [], 'requests': 0}
    client = client_factory(files, refresh=refresh, offline=offline)
    lock = None
    if apply:
        lock_path = files.path('ArcadeItalia/import.lock')
        lock_path.parent.mkdir(parents=True, exist_ok=True)
        try:
            lock = os.open(lock_path, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
        except FileExistsError as exc:
            raise ImportFailure('Importer lock exists; another import or an interrupted run needs review') from exc
    try:
        total = sum(game['platform'].casefold() in allowed for game in doc['games'])
        active = 0
        for game in doc['games']:
            item = {'id': game['id'], 'catalog_title': game['title'], 'assets': {}, 'warnings': []}
            report['games'].append(item)
            if game['platform'].casefold() not in allowed:
                item['status'] = 'skipped-platform'; continue
            active += 1
            if progress is not None:
                progress(active, total, game['id'], client.requests)
            try:
                name = shortname(game['rom'])
                item['rom'] = name
                name = overrides.get(game['id'].casefold(), name)
                item['query_rom'] = name
                envelope = client.api('query_mame', name)
                item['api_release'] = envelope['release']
                result = records(envelope)
                match = exact(result, name)
                if match is None:
                    item['status'] = 'ambiguous' if result else 'not-found'
                    suggestions = result or records(client.api('query_mame_like', name))
                    item['suggestions'] = [{'rom': row.get('game_name'), 'title': row.get('title')} for row in suggestions[:25]]
                    continue
                item['status'] = 'matched'
                item['metadata'] = {key: match[key] for key in FIELDS if key in match}
                item['metadata_missing'] = [key for key in ('title', 'year', 'manufacturer', 'genre', 'players') if match.get(key) in (None, '')]
                item['source_rom'] = match['game_name']
                item['cloneof'] = match.get('cloneof') or None
                # Merge only fields the current frontend actually reads; rich data stays in report.
                target = rows.get(game['id'].casefold())
                if target is None:
                    target = {'id': game['id']}; presentation['games'].append(target); rows[game['id'].casefold()] = target
                managed = state['metadata'].setdefault(game['id'].casefold(), {})
                for key in ('year', 'manufacturer', 'genre'):
                    value = match.get(key)
                    if value is None or value == '':
                        continue
                    value = str(value)
                    if key not in target or (refresh and key in managed and target[key] == managed[key]):
                        target[key] = value; managed[key] = value
                try:
                    media = exact(records(client.api('query_mame_media', name)), name)
                    if media is None:
                        item['warnings'].append('No exact media match; using exact metadata response media only')
                        media = match
                    else:
                        media = {**match, **media}
                except ServiceStopped:
                    raise
                except ImportFailure as exc:
                    media = match; item['warnings'].append('Media lookup failed: ' + str(exc))
                parent = None
                if parent_artwork and item['cloneof']:
                    pname = item['cloneof']
                    if isinstance(pname, str) and ROM.fullmatch(pname) and pname != name:
                        try:
                            parent = exact(records(client.api('query_mame_media', pname)), pname)
                        except ServiceStopped:
                            raise
                        except ImportFailure as exc:
                            item['warnings'].append('Parent media failed: ' + str(exc))
                directory = 'Artwork/games/' + folder(game['id'])
                for role, candidates in asset_urls(media, parent).items():
                    found = existing_role(files, directory, role)
                    asset = {'candidates': candidates}
                    item['assets'][role] = asset
                    if found:
                        owned = len(found) == 1 and isinstance(state['assets'].get(found[0]), dict) and digest(files.path(found[0]).read_bytes()) == state['assets'][found[0]].get('sha256')
                        if not owned or not refresh:
                            asset.update(status='preserved-managed' if owned else 'preserved-custom', paths=found)
                            continue
                    if not candidates:
                        asset['status'] = 'missing'; continue
                    if not apply:
                        asset['status'] = 'would-download'; continue
                    errors = []
                    for candidate in candidates:
                        try:
                            data, dimensions = resized(client.image(candidate['url']), ROLES[role])
                            path = directory + '/' + role + '.png'
                            existing = files.path(path)
                            expected = state['assets'].get(path, {}).get('sha256') if existing.exists() else None
                            files.write(path, data, expected)
                            state['assets'][path] = {**candidate, **dimensions, 'sha256': digest(data), 'source': CREDIT, 'copyright': RIGHTS}
                            # Persist ownership immediately so interrupted imports can resume safely.
                            files.write('ArcadeItalia/state.json', json_bytes(state), state_hash)
                            state_hash = digest(json_bytes(state))
                            asset.update(status='written', path=path, **dimensions, source=candidate)
                            break
                        except ServiceStopped:
                            raise
                        except ImportFailure as exc:
                            errors.append(str(exc))
                    else:
                        asset['status'] = 'failed'
                    if errors:
                        asset['errors'] = errors
            except UnsupportedRom as exc:
                item['status'] = 'skipped-unsupported-rom'; item['warnings'].append(str(exc))
            except ServiceStopped as exc:
                item['status'] = 'service-stopped'; item['warnings'].append(str(exc))
            except (ImportFailure, OSError) as exc:
                item['status'] = 'error'; item['warnings'].append(str(exc))
        if digest(catalog.read_bytes()) != original_hash:
            raise ImportFailure('Input catalog changed during import; refusing final metadata write')
        report['requests'] = client.requests
        summary = {'games': {}, 'assets': {}}
        for item in report['games']:
            status = item['status']
            summary['games'][status] = summary['games'].get(status, 0) + 1
            if status not in ('skipped-platform', 'skipped-unsupported-rom'):
                for role in ROLES:
                    item['assets'].setdefault(role, {'status': 'not-processed'})
            for asset in item['assets'].values():
                status = asset['status']
                summary['assets'][status] = summary['assets'].get(status, 0) + 1
        report['summary'] = summary
        report['managed_sources'] = state['assets']
        report['presentation'] = presentation
        if apply:
            if presentation_hash is None or presentation != presentation_before:
                files.write('Presentation.json', json_bytes(presentation), presentation_hash)
            files.write('ArcadeItalia/state.json', json_bytes(state), state_hash)
            filename = 'ArcadeItalia/reports/' + datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ') + '-' + uuid.uuid4().hex[:8] + '.json'
            report['report_path'] = filename
            files.write(filename, json_bytes(report))
            credits = 'ArcadeItalia/CREDITS.txt'
            if not files.path(credits).exists():
                files.write(credits, (CREDIT + '\n' + RIGHTS + '\nPer-asset source URLs and per-game metadata are recorded in state.json and reports/.\nHistory text is not exported or displayed.\n').encode('utf-8'))
        return report
    finally:
        if lock is not None:
            os.close(lock); files.path('ArcadeItalia/import.lock').unlink()


def progress_reporter():
    last = None

    def report(index, total, game, requests):
        nonlocal last
        now = time.monotonic()
        if last is None or now - last >= 10 or index == total:
            print(f'ArcadeItalia: checking game {index}/{total} ({game}), {requests} online requests so far.',
                  file=sys.stderr, flush=True)
            last = now

    return report


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('catalog', type=Path)
    parser.add_argument('--output', type=Path, required=True, help='LocalState-shaped staging directory (never the Xbox)')
    parser.add_argument('--apply', action='store_true', help='Enable local writes and image downloads; default is dry-run')
    parser.add_argument('--offline', action='store_true', help='Use existing caches only; no network')
    parser.add_argument('--refresh', action='store_true', help='Refresh cache and unchanged importer-owned fields/assets only')
    parser.add_argument('--parent-artwork', action='store_true', help='Allow labeled one-level clone-parent artwork fallback')
    parser.add_argument('--platform', action='append', help='Exact arcade platform ID; repeatable; defaults arcade,mame')
    parser.add_argument('--mapping', type=Path, help='Explicit JSON game-ID -> ROM short-name overrides; no fuzzy auto-match')
    parser.add_argument('--interval', type=float, default=1.0, help='Seconds between requests; minimum 1')
    parser.add_argument('--progress', action='store_true', help='Report progress to stderr during import')
    args = parser.parse_args(argv)
    try:
        if not 1 <= args.interval <= 60:
            raise ImportFailure('--interval must be between 1 and 60 seconds')
        if args.apply:
            try:
                from PIL import Image  # Fail before contacting provider or downloading images.
            except ImportError as exc:
                raise ImportFailure('Install Pillow: python3 -m pip install -r tools/arcade-artwork-requirements.txt') from exc
        mapping = parse_json(args.mapping.read_bytes()) if args.mapping else None
        if mapping is not None and not isinstance(mapping, dict):
            raise ImportFailure('Mapping must be a JSON object')
        report = import_catalog(args.catalog, args.output, apply=args.apply, offline=args.offline,
                                refresh=args.refresh, parent_artwork=args.parent_artwork,
                                platforms=args.platform or ('arcade', 'mame'), mapping=mapping,
                                client_factory=lambda files, **kw: Client(files, interval=args.interval, **kw),
                                progress=progress_reporter() if args.progress else None)
        print(json.dumps(report, ensure_ascii=False, indent=2, allow_nan=False))
        for game in report['games'] if not args.progress else ():
            roles = ', '.join(role + '=' + game['assets'].get(role, {}).get('status', 'n/a') for role in ('logo', 'background', 'screenshot'))
            print(game['id'] + ': ' + game['status'] + '; ' + roles, file=sys.stderr)
        failed = any(game['status'] in ('error', 'service-stopped', 'not-found', 'ambiguous') or any(asset.get('status') == 'failed' for asset in game['assets'].values()) for game in report['games'])
        return 2 if failed else 0
    except (ImportFailure, ProviderCertificateError, OSError) as exc:
        print('ERROR: ' + str(exc), file=sys.stderr); return 1


if __name__ == '__main__':
    raise SystemExit(main())
