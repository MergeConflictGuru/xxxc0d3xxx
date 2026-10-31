"""Launch the installed Codex desktop with runtime account controls."""
import ctypes
import hashlib
import json
import os
import pathlib
import secrets
import shutil
import socket
import subprocess
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parent.parent


def preflight():
    # Validate the controller before stopping an old app or opening a new one.
    from websockets.asyncio.client import connect
    from websockets.asyncio.server import serve
    (ROOT / 'bridge/controller-voice.js').read_text(encoding='utf-8')
    (ROOT / 'bridge/voice-readiness.js').read_text(encoding='utf-8')
    for name in ('runtime_attach.py', 'devtools_attach.py', 'router.py', 'account_store.py', 'remote_inference.py', 'persistent.py'):
        path = ROOT / 'bridge' / name
        compile(path.read_text(encoding='utf-8-sig'), str(path), 'exec')


def stop():
    metadata = ROOT / 'bridge/desktop-process.json'
    if not metadata.exists():
        return
    value = json.loads(metadata.read_text())
    pid = value['pid']
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.OpenProcess.argtypes = [ctypes.c_uint32, ctypes.c_int, ctypes.c_uint32]
    kernel.OpenProcess.restype = ctypes.c_void_p
    kernel.QueryFullProcessImageNameW.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_wchar_p, ctypes.POINTER(ctypes.c_uint32)]
    kernel.GetExitCodeProcess.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_uint32)]
    kernel.CloseHandle.argtypes = [ctypes.c_void_p]
    handle = kernel.OpenProcess(0x1000, False, pid)
    if not handle:
        return
    try:
        exit_code = ctypes.c_uint32()
        if kernel.GetExitCodeProcess(handle, ctypes.byref(exit_code)) and exit_code.value != 259:
            return
        buffer = ctypes.create_unicode_buffer(32768)
        length = ctypes.c_uint32(len(buffer))
        if not kernel.QueryFullProcessImageNameW(handle, 0, buffer, ctypes.byref(length)):
            raise RuntimeError('Cannot verify experiment process')
        actual = pathlib.Path(buffer.value).resolve()
        expected = pathlib.Path(value['app']).resolve() if value.get('mode') == 'installed-runtime' else (ROOT / 'desktop-app/ChatGPT.exe').resolve()
        if actual != expected:
            print('Ignoring stale desktop process record; PID now belongs to another executable', pid)
            return
        # Windows can reuse a PID for another instance of the same executable.
        if value.get('launchedAt'):
            created, exited, system, user = (ctypes.c_uint64() for _ in range(4))
            kernel.GetProcessTimes.argtypes = [ctypes.c_void_p] + [ctypes.POINTER(ctypes.c_uint64)] * 4
            if not kernel.GetProcessTimes(handle, *(ctypes.byref(v) for v in (created, exited, system, user))):
                raise ctypes.WinError(ctypes.get_last_error())
            created_at = created.value / 10000000 - 11644473600
            if abs(created_at - value['launchedAt']) > 10:
                print('Ignoring stale desktop process record; PID belongs to a different launch', pid)
                return
        result = subprocess.run(['taskkill', '/PID', str(pid), '/F'], capture_output=True, text=True,
            creationflags=subprocess.CREATE_NO_WINDOW)
        if result.returncode and (not kernel.GetExitCodeProcess(handle, ctypes.byref(exit_code)) or exit_code.value == 259):
            raise RuntimeError('Could not stop the previous desktop: ' + result.stderr.strip())
    finally:
        kernel.CloseHandle(handle)
    print('Stopped experimental desktop', pid)


def launch():
    pending_bridge = ROOT / 'bridge-next'
    if pending_bridge.exists():
        for binary in pending_bridge.glob('ProtocolBridge.*'):
            destination = ROOT / 'bridge' / binary.name
            # An installed bridge may still be releasing its executable mapping.
            # Reinstalling identical pending binaries is unnecessary and can fail
            # with WinError 32 after an otherwise successful desktop shutdown.
            if destination.exists() and hashlib.sha256(binary.read_bytes()).digest() == hashlib.sha256(destination.read_bytes()).digest():
                continue
            shutil.copy2(binary, destination)
    from account_store import sync
    sync(ROOT)
    config = json.loads((ROOT / 'bridge/router-config.json').read_text())
    main = config['main']
    home = pathlib.Path(config['sharedStore']).resolve()
    for profile in config['profiles'].values():
        auth = json.loads((pathlib.Path(profile['home']) / 'auth.json').read_text(encoding='utf-8-sig'))
        fingerprint = hashlib.sha256(auth['tokens']['account_id'].encode()).hexdigest()[:12]
        if fingerprint in config.get('protectedAccountFingerprints', []):
            raise RuntimeError('Original CLI account is excluded')
    return launch_installed(config)


def launch_installed(config):
    # Verify dependencies before starting a window that would have no controls.
    import websockets
    env = os.environ.copy()
    for key in list(env):
        if key.startswith('CODEX_APP_TOOLS_') or key in ('CODEX_THREAD_ID', 'CODEX_INTERNAL_ORIGINATOR_OVERRIDE', 'OPENAI_API_KEY', 'CA_BRIDGE_CONFIG', 'NODE_OPTIONS'):
            env.pop(key, None)
    from persistent import ensure, runtime
    persistent = ensure(ROOT)
    token = persistent['controlToken']
    with socket.socket() as listener:
        listener.bind(('127.0.0.1', 0))
        port = listener.getsockname()[1]
    endpoint = pathlib.Path(persistent['controlEndpoint'])
    user_data = ROOT / 'desktop-data/multi-account'
    user_data.mkdir(parents=True, exist_ok=True)
    env.update(CODEX_HOME=config['sharedStore'], CODEX_SQLITE_HOME=config['sharedStore'],
        CODEX_CLI_PATH=str(ROOT / 'bridge/ProtocolBridge.exe'), CODEX_ELECTRON_USER_DATA_PATH=str(user_data),
        CA_PERSISTENT_RECORD=str(runtime(ROOT) / 'server.json'), CA_PROJECT_ROOT=str(ROOT), CA_CONTROL_TOKEN=token, CA_CONTROL_ENDPOINT=str(endpoint), CODEX_APP_SERVER_FORCE_CLI='1')
    relay_log = ROOT / 'bridge/invisible-launcher/InvisibleLauncher.error.log'
    relay_offset = relay_log.stat().st_size if relay_log.exists() else 0
    result = subprocess.run([str(ROOT / 'bridge/invisible-launcher/InvisibleLauncher.exe'), '--hidden-powershell', '-NoProfile', '-NonInteractive', '-WindowStyle', 'Hidden', '-ExecutionPolicy', 'Bypass',
        '-File', str(ROOT / 'bridge/launch_installed.ps1'), '-Root', str(ROOT), '-UserData', str(user_data), '-Port', str(port)],
        env=env, capture_output=True, text=True, creationflags=subprocess.CREATE_NO_WINDOW, timeout=75)
    if result.returncode:
        detail = result.stderr.strip()
        if not detail and relay_log.exists():
            with relay_log.open('rb') as log:
                log.seek(relay_offset)
                detail = log.read()[-4000:].decode('utf-8', errors='replace').strip()
        raise RuntimeError('Installed Codex launch failed: ' + (detail or f'relay exit {result.returncode}'))
    metadata = json.loads((ROOT / 'bridge/installed-desktop-process.json').read_text(encoding='utf-8-sig'))
    if metadata['port'] != port:
        raise RuntimeError('Installed app launch returned stale process metadata')
    with (ROOT / 'bridge/runtime-attachment.log').open('ab') as log:
        log_offset = log.tell()
        controller = subprocess.Popen([sys.executable, str(ROOT / 'bridge/runtime_attach.py'), '--root', str(ROOT),
            '--port', str(port), '--pid', str(metadata['pid'])], env=env, stdout=log, stderr=log,
            stdin=subprocess.DEVNULL, creationflags=subprocess.CREATE_NO_WINDOW)
    metadata['controllerPid'] = controller.pid
    metadata['persistent'] = True
    (ROOT / 'bridge/desktop-process.json').write_text(json.dumps(metadata, indent=2))
    deadline = time.monotonic() + 90
    ready_path = ROOT / 'bridge/runtime-attachment-ready.json'
    while time.monotonic() < deadline:
        if controller.poll() is not None:
            with (ROOT / 'bridge/runtime-attachment.log').open('rb') as log:
                log.seek(log_offset)
                detail = log.read()[-4000:].decode('utf-8', errors='replace').strip()
            raise RuntimeError(f'Account controls exited during startup (exit {controller.returncode}). '
                + (detail or 'See bridge/runtime-attachment.log.'))
        try:
            ready = json.loads(ready_path.read_text())
            if ready.get('ready') and ready.get('desktopPid') == metadata['pid'] and ready.get('controllerPid') == controller.pid:
                break
        except (OSError, ValueError):
            pass
        time.sleep(.25)
    else:
        raise RuntimeError('GPT started but multi-user controls did not become ready within 90 seconds. See bridge/runtime-attachment.log.')
    print('Launched installed Codex with runtime account controls', metadata['pid'])


if __name__ == '__main__':
    if '--server-only' in sys.argv:
        from persistent import ensure
        preflight()
        value = ensure(ROOT)
        print('Persistent multi-account server ready; receiver', value['receiver'])
    elif '--stop-server' in sys.argv:
        from persistent import stop
        print('Persistent server stopped' if stop(ROOT) else 'Persistent server is not running')
    elif '--server-status' in sys.argv:
        from persistent import runtime, healthy
        import asyncio
        path = runtime(ROOT) / 'server.json'
        value = json.loads(path.read_text()) if path.exists() else None
        print('Persistent server running' if value and asyncio.run(healthy(value)) else 'Persistent server not running')
    elif '--stop' in sys.argv:
        stop()
    else:
        from router import claim_control_owner
        startup_owner = claim_control_owner(ROOT / 'bridge/launch-startup')
        if startup_owner is None:
            print('Multi-user GPT startup is already in progress.')
        else:
            try:
                preflight()
                stop()
                launch()
            finally:
                startup_owner.close()
