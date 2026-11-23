#!/usr/bin/env python3
"""Build a configured-core registry from an explicit RetroArch package, offline.

No Xbox access, package execution/extraction, ROM access, or existing-file writes.
The caller must compare package identity/version with the installed RetroArch.
"""
import argparse
from contextlib import contextmanager
from datetime import datetime, timezone
import json
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET
from zipfile import ZipFile, BadZipFile


class RegistryError(Exception):
    pass


@contextmanager
def application_archive(path):
    with ZipFile(path) as archive:
        manifests = [item for item in archive.infolist() if item.filename.lower() == 'appxmanifest.xml']
        if manifests:
            yield archive
            return
        candidates = []
        for item in archive.infolist():
            if item.filename.lower().endswith(('.appx', '.msix')):
                if item.file_size > 512 * 1024 * 1024:
                    raise RegistryError('Embedded package exceeds 512 MiB.')
                with archive.open(item) as stream, ZipFile(stream) as package:
                    identity = package_identity(package)
                    if identity.get('ProcessorArchitecture', '').lower() == 'x64' and identity['_retroarch_protocol']:
                        candidates.append(item)
        if len(candidates) != 1:
            raise RegistryError('Bundle must contain exactly one x64 application package.')
        with archive.open(candidates[0]) as stream, ZipFile(stream) as package:
            yield package


def package_identity(archive):
    names = {item.filename.lower(): item for item in archive.infolist()}
    manifest = names.get('appxmanifest.xml')
    if manifest is None or manifest.file_size > 1024 * 1024:
        raise RegistryError('Missing or oversized AppxManifest.xml.')
    root = ET.fromstring(archive.read(manifest))
    identity = next((node for node in root if node.tag.rsplit('}', 1)[-1] == 'Identity'), None)
    if identity is None:
        raise RegistryError('Package identity is missing.')
    result = dict(identity.attrib)
    result['_retroarch_protocol'] = any(node.tag.rsplit('}', 1)[-1] == 'Protocol' and
        node.get('Name', '').lower() == 'retroarch' for node in root.iter())
    return result


def core_registry(path, installed_version=None):
    with application_archive(path) as archive:
        identity = package_identity(archive)
        if identity.get('ProcessorArchitecture', '').lower() != 'x64':
            raise RegistryError('Use the Xbox x64 RetroArch package.')
        if not identity['_retroarch_protocol']:
            raise RegistryError('Selected package does not declare the retroarch protocol.')
        if installed_version and identity.get('Version') != installed_version:
            raise RegistryError('Package version differs from --installed-version; no registry written.')
        entries = archive.infolist()
        names = {}
        for item in entries:
            key = item.filename.replace('\\', '/').lower()
            if key in names:
                raise RegistryError('Duplicate case-insensitive package entry.')
            names[key] = item
        cores = []
        for name, item in names.items():
            if not re.fullmatch(r'cores/[a-z0-9_-][a-z0-9_.-]*\.dll', name):
                continue
            filename = item.filename.replace('\\', '/').split('/')[-1]
            if len(filename) > 255:
                raise RegistryError('Core basename exceeds the Windows filename limit.')
            if re.match(r'^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])\.', filename, re.I):
                raise RegistryError('Invalid Windows core basename.')
            friendly = re.sub(r'_libretro\.dll$', '', filename, flags=re.I).replace('_', ' ')
            info = names.get('info/' + filename[:-4].lower() + '.info')
            if info:
                if info.file_size > 64 * 1024:
                    raise RegistryError('Oversized core info file.')
                text = archive.read(info).decode('utf-8-sig')
                match = re.search(r'^\s*display_name\s*=\s*"([^"\r\n]+)"', text, re.M)
                if match:
                    friendly = match.group(1)
            if len(friendly) > 160 or any(ord(ch) < 32 or 127 <= ord(ch) <= 159 for ch in friendly):
                raise RegistryError('Invalid friendly core name.')
            cores.append({'file': filename, 'name': friendly, 'available': True})
        if not cores or len(cores) > 1000:
            raise RegistryError('Package must contain between 1 and 1000 cores/*.dll.')
        return {'version': 1,
                'source': f"Configured from {identity.get('Name', 'package')} {identity.get('Version', '?')}; verify this matches the installed Xbox package",
                'generatedUtc': datetime.now(timezone.utc).isoformat(),
                'cores': sorted(cores, key=lambda core: core['name'].casefold())}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--package', required=True, type=Path, help='Explicit RetroArch x64 APPX/MSIX or bundle already on this Mac/PC')
    parser.add_argument('--installed-version', help='User-observed installed RetroArch version; reject a different package version')
    parser.add_argument('--output', type=Path, help='Write one NEW CoreRegistry.json; otherwise print only')
    args = parser.parse_args(argv)
    try:
        registry = core_registry(args.package, args.installed_version)
        data = json.dumps(registry, indent=2, ensure_ascii=True) + '\n'
        if len(data.encode('utf-8')) > 1024 * 1024:
            raise RegistryError('Registry exceeds the launcher 1 MiB limit.')
        if args.output:
            with args.output.open('x', encoding='utf-8') as output:
                output.write(data)
        print(data, end='')
        return 0
    except (RegistryError, OSError, ValueError, BadZipFile, ET.ParseError, UnicodeError) as error:
        print('Core registry stopped: ' + str(error), file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
