import io
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from zipfile import ZipFile
import zlib

import arcade_sync as sync
import console_library as console
from test_arcade_sync import LP, MockXbox, seed


class ConsoleLibraryTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name).resolve()
        self.roms = self.root / 'Genesis'
        self.roms.mkdir()
        self.stage = self.root / 'stage'
        self.data = b'synthetic cartridge image'
        self.crc = f'{zlib.crc32(self.data):08x}'
        with ZipFile(self.roms / 'test.zip', 'w') as archive:
            archive.writestr('test.md', self.data)
            archive.writestr('readme.txt', 'Synthetic fixture')
        (self.roms / 'unknown.bin').write_bytes(b'unmatched')
        self.catalog = seed()
        self.catalog['platforms'].append({'id': 'genesis', 'title': 'Genesis',
                                          'defaultCore': 'genesis_plus_gx_libretro.dll'})
        self.catalog['games'].append({'id': 'keep-genesis', 'platform': 'genesis', 'title': 'test',
                                      'rom': r'D:\DevelopmentFiles\roms\genesis\test.zip'})

    def test_matches_zip_member_and_preserves_existing_catalog(self):
        names = console.dat_rows(
            (f'game (\n name "Game, The (USA) (Rev 1)"\n'
             f' rom ( name "Game, The (USA) (Rev 1).md" size {len(self.data)} crc {self.crc.upper()} )\n)\n').encode(),
            'name')
        genres = console.dat_rows(f'game (\n genre "Action"\n rom ( crc {self.crc} )\n)'.encode(), 'genre')
        catalog, report = console.prepare(self.catalog, self.roms, r'D:\DevelopmentFiles\roms\genesis', 'genesis',
                                           {'no-intro': names, 'genre': genres, 'publisher': {}, 'releaseyear': {}})
        self.assertEqual(catalog['contentRoot'], self.catalog['contentRoot'])
        self.assertEqual(catalog['games'][0], self.catalog['games'][0])
        self.assertEqual(catalog['games'][1], dict(self.catalog['games'][1], title='The Game'))
        self.assertEqual(self.catalog['games'][1]['title'], 'test')
        self.assertEqual(report[0]['metadata'], {'genre': 'Action'})
        self.assertEqual(report[1]['status'], 'unmatched')

    def test_rejects_ambiguous_dat_and_zip_matches(self):
        pattern = f'\n rom ( size {len(self.data)} crc {self.crc} )\n)\n'
        data = ('game (\n name "One"' + pattern + 'game (\n name "Two"' + pattern).encode()
        self.assertEqual(console.dat_rows(data, 'name'), {})
        with ZipFile(self.roms / 'test.zip', 'a') as archive:
            archive.writestr('another.md', self.data)
        self.assertIsNone(console.rom_signature(self.roms / 'test.zip', {'.md'}))

    def test_rom_alias_keeps_working_xbox_path_and_id(self):
        game = self.catalog['games'][1]
        game['rom'] = r'D:\DevelopmentFiles\roms\genesis\old-name.zip'
        result, report = console.prepare(self.catalog, self.roms, r'D:\DevelopmentFiles\roms\genesis', 'genesis',
                                         {'no-intro': {}}, {'test.zip': 'old-name.zip'})
        self.assertEqual(result['games'][1], game)
        self.assertEqual(report[0]['rom'], 'test.zip')
        self.assertEqual(report[0]['xbox_path'], game['rom'])

    def test_staged_platform_upload_uses_no_importer_and_keeps_catalog(self):
        self.stage.mkdir()
        (self.stage / 'Presentation.json').write_bytes(sync.encode_catalog({'version': 1, 'games': [
            {'id': 'arcade-1941', 'genre': 'Shooter'}, {'id': 'keep-genesis', 'genre': 'Action'}]}))
        device = MockXbox()
        original = sync.encode_catalog(self.catalog)
        device.files[LP, 'LocalState'][sync.CATALOG] = original
        portal = sync.SyncPortal('https://xbox.invalid', apply=True)
        portal.opener.add_handler(device)
        with patch('arcade_artwork.import_catalog') as importer, patch('sys.stderr', io.StringIO()):
            report = sync.upload_presentation_only(portal, self.catalog, LP, sync.LAUNCHPASS_IDENTITY,
                                                   self.stage, staged_platform='genesis')
        importer.assert_not_called()
        self.assertEqual(report['mode'], 'applied')
        self.assertEqual(device.upload_names, [sync.PRESENTATION])
        self.assertEqual(device.files[LP, 'LocalState'][sync.CATALOG], original)
        self.assertEqual(sync.json_load(device.files[LP, 'LocalState'][sync.PRESENTATION])['games'],
                         [{'id': 'keep-genesis', 'genre': 'Action'}])

    def test_presentation_splits_artwork_into_bounded_batches(self):
        paths = {name: self.root / name for name in ('a', 'b', 'c')}
        for path in paths.values():
            path.write_bytes(b'1234')
        self.assertEqual(list(sync.artwork_batches(paths, list(paths), limit=8)), [['a', 'b'], ['c']])
