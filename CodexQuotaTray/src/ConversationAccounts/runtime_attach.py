"""Run the installed Store app and attach account controls without patching its files.
Requires the Python websockets package. Debugging listens on loopback only.
"""
from __future__ import annotations
import argparse, asyncio, json, os, pathlib, secrets, socket, subprocess, sys, time, urllib.request, urllib.parse, urllib.error

ROOT = pathlib.Path(__file__).resolve().parent.parent


def read_json(url):
    with urllib.request.urlopen(url, timeout=3) as response:
        return json.load(response)


def control(root, token, method, params):
    if method not in ('state', 'select', 'switch'):
        raise ValueError('Unsupported account control operation')
    endpoint_path = pathlib.Path(os.environ.get('CA_CONTROL_ENDPOINT', str(root / 'bridge/control-endpoint.json')))
    endpoint = json.loads(endpoint_path.read_text())
    body = json.dumps({'method': method, 'params': params}).encode()
    request = urllib.request.Request(f"http://127.0.0.1:{int(endpoint['port'])}/", body,
        {'Content-Type': 'application/json', 'X-CA-Token': token})
    try:
        with urllib.request.urlopen(request, timeout=120) as response:
            answer = json.load(response)
    except urllib.error.HTTPError as error:
        try:
            detail = json.load(error).get('error')
        except (ValueError, OSError):
            detail = None
        raise RuntimeError(detail or str(error)) from error
    if 'error' in answer:
        raise RuntimeError(answer['error'])
    return answer['result']


def desktop_files(method, **params):
    import ctypes
    from ctypes import wintypes
    session = wintypes.DWORD()
    if not ctypes.windll.kernel32.ProcessIdToSessionId(os.getpid(), ctypes.byref(session)):
        raise OSError('Cannot determine desktop session')
    pipe = r'\\.\pipe\CodexDesktopFiles-' + str(session.value)
    if not ctypes.windll.kernel32.WaitNamedPipeW(pipe, 3000):
        raise RuntimeError('Start DesktopFileBridge.exe as the Windows user who owns your desktop, then try again.')
    with open(pipe, 'r+b', buffering=0) as stream:
        stream.write((json.dumps({'method': method, **params}) + '\n').encode())
        data = bytearray()
        while not data.endswith(b'\n'):
            chunk = stream.read(1)
            if not chunk: raise RuntimeError('Desktop file helper disconnected')
            data.extend(chunk)
            if len(data) > 1024 * 1024: raise RuntimeError('Desktop file response too large')
    result = json.loads(data)
    if 'error' in result: raise RuntimeError(result['error'])
    return result['result']


class Attachment:
    def __init__(self, root, token, ui, pid=None):
        self.root, self.token, self.ui = root, token, ui
        self.sequence, self.pending, self.jobs = 0, {}, set()
        self.trusted_contexts = set()
        self.pid = pid
        self.consumed_drops = set()
        self.binding = '__ca_' + secrets.token_hex(12)
        self.settle = '__ca_reply_' + secrets.token_hex(12)

    async def call(self, method, params=None):
        self.sequence += 1
        request_id = self.sequence
        future = asyncio.get_running_loop().create_future()
        self.pending[request_id] = future
        try:
            await self.ws.send(json.dumps({'id': request_id, 'method': method, 'params': params or {}}))
            return await asyncio.wait_for(future, 15)
        finally:
            self.pending.pop(request_id, None)

    async def answer(self, event):
        if event.get('executionContextId') not in self.trusted_contexts:
            return
        try:
            message = json.loads(event['payload'])
            request_id = message['id']
            if message['method'] == 'desktop/reveal':
                url = message.get('params', {}).get('url', '')
                parsed = urllib.parse.urlparse(url)
                if parsed.scheme == 'app' and parsed.netloc == 'fs' and parsed.path.startswith('/@fs/'):
                    path = urllib.parse.unquote(parsed.path[len('/@fs/'):])
                elif parsed.scheme == 'file' and not parsed.netloc:
                    path = urllib.request.url2pathname(parsed.path)
                else: raise ValueError('This image does not expose a local file path.')
                original = pathlib.Path(path)
                existing = original
                while not existing.exists() and existing.parent != existing:
                    existing = existing.parent
                if not existing.exists() or existing == existing.parent:
                    raise FileNotFoundError('The referenced file and its containing folder no longer exist.')
                result = await asyncio.to_thread(desktop_files, 'reveal', path=str(existing))
                if existing != original:
                    result['missingFile'] = True
                    result['openedFolder'] = str(existing)
            elif message['method'] == 'desktop/browser':
                url = message.get('params', {}).get('url', '')
                parsed = urllib.parse.urlparse(url)
                if len(url) > 16384 or any(ord(c) < 32 for c in url) or parsed.scheme not in ('http', 'https') or not parsed.hostname:
                    raise ValueError('Only HTTP and HTTPS browser links are supported')
                result = await asyncio.to_thread(desktop_files, 'openBrowser', url=url)
            elif message['method'] == 'desktop/files':
                result = await asyncio.to_thread(desktop_files, 'files')
                if result.get('files'):
                    coords = await self.call('Runtime.evaluate', {'expression': '(()=>{const e=[...document.querySelectorAll(\'[contenteditable="true"][role="textbox"],[contenteditable="true"],textarea\')].find(e=>{const r=e.getBoundingClientRect();return r.width>=40&&r.height>=15&&!e.disabled&&e.getAttribute(\'aria-hidden\')!==\'true\'});if(!e)throw Error("Open a chat composer first");const r=e.getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2}})()', 'returnByValue': True})
                    point = coords.get('result', {}).get('value')
                    if not point: raise RuntimeError('Could not find the chat composer')
                    for kind in ('dragEnter', 'dragOver', 'drop'):
                        await self.call('Input.dispatchDragEvent', {'type': kind, **point, 'data': {'items': [], 'files': result['files'], 'dragOperationsMask': 1}})
            else:
                result = await asyncio.to_thread(control, self.root, self.token, message['method'], message.get('params') or {})
            response = {'id': request_id, 'result': result}
        except Exception as error:
            if 'request_id' not in locals(): return
            response = {'id': request_id, 'error': str(error)}
        try:
            await self.call('Runtime.evaluate', {'expression': f'window[{json.dumps(self.settle)}]({json.dumps(response)})',
                'contextId': event['executionContextId']})
        except Exception:
            pass  # The document may have navigated while the router was replying.

    async def receive(self):
        try:
            async for raw in self.ws:
                message = json.loads(raw)
                future = self.pending.get(message.get('id'))
                if future and not future.done():
                    if 'error' in message: future.set_exception(RuntimeError(str(message['error'])))
                    else: future.set_result(message.get('result', {}))
                elif message.get('method') == 'Runtime.executionContextCreated':
                    context = message['params']['context']
                    if context.get('origin') == 'app://-' and context.get('auxData', {}).get('isDefault'):
                        self.trusted_contexts.add(context['id'])
                elif message.get('method') == 'Runtime.executionContextDestroyed':
                    self.trusted_contexts.discard(message['params']['executionContextId'])
                elif message.get('method') == 'Runtime.executionContextsCleared':
                    self.trusted_contexts.clear()
                elif message.get('method') == 'Runtime.bindingCalled' and message['params']['name'] == self.binding:
                    job = asyncio.create_task(self.answer(message['params']))
                    self.jobs.add(job)
                    job.add_done_callback(self.jobs.discard)
        finally:
            for future in self.pending.values():
                if not future.done(): future.set_exception(ConnectionError('App debugging connection closed'))

    async def composer_snapshot(self):
        result = await self.call('Runtime.evaluate', {'expression': '''(() => {
            if(location.pathname!=="/index.html" || /avatar-overlay|detached-window/.test(location.search))return null;
            const e=[...document.querySelectorAll('[contenteditable="true"][role="textbox"],[contenteditable="true"],textarea')].find(e=>{const r=e.getBoundingClientRect();return r.width>=40&&r.height>=15&&!e.disabled&&e.getAttribute('aria-hidden')!=='true'});
            if(!e||e.disabled||e.getAttribute('aria-disabled')==='true')return null;
            const r=e.getBoundingClientRect();
            if(r.width<40||r.height<15||r.x<0||r.y<0||r.right>innerWidth||r.bottom>innerHeight)return null;
            const row=document.querySelector('[data-app-action-sidebar-thread-active="true"][data-app-action-sidebar-thread-id]');
            return {x:r.x,y:r.y,width:r.width,height:r.height,viewWidth:innerWidth,viewHeight:innerHeight,
                context:location.href+'|'+(row?.getAttribute('data-app-action-sidebar-thread-id')||'')};
        })()''', 'returnByValue': True})
        return result.get('result', {}).get('value')

    async def seamless_drops(self):
        if not self.pid: return
        while True:
            try:
                snapshot = await self.composer_snapshot()
                if not snapshot:
                    await asyncio.sleep(.5)
                    continue
                # Accept drops across the chat viewport; deliver to the visible composer.
                target = {**snapshot, 'x': 0, 'y': 0, 'width': snapshot['viewWidth'],
                          'height': snapshot['viewHeight'], 'pid': self.pid, 'client': self.binding}
                reply = await asyncio.to_thread(desktop_files, 'target', target=target)
                drop = reply.get('drop')
                if drop and drop.get('Client') == self.binding:
                    identity = drop['Id']
                    if identity not in self.consumed_drops:
                        # Never retry an uncertain dispatch: that could duplicate attachments.
                        self.consumed_drops.add(identity)
                        try:
                            current = await self.composer_snapshot()
                            if not current or current['context'] != drop['Context']:
                                raise RuntimeError('The chat changed during the drag')
                            point = {'x': current['x']+current['width']/2, 'y':current['y']+current['height']/2}
                            for kind in ('dragEnter','dragOver','drop'):
                                await self.call('Input.dispatchDragEvent', {'type':kind, **point, 'data':{'items':[], 'files':drop['Files'], 'dragOperationsMask':1}})
                        except Exception as error:
                            print('File drop was not delivered:', str(error), file=sys.stderr, flush=True)
                            await asyncio.to_thread(desktop_files, 'dropError')
                    await asyncio.to_thread(desktop_files, 'ack', id=identity)
            except (OSError, RuntimeError, ConnectionError, TimeoutError):
                # A missing helper disables only seamless drops, never normal chat controls.
                await asyncio.sleep(2)
            await asyncio.sleep(.35)

    async def verify_ready(self):
        for attempt in range(60):
            await asyncio.sleep(2)
            try:
                result = await self.call('Runtime.evaluate', {'expression': '(async()=>{const h=document.querySelector("#conversation-accounts-controls");if(!h||h.getBoundingClientRect().width===0)return false;return (await window.conversationAccounts.state()).accounts.length>0})()', 'awaitPromise':True, 'returnByValue':True})
            except Exception:
                continue
            if result.get('result', {}).get('value') is True:
                (self.root / 'bridge/runtime-attachment-ready.json').write_text(json.dumps({'ready': True, 'time': time.time(), 'controllerPid': os.getpid(), 'desktopPid': self.pid}))
                config = json.loads((self.root / 'bridge/router-config.json').read_text())
                if config.get('retireCopiedDesktop'):
                    result = await asyncio.to_thread(subprocess.run, ['powershell.exe', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', str(self.root / 'bridge/retire_desktop_copy.ps1'), '-Root', str(self.root)], capture_output=True, text=True, creationflags=subprocess.CREATE_NO_WINDOW)
                    print(result.stdout or result.stderr, file=sys.stderr, flush=True)
                return
        print('Account selector did not become ready within two minutes.', file=sys.stderr, flush=True)

    async def run(self, url):
        from websockets.asyncio.client import connect
        async with connect(url, max_size=8*1024*1024) as ws:
            self.ws = ws
            reader = asyncio.create_task(self.receive())
            try:
                await self.call('Runtime.enable')
                await self.call('Page.enable')
                # A reconnect must reattach the existing API's binding: UI
                # closures and outstanding requests still reference that name.
                existing = await self.call('Runtime.evaluate', {'expression': '''(() => {
                    if (!window.conversationAccounts) return null;
                    const keys = Object.keys(window);
                    const binding = keys.find(k => /^__ca_[0-9a-f]{24}$/.test(k));
                    const settle = keys.find(k => /^__ca_reply_[0-9a-f]{24}$/.test(k));
                    return binding && settle ? {binding, settle} : null;
                })()''', 'returnByValue': True})
                names = existing.get('result', {}).get('value')
                if names:
                    self.binding, self.settle = names['binding'], names['settle']
                await self.call('Runtime.addBinding', {'name': self.binding})
                script = '''(() => {
                  if (location.origin !== 'app://-' || location.pathname !== '/index.html' || /detached-window/.test(new URL(location.href).searchParams.get('initialRoute') || '')) return;
                  if (window.conversationAccounts) return;
                  const pending = new Map(); let sequence = 0;
                  window[SETTLE] = response => { const p=pending.get(response.id); if(!p)return;
                    pending.delete(response.id); clearTimeout(p.timer);
                    response.error ? p.reject(new Error(response.error)) : p.resolve(response.result); };
                  const invoke = (method, params) => new Promise((resolve,reject) => {
                    const id=++sequence;
                    const timer=setTimeout(()=>{pending.delete(id);reject(new Error('Account router timed out'));},305000);
                    pending.set(id,{resolve,reject,timer});
                    window[BINDING](JSON.stringify({id,method,params}));
                  });
                  window.conversationAccounts = Object.freeze({state:()=>invoke('state',{}),
                    select:account=>invoke('select',{account}), switch:(threadId,account)=>invoke('switch',{threadId,account}), desktopFiles:()=>invoke('desktop/files',{}), reveal:url=>invoke('desktop/reveal',{url}), openBrowser:url=>invoke('desktop/browser',{url})});
                })();'''.replace('SETTLE', json.dumps(self.settle)).replace('BINDING', json.dumps(self.binding))
                # Register on navigation as well as the current document.
                script += '\n(() => { const start = () => {' + (self.root / 'bridge/desktop-files-ui.js').read_text() + '\nif (!location.search.includes(\"avatar-overlay\")) {' + self.ui + '\n}' + '\n}; if (document.readyState === \"loading\") document.addEventListener(\"DOMContentLoaded\", start, {once:true}); else start(); })();'
                script += '\n' + (self.root / 'bridge/controller-voice.js').read_text(encoding='utf-8')
                script += '\n' + (self.root / 'bridge/voice-readiness.js').read_text(encoding='utf-8')
                await self.call('Page.addScriptToEvaluateOnNewDocument', {'source': script})
                result = await self.call('Runtime.evaluate', {'expression': script})
                if 'exceptionDetails' in result: raise RuntimeError(str(result['exceptionDetails']))
                drop_monitor = asyncio.create_task(self.seamless_drops())
                self.jobs.add(drop_monitor)
                drop_monitor.add_done_callback(self.jobs.discard)
                verification = asyncio.create_task(self.verify_ready())
                self.jobs.add(verification)
                verification.add_done_callback(self.jobs.discard)
                await reader
            finally:
                reader.cancel()
                for job in self.jobs: job.cancel()


async def attach(root, port, token, duration=None, pid=None):
    ui = (root / 'bridge/accounts-ui.js').read_text() + '\n' + (root / 'bridge/quota-gate.js').read_text()
    from devtools_attach import serve as serve_devtools
    devtools_task = asyncio.create_task(serve_devtools(root, port))
    tasks = {}
    started = time.monotonic()
    process_handle = None
    if pid and os.name == 'nt':
        import ctypes
        kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        kernel.OpenProcess.argtypes = [ctypes.c_uint32, ctypes.c_int, ctypes.c_uint32]
        kernel.OpenProcess.restype = ctypes.c_void_p
        kernel.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
        kernel.CloseHandle.argtypes = [ctypes.c_void_p]
        process_handle = kernel.OpenProcess(0x100000, False, pid)
        if not process_handle:
            raise OSError(ctypes.get_last_error(), f'Cannot monitor desktop process {pid}')
    try:
        while duration is None or time.monotonic()-started < duration:
            if process_handle and kernel.WaitForSingleObject(process_handle, 0) != 258:
                print(f'Desktop process {pid} exited; stopping account controls.', file=sys.stderr, flush=True)
                break
            try:
                targets = await asyncio.to_thread(read_json, f'http://127.0.0.1:{port}/json/list')
                for target in targets:
                    if target.get('type') != 'page' or not target.get('url', '').startswith('app://-/index.html'): continue
                    route = urllib.parse.parse_qs(urllib.parse.urlparse(target['url']).query).get('initialRoute', [''])[0]
                    if 'detached-window' in route: continue
                    key = target['id']
                    if key in tasks and tasks[key].done():
                        try: tasks[key].result()
                        except Exception as error: print('Attachment retry:', type(error).__name__, file=sys.stderr, flush=True)
                        del tasks[key]
                    if key not in tasks:
                        tasks[key] = asyncio.create_task(Attachment(root, token, ui, pid=pid).run(target['webSocketDebuggerUrl']))
            except (OSError, ValueError):
                pass
            await asyncio.sleep(1)
    finally:
        devtools_task.cancel()
        await asyncio.gather(devtools_task, return_exceptions=True)
        for task in tasks.values(): task.cancel()
        await asyncio.gather(*tasks.values(), return_exceptions=True)
        if process_handle: kernel.CloseHandle(process_handle)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--root', type=pathlib.Path, default=ROOT)
    parser.add_argument('--port', type=int, required=True)
    parser.add_argument('--pid', type=int)
    args = parser.parse_args()
    token = os.environ['CA_CONTROL_TOKEN']
    asyncio.run(attach(args.root, args.port, token, pid=args.pid))

if __name__ == '__main__': main()
