import json
import os
from pathlib import Path
import tempfile
import types
import unittest
from unittest.mock import patch
import zipfile
from package_windows import cleanup_stale, safe_target, stage


class PackageTests(unittest.TestCase):
    @unittest.skipUnless(os.name == 'nt', 'Windows uses case-insensitive package paths')
    def test_cleanup_keeps_files_present_under_new_capitalization(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            tools = root / 'Tools'
            tools.mkdir()
            current = tools / 'Bridge.py'
            current.write_text('new content')
            cleanup_stale(root, ['Tools/bridge.py'], {'Tools/Bridge.py'})
            self.assertEqual(current.read_text(), 'new content')

    def test_unsafe_destinations_and_links(self):
        with tempfile.TemporaryDirectory() as directory:
            for name in ('../secret', '/absolute', 'C:/file', 'a\\file', 'Tools//bridge.py', 'Tools/./bridge.py',
                         'Tools/bridge.py.', 'Tools/bridge.py '):
                with self.assertRaises(ValueError):
                    safe_target(directory, name)
            self.assertEqual(safe_target(directory, 'Sakura/app.py'), Path(directory)/'Sakura/app.py')

    def test_update_preserves_models_and_settings_and_isolates_editions(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); source = root/'source'; player = root/'player'; output = root/'install'
            (source/'tools/packaging-cache').mkdir(parents=True)
            with zipfile.ZipFile(source/'tools/packaging-cache/python-3.12.10-embed-amd64.zip', 'w') as runtime:
                runtime.writestr('python.exe', b'fixture')
            (player/'Tools').mkdir(parents=True)
            (player/'Sakura Companion.exe').write_bytes(b'fixture')
            (player/'Tools/backend_bridge.py').write_text('# fixture')
            backend = source/'app.py'; backend.write_text('# application')
            builder = types.SimpleNamespace(app_files=lambda: [(backend, 'Sakura/app.py')], editor_files=lambda _: [], DEFAULTS={})
            with patch('package_windows.load_builder', return_value=builder):
                stage(source, player, output, 'companion')
                settings = output/'Sakura/settings.json'; settings.write_text('{"keep":true}')
                models = output/'Sakura/models'; models.mkdir(); (models/'owner.pmx').write_bytes(b'unique')
                stage(source, player, output, 'companion')
                self.assertEqual(json.loads(settings.read_text()), {'keep': True})
                self.assertEqual((models/'owner.pmx').read_bytes(), b'unique')
                with self.assertRaises(ValueError):
                    stage(source, player, output, 'vtuber')

    def test_update_removes_only_stale_managed_files(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); source = root/'source'; player = root/'player'; output = root/'install'
            (source/'tools/packaging-cache').mkdir(parents=True)
            with zipfile.ZipFile(source/'tools/packaging-cache/python-3.12.10-embed-amd64.zip', 'w') as runtime:
                runtime.writestr('python.exe', b'fixture')
            (player/'Tools').mkdir(parents=True)
            (player/'Sakura Companion.exe').write_bytes(b'fixture')
            (player/'Tools/backend_bridge.py').write_text('# fixture')
            current = [source/'app.py', source/'obsolete.py']
            for file in current: file.write_text(file.name)
            builder = types.SimpleNamespace(
                app_files=lambda: [(file, 'Sakura/'+file.name) for file in current],
                editor_files=lambda _: [], DEFAULTS={})
            with patch('package_windows.load_builder', return_value=builder):
                stage(source, player, output, 'companion')
                (output/'Sakura/models').mkdir(parents=True)
                (output/'Sakura/models/owner.pmx').write_bytes(b'owner model')
                (output/'Sakura/desktop-profile').mkdir(parents=True)
                (output/'Sakura/desktop-profile/notes.json').write_text('owner data')
                current = [source/'app.py']
                stage(source, player, output, 'companion')
            self.assertFalse((output/'Sakura/obsolete.py').exists())
            self.assertEqual((output/'Sakura/models/owner.pmx').read_bytes(), b'owner model')
            self.assertEqual((output/'Sakura/desktop-profile/notes.json').read_text(), 'owner data')

    def test_untrusted_manifest_cannot_delete_unmanaged_user_files(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); source = root/'source'; player = root/'player'; output = root/'install'
            (source/'tools/packaging-cache').mkdir(parents=True)
            with zipfile.ZipFile(source/'tools/packaging-cache/python-3.12.10-embed-amd64.zip', 'w') as runtime:
                runtime.writestr('python.exe', b'fixture')
            (player/'Tools').mkdir(parents=True)
            (player/'Sakura Companion.exe').write_bytes(b'fixture')
            (player/'Tools/backend_bridge.py').write_text('# fixture')
            backend = source/'app.py'; backend.write_text('# application')
            builder = types.SimpleNamespace(app_files=lambda: [(backend, 'Sakura/app.py')], editor_files=lambda _: [], DEFAULTS={})
            with patch('package_windows.load_builder', return_value=builder):
                stage(source, player, output, 'companion')
                marker = output/'.sakura-unity-package.json'
                state = json.loads(marker.read_text())
                state['files'].append('Sakura/private/notes.json')
                marker.write_text(json.dumps(state))
                private = output/'Sakura/private/notes.json'
                private.parent.mkdir(); private.write_text('keep this')
                with self.assertRaises(ValueError):
                    stage(source, player, output, 'companion')
                self.assertEqual(private.read_text(), 'keep this')

    def test_generated_paths_are_checked_before_packaging(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / 'source'
            player = root / 'player'
            output = root / 'install'
            (player / 'Tools').mkdir(parents=True)
            (player / 'Sakura Companion.exe').touch()
            (player / 'Tools/backend_bridge.py').touch()

            def validate(root, path):
                if path.endswith('python312._pth'):
                    raise ValueError('Blocked generated path')
                return safe_target(root, path)

            with patch('package_windows.safe_target', side_effect=validate), \
                 patch('package_windows.load_builder', side_effect=AssertionError('Started packaging before validation')):
                with self.assertRaisesRegex(ValueError, 'Blocked generated path'):
                    stage(source, player, output, 'companion')
            self.assertFalse(output.exists())


if __name__ == '__main__':
    unittest.main()
