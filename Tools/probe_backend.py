"""Exercise existing API authorization without starting models or device services."""
import asyncio
import os
from pathlib import Path
import sys

source = Path(r'F:\AI\assistant\Sakura')
os.environ['SAKURA_DESKTOP'] = '1'
sys.path.insert(0, str(source))
import app
import gui_server
import httpx
from local_api_security import DESKTOP_TOKEN


async def main():
    transport = httpx.ASGITransport(app=app.app)
    async with httpx.AsyncClient(transport=transport, base_url=f'http://127.0.0.1:{gui_server.port}') as client:
        denied = await client.get('/api/models')
        assert denied.status_code == 403, denied.status_code
        headers = {'X-Sakura-Desktop': DESKTOP_TOKEN,
                   'Origin': f'http://127.0.0.1:{gui_server.port}'}
        allowed = await client.get('/api/models', headers=headers)
        assert allowed.status_code == 200, allowed.status_code
        identity = await client.get('/api/identity', headers=headers)
        assert identity.status_code == 200, identity.status_code
        print('PASS authenticated source API, denied anonymous API, source identity and model catalog')


if __name__ == '__main__':
    asyncio.run(main())
