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

    def test_texture_picker_accepts_each_imported_format_and_rejects_escape(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            models = root / 'models'
            models.mkdir()
            for name in ('avatar.pmx', 'avatar.vrm', 'avatar.model3.json'):
                target = models / name
                target.write_bytes(b'fixture')
                self.assertEqual(self.bridge.texture_model(root, name), target.resolve())
            (root / 'external.pmx').write_bytes(b'fixture')
            for value in ('../external.pmx', 'missing.vrm', 'settings.json', None, ''):
                with self.assertRaises(ValueError):
                    self.bridge.texture_model(root, value)

    def test_cancelled_visual_picker_returns_without_reading_a_file(self):
        window = types.SimpleNamespace(withdraw=lambda: None, attributes=lambda *args: None, destroy=lambda: None)
        dialog = types.SimpleNamespace(askopenfilename=lambda **kwargs: '')
        tk = types.SimpleNamespace(Tk=lambda: window, filedialog=dialog)
        with patch.dict('sys.modules', {'tkinter': tk, 'tkinter.filedialog': dialog}):
            self.assertEqual(self.bridge.pick_visual_asset(), {'cancelled': True})

    def test_video_picker_rejects_other_file_extensions(self):
        with tempfile.TemporaryDirectory() as folder:
            selected = Path(folder) / 'script.exe'
            selected.write_bytes(b'fixture')
            window = types.SimpleNamespace(withdraw=lambda: None, attributes=lambda *args: None, destroy=lambda: None)
            dialog = types.SimpleNamespace(askopenfilename=lambda **kwargs: str(selected))
            tk = types.SimpleNamespace(Tk=lambda: window, filedialog=dialog)
            with patch.dict('sys.modules', {'tkinter': tk, 'tkinter.filedialog': dialog}):
                with self.assertRaises(ValueError):
                    self.bridge.pick_visual_asset(video=True)

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
        module.app.router.lifespan_context = service_lifetime
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

    def test_cold_vtuber_shutdown_stops_started_output_without_starting_ai(self):
        import asyncio
        from fastapi import FastAPI
        calls = []
        app = FastAPI()
        @asynccontextmanager
        async def ai_lifetime(app):
            self.fail('Cold output shutdown must not start AI')
            yield
        app.router.lifespan_context = ai_lifetime
        self.bridge.install_unity_lifecycle(types.SimpleNamespace(app=app))
        async def run():
            with patch.dict('sys.modules', {'spout_output': types.SimpleNamespace(output=types.SimpleNamespace(stop=lambda: calls.append('stop'))) }):
                async with app.router.lifespan_context(app):
                    self.assertEqual(calls, [])
                self.assertEqual(calls, ['stop'])
        asyncio.run(run())

    def test_lazy_start_keeps_included_router_lifespans(self):
        import asyncio
        import httpx
        from fastapi import APIRouter, FastAPI
        calls = []

        @asynccontextmanager
        async def app_lifetime(app):
            calls.append('app start')
            yield
            calls.append('app stop')

        @asynccontextmanager
        async def router_lifetime(app):
            calls.append('router start')
            yield
            calls.append('router stop')

        app = FastAPI(lifespan=app_lifetime)
        app.include_router(APIRouter(lifespan=router_lifetime))
        self.bridge.install_unity_lifecycle(types.SimpleNamespace(app=app, lifespan=app_lifetime))

        async def run():
            async with app.router.lifespan_context(app):
                self.assertEqual(calls, [])
                async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url='http://localhost') as client:
                    self.assertEqual((await client.post('/api/unity/start-services')).status_code, 200)
                    self.assertEqual(calls, ['app start', 'router start'])
            self.assertEqual(calls, ['app start', 'router start', 'router stop', 'app stop'])
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

    def test_mode_switching_does_not_start_ai_in_vtuber(self):
        import asyncio
        import httpx
        from fastapi import FastAPI
        calls = []
        app = FastAPI()
        @asynccontextmanager
        async def lifetime(app):
            calls.append('start')
            yield
            calls.append('stop')
        async def set_mode(payload):
            calls.append(('tracking', payload['enabled']))
            app.state.tracking_only = payload['enabled']
            return {'ok': True}
        module = types.SimpleNamespace(app=app, lifespan=lifetime, set_tracking_mode=set_mode)
        app.router.lifespan_context = lifetime
        self.bridge.install_unity_lifecycle(module)
        async def run():
            async with app.router.lifespan_context(app):
                async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url='http://localhost') as client:
                    self.assertEqual((await client.post('/api/unity/mode', json={'mode': 'VTuber', 'revision': 2})).status_code, 200)
                    self.assertTrue(app.state.tracking_only)
                    self.assertEqual((await client.post('/api/unity/start-services')).status_code, 409)
                    self.assertEqual(calls, [])
                    await client.post('/api/unity/mode', json={'mode': 'Companion', 'revision': 3})
                    await client.post('/api/unity/start-services')
                    await client.post('/api/unity/mode', json={'mode': 'VTuber', 'revision': 4})
                    await client.post('/api/unity/mode', json={'mode': 'Agent', 'revision': 5})
                    await client.post('/api/unity/mode', json={'mode': 'VTuber', 'revision': 1})
                    status = (await client.get('/api/unity/status')).json()
                    self.assertEqual(status['mode'], 'Agent')
                    self.assertFalse(app.state.tracking_only)
                    self.assertEqual(calls, ['start', ('tracking', True), ('tracking', False)])
                    self.assertEqual((await client.post('/api/unity/mode', json={'mode': 'invalid', 'revision': 6})).status_code, 400)
        asyncio.run(run())

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
