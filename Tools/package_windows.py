"""Stage a standalone Unity player with Sakura's allowlisted backend and Python.

Never copies settings, memories, imported models or owner sessions from the source.
Existing user state in a staged install is preserved on updates.
"""
import argparse
import importlib.util
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import sys
import tempfile
import zipfile


def safe_target(root, name):
    parts = name.split('/')
    if '\\' in name or ':' in name or any(part in ('', '.', '..') or part.endswith(('.', ' ')) for part in parts):
        raise ValueError('Invalid package destination.')
    root = Path(root).resolve()
    result = root.joinpath(*parts)
    current = result
    while current != root:
        if current.is_symlink() or getattr(current, 'is_junction', lambda: False)():
            raise ValueError('Package destination crosses a link.')
        current = current.parent
    return result


def atomic_copy(source, target):
    target.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(dir=target.parent, prefix='.sakura-update-', delete=False) as temporary:
        temporary_path = Path(temporary.name)
        with source.open('rb') as incoming:
            shutil.copyfileobj(incoming, temporary)
    try:
        os.replace(temporary_path, target)
    finally:
        temporary_path.unlink(missing_ok=True)


def atomic_write(target, content):
    target.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(dir=target.parent, prefix='.sakura-update-', delete=False) as temporary:
        temporary_path = Path(temporary.name)
        temporary.write(content)
    try:
        os.replace(temporary_path, target)
    finally:
        temporary_path.unlink(missing_ok=True)


def managed_name(name):
    """Return whether a manifest path belongs to the application payload.

    Keep the accepted roots narrow: a corrupt manifest must not turn arbitrary
    files under the install directory into cleanup targets.
    """
    path = PurePosixPath(name)
    if not name or "\\" in name or ":" in name or path.is_absolute() or ".." in path.parts:
        return False
    parts = path.parts
    if len(parts) >= 2 and parts[0] == 'Sakura':
        blocked = ('settings.json', 'models', 'voices', 'desktop-profile', 'llm-models')
        if parts[1] in blocked:
            return False
        if parts[1] in ('runtime', 'static', 'templates', 'kokoro-assets', 'sdk',
                        'launcher', 'personality'):
            return True
        if parts[1:3] in (('tools', 'pmx-editor'), ('native', 'sakura-camera')):
            return True
        if name == 'Sakura/product.json':
            return True
        return len(parts) == 2 and Path(parts[1]).suffix.lower() in (
            '.py', '.pyd', '.dll', '.txt', '.md', '.ps1', '.command', '.ico')
    if name == 'Tools/backend_bridge.py':
        return True
    # Unity player-owned roots. User data is never stored beneath these.
    if parts[0] in ('D3D12', 'MonoBleedingEdge', 'Tools'):
        return True
    if parts[0] in ('Sakura Companion_Data', 'Sakura VTuber_Data'):
        return True
    return len(parts) == 1 and Path(name).suffix.lower() in ('.exe', '.dll')


def cleanup_stale(output, old_names, new_names):
    blocked = ('Sakura/settings.json', 'Sakura/models/', 'Sakura/voices/',
               'Sakura/desktop-profile/', 'Sakura/llm-models/')
    current_names = {os.path.normcase(name) for name in new_names}
    for name in old_names:
        if os.path.normcase(name) in current_names or name.startswith(blocked) or not managed_name(name):
            continue
        target = safe_target(output, name)
        if target.is_file():
            target.unlink()
        parent = target.parent
        while parent != output and parent.exists():
            try:
                parent.rmdir()
            except OSError:
                break
            parent = parent.parent


def load_builder(source):
    sys.path.insert(0, str(source))
    spec = importlib.util.spec_from_file_location('unity_backend_builder', source/'tools/build-installers.py')
    builder = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(builder)
    return builder


def records(source, player, builder):
    yield from builder.app_files()
    yield from builder.editor_files(source/'tools/pmx-editor')
    for file in player.rglob('*'):
        if not file.is_file():
            continue
        relative = file.relative_to(player)
        if any('DoNotShip' in part or 'DontShip' in part for part in relative.parts) or relative.name == 'TEST-BUILD.txt':
            continue
        if relative.parts[0] == 'Sakura':
            raise ValueError('Choose a clean Unity build, not a previously packaged install.')
        yield file, relative.as_posix()
    packages = source/'venv/Lib/site-packages'
    for file in packages.rglob('*'):
        if not file.is_file():
            continue
        relative = file.relative_to(packages)
        if any(part in ('__pycache__', '.pytest_cache', 'tests', 'test', 'datasets') for part in relative.parts):
            continue
        if relative.parts[0].startswith(('pytest', '_pytest', 'pycdlib', 'onnxruntime-dml', 'edge_tts', 'phonemizer_fork-')):
            continue
        if file.suffix.lower() in ('.pyc', '.pmx', '.vrm', '.gguf', '.safetensors', '.pth'):
            continue
        yield file, 'Sakura/runtime/Lib/site-packages/' + relative.as_posix()
    # The embeddable Python distribution omits Tk; native import dialogs use it.
    python_home = Path(sys.base_prefix)
    for name in ('_tkinter.pyd', 'tcl86t.dll', 'tk86t.dll'):
        file = python_home/'DLLs'/name
        if not file.is_file():
            raise FileNotFoundError('Install Python with Tcl/Tk before packaging native dialogs: ' + name)
        yield file, 'Sakura/runtime/' + name
    for folder, destination in ((python_home/'Lib/tkinter', 'Sakura/runtime/Lib/site-packages/tkinter'), (python_home/'tcl', 'Sakura/runtime/tcl')):
        for file in folder.rglob('*'):
            if file.is_file() and '__pycache__' not in file.parts:
                yield file, destination + '/' + file.relative_to(folder).as_posix()


def stage(source, player, output, edition, dry_run=False):
    source, player, output = map(lambda value: Path(value).resolve(), (source, player, output))
    executable = 'Sakura VTuber.exe' if edition == 'vtuber' else 'Sakura Companion.exe'
    if not (player/executable).is_file() or not (player/'Tools/backend_bridge.py').is_file():
        raise ValueError('Build this Unity edition before packaging it.')
    if source == output or source in output.parents or player == output or player in output.parents:
        raise ValueError('Package output must be outside the source and clean player folders.')
    marker = safe_target(output, '.sakura-unity-package.json')
    settings = safe_target(output, 'Sakura/settings.json')
    runtime_config = safe_target(output, 'Sakura/runtime/python312._pth')
    product = safe_target(output, 'Sakura/product.json')
    identity = {'edition': edition, 'format': 1}
    old_names = []
    if output.exists():
        try:
            existing = json.loads(marker.read_text(encoding='utf-8'))
        except (OSError, ValueError, TypeError):
            existing = None
        if not isinstance(existing, dict) or any(existing.get(key) != value for key, value in identity.items()):
            raise ValueError('Choose a new output folder or the same edition staging folder.')
        old_names = existing.get('files', [])
        if not isinstance(old_names, list) or any(not isinstance(name, str) or not managed_name(name) for name in old_names):
            raise ValueError('Existing package manifest is invalid; refusing to modify this folder.')
        for name in old_names:
            safe_target(output, name)
    builder = load_builder(source)
    files = list(records(source, player, builder))
    blocked = ('Sakura/settings.json', 'Sakura/models/', 'Sakura/voices/', 'Sakura/desktop-profile/', 'Sakura/llm-models/')
    for file, name in files:
        safe_target(output, name)
        if name.startswith(blocked) or file.is_symlink() or getattr(file, 'is_junction', lambda: False)():
            raise ValueError('Personal data or linked input cannot be packaged.')
    runtime_path = source/'tools/packaging-cache/python-3.12.10-embed-amd64.zip'
    with zipfile.ZipFile(runtime_path) as runtime:
        runtime_files = [(name, 'Sakura/runtime/' + name) for name in runtime.namelist()
                         if name != 'python312._pth' and not name.endswith('/')]
    for _, target in runtime_files:
        safe_target(output, target)
    destinations = ([name for _, name in files] + [target for _, target in runtime_files]
                    + ['Sakura/runtime/python312._pth', 'Sakura/product.json'])
    if len(destinations) != len({name.casefold() for name in destinations}):
        raise ValueError('Duplicate package destinations.')
    new_names = set(destinations)
    if dry_run:
        with zipfile.ZipFile(runtime_path) as runtime:
            runtime_bytes = sum(item.file_size for item in runtime.infolist() if item.filename in {name for name, _ in runtime_files})
        return {'files': len(new_names), 'bytes': sum(file.stat().st_size for file, _ in files) + runtime_bytes,
                'output': str(output)}
    output.mkdir(parents=True, exist_ok=True)
    for file, name in files:
        target = safe_target(output, name)
        atomic_copy(file, target)
    with zipfile.ZipFile(runtime_path) as runtime:
        for name, destination in runtime_files:
            target = safe_target(output, destination)
            target.parent.mkdir(parents=True, exist_ok=True)
            with runtime.open(name) as incoming:
                with tempfile.NamedTemporaryFile(dir=target.parent, prefix='.sakura-update-', delete=False) as outgoing:
                    temporary_path = Path(outgoing.name)
                    shutil.copyfileobj(incoming, outgoing)
            try:
                os.replace(temporary_path, target)
            finally:
                temporary_path.unlink(missing_ok=True)
    try:
        with settings.open('x', encoding='utf-8') as stream:
            json.dump(dict(builder.DEFAULTS, vtuber_tracking_only=edition == 'vtuber'), stream, indent=2)
    except FileExistsError:
        pass
    atomic_write(runtime_config, b'python312.zip\n.\nLib/site-packages\n..\nimport site\n')
    atomic_write(product, json.dumps({'edition': edition, 'display_name': executable[:-4], 'version': '2.2'}).encode('utf-8'))
    cleanup_stale(output, old_names, new_names)
    atomic_write(marker, json.dumps({**identity, 'files': sorted(new_names)}, separators=(',', ':')).encode('utf-8'))
    return {'files': len(files), 'output': str(output), 'edition': edition}


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--source', required=True)
    parser.add_argument('--player', required=True)
    parser.add_argument('--output', required=True)
    parser.add_argument('--edition', choices=('companion', 'vtuber'), required=True)
    parser.add_argument('--dry-run', action='store_true')
    args = parser.parse_args()
    print(json.dumps(stage(args.source, args.player, args.output, args.edition, args.dry_run)))
