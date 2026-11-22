"""Entirely mocked Portal transport; these tests never contact an Xbox."""
import copy
import errno
from email.message import Message
from email.parser import BytesParser
from email.policy import default
from http.client import RemoteDisconnected
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
from urllib.error import HTTPError, URLError
from urllib.parse import parse_qs, urlsplit
from urllib.request import BaseHandler
from urllib.response import addinfourl
from zipfile import ZipFile

import arcade_sync as sync

RA = 'RetroArch_1.0.0.0_x64__test'
LP = sync.LAUNCHPASS_IDENTITY + '_2.1.8.0_x64__test'
FAMILY = 'RetroArch_test'
ROOT = r'U:\Users\UserMgr0\AppData\Local\Packages\RetroArch_test\LocalState\roms'
DIRECTORY = r'LocalState\roms\arcade'


def seed():
    return {'version': 1, 'contentRoot': ROOT,
            'platforms': [{'id': 'arcade', 'title': 'Arcade', 'defaultCore': 'mame2003_plus_libretro.dll',
                           'notes': {'manual': True}}],
            'games': [{'id': 'arcade-1941', 'platform': 'arcade', 'title': '1941: manual title',
                       'rom': 'arcade/1941.zip', 'core': 'fbneo_libretro.dll',
                       'artwork': 'Artwork/1941.png', 'notes': {'rating': 5}}],
            'manualMetadata': {'owner': 'synthetic'}}


class MockXbox(BaseHandler):
    handler_order = 100
    def __init__(self):
        self.calls, self.posts = [], []
        self.upload_names = []
        self.packages = {'InstalledPackages': [
            {'PackageFullName': RA, 'PackageFamilyName': FAMILY, 'Name': 'RetroArch'},
            {'PackageFullName': LP, 'PackageFamilyName': sync.LAUNCHPASS_IDENTITY + '_test', 'Name': 'LaunchPass'}]}
        self.files = {(RA, r'LocalState\roms'): {}, (RA, DIRECTORY): {}, (LP, 'LocalState'): {}}
        self.auth_status = None
        self.bad_listing = None
        self.interrupt = False
        self.bad_size = False
        self.redirect = False
        self.refresh_once = False
        self.remote_edit_after_rom = False
        self.post_status = None

    def https_open(self, request):
        self.calls.append(request)
        path = urlsplit(request.full_url).path
        query = {k: v[0] for k, v in parse_qs(urlsplit(request.full_url).query).items()}
        headers = Message()
        def error(status):
            raise HTTPError(request.full_url, status, 'secret message', headers, io.BytesIO(b'secret server body'))
        status = 200
        if self.auth_status:
            error(self.auth_status)
        if self.redirect:
            headers['Location'] = 'https://other.invalid/'
            response = addinfourl(io.BytesIO(b''), headers, request.full_url, 302)
            response.msg = 'redirect'
            return response
        if path == '/api/os/info':
            headers['Set-Cookie'] = 'CSRF-Token=synthetic-csrf; Path=/; Secure'
            body = b'{}'
        elif path == sync.PACKAGES:
            body = json.dumps(self.packages).encode()
        else:
            key = (query['packagefullname'], query['path'].lstrip('\\'))
            if key not in self.files:
                error(404)
            files = self.files[key]
            if path == sync.FILES:
                if self.refresh_once:
                    self.refresh_once = False
                    error(403)
                listing = {'Items': [{'Name': name, 'FileSize': len(data), 'Type': 32} for name, data in files.items()]}
                body = json.dumps(self.bad_listing if self.bad_listing is not None else listing).encode()
            elif request.get_method() == 'GET':
                if query['filename'] not in files:
                    error(404)
                body = files[query['filename']]
            else:
                self.posts.append(request)
                if self.post_status:
                    error(self.post_status)
                assert request.get_header('X-csrf-token') == 'synthetic-csrf'
                if self.interrupt:
                    next(iter(request.data))
                    raise URLError('synthetic secret interrupted')
                data = b''.join(request.data)
                assert len(data) == int(request.get_header('Content-length'))
                message = BytesParser(policy=default).parsebytes(
                    ('Content-Type: ' + request.get_header('Content-type') + '\r\nMIME-Version: 1.0\r\n\r\n').encode() + data)
                parts = list(message.iter_parts())
                assert len(parts) == 1 and parts[0].get_param('name', header='content-disposition') == 'file'
                name = parts[0].get_filename()
                self.upload_names.append(name)
                payload = parts[0].get_payload(decode=True)
                if query['extract'] == 'true':
                    assert key == (LP, 'LocalState')
                    with ZipFile(io.BytesIO(payload)) as bundle:
                        for member in bundle.namelist():
                            directory, filename = member.replace('/', '\\').rsplit('\\', 1)
                            self.files.setdefault((LP, 'LocalState\\' + directory), {})[filename] = bundle.read(member)
                else:
                    assert query['extract'] == 'false'
                    files[name] = b'wrong size' if self.bad_size else payload
                if self.remote_edit_after_rom and key[0] == RA:
                    edited = seed()
                    edited['games'][0]['title'] = 'Edited during transfer'
                    self.files[LP, 'LocalState'][sync.CATALOG] = sync.encode_catalog(edited)
                status, body = 201, b'{}'
        response = addinfourl(io.BytesIO(body), headers, request.full_url, status)
        response.msg = 'synthetic'
        return response


class SyncTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        (self.root / 'arcade').mkdir()
        (self.root / 'arcade/1941.zip').write_bytes(b'PK\x03\x04synthetic-one')
        (self.root / 'arcade/1942.zip').write_bytes(b'PK\x03\x04synthetic-two')
        self.device = MockXbox()
        self.portal = sync.SyncPortal('https://xbox.invalid:11443', username='synthetic-user', password='synthetic-pass', apply=True)
        self.portal.opener.add_handler(self.device)
        self.options = dict(seed=seed(), root=self.root, retroarch=RA, launchpass=LP,
                            identity=sync.LAUNCHPASS_IDENTITY, defaults={}, overrides={},
                            platform_dirs={'arcade': 'arcade'}, extensions={'.zip'})

    def plan(self):
        return sync.build_plan(self.portal, **self.options)

    def test_default_plan_get_only_and_preserves_metadata(self):
        original = copy.deepcopy(self.options['seed'])
        plan = self.plan()
        self.assertEqual(len(plan.uploads), 2)
        self.assertEqual(plan.catalog['games'][0], original['games'][0])
        self.assertEqual(plan.catalog['manualMetadata'], original['manualMetadata'])
        self.assertEqual(self.options['seed'], original)
        with self.assertRaises(sync.SyncError):
            sync.merge_fields({'custom': 1}, {'custom': True}, 'test')
        self.assertTrue(all(r.get_method() == 'GET' for r in self.device.calls))
        self.assertEqual(self.device.posts, [])
        second = self.plan()
        self.assertEqual(plan.catalog, second.catalog)
        self.assertEqual(plan.report['remote_deletions'], [])

    def test_filename_size_comparison_and_no_remote_deletion(self):
        data = self.device.files[RA, DIRECTORY]
        data['1941.ZIP'] = (self.root / 'arcade/1941.zip').read_bytes()
        data['1942.zip'] = b'old'
        data['remote-only.zip'] = b'keep'
        plan = self.plan()
        self.assertEqual(plan.report['unchanged_roms'], ['arcade/1941.zip'])
        self.assertEqual(plan.uploads[0]['reason'], 'size_changed')
        sync.apply_plan(self.portal, plan, self.options)
        self.assertEqual(data['remote-only.zip'], b'keep')

    def test_apply_raw_zip_then_catalog_with_csrf_and_encoding(self):
        plan = self.plan()
        result = sync.apply_plan(self.portal, plan, self.options)
        self.assertEqual(result['mode'], 'applied')
        self.assertEqual(len(self.device.posts), 3)
        self.assertEqual(self.device.files[RA, DIRECTORY]['1941.zip'], (self.root / 'arcade/1941.zip').read_bytes())
        self.assertEqual(sync.json_load(self.device.files[LP, 'LocalState'][sync.CATALOG]), plan.catalog)
        query = parse_qs(urlsplit(self.device.posts[-1].full_url).query)
        self.assertEqual(query['path'], ['\\LocalState'])
        self.assertEqual(query['packagefullname'], [LP])
        self.assertEqual(query['extract'], ['false'])
        self.assertTrue(all(r.get_header('Authorization') for r in self.device.calls))
        self.assertNotIn('synthetic-pass', json.dumps(result))
        self.assertNotIn('synthetic-csrf', json.dumps(result))

    def test_remote_metadata_union_and_conflict(self):
        remote = seed()
        remote['games'][0]['customOnlyOnXbox'] = ['keep', 1]
        self.device.files[LP, 'LocalState'][sync.CATALOG] = sync.encode_catalog(remote)
        self.assertEqual(self.plan().catalog['games'][0]['customOnlyOnXbox'], ['keep', 1])
        remote['games'][0]['title'] = 'A conflicting manual edit'
        self.device.files[LP, 'LocalState'][sync.CATALOG] = sync.encode_catalog(remote)
        with self.assertRaisesRegex(sync.SyncError, "Conflicting manually edited metadata in game arcade-1941, field 'title'"):
            self.plan()
        self.assertEqual(self.device.posts, [])

    def test_platform_conflict_names_field_without_printing_values(self):
        local = seed()
        remote = copy.deepcopy(local)
        remote['platforms'][0]['title'] = 'Private title'
        with self.assertRaises(sync.SyncError) as error:
            sync.merge_catalogs(local, remote)
        self.assertIn("platform arcade, field 'title'", str(error.exception))
        self.assertNotIn('Private title', str(error.exception))

    def test_preserve_remote_only_game_and_locally_absent_game(self):
        remote = seed()
        remote['games'].append({'id': 'remote-only', 'platform': 'arcade', 'title': 'Keep', 'rom': 'arcade/remote.zip'})
        self.device.files[LP, 'LocalState'][sync.CATALOG] = sync.encode_catalog(remote)
        (self.root / 'arcade/1941.zip').unlink()
        games = self.plan().catalog['games']
        self.assertTrue(any(g['id'] == 'remote-only' for g in games))
        self.assertTrue(any(g['id'] == 'arcade-1941' for g in games))

    def test_multiple_platforms_nested_scan_defaults_and_override(self):
        for pid in ('genesis', 'snes'):
            (self.root / pid).mkdir()
            (self.root / pid / 'game.zip').write_bytes(b'game')
            self.device.files[RA, 'LocalState\\roms\\' + pid] = {}
        (self.root / 'arcade/sub').mkdir()
        (self.root / 'arcade/sub/日本語 &+%#.zip').write_bytes(b'archive')
        self.device.files[RA, DIRECTORY + '\\sub'] = {}
        self.options['platform_dirs'].update(genesis='genesis', snes='snes')
        self.options['defaults'] = {'genesis': 'genesis_plus_gx_libretro.dll', 'snes': 'snes9x_libretro.dll'}
        self.options['overrides'] = {'arcade/1942.zip': 'mame_libretro.dll'}
        plan = self.plan()
        games = {g['rom']: g for g in plan.catalog['games']}
        self.assertEqual(games['arcade/1942.zip']['core'], 'mame_libretro.dll')
        self.assertEqual(games['arcade/1941.zip']['core'], 'fbneo_libretro.dll')
        self.assertEqual(len(plan.catalog['platforms']), 3)
        sync.apply_plan(self.portal, plan, self.options)
        self.assertEqual(self.device.files[RA, DIRECTORY + '\\sub']['日本語 &+%#.zip'], b'archive')

    def test_override_conflicts_are_not_silently_overwritten(self):
        self.options['overrides'] = {'arcade/1941.zip': 'different.dll'}
        with self.assertRaisesRegex(sync.SyncError, 'per-game core'):
            self.plan()
        self.options['overrides'] = {}
        self.options['defaults'] = {'arcade': 'different.dll'}
        with self.assertRaisesRegex(sync.SyncError, 'default core'):
            self.plan()

    def test_missing_directory_plans_but_blocks_apply_before_write(self):
        del self.device.files[RA, DIRECTORY]
        plan = self.plan()
        self.assertEqual(plan.report['missing_directories'], [DIRECTORY])
        with self.assertRaisesRegex(sync.SyncError, 'Create the reported'):
            sync.apply_plan(self.portal, plan, self.options)
        self.assertEqual(self.device.posts, [])

    def test_missing_local_directory_or_file(self):
        self.options['platform_dirs'] = {'arcade': 'missing'}
        with self.assertRaisesRegex(sync.SyncError, 'missing'):
            self.plan()
        self.options['platform_dirs'] = {'arcade': 'arcade'}
        plan = self.plan()
        (self.root / 'arcade/1942.zip').unlink()
        with self.assertRaisesRegex(sync.SyncError, 'changed after planning'):
            sync.apply_plan(self.portal, plan, self.options)
        self.assertEqual(self.device.posts, [])

    def test_interrupted_transfer_does_not_publish_catalog_or_retry(self):
        plan = self.plan()
        self.device.interrupt = True
        with self.assertRaisesRegex(sync.SyncError, 'POST .* interrupted.*State may be partial'):
            sync.apply_plan(self.portal, plan, self.options)
        self.assertEqual(len(self.device.posts), 1)
        self.assertNotIn(sync.CATALOG, self.device.files[LP, 'LocalState'])

    def test_bootstrap_disconnect_reports_operation_without_secret(self):
        with patch.object(self.portal.opener, 'open', side_effect=RemoteDisconnected('synthetic secret')):
            with self.assertRaises(sync.SyncError) as error:
                self.plan()
        self.assertEqual(str(error.exception),
                         'Portal GET /api/os/info: server closed the connection without an HTTP response.')
        self.assertEqual(self.device.posts, [])

    def test_bootstrap_os_error_reports_only_type_and_errno(self):
        failure = URLError(OSError(errno.EHOSTUNREACH, 'synthetic secret'))
        with patch.object(self.portal.opener, 'open', side_effect=failure):
            with self.assertRaises(sync.SyncError) as error:
                self.plan()
        self.assertEqual(str(error.exception),
                         f'Portal GET /api/os/info: transfer/connection interrupted (OSError), errno {errno.EHOSTUNREACH}.')

    def test_post_upload_size_failure_does_not_publish_catalog(self):
        plan = self.plan()
        self.device.bad_size = True
        with self.assertRaisesRegex(sync.SyncError, 'Post-upload'):
            sync.apply_plan(self.portal, plan, self.options)
        self.assertEqual(len(self.device.posts), 1)
        self.assertNotIn(sync.CATALOG, self.device.files[LP, 'LocalState'])

    def test_auth_failures_and_redacted_error(self):
        for status in (401, 403):
            with self.subTest(status=status):
                self.device.auth_status = status
                with self.assertRaisesRegex(sync.DiagnosticError, 'HTTP status ' + str(status)) as error:
                    self.plan()
                self.assertNotIn('secret', str(error.exception))
                self.assertEqual(self.device.posts, [])

    def test_csrf_refresh_for_get(self):
        self.device.refresh_once = True
        self.plan()
        self.assertEqual(sum(urlsplit(r.full_url).path == '/api/os/info' for r in self.device.calls), 2)

    def test_redirect_never_forwards_credentials(self):
        self.device.redirect = True
        with self.assertRaisesRegex(sync.DiagnosticError, '302'):
            self.plan()
        self.assertEqual(len(self.device.calls), 1)

    def test_unknown_or_wrong_packages_block_writes(self):
        for alter in ('missing_lp', 'wrong_identity', 'wrong_root_family', 'same_package'):
            options = copy.deepcopy(self.options)
            if alter == 'missing_lp': options['launchpass'] = 'Missing_1'
            elif alter == 'wrong_identity': options['identity'] = 'Other'
            elif alter == 'same_package': options['launchpass'] = RA
            else: options['seed']['contentRoot'] = ROOT.replace('RetroArch_test', 'Wrong_test')
            with self.subTest(alter=alter), self.assertRaises(sync.SyncError):
                sync.build_plan(self.portal, **options)
        self.assertEqual(self.device.posts, [])

    def test_launchpass_identity_accepts_case_variants_but_rejects_other_family(self):
        package = self.device.packages['InstalledPackages'][1]
        package['PackageFamilyName'] = package['PackageFamilyName'].upper()
        self.assertEqual(self.plan().report['mode'], 'dry-run')
        package['PackageFamilyName'] = 'Other_test'
        with self.assertRaisesRegex(sync.SyncError, "PackageFamilyName 'Other_test'"):
            self.plan()
        self.assertEqual(self.device.posts, [])

    def test_bare_portal_package_names_resolve_from_versioned_full_names(self):
        retroarch, launchpass = self.device.packages['InstalledPackages']
        retroarch['PackageFamilyName'] = 'RetroArch'
        launchpass['PackageFamilyName'] = sync.LAUNCHPASS_IDENTITY
        self.assertEqual(self.plan().report['mode'], 'dry-run')
        launchpass['PackageFamilyName'] = 'Other'
        with self.assertRaisesRegex(sync.SyncError, 'cannot be resolved from PackageFullName'):
            self.plan()
        self.assertEqual(self.device.posts, [])

    def test_package_lookup_identifies_missing_or_ambiguous_argument(self):
        for option in ('retroarch', 'launchpass'):
            with self.subTest(option=option), self.assertRaisesRegex(
                    sync.SyncError, f"--{option}-package-full-name 'Old_1' did not match"):
                sync.build_plan(self.portal, **dict(self.options, **{option: 'Old_1'}))
        self.device.packages['InstalledPackages'].append(self.device.packages['InstalledPackages'][1])
        with self.assertRaisesRegex(sync.SyncError, '--launchpass-package-full-name.*matched multiple'):
            self.plan()
        self.assertEqual(self.device.posts, [])

    def test_malformed_listing_fails_closed(self):
        invalid = [{}, {'Items': None}, {'Items': [None]}, {'Items': [{'Name': '../evil', 'FileSize': 4, 'Type': 32}]},
                   {'Items': [{'Name': 'file.zip', 'Type': 32}]}, {'Items': [{'Name': 'file.zip', 'Type': 'File', 'FileSize': 1}]},
                   {'Items': [{'Name': 'x.zip', 'Type': 32, 'FileSize': -1}]},
                   {'Items': [{'Name': 'x.zip', 'Type': 32, 'FileSize': True}]},
                   {'Items': [{'Name': 'x.zip', 'Type': 0x400, 'FileSize': 1}]},
                   {'Items': [{'Name': 'x.zip', 'Type': 32, 'FileSize': 1}, {'Name': 'X.zip', 'Type': 32, 'FileSize': 2}]}]
        for listing in invalid:
            self.device.bad_listing = listing
            with self.subTest(listing=listing), self.assertRaises(sync.SyncError):
                self.plan()
        self.assertEqual(self.device.posts, [])

    def test_remote_catalog_listed_but_download_missing(self):
        self.device.files[LP, 'LocalState'][sync.CATALOG] = sync.encode_catalog(seed())
        with patch.object(self.portal, 'read_catalog', side_effect=sync.DiagnosticError('Portal HTTP status 404')):
            with self.assertRaisesRegex(sync.DiagnosticError, '404'):
                self.plan()
        self.assertEqual(self.device.posts, [])

    def test_malformed_remote_catalog_not_replaced(self):
        self.device.files[LP, 'LocalState'][sync.CATALOG] = b'{invalid'
        with self.assertRaises(sync.SyncError):
            self.plan()
        self.assertEqual(self.device.posts, [])

    def test_remote_catalog_edit_after_planning_or_during_transfer(self):
        plan = self.plan()
        self.device.files[LP, 'LocalState'][sync.CATALOG] = sync.encode_catalog(seed())
        with self.assertRaisesRegex(sync.SyncError, 'changed after planning'):
            sync.apply_plan(self.portal, plan, self.options)
        self.assertEqual(self.device.posts, [])
        plan = self.plan()
        self.device.remote_edit_after_rom = True
        with self.assertRaisesRegex(sync.SyncError, 'changed during ROM transfer'):
            sync.apply_plan(self.portal, plan, self.options)
        self.assertTrue(all(parse_qs(urlsplit(r.full_url).query)['packagefullname'] == [RA] for r in self.device.posts))

    def test_local_case_collision_and_symlink_rejected(self):
        # Use different suffix spelling so a case-sensitive macOS volume is not required.
        (self.root / 'arcade/link.zip').symlink_to(self.root / 'arcade/1941.zip')
        with self.assertRaisesRegex(sync.SyncError, 'symlink'):
            self.plan()
        with patch.object(sync.os, 'walk', return_value=[(str(self.root / 'arcade'), [], ['1941.zip', '1941.zip'])]):
            with self.assertRaisesRegex(sync.SyncError, 'collide'):
                self.plan()

    def test_absolute_existing_rom_keeps_identity_and_edits(self):
        self.options['seed']['games'][0]['rom'] = ROOT + r'\arcade\1941.zip'
        result = self.plan().catalog['games']
        self.assertEqual(result[0], self.options['seed']['games'][0])
        self.assertEqual(len(result), 2)

    def test_xbox_paths_accept_relative_and_absolute_roms(self):
        for rom in ('arcade/1941.zip', r'arcade\1941.zip', ROOT + r'\arcade\1941.zip'):
            with self.subTest(rom=rom):
                document = seed()
                document['games'][0]['rom'] = rom
                self.assertEqual(sync.validate_catalog(document), document)
                self.assertEqual(sync.rom_path(ROOT, rom), (ROOT + r'\arcade\1941.zip').casefold())

    def test_traversal_and_dot_still_rejected_in_xbox_paths(self):
        for rom in ('.', '../outside.zip', r'arcade\..\outside.zip', ROOT + r'\..\outside.zip'):
            with self.subTest(rom=rom):
                document = seed()
                document['games'][0]['rom'] = rom
                with self.assertRaisesRegex(sync.SyncError, 'Unsafe Windows path'):
                    sync.validate_catalog(document)

    def test_platform_folder_rejects_traversal_and_absolute_paths(self):
        for folder in ('..', '../outside', r'arcade\..', './arcade', '/outside', r'U:\outside'):
            with self.subTest(folder=folder), self.assertRaisesRegex(sync.SyncError, 'Unsafe Windows path'):
                sync.scan_library(self.root, {'arcade': folder}, {'.zip'})

    def test_generated_titles_metadata_and_ids_survive_regeneration(self):
        first = self.plan().catalog
        generated = first['games'][1]
        generated.update(title='My renamed game', custom={'favorite': True}, core='custom_libretro.dll')
        self.options['seed'] = first
        second = self.plan().catalog
        self.assertEqual(first, second)
        self.assertEqual(second['games'][1]['id'], generated['id'])

    def test_post_auth_failure_is_not_retried_and_does_not_publish(self):
        plan = self.plan()
        self.device.post_status = 403
        with self.assertRaisesRegex(sync.DiagnosticError, '403'):
            sync.apply_plan(self.portal, plan, self.options)
        self.assertEqual(len(self.device.posts), 1)
        self.assertNotIn(sync.CATALOG, self.device.files[LP, 'LocalState'])

    def test_post_500_identifies_rom_or_catalog_upload(self):
        self.device.post_status = 500
        for catalog_only in (False, True):
            with self.subTest(catalog_only=catalog_only):
                if catalog_only:
                    for file in (self.root / 'arcade').iterdir():
                        self.device.files[RA, DIRECTORY][file.name] = file.read_bytes()
                plan = self.plan()
                with self.assertRaises(sync.DiagnosticError) as error:
                    sync.apply_plan(self.portal, plan, self.options)
                operation = (r'Uploading LaunchPass LocalState\InternalCatalog.json' if catalog_only
                             else "Uploading ROM 'arcade/1941.zip'")
                self.assertEqual(str(error.exception), operation + ': Portal HTTP status 500')
        self.assertEqual(len(self.device.posts), 2)

    def test_enrichment_dry_run_and_apply_preserve_existing_xbox_metadata(self):
        stage = self.root.resolve() / 'stage'
        presentation = {'version': 1, 'games': [{'id': 'arcade-1941', 'year': 'custom'}]}
        self.device.files[LP, 'LocalState'][sync.PRESENTATION] = sync.encode_catalog(presentation)
        directory = (LP, r'LocalState\Artwork\games\arcade-1941')
        self.device.files[directory] = {'logo.jpg': b'custom artwork'}

        def importer(catalog, output, *, apply, progress):
            self.assertEqual(sync.read_json(catalog), plan.catalog)
            self.assertEqual(output, stage)
            progress(1, 2, 'arcade-1941', 0)
            if apply:
                asset_dir = output / 'Artwork/games/arcade-1941'
                asset_dir.mkdir(parents=True)
                (asset_dir / 'background.png').write_bytes(b'staged image')
                (asset_dir / 'logo.png').write_bytes(b'new logo')
            return {'games': [{'id': 'arcade-1941', 'assets': {
                        role: {'status': 'written' if apply else 'would-download'} for role in ('logo', 'background')
                    }}],
                    'presentation': {'version': 1, 'games': [
                        {'id': 'arcade-1941', 'year': '1941', 'manufacturer': 'Capcom'}]}}

        plan = self.plan()
        stderr = io.StringIO()
        with patch('arcade_artwork.import_catalog', side_effect=importer), patch('sys.stderr', stderr):
            preview, _ = sync.stage_enrichment(self.portal, LP, plan.catalog, stage, False)
            self.assertFalse(stage.exists())
            self.assertEqual(self.device.posts, [])
            self.assertEqual(preview['xbox']['artwork_uploads'], ['Artwork/games/arcade-1941/background.png'])
            # Mock the dependency probe, since this test supplies already-staged bytes.
            with patch.dict('sys.modules', {'PIL': type('PIL', (), {'Image': object()})}):
                report, publish = sync.stage_enrichment(self.portal, LP, plan.catalog, stage, True)
            self.assertEqual(report['xbox'], preview['xbox'])
            sync.apply_plan(self.portal, plan, self.options, publish)
        self.assertEqual(self.device.files[directory], {'logo.jpg': b'custom artwork', 'background.png': b'staged image'})
        presentation['games'][0]['manufacturer'] = 'Capcom'
        self.assertEqual(sync.json_load(self.device.files[LP, 'LocalState'][sync.PRESENTATION]), presentation)
        self.assertEqual(self.device.upload_names[:2], ['1941.zip', '1942.zip'])
        self.assertTrue(self.device.upload_names[2].startswith('LaunchPassArtwork-'))
        self.assertEqual(self.device.upload_names[3:], [sync.PRESENTATION, sync.CATALOG])
        self.assertIn('checking game 1/2 (arcade-1941)', stderr.getvalue())
        self.assertIn('Checking Xbox for 2 artwork paths', stderr.getvalue())

    def test_enrichment_can_run_importer_in_separate_python(self):
        catalog = seed()
        catalog['platforms'][0]['id'] = 'snes'
        catalog['games'][0]['platform'] = 'snes'
        stage = self.root.resolve() / 'stage'
        with patch('sys.stderr', io.StringIO()):
            report, _ = sync.stage_enrichment(self.portal, LP, catalog, stage, False, sys.executable)
        self.assertEqual(report['games'][0]['status'], 'skipped-platform')
        self.assertEqual(report['requests'], 0)
        self.assertEqual(self.device.posts, [])
        self.assertFalse(stage.exists())

    def test_enrichment_publishes_full_titles_only_for_archive_stems_and_can_resync(self):
        for file in (self.root / 'arcade').iterdir():
            self.device.files[RA, DIRECTORY][file.name] = file.read_bytes()
        original = self.plan().catalog
        self.options.update(seed=original, prefer_named_arcade_games=True)
        self.device.files[LP, 'LocalState'][sync.CATALOG] = sync.encode_catalog(original)
        plan = self.plan()
        self.assertEqual(plan.report['catalog']['action'], 'unchanged')
        first, second = original['games']
        enrichment = {'games': [
            {'id': first['id'], 'status': 'matched', 'metadata': {'title': 'First game'}},
            {'id': second['id'], 'status': 'matched', 'metadata': {'title': '1942: Full title'}},
        ]}
        preview = sync.build_publication_report(plan, enrichment)
        self.assertEqual(preview['catalog']['enriched_title_game_ids'], [second['id']])
        self.assertEqual(preview['catalog']['action'], 'update')
        self.assertEqual(plan.catalog, original)
        self.assertEqual(self.device.posts, [])
        sync.apply_plan(self.portal, plan, self.options, publication_report=preview)
        published = sync.json_load(self.device.files[LP, 'LocalState'][sync.CATALOG])
        self.assertEqual(published['games'], [first, dict(second, title='1942: Full title')])
        self.assertEqual(self.device.upload_names, [sync.CATALOG])
        self.assertEqual(self.plan().report['catalog']['action'], 'unchanged')
        self.options['seed'] = copy.deepcopy(original)
        self.options['seed']['games'][1]['title'] = 'A different human title'
        with self.assertRaisesRegex(sync.SyncError, "field 'title'"):
            self.plan()

    def test_enrichment_updates_previously_imported_mame_titles(self):
        original = self.plan().catalog
        self.options.update(seed=original, prefer_named_arcade_games=True)
        remote = copy.deepcopy(original)
        raw = 'Armored Warriors (Europe 941024)'
        remote['games'][1]['title'] = raw
        self.device.files[LP, 'LocalState'][sync.CATALOG] = sync.encode_catalog(remote)
        plan = self.plan()
        game_id = remote['games'][1]['id']
        enrichment = {'games': [{'id': game_id, 'status': 'matched', 'metadata': {'title': raw}}]}
        report = sync.build_publication_report(plan, enrichment)
        self.assertEqual(report['generated_catalog']['games'][1]['title'], 'Armored Warriors')
        self.assertEqual(report['catalog']['enriched_title_game_ids'], [game_id])
        for old, expected in (
                ('Alien vs. Predator (Europe 940520)', 'Alien vs. Predator'),
                ("JoJo's Bizarre Adventure (Euro 990927, NO CD)", "JoJo's Bizarre Adventure"),
                ('Mortal Kombat (rev 5.0 T-Unit 03/19/93)', 'Mortal Kombat'),
                ('Altered Beast (set 8) (8751 317-0078)', 'Altered Beast'),
                ('Double Dragon (Neo-Geo) (World)', 'Double Dragon (Neo-Geo)'),
                ('Game (World of Light)', 'Game (World of Light)'),
                ('Game (Version of Tomorrow)', 'Game (Version of Tomorrow)')):
            with self.subTest(title=old):
                self.assertEqual(sync.friendly_arcade_title(old), expected)

    def test_excluding_bios_removes_catalog_entry_but_still_uploads_archive(self):
        existing = self.plan().catalog
        self.device.files[LP, 'LocalState'][sync.CATALOG] = sync.encode_catalog(existing)
        plan = self.plan()
        game, bios = existing['games']
        enrichment = {'games': [
            {'id': game['id'], 'status': 'matched', 'metadata': {'genre': 'System / BIOS Test'}},
            {'id': bios['id'], 'status': 'matched', 'metadata': {'genre': 'System / BIOS'}},
        ]}
        report = sync.build_publication_report(plan, enrichment, exclude_bios=True)
        self.assertEqual(report['catalog']['excluded_bios'], [{'id': bios['id'], 'rom': bios['rom']}])
        self.assertEqual(report['generated_catalog']['games'], [game])
        self.assertEqual(report['catalog']['action'], 'update')
        self.assertEqual(report['uploads'], plan.report['uploads'])
        self.assertEqual(self.device.posts, [])
        sync.apply_plan(self.portal, plan, self.options, publication_report=report)
        self.assertEqual(self.device.files[RA, DIRECTORY]['1942.zip'], (self.root / 'arcade/1942.zip').read_bytes())
        self.assertEqual(sync.json_load(self.device.files[LP, 'LocalState'][sync.CATALOG])['games'], [game])

    def test_loose_component_cleanup_preserves_archives_and_custom_entries(self):
        original = self.plan().catalog
        for rom, title in [('arcade/1942/chip.bin', 'chip'), ('arcade/1942/custom.bin', 'Custom title'),
                           ('arcade/missing/chip.bin', 'chip')]:
            game_id = sync.generated_game_id('arcade', sync.rom_path(ROOT, rom))
            original['games'].append({'id': game_id, 'platform': 'arcade', 'rom': rom, 'title': title})
        removed = original['games'][2]
        self.options['seed'] = original
        self.device.files[LP, 'LocalState'][sync.CATALOG] = sync.encode_catalog(original)
        for file in (self.root / 'arcade').iterdir():
            self.device.files[RA, DIRECTORY][file.name] = file.read_bytes()
        self.device.files[RA, r'LocalState\roms\arcade\1942'] = {'chip.bin': b'keep the ROM file'}
        plan = self.plan()
        report = sync.build_publication_report(plan, clean_loose_components=True)
        self.assertEqual(report['catalog']['excluded_loose_arcade_components'],
                         [{'id': removed['id'], 'rom': removed['rom']}])
        self.assertEqual(report['generated_catalog']['games'], original['games'][:2] + original['games'][3:])
        self.assertEqual(self.device.posts, [])
        sync.apply_plan(self.portal, plan, self.options, publication_report=report)
        self.assertEqual(self.device.upload_names, [sync.CATALOG])
        self.assertEqual(self.device.files[RA, r'LocalState\roms\arcade\1942']['chip.bin'], b'keep the ROM file')

    def test_missing_csrf_cookie_refuses_post_before_transport(self):
        self.portal.cookies.clear()
        with self.assertRaisesRegex(sync.SyncError, 'CSRF'):
            self.portal.upload(RA, DIRECTORY, 'file.zip', io.BytesIO(b'data'), 4)
        self.assertEqual(self.device.posts, [])

    def test_local_file_size_changed_after_plan_blocks_all_writes(self):
        plan = self.plan()
        (self.root / 'arcade/1942.zip').write_bytes(b'a different size')
        with self.assertRaisesRegex(sync.SyncError, 'changed after planning'):
            sync.apply_plan(self.portal, plan, self.options)
        self.assertEqual(self.device.posts, [])

    def test_unknown_listing_directory_type_and_download_size_mismatch(self):
        entries = sync.strict_listing({'Items': [{'Name': 'nested', 'Type': 16}]})
        self.assertTrue(entries['nested']['directory'])
        self.device.files[LP, 'LocalState'][sync.CATALOG] = sync.encode_catalog(seed())
        with patch.object(self.portal, 'read_catalog', return_value=b'{}'):
            with self.assertRaisesRegex(sync.SyncError, 'changed while reading'):
                self.plan()

    def test_cli_apply_snapshot_forbidden_without_auth_or_writes(self):
        stderr = io.StringIO()
        args = ['--rom-root', str(self.root), '--catalog', 'unused.json', '--snapshot', 'unused-snapshot.json',
                '--retroarch-package-full-name', RA, '--launchpass-package-full-name', LP, '--apply']
        with patch('sys.stderr', stderr), patch.object(sync, 'SyncPortal') as portal:
            self.assertEqual(sync.main(args), 1)
            portal.assert_not_called()
        self.assertIn('forbidden', stderr.getvalue())

    def test_cli_flat_rom_root_writes_catalog_with_xbox_platform_prefix(self):
        seed_path = self.root / 'seed.json'
        seed_path.write_bytes(sync.encode_catalog(seed()))
        output_path = self.root / 'InternalCatalog.json'
        args = ['--rom-root', str(self.root / 'arcade'), '--platform-dir', 'arcade=.',
                '--catalog', str(seed_path), '--url', 'https://xbox.invalid',
                '--retroarch-package-full-name', RA, '--launchpass-package-full-name', LP,
                '--output', str(output_path)]
        stdout = io.StringIO()
        with patch.dict(sync.os.environ, {'XBOX_USER': 'synthetic-user', 'XBOX_PASS': 'synthetic-pass'}), \
                patch.object(sync, 'SyncPortal', return_value=self.portal), patch('sys.stdout', stdout):
            self.assertEqual(sync.main(args), 0)
        self.assertEqual(json.loads(stdout.getvalue())['scanned_roms'], 2)
        document = sync.read_json(output_path)
        self.assertEqual(document['contentRoot'], ROOT)
        self.assertEqual({game['rom'] for game in document['games']}, {'arcade/1941.zip', 'arcade/1942.zip'})
        self.assertEqual(self.device.posts, [])

    def test_real_example_schema_and_invalid_manifest_cases(self):
        example = sync.read_json(Path(__file__).parents[1] / 'Docs/InternalCatalog.example.json')
        sync.validate_catalog(example)
        for update in ({'version': True}, {'games': []}, {'contentRoot': r'C:relative'}, {'platforms': None}):
            invalid = dict(seed(), **update)
            with self.subTest(update=update), self.assertRaises(sync.SyncError):
                sync.validate_catalog(invalid)
        for raw in ('{} {}', '{"a":1,"a":2}', '{"a":NaN}'):
            with self.assertRaises(sync.SyncError): sync.json_load(raw)

    def test_snapshot_apply_and_transport_write_gates(self):
        snapshot = sync.SnapshotPortal({})
        with self.assertRaisesRegex(sync.SyncError, '--apply'):
            sync.apply_plan(snapshot, self.plan(), self.options)
        portal = sync.SyncPortal('https://xbox.invalid')
        with self.assertRaisesRegex(sync.SyncError, 'not authorized'):
            portal._exchange('POST', sync.FILE)
        with self.assertRaises(sync.SyncError):
            self.portal._exchange('DELETE', sync.FILE)

    def test_cli_defaults_to_dry_run_no_output_overwrite_and_no_credentials_in_report(self):
        seed_path = self.root / 'seed.json'
        seed_path.write_bytes(sync.encode_catalog(seed()))
        args = ['--rom-root', str(self.root), '--catalog', str(seed_path), '--url', 'https://xbox.invalid',
                '--retroarch-package-full-name', RA, '--launchpass-package-full-name', LP]
        output = io.StringIO()
        with patch.dict(sync.os.environ, {'XBOX_USER': 'synthetic-user', 'XBOX_PASS': 'synthetic-pass'}), \
                patch.object(sync, 'SyncPortal', return_value=self.portal), patch('sys.stdout', output):
            self.assertEqual(sync.main(args), 0)
        self.assertEqual(json.loads(output.getvalue())['mode'], 'dry-run')
        self.assertEqual(self.device.posts, [])
        for secret in ('synthetic-pass', 'synthetic-user', 'synthetic-csrf'):
            self.assertNotIn(secret, output.getvalue())
        with patch('sys.stderr', io.StringIO()), patch.object(sync, 'SyncPortal') as portal:
            self.assertEqual(sync.main(args + ['--output', str(seed_path)]), 1)
            portal.assert_not_called()
        self.assertEqual(sync.read_json(seed_path), seed())


if __name__ == '__main__':
    unittest.main()
