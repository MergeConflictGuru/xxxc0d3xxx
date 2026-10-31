"""Developer-only release builder. End users run the resulting single EXE."""
from __future__ import annotations

import concurrent.futures
import hashlib
import importlib.util
import json
import os
import pathlib
import shutil
import subprocess
import sys
import zipfile

SOURCE = pathlib.Path(__file__).resolve().parent
REPO = SOURCE.parents[2]
WORK = SOURCE / '.app_tmp/release-build'
OUTPUT = REPO / 'CodexQuotaTray/release'


def publish(project, destination, extra=()):
    destination.mkdir(parents=True, exist_ok=True)
    subprocess.run(['dotnet', 'publish', str(project), '-c', 'Release', '-r', 'win-x64',
                    '--self-contained', 'true', '-p:PlatformTarget=x64',
                    '-o', str(destination), *extra], check=True)


def python_runtime(destination):
    if sys.version_info[:2] != (3, 12) or sys.maxsize < 2**32:
        raise RuntimeError('Build the release with 64-bit Python 3.12 (the bundled runtime).')
    base = pathlib.Path(sys.base_prefix)
    destination.mkdir(parents=True, exist_ok=True)
    for name in ['python.exe', 'pythonw.exe', 'python3.dll', 'python312.dll', 'LICENSE.txt']:
        shutil.copy2(base / name, destination / name)
    for library in base.glob('vcruntime*.dll'):
        shutil.copy2(library, destination / library.name)
    shutil.copytree(base / 'DLLs', destination / 'DLLs', dirs_exist_ok=True,
                    ignore=shutil.ignore_patterns('__pycache__', '*.pyc'))
    with zipfile.ZipFile(destination / 'python312.zip', 'w', zipfile.ZIP_DEFLATED) as archive:
        excluded = {'site-packages', 'test', 'tests', '__pycache__', 'idlelib', 'tkinter',
                    'ensurepip', 'turtledemo'}
        for directory, dirs, files in os.walk(base / 'Lib'):
            dirs[:] = [d for d in dirs if d not in excluded]
            for name in files:
                path = pathlib.Path(directory) / name
                if path.suffix in {'.py', '.txt', '.pem', '.crt', '.json'}:
                    archive.write(path, path.relative_to(base / 'Lib').as_posix())
    spec = importlib.util.find_spec('websockets')
    if not spec or not spec.origin:
        raise RuntimeError('Install websockets 15.0.1 in the release-builder Python first.')
    shutil.copytree(pathlib.Path(spec.origin).parent, destination / 'Lib/site-packages/websockets',
                    dirs_exist_ok=True, ignore=shutil.ignore_patterns('__pycache__', '*.pyc'))
    for metadata in pathlib.Path(spec.origin).parent.parent.glob('websockets-*.dist-info'):
        shutil.copytree(metadata, destination / 'Lib/site-packages' / metadata.name, dirs_exist_ok=True)
    (destination / 'python312._pth').write_text('python312.zip\nDLLs\nLib/site-packages\n.\nimport site\n')
    subprocess.run([str(destination / 'python.exe'), '-c',
                    'import ssl,sqlite3,asyncio,websockets; print("Bundled Python imports OK")'], check=True)


def main():
    WORK.mkdir(parents=True, exist_ok=True)
    payload_root = WORK / 'payload'
    # All output lives in this exact build-owned directory; avoid stale payload files.
    if payload_root.exists():
        if payload_root.resolve().parent != WORK.resolve():
            raise RuntimeError('Unexpected build directory')
        shutil.rmtree(payload_root)
    payload_root.mkdir()
    jobs = [
        (SOURCE / 'ProtocolBridge/ProtocolBridge.csproj', payload_root / 'bridge'),
        (REPO / 'InvisibleLauncher/InvisibleLauncher.csproj', payload_root / 'bridge/invisible-launcher'),
        (SOURCE / 'DesktopFileBridge/DesktopFileBridge.csproj', payload_root / 'desktop-file-bridge'),
    ]
    with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
        futures = [pool.submit(publish, *job) for job in jobs]
        python_runtime(payload_root / 'python')
        for future in futures:
            future.result()
    bridge = payload_root / 'bridge'
    for path in SOURCE.iterdir():
        if path.is_file() and path.suffix in {'.py', '.js', '.cjs', '.ps1'} and not path.name.startswith(('test_', 'build_')):
            shutil.copy2(path, bridge / path.name)
    shutil.copy2(REPO / 'InvisibleLauncher/InvisibleLauncher.ps1', bridge / 'invisible-launcher/InvisibleLauncher.ps1')
    payload = WORK / 'payload.zip'
    manifest, stored = {}, set()
    with zipfile.ZipFile(payload, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for path in sorted(payload_root.rglob('*')):
            if not path.is_file() or path.suffix == '.pdb':
                continue
            digest = hashlib.sha256(path.read_bytes()).hexdigest().upper()
            manifest[path.relative_to(payload_root).as_posix()] = digest
            if digest not in stored:
                archive.write(path, 'blobs/' + digest)
                stored.add(digest)
        archive.writestr('manifest.json', json.dumps(manifest, indent=2))
    print(f'Payload: {len(manifest)} files, {len(stored)} unique blobs, {payload.stat().st_size // 1024**2} MiB')
    publish(SOURCE / 'Launcher/ConversationAccounts.csproj', OUTPUT,
            ['-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
             '-p:EnableCompressionInSingleFile=true', '-p:PayloadPath=' + str(payload)])
    print('User entry point:', OUTPUT / 'ConversationAccounts.exe')


if __name__ == '__main__':
    main()
