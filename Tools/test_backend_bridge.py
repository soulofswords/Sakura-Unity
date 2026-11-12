import importlib.util
from pathlib import Path
import tempfile
import unittest
import types
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


if __name__ == '__main__':
    unittest.main()
