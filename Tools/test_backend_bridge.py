import importlib.util
from pathlib import Path
import tempfile
import unittest
import types
from unittest.mock import patch
from contextlib import asynccontextmanager


class BridgeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        spec = importlib.util.spec_from_file_location('bridge', Path(__file__).with_name('backend_bridge.py'))
        cls.bridge = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.bridge)

    def test_missing_source_is_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            with self.assertRaises(ValueError):
                self.bridge.validate_source(folder)

    def test_real_source_resolves(self):
        source = self.bridge.validate_source(r'F:\AI\assistant\Sakura')
        self.assertTrue((source / 'app.py').is_file())

    def test_local_catalog_retains_format_and_relative_path(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / 'models/pmx/Game/Character').mkdir(parents=True)
            (root / 'models/pmx/Game/Character/avatar.pmx').write_bytes(b'PMX ')
            (root / 'models/live2d/Character').mkdir(parents=True)
            (root / 'models/live2d/Character/avatar.model3.json').write_text('{}')
            items = self.bridge.read_catalog(root)
            self.assertEqual({item['format'] for item in items}, {'PMX', 'Live2D'})
            self.assertTrue(all('\\' not in item['path'] for item in items))

    def test_services_are_lazy_and_started_once(self):
        import asyncio
        import httpx
        from fastapi import FastAPI
        calls = []

        @asynccontextmanager
        async def service_lifetime(app):
            calls.append('start')
            yield
            calls.append('stop')

        module = types.SimpleNamespace(app=FastAPI(), lifespan=service_lifetime)
        self.bridge.install_unity_lifecycle(module)
        self.assertEqual(calls, [])

        async def run():
            async with module.app.router.lifespan_context(module.app):
                async with httpx.AsyncClient(transport=httpx.ASGITransport(app=module.app), base_url='http://localhost') as client:
                    for _ in range(2):
                        response = await client.post('/api/unity/start-services')
                        self.assertEqual(response.status_code, 200)
                    self.assertEqual(calls, ['start'])
            self.assertEqual(calls, ['start', 'stop'])

        asyncio.run(run())

    def test_package_files_retain_hierarchy_and_reject_links(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / 'textures').mkdir()
            (root / 'model.pmx').write_bytes(b'PMX ')
            (root / 'textures/body.png').write_bytes(b'image')
            records = list(self.bridge.package_files(root, 100, 10))
            self.assertEqual({name for name, _ in records}, {'model.pmx', 'textures/body.png'})
            self.assertEqual(list(self.bridge.package_files(root / 'model.pmx', 100, 10)), [('model.pmx', b'PMX ')])

    def test_package_size_and_count_are_bounded(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / 'model.pmx').write_bytes(b'PMX ')
            (root / 'image.png').write_bytes(b'image')
            with self.assertRaises(ValueError):
                list(self.bridge.package_files(root, 3, 10))
            with self.assertRaises(ValueError):
                list(self.bridge.package_files(root, 100, 1))

    def test_pmx_tool_validates_model_before_opening_editor(self):
        with tempfile.TemporaryDirectory() as folder:
            source = Path(folder)
            (source / 'settings.json').write_text('{"pmx_editor_path":"installed-editor"}')
            checked = []
            opened = []
            def model_file(model):
                checked.append(model)
                if model != 'pmx/example.pmx':
                    raise ValueError('Outside model root')
            def open_model(model, executable):
                opened.append((model, executable))
                return {'opened': True, 'model': model}
            module = types.SimpleNamespace(model_file=model_file, open_model=open_model)
            with patch.dict('sys.modules', {'pmx_editor': module}):
                with self.assertRaises(ValueError):
                    self.bridge.pmx_tool(source, 'editor', {'model': '../outside.pmx'})
                self.assertEqual(opened, [])
                result = self.bridge.pmx_tool(source, 'editor', {'model': 'pmx/example.pmx'})
            self.assertTrue(result['opened'])
            self.assertEqual(opened, [('pmx/example.pmx', 'installed-editor')])

    def test_pmx_tools_reject_unknown_action(self):
        with tempfile.TemporaryDirectory() as folder:
            source = Path(folder)
            (source / 'settings.json').write_text('{}')
            module = types.SimpleNamespace(model_file=lambda model: None, open_model=lambda *args: self.fail('Should not launch'))
            with patch.dict('sys.modules', {'pmx_editor': module}):
                with self.assertRaisesRegex(ValueError, 'Unsupported PMX tool'):
                    self.bridge.pmx_tool(source, 'shell', {'model': 'pmx/example.pmx'})


if __name__ == '__main__':
    unittest.main()
