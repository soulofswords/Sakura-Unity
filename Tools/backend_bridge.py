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


def install_unity_lifecycle(module):
    """Account/model UI starts without loading AI, audio, camera or automations."""
    service_context = None
    service_lock = asyncio.Lock()

    @asynccontextmanager
    async def lifetime(app):
        try:
            yield
        finally:
            async with service_lock:
                if service_context is not None:
                    await service_context.__aexit__(None, None, None)

    @module.app.get('/api/unity/status')
    async def service_status():
        return {'services_started': service_context is not None}

    @module.app.post('/api/unity/start-services')
    async def start_services():
        nonlocal service_context
        async with service_lock:
            if service_context is None:
                candidate = module.lifespan(module.app)
                await candidate.__aenter__()
                service_context = candidate
        return {'ok': True}

    module.app.router.lifespan_context = lifetime


def serve(source):
    os.environ['SAKURA_DESKTOP'] = '1'
    sys.path.insert(0, str(source))
    os.chdir(source)
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
    print('SAKURA_BRIDGE ' + json.dumps({'port': gui_server.port,
          'token': local_api_security.DESKTOP_TOKEN}), flush=True)
    server = uvicorn.Server(uvicorn.Config(app.app, log_level='warning', access_log=False))
    def watch_parent():
        sys.stdin.read()
        server.should_exit = True
    threading.Thread(target=watch_parent, daemon=True).start()
    server.run(sockets=[sock])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--source', required=True)
    parser.add_argument('--serve', action='store_true')
    args = parser.parse_args()
    try:
        source = validate_source(args.source)
        if args.serve:
            serve(source)
        else:
            print(json.dumps({'items': read_catalog(source)}, ensure_ascii=True), flush=True)
    except (OSError, ValueError) as error:
        print(json.dumps({'error': str(error)}), flush=True)
        return 1
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
