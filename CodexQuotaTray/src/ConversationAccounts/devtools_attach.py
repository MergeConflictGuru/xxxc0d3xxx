"""Open Electron inspector targets directly; no desktop file helper required."""
import asyncio
import json
import pathlib
import secrets
import urllib.parse
import urllib.request


async def serve_target(root, target, relay_port):
    from websockets.asyncio.client import connect
    binding = '__ca_devtools_' + secrets.token_hex(12)
    address = urllib.parse.urlparse(target['webSocketDebuggerUrl'])
    inspector = f'http://{address.netloc}/devtools/inspector.html?' + urllib.parse.urlencode({'ws': f'127.0.0.1:{relay_port}' + address.path})
    script = (root / 'bridge/devtools-ui.js').read_text().replace('__CA_OPEN_DEVTOOLS__', '() => window[' + json.dumps(binding) + ']("open")')
    async with connect(target['webSocketDebuggerUrl']) as ws:
        sequence = 0
        async def send(method, params):
            nonlocal sequence
            sequence += 1
            await ws.send(json.dumps({'id': sequence, 'method': method, 'params': params}))
        await send('Runtime.enable', {})
        await send('Runtime.addBinding', {'name': binding})
        await send('Page.addScriptToEvaluateOnNewDocument', {'source': script})
        await send('Runtime.evaluate', {'expression': script})
        contexts = set()
        async for raw in ws:
            message = json.loads(raw)
            method, params = message.get('method'), message.get('params', {})
            if method == 'Runtime.executionContextCreated':
                context = params['context']
                if context.get('origin') == 'app://-' and context.get('auxData', {}).get('isDefault'):
                    contexts.add(context['id'])
            elif method == 'Runtime.executionContextsCleared':
                contexts.clear()
            elif method == 'Runtime.executionContextDestroyed':
                contexts.discard(params['executionContextId'])
            elif method == 'Runtime.bindingCalled' and params.get('name') == binding and params.get('executionContextId') in contexts and params.get('payload') == 'open':
                await send('Target.createTarget', {'url': inspector})
            elif 'error' in message:
                print('DevTools:', message['error'], flush=True)


async def serve(root, port):
    from websockets.asyncio.server import serve as websocket_server
    from websockets.asyncio.client import connect
    import re
    async def relay(client):
        path = client.request.path
        if not re.fullmatch(r'/devtools/page/[A-Fa-f0-9]+', path):
            await client.close(1008, 'Unsupported inspector target')
            return
        try:
            async with connect(f'ws://127.0.0.1:{port}{path}', max_size=None) as upstream:
                async def pump(source, destination):
                    async for message in source:
                        await destination.send(message)
                jobs = [asyncio.create_task(pump(client, upstream)), asyncio.create_task(pump(upstream, client))]
                try:
                    await asyncio.wait(jobs, return_when=asyncio.FIRST_COMPLETED)
                finally:
                    for job in jobs: job.cancel()
                    await asyncio.gather(*jobs, return_exceptions=True)
        except Exception as error:
            print('DevTools relay:', type(error).__name__, flush=True)
    server = await websocket_server(relay, '127.0.0.1', 0, origins=[f'http://127.0.0.1:{port}'], max_size=None)
    relay_port = server.sockets[0].getsockname()[1]
    tasks = {}
    def targets():
        with urllib.request.urlopen(f'http://127.0.0.1:{port}/json/list', timeout=3) as response:
            return json.load(response)
    try:
        while True:
            try:
                for target in await asyncio.to_thread(targets):
                    if target.get('type') != 'page' or not target.get('url', '').startswith('app://-/index.html'):
                        continue
                    key = target['id']
                    if key in tasks and tasks[key].done():
                        try: tasks[key].result()
                        except Exception as error: print('DevTools retry:', type(error).__name__, flush=True)
                        del tasks[key]
                    if key not in tasks:
                        tasks[key] = asyncio.create_task(serve_target(root, target, relay_port))
            except OSError:
                return
            await asyncio.sleep(1)
    finally:
        server.close()
        await server.wait_closed()
        for task in tasks.values(): task.cancel()
        await asyncio.gather(*tasks.values(), return_exceptions=True)


if __name__ == '__main__':
    root = pathlib.Path(__file__).resolve().parent.parent
    metadata = json.loads((root / 'bridge/installed-desktop-process.json').read_text())
    asyncio.run(serve(root, metadata['port']))
