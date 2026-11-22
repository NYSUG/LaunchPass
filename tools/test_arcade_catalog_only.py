"""Offline synthetic ROM fixtures and mocked catalog-only Portal publication."""
import copy
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import arcade_sync as sync
from test_arcade_sync import seed, MockXbox, LP, RA

XBOX_ROOT = r'D:\DevelopmentFiles\roms\arcade'


class CatalogOnlyTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.root = self.base / 'Users/timothyjohnson/Roms/Arcade'
        self.root.mkdir(parents=True)
        for name in ('1941.zip', 'mk.zip', 'neogeo.zip', 'qsound_hle.zip'):
            (self.root / name).write_bytes(b'synthetic unchanged archive')
        (self.root / 'mk').mkdir()
        (self.root / 'mk/disk.chd').write_bytes(b'synthetic chd')
        self.seed = seed()
        self.source = self.base / 'InternalCatalog.current.json'
        self.source.write_bytes(sync.encode_catalog(self.seed))
        self.output = self.base / 'InternalCatalog.developmentfiles.json'

    def plan(self, **kwargs):
        return sync.catalog_only_plan(self.seed, self.root, XBOX_ROOT, **kwargs)

    def cli(self, *extra):
        args = ['--catalog-only', '--rom-root', str(self.root), '--catalog', str(self.source),
                '--xbox-rom-root', XBOX_ROOT, *extra]
        stdout, stderr = io.StringIO(), io.StringIO()
        with patch.object(sync, 'SyncPortal') as portal, patch.object(sync, 'read_secret') as secret, \
                patch('sys.stdout', stdout), patch('sys.stderr', stderr):
            result = sync.main(args)
        portal.assert_not_called()
        secret.assert_not_called()
        return result, stdout.getvalue(), stderr.getvalue()

    def xml(self, body):
        path = self.base / 'mame.xml'
        path.write_text('<mame>' + body + '</mame>', encoding='utf-8')
        return sync.offline_metadata(path)

    def test_flat_macos_library_absolute_d_paths_and_no_platform_duplication(self):
        plan = self.plan()
        self.assertEqual(plan['scanned_files'], 5)
        game = next(g for g in plan['generated_catalog']['games'] if g['id'] == 'arcade-1941')
        self.assertEqual(game['rom'], XBOX_ROOT + r'\1941.zip')
        self.assertNotIn('\\arcade\\arcade\\', game['rom'])
        self.assertEqual(plan['generated_catalog']['contentRoot'], XBOX_ROOT)
        self.assertTrue(all(sync.ntpath.isabs(g['rom']) for g in plan['generated_catalog']['games']))

    def test_curated_fields_ids_and_core_defaults_preserved(self):
        before = copy.deepcopy(self.seed)
        self.seed['games'][0]['cloneof'] = 'curated-parent'
        expected = copy.deepcopy(self.seed['games'][0])
        plan = self.plan()
        migrated = plan['generated_catalog']['games'][0]
        expected['rom'] = XBOX_ROOT + r'\1941.zip'
        self.assertEqual(migrated, expected)
        self.assertEqual(plan['generated_catalog']['platforms'], before['platforms'])
        self.assertEqual(plan['generated_catalog']['manualMetadata'], before['manualMetadata'])
        self.assertEqual(self.seed['games'][0]['rom'], 'arcade/1941.zip')

    def test_unique_basename_migrates_outside_old_root_and_keeps_platform(self):
        self.seed['platforms'].append({'id': 'mame', 'title': 'MAME', 'defaultCore': 'mame_libretro.dll'})
        self.seed['games'][0].update(rom=r'U:\different\1941.zip', platform='mame')
        game = self.plan()['generated_catalog']['games'][0]
        self.assertEqual(game['id'], 'arcade-1941')
        self.assertEqual(game['platform'], 'mame')
        self.assertEqual(game['rom'], XBOX_ROOT + r'\1941.zip')

    def test_xml_excludes_exact_bios_and_devices_only(self):
        evidence = self.xml('<machine name="neogeo" isbios="yes"/><machine name="qsound_hle" isdevice="yes"/>'
                            '<machine name="mk"><description>Mortal Kombat</description></machine>')
        before = {p: p.read_bytes() for p in self.root.rglob('*') if p.is_file()}
        plan = self.plan(evidence=evidence)
        self.assertEqual({g['title'] for g in plan['generated_catalog']['games']}, {'1941: manual title', 'Mortal Kombat'})
        self.assertEqual({g['classification'] for g in plan['excluded']}, {'bios', 'device'})
        self.assertEqual({p: p.read_bytes() for p in before}, before)

    def test_friendly_titles_update_archive_stems_and_prior_imports_but_preserve_curation(self):
        self.seed['games'].extend([
            {'id': 'keep-mk', 'platform': 'arcade', 'title': 'mk', 'rom': 'arcade/mk.zip'},
            {'id': 'keep-neogeo', 'platform': 'arcade', 'title': 'Neo Geo (World)', 'rom': 'arcade/neogeo.zip'},
        ])
        evidence = self.xml('<machine name="1941"><description>1941 (World)</description></machine>'
                            '<machine name="mk"><description>Mortal Kombat (rev 5.0)</description></machine>'
                            '<machine name="neogeo"><description>Neo Geo (World)</description></machine>')
        result = self.plan(evidence=evidence, friendly_titles=True)
        titles = {game['id']: game['title'] for game in result['generated_catalog']['games']}
        self.assertEqual(titles['arcade-1941'], '1941: manual title')
        self.assertEqual(titles['keep-mk'], 'Mortal Kombat')
        self.assertEqual(titles['keep-neogeo'], 'Neo Geo')
        self.assertEqual([change['id'] for change in result['title_changes']], ['keep-mk', 'keep-neogeo'])

    def test_unknown_bios_name_is_retained_and_reported(self):
        plan = self.plan()
        self.assertIn('neogeo', {g['title'] for g in plan['generated_catalog']['games']})
        self.assertIn('neogeo.zip', {g['local_relative'] for g in plan['ambiguous']})

    def test_conflicting_classification_retained(self):
        evidence = self.xml('<machine name="neogeo" isbios="yes"/><machine name="neogeo"/>')
        plan = self.plan(evidence=evidence)
        item = next(i for i in plan['ambiguous'] if i['local_relative'] == 'neogeo.zip')
        self.assertEqual(item['reason'], 'conflicting exact metadata')
        self.assertFalse(plan['excluded'])

    def test_clones_and_parent_requirements_retained(self):
        evidence = self.xml('<machine name="1941" cloneof="1941-parent" romof="bios-set"/>')
        item = next(i for i in self.plan(evidence=evidence)['entries'] if i['id'] == 'arcade-1941')
        self.assertEqual((item['cloneof'], item['romof']), ('1941-parent', 'bios-set'))
        self.assertEqual(self.plan(evidence=evidence)['dependencies_not_in_local_scan'], ['1941-parent', 'bios-set'])

    def test_chds_not_promoted_to_visible_games(self):
        plan = self.plan()
        self.assertEqual(len(plan['generated_catalog']['games']), 4)
        self.assertIn('dependency-chd', {item['classification'] for item in plan['entries']})

    def test_curated_bios_entry_removed_only_from_generated_output(self):
        self.seed['games'].append({'id': 'curated-bios', 'title': 'BIOS', 'platform': 'arcade', 'rom': 'arcade/neogeo.zip'})
        plan = self.plan(evidence=self.xml('<machine name="neogeo" isbios="yes"/>'))
        self.assertNotIn('curated-bios', {g['id'] for g in plan['generated_catalog']['games']})
        self.assertIn('curated-bios', {g['id'] for g in self.seed['games']})

    def test_existing_migrated_catalog_and_ids_are_idempotent(self):
        first = self.plan()['generated_catalog']
        again = sync.catalog_only_plan(first, self.root, XBOX_ROOT)['generated_catalog']
        self.assertEqual(first, again)

    def test_generated_ids_do_not_depend_on_drive_root(self):
        first = self.plan()['generated_catalog']['games']
        other = sync.catalog_only_plan(self.seed, self.root, r'E:\other')['generated_catalog']['games']
        self.assertEqual([g['id'] for g in first], [g['id'] for g in other])

    def test_unscanned_seed_entries_keep_old_absolute_meaning(self):
        self.seed['games'].append({'id': 'absent', 'title': 'Missing locally', 'platform': 'arcade', 'rom': 'arcade/absent.zip'})
        plan = self.plan()
        self.assertIn('absent', plan['retained_unscanned'])
        self.assertEqual(next(g for g in plan['generated_catalog']['games'] if g['id'] == 'absent')['rom'],
                         self.seed['contentRoot'] + r'\arcade\absent.zip')

    def test_core_override_conflicts_fail(self):
        with self.assertRaisesRegex(sync.SyncError, 'curated per-game'):
            self.plan(overrides={'arcade/1941.zip': 'mame_libretro.dll'})
        with self.assertRaisesRegex(sync.SyncError, 'default core'):
            self.plan(defaults={'arcade': 'mame_libretro.dll'})
        plan = self.plan(overrides={'arcade/mk.zip': 'fbneo_libretro.dll'})
        self.assertEqual(next(g for g in plan['generated_catalog']['games'] if g['title'] == 'mk')['core'], 'fbneo_libretro.dll')

    def test_unsafe_windows_roots_rejected(self):
        for root in (r'D:\DevelopmentFiles\..\other', r'D:relative', r'\\xbox\DevelopmentFiles',
                     'smb://xbox/DevelopmentFiles', r'D:\root\NUL', r'D:\root\bad:stream'):
            with self.subTest(root=root), self.assertRaises(sync.SyncError):
                sync.catalog_only_plan(self.seed, self.root, root)

    def test_platform_directory_traversal_rejected(self):
        with self.assertRaises(sync.SyncError):
            self.plan(folder='../Arcade')

    def test_windows_reserved_source_filename_rejected(self):
        # Windows itself refuses NUL.zip; emulate the logical scanned path.
        rom = sync.LocalRom(self.root / 'NUL.zip', 'arcade/NUL.zip', 'arcade', (0, 0, 1, 0))
        with patch.object(sync, 'scan_library', return_value=[rom]), self.assertRaisesRegex(sync.SyncError, 'unsafe on Windows'):
            self.plan()

    @unittest.skipIf(os.name == 'nt', 'Literal backslash filenames require a POSIX filesystem')
    def test_literal_posix_backslash_filename_is_not_a_windows_directory(self):
        (self.root / 'bad\\name.zip').write_bytes(b'keep')
        with self.assertRaisesRegex(sync.SyncError, 'unsafe on Windows'):
            self.plan()

    def test_symlink_source_rejected(self):
        link = self.root / 'link.zip'
        try:
            link.symlink_to(self.root / '1941.zip')
        except (OSError, NotImplementedError):
            self.skipTest('Symlink creation unavailable')
        with self.assertRaisesRegex(sync.SyncError, 'symlink'):
            self.plan()

    def test_ambiguous_basename_does_not_guess(self):
        self.seed['games'].append({'id': 'duplicate', 'title': 'Other version', 'platform': 'arcade', 'rom': r'U:\other\1941.zip'})
        self.seed['games'][0]['rom'] = r'U:\different\1941.zip'
        with self.assertRaisesRegex(sync.SyncError, 'Ambiguous curated basename'):
            self.plan()

    def test_cached_artwork_report_is_exact_only(self):
        report = self.base / 'artwork-report.json'
        report.write_text(json.dumps({'games': [
            {'status': 'matched', 'rom': 'neogeo', 'source_rom': 'neogeo', 'metadata': {'genre': 'System / BIOS'}},
            {'status': 'matched', 'rom': 'mk', 'source_rom': 'other', 'metadata': {'genre': 'System / BIOS'}}]}), encoding='utf-8')
        plan = self.plan(evidence=sync.offline_metadata(report_path=report))
        self.assertEqual([i['local_relative'] for i in plan['excluded']], ['neogeo.zip'])
        self.assertIn('mk', {g['title'] for g in plan['generated_catalog']['games']})

    def test_cli_no_output_is_read_only_and_no_credentials(self):
        before = {p: p.read_bytes() for p in self.base.rglob('*') if p.is_file()}
        code, stdout, stderr = self.cli()
        self.assertEqual((code, stderr), (0, ''))
        self.assertEqual(json.loads(stdout)['rom_writes'], 0)
        self.assertEqual(before, {p: p.read_bytes() for p in self.base.rglob('*') if p.is_file()})

    def test_cli_explicit_new_output_and_no_clobber(self):
        code, stdout, stderr = self.cli('--output', str(self.output))
        self.assertEqual((code, stderr), (0, ''))
        sync.validate_catalog(sync.read_json(self.output))
        original = self.output.read_bytes()
        self.assertEqual(self.cli('--output', str(self.output))[0], 1)
        self.assertEqual(self.output.read_bytes(), original)
        self.assertEqual(sync.read_json(self.source), self.seed)

    def test_cli_output_in_rom_tree_and_apply_forbidden(self):
        self.assertEqual(self.cli('--output', str(self.root / 'new.json'))[0], 1)
        self.assertEqual(self.cli('--apply')[0], 1)
        self.assertEqual(self.cli('--url', 'https://xbox.invalid')[0], 1)

    def test_malformed_xml_reports_details(self):
        path = self.base / 'bad.xml'
        path.write_text('<mame><machine>', encoding='utf-8')
        with self.assertRaisesRegex(sync.SyncError, 'Invalid MAME XML:'):
            sync.offline_metadata(path)

    def test_nested_rom_path_kept_under_target_and_platform_subfolder_supported(self):
        (self.root / 'nested').mkdir()
        (self.root / 'nested/another.zip').write_bytes(b'keep')
        plan = self.plan()
        self.assertIn(XBOX_ROOT + r'\nested\another.zip', {g['rom'] for g in plan['generated_catalog']['games']})
        parent_plan = sync.catalog_only_plan(self.seed, self.root.parent, XBOX_ROOT, folder='Arcade')
        self.assertEqual(plan['generated_catalog'], parent_plan['generated_catalog'])


class CatalogPublicationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.backup = Path(self.temp.name) / 'backup.json'
        self.device = MockXbox()
        self.portal = sync.SyncPortal('https://xbox.invalid', username='synthetic', password='synthetic', apply=True)
        self.portal.opener.add_handler(self.device)
        self.catalog = seed()
        self.catalog['games'][0]['rom'] = XBOX_ROOT + r'\1941.zip'

    def test_publication_only_writes_launchpass_catalog(self):
        result = sync.upload_catalog_only(self.portal, self.catalog, LP, sync.LAUNCHPASS_IDENTITY)
        self.assertEqual(result['mode'], 'applied')
        self.assertEqual(self.device.upload_names, [sync.CATALOG])
        self.assertEqual(sync.json_load(self.device.files[LP, 'LocalState'][sync.CATALOG]), self.catalog)
        self.assertFalse(any(package == RA and files for (package, _), files in self.device.files.items()))

    def test_dry_run_does_not_publish(self):
        self.portal.apply_enabled = False
        result = sync.upload_catalog_only(self.portal, self.catalog, LP, sync.LAUNCHPASS_IDENTITY)
        self.assertEqual(result['mode'], 'dry-run')
        self.assertFalse(self.device.posts)

    def test_upload_cli_needs_no_rom_root_or_retroarch_package(self):
        source = Path(self.temp.name) / 'generated.json'
        source.write_bytes(sync.encode_catalog(self.catalog))
        args = ['--upload-catalog-only', '--catalog', str(source), '--url', 'https://xbox.invalid',
                '--launchpass-package-full-name', LP]
        self.portal.apply_enabled = False
        with patch.dict(sync.os.environ, {'XBOX_USER': 'synthetic', 'XBOX_PASS': 'synthetic'}), \
                patch.object(sync, 'SyncPortal', return_value=self.portal), patch('sys.stdout', io.StringIO()):
            self.assertEqual(sync.main(args), 0)
        self.assertFalse(self.device.posts)

    def test_replacement_requires_new_backup_and_preserves_original_bytes(self):
        original = sync.encode_catalog(seed())
        self.device.files[LP, 'LocalState'][sync.CATALOG] = original
        with self.assertRaisesRegex(sync.SyncError, 'requires --catalog-backup'):
            sync.upload_catalog_only(self.portal, self.catalog, LP, sync.LAUNCHPASS_IDENTITY)
        self.assertFalse(self.device.posts)
        sync.upload_catalog_only(self.portal, self.catalog, LP, sync.LAUNCHPASS_IDENTITY, self.backup)
        self.assertEqual(self.backup.read_bytes(), original)

    def test_presentation_only_cli_uploads_art_and_metadata_for_published_catalog(self):
        root = Path(self.temp.name).resolve()
        source = root / 'generated.json'
        original = sync.encode_catalog(self.catalog)
        source.write_bytes(original)
        self.device.files[LP, 'LocalState'][sync.CATALOG] = original
        stage = root / 'artwork-stage'
        identifier = self.catalog['games'][0]['id']
        assets = stage / 'Artwork/games' / identifier
        assets.mkdir(parents=True)
        (assets / 'logo.png').write_bytes(b'staged logo')
        sidecar = {'version': 1, 'games': [{'id': identifier, 'year': '1941'}]}
        report = {'presentation': sidecar, 'games': [{'id': identifier, 'assets': {
            'logo': {'status': 'preserved-custom', 'paths': [f'Artwork/games/{identifier}/logo.png']},
            'extras/flyer': {'status': 'would-download'}}}]}
        args = ['--upload-presentation-only', '--catalog', str(source), '--url', 'https://xbox.invalid',
                '--launchpass-package-full-name', LP, '--enrichment-dir', str(stage)]
        with patch.dict(sync.os.environ, {'XBOX_USER': 'synthetic', 'XBOX_PASS': 'synthetic'}), \
                patch.object(sync, 'SyncPortal', return_value=self.portal), \
                patch('arcade_artwork.import_catalog', return_value=report), \
                patch.dict('sys.modules', {'PIL': type('PIL', (), {'Image': object()})}), \
                patch('sys.stdout', io.StringIO()), patch('sys.stderr', io.StringIO()):
            self.portal.apply_enabled = False
            self.assertEqual(sync.main(args), 0)
            self.assertFalse(self.device.posts)
            self.portal.apply_enabled = True
            self.assertEqual(sync.main(args + ['--apply']), 0)
        self.assertEqual(len(self.device.upload_names), 2)
        self.assertTrue(self.device.upload_names[0].startswith('LaunchPassArtwork-'))
        self.assertEqual(self.device.upload_names[1], sync.PRESENTATION)
        self.assertEqual(self.device.files[LP, 'LocalState'][sync.CATALOG], original)
        self.assertEqual(sync.json_load(self.device.files[LP, 'LocalState'][sync.PRESENTATION]), sidecar)
        self.assertFalse(any(package == RA and files for (package, _), files in self.device.files.items()))

    def test_presentation_upload_rejects_a_different_installed_catalog(self):
        self.device.files[LP, 'LocalState'][sync.CATALOG] = sync.encode_catalog(seed())
        with patch.object(sync, 'stage_enrichment') as stage, self.assertRaisesRegex(sync.SyncError, 'differs'):
            sync.upload_presentation_only(self.portal, self.catalog, LP, sync.LAUNCHPASS_IDENTITY, Path(self.temp.name))
        stage.assert_not_called()
        self.assertFalse(self.device.posts)

    def test_existing_backup_never_overwritten(self):
        self.device.files[LP, 'LocalState'][sync.CATALOG] = sync.encode_catalog(seed())
        self.backup.write_bytes(b'keep')
        with self.assertRaisesRegex(sync.SyncError, 'NEW catalog backup'):
            sync.upload_catalog_only(self.portal, self.catalog, LP, sync.LAUNCHPASS_IDENTITY, self.backup)
        self.assertEqual(self.backup.read_bytes(), b'keep')
        self.assertFalse(self.device.posts)

    def test_remote_edit_before_post_is_rejected(self):
        first = sync.encode_catalog(seed())
        with patch.object(sync, 'remote_catalog', side_effect=[(seed(), first), (self.catalog, b'changed')]):
            with self.assertRaisesRegex(sync.SyncError, 'changed after planning'):
                sync.upload_catalog_only(self.portal, self.catalog, LP, sync.LAUNCHPASS_IDENTITY, self.backup)
        self.assertFalse(self.device.posts)


if __name__ == '__main__':
    unittest.main()
