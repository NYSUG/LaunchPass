import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from zipfile import ZipFile
import retroarch_core_registry as registry


class CoreRegistryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.package = self.root / 'RetroArch.msix'

    def make(self, extra=None, architecture='x64'):
        with ZipFile(self.package, 'w') as archive:
            archive.writestr('AppxManifest.xml', '<Package><Identity Name="RetroArch" Version="1.0.0.0" ProcessorArchitecture="'+architecture+'" /><Applications><Application><Extensions><Protocol Name="retroarch" /></Extensions></Application></Applications></Package>')
            archive.writestr('cores/Exact_CASE_libretro.dll', b'synthetic DLL, never executed')
            archive.writestr('info/Exact_CASE_libretro.info', 'display_name = "Arcade friendly name"')
            if extra:
                for name, data in extra.items():
                    archive.writestr(name, data)

    def test_exact_core_filename_and_friendly_info(self):
        self.make()
        result = registry.core_registry(self.package, '1.0.0.0')
        self.assertEqual(result['cores'], [{'file': 'Exact_CASE_libretro.dll', 'name': 'Arcade friendly name', 'available': True}])
        self.assertIn('verify', result['source'])

    def test_bundle_x64_selection(self):
        self.make()
        bundle = self.root / 'RetroArch.msixbundle'
        with ZipFile(bundle, 'w') as archive:
            archive.writestr('RetroArch_x64.msix', self.package.read_bytes())
        self.assertEqual(len(registry.core_registry(bundle)['cores']), 1)

    def test_version_or_architecture_mismatch(self):
        self.make()
        with self.assertRaises(registry.RegistryError):
            registry.core_registry(self.package, '9.0.0.0')
        self.make(architecture='x86')
        with self.assertRaises(registry.RegistryError):
            registry.core_registry(self.package)

    def test_case_collisions_rejected(self):
        self.make({'cores/exact_case_libretro.dll': b'collision'})
        with self.assertRaises(registry.RegistryError):
            registry.core_registry(self.package)

    def test_no_extraction_or_path_traversal(self):
        self.make({'../outside.dll': b'never extract', 'cores/../../evil.dll': b'ignore', 'lib/other.dll': b'not a core'})
        before = {p.name for p in self.root.iterdir()}
        self.assertEqual(len(registry.core_registry(self.package)['cores']), 1)
        self.assertEqual(before, {p.name for p in self.root.iterdir()})

    def test_print_only_then_explicit_no_clobber(self):
        self.make()
        output = self.root / 'CoreRegistry.json'
        with patch('sys.stdout', io.StringIO()):
            self.assertEqual(registry.main(['--package', str(self.package)]), 0)
        self.assertFalse(output.exists())
        with patch('sys.stdout', io.StringIO()):
            self.assertEqual(registry.main(['--package', str(self.package), '--output', str(output)]), 0)
        original = output.read_bytes()
        with patch('sys.stderr', io.StringIO()):
            self.assertEqual(registry.main(['--package', str(self.package), '--output', str(output)]), 1)
        self.assertEqual(output.read_bytes(), original)


if __name__ == '__main__':
    unittest.main()
