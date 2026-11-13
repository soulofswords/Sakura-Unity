"""CPU tracking router for the authenticated Unity development bridge."""
import asyncio
import base64
from concurrent.futures import ThreadPoolExecutor, TimeoutError
from contextlib import asynccontextmanager
import json
import os
from pathlib import Path
import subprocess
import threading


class TrackingProcess:
    def __init__(self, source, python=None):
        self.source = Path(source)
        self.python = Path(python or os.environ.get('SAKURA_TRACKING_PYTHON', 'C:/AI/SakuraTracking-venv/Scripts/python.exe'))
        self.process = None
        self.lock = threading.RLock()
        self.reader = ThreadPoolExecutor(max_workers=1, thread_name_prefix='SakuraTrackingReader')

    def _read(self, timeout=20):
        process = self.process
        future = self.reader.submit(process.stdout.readline)
        try:
            line = future.result(timeout)
        except TimeoutError:
            process.kill()
            raise RuntimeError('CPU tracker timed out; stop tracking and restart it.')
        if not line:
            raise RuntimeError('CPU tracker exited unexpectedly.')
        reply = json.loads(line)
        if reply.get('error'):
            raise RuntimeError(reply['error'])
        return reply

    def start(self):
        with self.lock:
            if self.process is not None and self.process.poll() is None:
                return {'ready': True}
            if not self.python.is_file():
                raise RuntimeError('Install the isolated MediaPipe runtime at C:/AI/SakuraTracking-venv or set SAKURA_TRACKING_PYTHON.')
            models = self.source / 'static' / 'vendor' / 'tracking'
            if not all((models / name).is_file() for name in ('face_landmarker.task', 'hand_landmarker.task', 'pose_landmarker_lite.task')):
                raise RuntimeError('The existing Sakura local tracking models are missing.')
            self.process = subprocess.Popen([str(self.python), '-u', str(Path(__file__).with_name('unity_tracking_worker.py')), '--models', str(models)],
                                            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                                            creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
            try:
                return self._read(45)
            except Exception:
                self.stop()
                raise

    def frame(self, jpeg, width, height, hands, pose):
        if not jpeg or len(jpeg) > 4 * 1024 * 1024 or not 1 <= width <= 1920 or not 1 <= height <= 1080:
            raise ValueError('Camera frames require bounded JPEG bytes and dimensions up to 1920x1080.')
        with self.lock:
            if self.process is None or self.process.poll() is not None:
                raise RuntimeError('Start CPU tracking first.')
            self.process.stdin.write((json.dumps({'jpeg': base64.b64encode(jpeg).decode('ascii'), 'width': width, 'height': height, 'hands': bool(hands), 'pose': bool(pose)}) + '\n').encode('utf-8'))
            self.process.stdin.flush()
            return self._read()

    def stop(self):
        with self.lock:
            process, self.process = self.process, None
            if process is not None:
                try:
                    process.stdin.close()
                    process.wait(3)
                except (OSError, subprocess.TimeoutExpired):
                    process.kill()
                    process.wait(3)
                finally:
                    process.stdout.close()
        return {'ok': True}


def install(app, source):
    from fastapi import Request
    from starlette.responses import JSONResponse
    from starlette.concurrency import run_in_threadpool
    tracker = TrackingProcess(source)
    original_lifetime = app.router.lifespan_context

    @asynccontextmanager
    async def lifetime(application):
        try:
            async with original_lifetime(application):
                yield
        finally:
            await run_in_threadpool(tracker.stop)
            tracker.reader.shutdown(wait=False, cancel_futures=True)
    app.router.lifespan_context = lifetime

    @app.post('/api/unity/tracking/start')
    async def start_tracking():
        try:
            return await run_in_threadpool(tracker.start)
        except (OSError, ValueError, RuntimeError) as error:
            return JSONResponse({'error': str(error)}, 409)

    @app.post('/api/unity/tracking/stop')
    async def stop_tracking():
        return await run_in_threadpool(tracker.stop)

    @app.post('/api/unity/tracking/frame')
    async def tracking_frame(request: Request, width: int, height: int, hands: bool = True, pose: bool = True):
        if request.headers.get('content-type', '').split(';')[0] != 'image/jpeg':
            return JSONResponse({'error': 'Expected a JPEG camera frame.'}, 415)
        try:
            data = bytearray()
            async for chunk in request.stream():
                if len(data) + len(chunk) > 4 * 1024 * 1024:
                    return JSONResponse({'error': 'Camera JPEG exceeds 4 MB.'}, 413)
                data.extend(chunk)
            return await run_in_threadpool(tracker.frame, bytes(data), width, height, hands, pose)
        except (OSError, ValueError, RuntimeError) as error:
            return JSONResponse({'error': str(error)}, 409)
    return tracker
