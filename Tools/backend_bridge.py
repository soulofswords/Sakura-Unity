"""Development bridge to the preserved Sakura source. Credentials stay in memory."""
import argparse
import json
import os
from pathlib import Path
import socket
import sys
import threading
from contextlib import asynccontextmanager
import asyncio


def validate_source(value):
    source = Path(value).resolve()
    if not (source / 'app.py').is_file() or not (source / 'local_api_security.py').is_file():
        raise ValueError('Choose the existing Sakura source folder containing app.py.')
    return source


def read_catalog(source):
    models = source / 'models'
    entries = []
    if not models.is_dir():
        return entries
    for root, folders, files in os.walk(models, followlinks=False):
        folders[:] = sorted(name for name in folders if not name.startswith('.') and name != '__MACOSX')
        for name in sorted(files):
            kind = ('Live2D' if name.lower().endswith('.model3.json') else
                    'PMX' if name.lower().endswith('.pmx') else
                    'VRM' if name.lower().endswith('.vrm') else None)
            if kind and not name.startswith('._'):
                path = Path(root) / name
                # Directory links outside the model library are never exposed.
                if not path.resolve().is_relative_to(models.resolve()):
                    continue
                relative = path.relative_to(models)
                label = path.stem if len(relative.parts) <= 2 else path.parent.name
                entries.append({'name': label, 'path': relative.as_posix(),
                                'format': kind})
    return entries


def package_files(selection, max_bytes, max_files):
    """Read a selected package with the same bounds as the existing importer."""
    import stat
    selection = Path(selection)
    root = selection if selection.is_dir() else selection.parent
    files = selection.rglob('*') if selection.is_dir() else [selection]
    total = count = 0
    for file in files:
        if file.is_symlink() or file.is_junction():
            raise ValueError('Model imports cannot include symbolic links or junctions.')
        if not file.is_file():
            continue
        if getattr(file.stat(), 'st_file_attributes', 0) & getattr(stat, 'FILE_ATTRIBUTE_REPARSE_POINT', 0):
            raise ValueError('Model imports cannot include linked files.')
        count += 1
        if count > max_files or file.stat().st_size > max_bytes - total:
            raise ValueError('Model packages must be under 512 MB and 5,000 files.')
        with file.open('rb') as stream:
            content = stream.read(max_bytes - total + 1)
        total += len(content)
        if total > max_bytes:
            raise ValueError('Model packages must be under 512 MB and 5,000 files.')
        if len(content) != file.stat().st_size:
            raise ValueError('A model file changed during import. Try again after copying finishes.')
        yield file.relative_to(root).as_posix(), content


def import_selected(source, kind, selection=None):
    if kind not in ('pmx', 'vrm', 'live2d'):
        raise ValueError('Choose PMX, VRM or Live2D.')
    if selection is None:
        import tkinter
        from tkinter import filedialog
        window = tkinter.Tk()
        window.withdraw()
        window.attributes('-topmost', True)
        try:
            selection = (filedialog.askopenfilename(title='Import a VRM character', filetypes=[('VRM model', '*.vrm')], parent=window)
                         if kind == 'vrm' else filedialog.askdirectory(title='Choose the complete ' + kind.upper() + ' model folder', parent=window))
        finally:
            window.destroy()
    if not selection:
        return {'cancelled': True}
    sys.path.insert(0, str(source))
    from model_import import import_package, MAX_BYTES, MAX_FILES
    return import_package(source / 'models', package_files(selection, MAX_BYTES, MAX_FILES), kind)


def install_unity_lifecycle(module):
    """Account/model UI starts without loading AI, audio, camera or automations."""
    service_context = None
    original_lifetime = module.app.router.lifespan_context
    service_lock = asyncio.Lock()
    mode = 'Companion'
    revision = 0
    from starlette.responses import JSONResponse

    @asynccontextmanager
    async def lifetime(app):
        try:
            yield
        finally:
            async with service_lock:
                if service_context is not None:
                    await service_context.__aexit__(None, None, None)
                else:
                    # Cold VTuber sessions may start outputs without ever starting AI services.
                    for name, member, action in (
                        ('live_chat', 'chat', 'stop'), ('spout_output', 'output', 'stop'),
                        ('ndi_output', 'output', 'stop'), ('virtual_camera', 'output', 'stop'),
                        ('nvidia_ar_tracker', 'tracker', 'stop'), ('nvidia_expressionapp_tracker', 'tracker', 'stop'),
                        ('vmc_sender', 'sender', 'stop'), ('cubism_editor', 'cubism_editor', 'disconnect'),
                        ('screen_audio', 'listener', 'stop'),
                    ):
                        loaded = sys.modules.get(name)
                        target = getattr(loaded, member, None)
                        method = getattr(target, action, None)
                        if callable(method):
                            try:
                                await asyncio.to_thread(method)
                            except Exception:
                                print(f'Could not stop owned {name} service cleanly.', file=sys.stderr)
                    rvc = sys.modules.get('rvc_manager')
                    if rvc is not None:
                        await asyncio.to_thread(rvc.stop_rvc_service)

    @module.app.get('/api/unity/status')
    async def service_status():
        return {'services_started': service_context is not None, 'mode': mode,
                'tracking_only': getattr(module.app.state, 'tracking_only', False)}

    @module.app.post('/api/unity/mode')
    async def set_mode(payload: dict):
        nonlocal mode, revision
        requested, sequence = payload.get('mode'), payload.get('revision')
        if requested not in ('Companion', 'VTuber', 'Agent') or type(sequence) is not int or sequence < 1:
            return JSONResponse({'error': 'Choose a valid mode and revision.'}, 400)
        async with service_lock:
            if sequence <= revision:
                return {'ok': True, 'mode': mode, 'stale': True}
            revision = sequence
            if service_context is not None:
                result = await module.set_tracking_mode({'enabled': requested == 'VTuber'})
                if getattr(result, 'status_code', 200) >= 400:
                    return result
            else:
                module.app.state.tracking_only = requested == 'VTuber'
                module.app.state.ai_unloaded = requested == 'VTuber'
            mode = requested
            return {'ok': True, 'mode': mode, 'tracking_only': requested == 'VTuber'}

    @module.app.post('/api/unity/start-services')
    async def start_services():
        nonlocal service_context
        async with service_lock:
            if mode == 'VTuber':
                return JSONResponse({'error': 'AI conversation stays off in VTuber mode.'}, 409)
            if service_context is None:
                candidate = original_lifetime(module.app)
                # Reuse the saved provider without inheriting the reference app's last tracking-only session.
                previous = os.environ.get('SAKURA_START_MODE')
                os.environ['SAKURA_START_MODE'] = 'agent'
                try:
                    await candidate.__aenter__()
                finally:
                    if previous is None:
                        os.environ.pop('SAKURA_START_MODE', None)
                    else:
                        os.environ['SAKURA_START_MODE'] = previous
                service_context = candidate
        return {'ok': True}

    @module.app.post('/api/unity/start-voice')
    async def start_voice():
        async with service_lock:
            if mode != 'VTuber':
                return JSONResponse({'error': 'Use normal conversation voice controls in Companion mode.'}, 409)
            if getattr(module, 'tts', None) is None:
                from tts import TextToSpeech
                cfg = module.get_settings()
                voice = TextToSpeech()
                voice.set_voice_preset(cfg.get('selected_voice', 'kokoro_af_heart'), cfg.get('tts_pitch'), cfg.get('tts_rate'))
                if cfg.get('selected_voice', '').startswith('rvc_') and cfg.get('rvc_model'):
                    voice.set_rvc_model(str(module.BASE_DIR / cfg['rvc_model']),
                                        str(module.BASE_DIR / cfg['rvc_index']) if cfg.get('rvc_index') else None,
                                        pitch=cfg.get('rvc_pitch', 0), index_rate=cfg.get('rvc_index_rate', .75))
                module.tts = voice
            if getattr(module, 'stt_loader', None) is None:
                def load_stt():
                    from stt import SpeechToText
                    module.stt = SpeechToText()
                module.stt_loader = load_stt
            return {'ok': True}

    module.app.router.lifespan_context = lifetime


def serve(source, edition='companion'):
    os.environ['SAKURA_DESKTOP'] = '1'
    sys.path.insert(0, str(source))
    os.chdir(source)
    if edition == 'vtuber':
        import product
        product.EDITION = 'vtuber'
        product.DISPLAY_NAME = 'Sakura VTuber'
    # Bind before publishing the address so another process cannot take this port.
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.bind(('127.0.0.1', 0))
    sock.listen(128)
    sock.set_inheritable(False)
    import app
    import gui_server
    import local_api_security
    import uvicorn
    gui_server.port = sock.getsockname()[1]
    install_unity_lifecycle(app)
    from unity_tracking_api import install as install_tracking
    install_tracking(app.app, source)
    print('SAKURA_BRIDGE ' + json.dumps({'port': gui_server.port,
          'token': local_api_security.DESKTOP_TOKEN}), flush=True)
    server = uvicorn.Server(uvicorn.Config(app.app, log_level='warning', access_log=False))
    def watch_parent():
        sys.stdin.read()
        server.should_exit = True
    threading.Thread(target=watch_parent, daemon=True).start()
    server.run(sockets=[sock])


def texture_model(source, relative):
    if not isinstance(relative, str) or not relative or len(relative) > 2048:
        raise ValueError('Choose an imported character first.')
    root = (source / 'models').resolve()
    model = (root / relative).resolve()
    if not model.is_relative_to(root) or not model.is_file() or not (
        model.suffix.lower() in ('.pmx', '.vrm') or model.name.lower().endswith('.model3.json')
    ):
        raise ValueError('Choose an existing character inside the models folder.')
    return model


def pick_visual_asset(video=False):
    import tkinter
    from tkinter import filedialog
    window = tkinter.Tk()
    window.withdraw()
    window.attributes('-topmost', True)
    try:
        selected = filedialog.askopenfilename(title='Choose stage video' if video else 'Choose image', parent=window,
            filetypes=[('Video', '*.mp4 *.webm *.mov')] if video else [('Images', '*.png *.jpg *.jpeg *.bmp *.tga *.webp *.dds *.sph *.spa')])
    finally:
        window.destroy()
    if not selected:
        return {'cancelled': True}
    file = Path(selected).resolve()
    if not file.is_file() or str(file).startswith('\\\\'):
        raise ValueError('Choose a file stored on this PC.')
    if video:
        if file.suffix.lower() not in ('.mp4', '.webm', '.mov') or file.stat().st_size > 2 * 1024**3:
            raise ValueError('Choose an MP4, WebM or MOV video under 2 GB.')
        return {'path': str(file), 'name': file.name}
    from PIL import Image
    import base64
    import io
    if file.stat().st_size > 64 * 1024 * 1024:
        raise ValueError('Image exceeds 64 MB.')
    with Image.open(file) as image:
        if image.width * image.height > 16777216:
            raise ValueError('Image exceeds 16 megapixels.')
        encoded = io.BytesIO()
        image.convert('RGBA').save(encoded, format='PNG')
    return {'image': base64.b64encode(encoded.getvalue()).decode('ascii'), 'name': file.name}


def pmx_tool(source, tool, payload):
    """Explicit native UI actions; never run a shell or load AI services."""
    sys.path.insert(0, str(source))
    if tool in ('backdrop-image', 'backdrop-video'):
        return pick_visual_asset(tool == 'backdrop-video')
    if tool == 'texture':
        texture_model(source, payload.get('model'))
        return pick_visual_asset()
    from pmx_editor import model_file, open_model
    relative = payload.get('model')
    model_file(relative)
    settings = json.loads((source / 'settings.json').read_text(encoding='utf-8-sig'))
    if tool == 'editor':
        return open_model(relative, settings.get('pmx_editor_path'))
    if tool == 'mmd':
        from mmd_launcher import open_mmd
        return open_mmd(settings.get('mmd_path'), relative)
    if tool == 'reference':
        from pmx_appearance_reference import capture_reference
        return capture_reference(relative, payload.get('direction', [0, 0, 1]), settings.get('pmx_editor_path'))
    raise ValueError('Unsupported PMX tool.')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--source', required=True)
    parser.add_argument('--serve', action='store_true')
    parser.add_argument('--edition', choices=('companion', 'vtuber'), default='companion')
    parser.add_argument('--import-format', choices=('pmx', 'vrm', 'live2d'))
    parser.add_argument('--package')
    parser.add_argument('--pmx-tool', choices=('editor', 'mmd', 'reference', 'texture', 'backdrop-image', 'backdrop-video'))
    args = parser.parse_args()
    try:
        source = validate_source(args.source)
        if args.pmx_tool:
            payload = json.loads(sys.stdin.readline(16385))
            if not isinstance(payload, dict):
                raise ValueError('Invalid PMX request.')
            print(json.dumps(pmx_tool(source, args.pmx_tool, payload), ensure_ascii=True), flush=True)
        elif args.import_format:
            print(json.dumps(import_selected(source, args.import_format, args.package), ensure_ascii=True), flush=True)
        elif args.serve:
            serve(source, args.edition)
        else:
            print(json.dumps({'items': read_catalog(source)}, ensure_ascii=True), flush=True)
    except (OSError, ValueError, RuntimeError) as error:
        print(json.dumps({'error': str(error)}), flush=True)
        return 1
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
