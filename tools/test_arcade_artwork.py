"""Offline contract, HTTP and real temporary-filesystem tests for arcade_artwork."""
import copy
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from urllib.error import HTTPError, URLError

import arcade_artwork as art

try:
    from PIL import Image
except ImportError:
    Image = None


def sample():
    return {'version': 1, 'contentRoot': r'U:\roms',
            'platforms': [{'id': 'arcade', 'title': 'Arcade', 'defaultCore': 'mame2003_plus_libretro.dll'}],
            'games': [{'id': 'arcade-1941', 'platform': 'arcade', 'title': 'My custom title', 'rom': 'arcade/1941.zip'}]}


def remote(name='1941', **changes):
    data = {'game_name': name, 'title': '1941: Counter Attack', 'year': '1990',
            'manufacturer': 'Capcom', 'genre': 'Shooter', 'players': 2, 'cloneof': '',
            'url': 'https://adb.arcadeitalia.net/?mame=' + name,
            'url_image_logo': 'https://adb.arcadeitalia.net/?mame=' + name + '&type=logo',
            'url_image_flyer': 'https://adb.arcadeitalia.net/?mame=' + name + '&type=flyer',
            'url_image_ingame': 'https://adb.arcadeitalia.net/?mame=' + name + '&type=ingame'}
    data.update(changes)
    return data


class FakeClient:
    def __init__(self, files, **kwargs):
        self.requests = 0
        self.calls = []
        self.image_calls = []
        self.responses = {}
        self.images = {}

    def api(self, function, name=None, **kwargs):
        self.requests += 1; self.calls.append((function, name))
        value = self.responses.get((function, name), [remote(name)])
        if isinstance(value, Exception):
            raise value
        return {'release': 2 if function == 'query_mame_media' else 6, 'result': copy.deepcopy(value)}

    def image(self, url):
        self.requests += 1; self.image_calls.append(url)
        value = self.images.get(url, b'raw image')
        if isinstance(value, Exception):
            raise value
        return value


class ImportTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.catalog = self.root / 'InternalCatalog.json'
        self.catalog.write_bytes(art.json_bytes(sample()))
        self.original = self.catalog.read_bytes()
        self.output = self.root / 'LocalState'
        self.client = FakeClient(None)
        self.render = patch.object(art, 'resized', side_effect=lambda data, bounds: (b'png:' + data + str(bounds).encode(), {'width': 320, 'height': 100, 'mode': 'RGBA'}))
        self.render.start(); self.addCleanup(self.render.stop)

    def run_import(self, **kwargs):
        return art.import_catalog(self.catalog, self.output, client_factory=lambda *a, **k: self.client, **kwargs)

    def write(self, name, value):
        path = self.output / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(art.json_bytes(value) if not isinstance(value, bytes) else value)

    def test_dry_run_resolves_without_writes_or_asset_downloads(self):
        report = self.run_import()
        self.assertFalse(self.output.exists())
        self.assertEqual(self.client.image_calls, [])
        self.assertEqual(report['games'][0]['metadata']['players'], 2)
        self.assertEqual(report['games'][0]['assets']['logo']['status'], 'would-download')
        self.assertEqual(self.catalog.read_bytes(), self.original)

    def test_apply_generates_frontend_paths_and_supported_metadata(self):
        report = self.run_import(apply=True)
        for role in ('logo', 'background', 'screenshot'):
            self.assertTrue((self.output / 'Artwork/games/arcade-1941' / (role + '.png')).is_file())
        doc = art.parse_json((self.output / 'Presentation.json').read_bytes())
        self.assertEqual(doc, {'version': 1, 'games': [{'id': 'arcade-1941', 'year': '1990', 'manufacturer': 'Capcom', 'genre': 'Shooter'}]})
        self.assertEqual(report['games'][0]['metadata']['title'], '1941: Counter Attack')
        self.assertEqual(report['games'][0]['catalog_title'], 'My custom title')
        self.assertEqual(self.catalog.read_bytes(), self.original)
        self.assertFalse((self.output / 'ArcadeItalia/import.lock').exists())
        self.assertIn('motoschifo', (self.output / 'ArcadeItalia/CREDITS.txt').read_text())

    def test_custom_artwork_all_extensions_and_case_preserved(self):
        self.write('Artwork/games/arcade-1941/Logo.JPG', b'custom logo')
        report = self.run_import(apply=True, refresh=True)
        self.assertEqual(report['games'][0]['assets']['logo']['status'], 'preserved-custom')
        self.assertEqual((self.output / 'Artwork/games/arcade-1941/Logo.JPG').read_bytes(), b'custom logo')
        self.assertFalse((self.output / 'Artwork/games/arcade-1941/logo.png').exists())

    def test_manual_metadata_and_unknown_fields_and_other_games_preserved(self):
        existing = {'version': 1, 'custom': 'keep', 'games': [{'id': 'ARCADE-1941', 'year': '1991?', 'genre': '', 'notes': 'hand edited'}, {'id': 'other', 'manufacturer': 'Manual'}]}
        self.write('Presentation.json', existing)
        report = self.run_import(apply=True, refresh=True)
        out = report['presentation']
        self.assertEqual(out['custom'], 'keep')
        self.assertEqual(out['games'][0]['year'], '1991?')
        self.assertEqual(out['games'][0]['genre'], '')
        self.assertEqual(out['games'][0]['notes'], 'hand edited')
        self.assertEqual(out['games'][1], existing['games'][1])

    def test_unchanged_owned_assets_incremental_no_download(self):
        self.run_import(apply=True)
        self.client.image_calls.clear()
        report = self.run_import(apply=True)
        self.assertEqual(self.client.image_calls, [])
        self.assertEqual(report['games'][0]['assets']['logo']['status'], 'preserved-managed')

    def test_refresh_updates_owned_but_not_manually_edited_fields_or_art(self):
        self.run_import(apply=True)
        self.write('Artwork/games/arcade-1941/logo.png', b'manual replacement')
        path = self.output / 'Presentation.json'
        doc = art.parse_json(path.read_bytes()); doc['games'][0]['genre'] = 'Hand entered'
        path.write_bytes(art.json_bytes(doc))
        self.client.responses[('query_mame', '1941')] = [remote(year='1992', genre='New genre')]
        report = self.run_import(apply=True, refresh=True)
        self.assertEqual(report['presentation']['games'][0]['year'], '1992')
        self.assertEqual(report['presentation']['games'][0]['genre'], 'Hand entered')
        self.assertEqual(report['games'][0]['assets']['logo']['status'], 'preserved-custom')

    def test_failed_download_continues_to_marquee_fallback(self):
        data = remote(url_image_marquee='https://adb.arcadeitalia.net/?type=marquee')
        self.client.responses[('query_mame_media', '1941')] = [data]
        self.client.images[data['url_image_logo']] = art.ImportFailure('404')
        report = self.run_import(apply=True)
        self.assertEqual(report['games'][0]['assets']['logo']['source']['field'], 'url_image_marquee')
        self.assertTrue(report['games'][0]['assets']['logo']['errors'])

    def test_missing_media_is_reported_not_fabricated(self):
        self.client.responses[('query_mame', '1941')] = [remote(url_image_logo='', url_image_flyer='', url_image_ingame='')]
        self.client.responses[('query_mame_media', '1941')] = self.client.responses[('query_mame', '1941')]
        report = self.run_import(apply=True)
        self.assertTrue(all(asset['status'] == 'missing' for asset in report['games'][0]['assets'].values()))
        self.assertFalse((self.output / 'Artwork').exists())

    def test_unknown_rom_suggestions_never_auto_match(self):
        self.client.responses[('query_mame', '1941')] = []
        self.client.responses[('query_mame_like', '1941')] = [remote('1941j')]
        report = self.run_import(apply=True)
        self.assertEqual(report['games'][0]['status'], 'not-found')
        self.assertEqual(report['games'][0]['suggestions'][0]['rom'], '1941j')
        self.assertEqual(report['presentation']['games'], [])

    def test_ambiguous_or_nonexact_response_is_not_used(self):
        self.client.responses[('query_mame', '1941')] = [remote('1941j'), remote('1941u')]
        self.assertEqual(self.run_import()['games'][0]['status'], 'ambiguous')
        self.client.responses[('query_mame', '1941')] = [remote(), remote()]
        self.assertEqual(self.run_import()['games'][0]['status'], 'ambiguous')

    def test_clone_metadata_stays_child_parent_art_explicit_and_labeled(self):
        child = remote(cloneof='1941parent', url_image_logo='', url_image_flyer='', url_image_ingame='')
        self.client.responses[('query_mame', '1941')] = [child]
        self.client.responses[('query_mame_media', '1941')] = [child]
        first = self.run_import()
        self.assertEqual(first['games'][0]['assets']['logo']['status'], 'missing')
        self.assertNotIn(('query_mame_media', '1941parent'), self.client.calls)
        report = self.run_import(apply=True, parent_artwork=True)
        self.assertTrue(report['games'][0]['assets']['logo']['source']['parent'])
        self.assertEqual(report['games'][0]['metadata']['year'], child['year'])

    def test_explicit_legacy_rom_mapping(self):
        report = self.run_import(mapping={'arcade-1941': '1941j'})
        self.assertEqual(report['games'][0]['rom'], '1941')
        self.assertEqual(report['games'][0]['query_rom'], '1941j')
        with self.assertRaises(art.ImportFailure):
            self.run_import(mapping={'missing-id': '1941'})

    def test_non_arcade_and_nonarchive_are_skipped_or_reported(self):
        doc = sample(); doc['games'][0]['rom'] = '1941.chd'
        self.catalog.write_bytes(art.json_bytes(doc))
        self.assertEqual(self.run_import()['games'][0]['status'], 'error')
        self.assertEqual(self.run_import(platforms=('other',))['games'][0]['status'], 'skipped-platform')

    def test_malformed_existing_presentation_stops_before_writes(self):
        self.write('Presentation.json', b'broken')
        with self.assertRaises(art.ImportFailure):
            self.run_import(apply=True)
        self.assertEqual((self.output / 'Presentation.json').read_bytes(), b'broken')
        self.assertFalse((self.output / 'ArcadeItalia').exists())

    def test_lock_prevents_concurrent_apply_and_is_never_removed(self):
        self.write('ArcadeItalia/import.lock', b'existing run')
        with self.assertRaises(art.ImportFailure):
            self.run_import(apply=True)
        self.assertEqual((self.output / 'ArcadeItalia/import.lock').read_bytes(), b'existing run')

    def test_input_catalog_cannot_be_overwritten_via_output_alias(self):
        files = art.LocalFiles(self.root, self.catalog, True)
        with self.assertRaises(art.ImportFailure):
            files.write('InternalCatalog.json', b'replace')
        self.assertEqual(self.catalog.read_bytes(), self.original)

    def test_atomic_write_rejects_changed_target(self):
        files = art.LocalFiles(self.output, self.catalog, True)
        self.write('manual.txt', b'custom')
        with self.assertRaises(art.ImportFailure):
            files.write('manual.txt', b'overwrite', 'old hash')
        self.assertEqual((self.output / 'manual.txt').read_bytes(), b'custom')

    def test_symlink_output_refused(self):
        outside = self.root / 'outside'; outside.mkdir()
        self.output.mkdir()
        try:
            (self.output / 'Artwork').symlink_to(outside, target_is_directory=True)
        except OSError:
            self.skipTest('OS does not permit symlink creation')
        with self.assertRaises(art.ImportFailure):
            art.LocalFiles(self.output, self.catalog, True).path('Artwork/games/test/logo.png')

    def test_case_collision_refused_on_case_sensitive_filesystems(self):
        self.write('a.txt', b'a'); self.write('A.txt', b'b')
        if len(list(self.output.iterdir())) != 2:
            self.skipTest('Case insensitive filesystem')
        with self.assertRaises(art.ImportFailure):
            art.LocalFiles(self.output, self.catalog).path('a.txt')

    def test_existing_cabinet_input_rom_and_diagnostics_untouched(self):
        for path in ('CabinetInput.json', 'Startup.log', 'roms/1941.zip', 'Artwork/custom.png'):
            self.write(path, b'keep me')
        self.run_import(apply=True)
        for path in ('CabinetInput.json', 'Startup.log', 'roms/1941.zip', 'Artwork/custom.png'):
            self.assertEqual((self.output / path).read_bytes(), b'keep me')


class PathTests(unittest.TestCase):
    def test_shortname_uses_rom_not_catalog_title_and_both_path_styles(self):
        for path in ('1941.zip', 'Arcade/1941.ZIP', r'U:\Users\roms\1941.zip', '1941.7z'):
            self.assertEqual(art.shortname(path), '1941')
        for path in ('1941 (World).zip', '../folder/', '1941.chd', '1941.zip?x=1', ''):
            with self.subTest(path=path), self.assertRaises(art.ImportFailure):
                art.shortname(path)

    def test_folder_matches_premium_resolver_device_encoding(self):
        self.assertEqual(art.folder('arcade-1941'), 'arcade-1941')
        self.assertEqual(art.folder('CON'), '~636F6E')
        self.assertEqual(art.folder('con'), '~636F6E')
        self.assertNotEqual(art.folder('_id_636F6E'), art.folder('CON'))
        for value in ('../escape', 'C:\\root', 'a/b', 'a.b', '~636F6E', ''):
            with self.subTest(value=value), self.assertRaises(art.ImportFailure):
                art.folder(value)

    def test_duplicate_json_keys_and_nonfinite_values_rejected(self):
        for data in (b'{"version":1,"version":2}', b'{"x":NaN}', b'{} {}'):
            with self.assertRaises(art.ImportFailure):
                art.parse_json(data)


class Response(io.BytesIO):
    def __init__(self, data, headers=None):
        super().__init__(data); self.headers = headers or {}


class Opener:
    def __init__(self, *responses):
        self.responses = list(responses); self.requests = []
    def open(self, request, timeout):
        self.requests.append(request)
        response = self.responses.pop(0)
        if isinstance(response, Exception):
            raise response
        return response


class HttpTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        root = Path(self.temp.name)
        self.catalog = root / 'catalog.json'; self.catalog.write_text('{}')
        self.files = art.LocalFiles(root / 'output', self.catalog, True)
        self.seconds = 0
    def sleep(self, delay):
        self.seconds += delay
    def client(self, opener, **kwargs):
        return art.Client(self.files, opener=opener, sleep=self.sleep, clock=lambda: self.seconds, **kwargs)
    def envelope(self, result, release=6):
        return Response(art.json_bytes({'release': release, 'result': result}))

    def test_june2026_single_rom_and_no_removed_parameters(self):
        opener = Opener(self.envelope([remote()]))
        self.client(opener).api('query_mame', '1941')
        self.assertEqual(opener.requests[0].full_url, art.API + '?ajax=query_mame&game_name=1941')

    def test_metadata_cached_and_offline_reused_without_requests(self):
        self.client(Opener(self.envelope([remote()]))).api('query_mame', '1941')
        opener = Opener()
        result = self.client(opener, offline=True).api('query_mame', '1941')
        self.assertEqual(result['result'][0]['game_name'], '1941')
        self.assertEqual(opener.requests, [])

    def test_dry_run_http_metadata_does_not_write_cache(self):
        self.files.apply = False
        self.client(Opener(self.envelope([remote()]))).api('query_mame', '1941')
        self.assertFalse(self.files.root.exists())

    def test_quota_checked_before_download_and_no_removed_url_rewrite(self):
        url = 'https://adb.arcadeitalia.net/?mame=1941&type=logo&resize=0'
        opener = Opener(self.envelope({'download_limit_files': 2, 'download_limit_bytes': 100}, 1), Response(b'image'))
        self.assertEqual(self.client(opener).image(url), b'image')
        self.assertIn('ajax=download_status', opener.requests[0].full_url)
        self.assertEqual(opener.requests[1].full_url, url)
        self.assertGreaterEqual(self.seconds, 1)

    def test_zero_quota_stops_entire_network(self):
        opener = Opener(self.envelope({'download_limit_files': 0, 'download_limit_bytes': 100}, 1))
        client = self.client(opener)
        with self.assertRaises(art.ServiceStopped):
            client.image('https://adb.arcadeitalia.net/logo.png')
        with self.assertRaises(art.ServiceStopped):
            client.api('query_mame', '1941')
        self.assertEqual(len(opener.requests), 1)

    def test_invalid_quota_fail_closed(self):
        opener = Opener(self.envelope({'download_limit_files': 'unlimited', 'download_limit_bytes': -1}, 1))
        with self.assertRaises(art.ImportFailure):
            self.client(opener).image('https://adb.arcadeitalia.net/logo.png')
        self.assertEqual(len(opener.requests), 1)

    def test_429_and_403_not_retried(self):
        for code in (429, 403):
            opener = Opener(HTTPError(art.API, code, 'stop', {'Retry-After': '600'}, io.BytesIO()))
            client = self.client(opener)
            with self.assertRaises(art.ServiceStopped):
                client.api('query_mame', '1941')
            with self.assertRaises(art.ServiceStopped):
                client.api('query_mame', '1942')
            self.assertEqual(len(opener.requests), 1)

    def test_503_retry_after_honored(self):
        opener = Opener(HTTPError(art.API, 503, 'maintenance', {'Retry-After': '5'}, io.BytesIO()), self.envelope([remote()]))
        self.client(opener).api('query_mame', '1941')
        self.assertGreaterEqual(self.seconds, 5)

    def test_long_retry_after_stops_without_early_retry(self):
        opener = Opener(HTTPError(art.API, 503, 'maintenance', {'Retry-After': '3600'}, io.BytesIO()))
        with self.assertRaises(art.ServiceStopped):
            self.client(opener).api('query_mame', '1941')
        self.assertEqual(len(opener.requests), 1)

    def test_no_external_redirect_or_tls_downgrade(self):
        for destination in ('https://example.com/x', 'http://adb.arcadeitalia.net/x'):
            opener = Opener(HTTPError(art.API, 302, 'redirect', {'Location': destination}, io.BytesIO()))
            with self.assertRaises(art.ImportFailure):
                self.client(opener).api('query_mame', '1941')
            self.assertEqual(len(opener.requests), 1)

    def test_html_and_wrong_response_shape_rejected(self):
        for body in (b'<html>Error</html>', b'{"release":6}', b'[]'):
            with self.assertRaises(art.ImportFailure):
                self.client(Opener(Response(body))).api('query_mame', '1941')

    def test_oversized_response_rejected(self):
        opener = Opener(Response(b'', {'Content-Length': str(art.MAX_JSON + 1)}))
        with self.assertRaises(art.ImportFailure):
            self.client(opener).api('query_mame', '1941')

    def test_offline_cache_miss_no_network(self):
        opener = Opener()
        with self.assertRaises(art.ImportFailure):
            self.client(opener, offline=True).api('query_mame', '1941')
        self.assertEqual(opener.requests, [])

    def test_network_failure_reports_without_traceback(self):
        with self.assertRaises(art.ImportFailure):
            self.client(Opener(URLError('offline'))).api('query_mame', '1941')


@unittest.skipUnless(Image is not None, 'Install Pillow to run image-processing tests')
class ImageTests(unittest.TestCase):
    def image(self, mode, size, color, **kwargs):
        result = io.BytesIO()
        Image.new(mode, size, color).save(result, format='PNG', **kwargs)
        return result.getvalue()
    def test_transparent_logo_aspect_ratio_and_alpha_preserved(self):
        result, info = art.resized(self.image('RGBA', (2400, 600), (255, 0, 0, 64)), (1200, 400))
        self.assertEqual((info['width'], info['height']), (1200, 300))
        with Image.open(io.BytesIO(result)) as image:
            self.assertEqual(image.getpixel((0, 0))[3], 64)
    def test_portrait_flyer_not_stretched_or_cropped(self):
        _, info = art.resized(self.image('RGB', (1000, 2000), 'red'), (1920, 1080))
        self.assertEqual((info['width'], info['height']), (540, 1080))
    def test_small_screenshot_not_upscaled(self):
        _, info = art.resized(self.image('RGB', (224, 384), 'blue'), (1000, 800))
        self.assertEqual((info['width'], info['height']), (224, 384))
    def test_palette_transparency_preserved(self):
        result, info = art.resized(self.image('P', (20, 10), 0, transparency=0), (1200, 400))
        self.assertEqual(info['mode'], 'RGBA')
        with Image.open(io.BytesIO(result)) as image:
            self.assertEqual(image.getpixel((0, 0))[3], 0)
    def test_corrupt_image_rejected(self):
        with self.assertRaises(art.ImportFailure):
            art.resized(b'<html>quota reached</html>', (1200, 400))


if __name__ == '__main__':
    unittest.main()
