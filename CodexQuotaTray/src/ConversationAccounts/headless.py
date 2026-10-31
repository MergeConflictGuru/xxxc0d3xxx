"""Opt-in headless multi-account remote supervisor. Does not start Electron."""
import argparse
import csv
import io
import asyncio
import json
import os
import pathlib
import subprocess

from remote_inference import RemoteInference
from router import AccountRouter, claim_control_owner


def receiver_command(config, adapter, env, receiver):
    env = env.copy()
    # Desktop-only pipes and launch capabilities must not leak into the receiver.
    for key in list(env):
        if key.startswith(('CODEX_APP_TOOLS_', 'CA_CONTROL_')) or key in (
                'CODEX_THREAD_ID', 'OPENAI_API_KEY', 'CODEX_INTERNAL_ORIGINATOR_OVERRIDE'):
            env.pop(key, None)
    env['CODEX_HOME'] = config['profiles'][receiver]['home']
    env['CODEX_SQLITE_HOME'] = config['sharedStore']
    env['CODEX_CLI_PATH'] = config['engine']
    arguments = ['remote-control', '--no-daemon', '--json', '-c',
                 'sqlite_home='+json.dumps(config['sharedStore'])]
    return [config['engine'], *adapter.configure(arguments, env)], env


class HeadlessState:
    def __init__(self, router):
        self.router = router

    async def read(self):
        # The existing pin file remains authoritative; headless never overwrites it.
        if self.router.path.exists():
            saved = json.loads(self.router.path.read_text(encoding='utf-8'))
            self.router.routes = saved.get('routes', {})
        return await self.router.control('state', {})


def desktop_is_running(root):
    metadata = root / 'bridge/desktop-process.json'
    if os.name != 'nt' or not metadata.exists():
        return False
    value = json.loads(metadata.read_text(encoding='utf-8-sig'))
    pid = int(value['pid'])
    result = subprocess.run(['tasklist', '/FI', f'PID eq {pid}', '/FO', 'CSV', '/NH'],
        capture_output=True, text=True, check=True, creationflags=subprocess.CREATE_NO_WINDOW)
    expected = pathlib.Path(value.get('app', 'ChatGPT.exe')).name.casefold()
    return any(row and row[0].casefold() == expected for row in csv.reader(io.StringIO(result.stdout)))


async def run(root, config, receiver):
    if desktop_is_running(root):
        raise RuntimeError('Desktop is still running; headless has not started. Stop it before reusing its receiver profile.')
    runtime = root / '.app_tmp/headless'
    runtime.mkdir(parents=True, exist_ok=True)
    owner = claim_control_owner(runtime/'supervisor')
    if owner is None:
        raise RuntimeError('A headless supervisor is already running')
    router = AccountRouter(config, root/'bridge/thread-routes.json', lambda value: None, root)
    adapter = None
    process = None
    loop = asyncio.get_running_loop()
    try:
        await router.refresh_quotas()
        if not any(not value.get('error') and value.get('primary') for value in router.quotas.values()):
            raise RuntimeError('No account has readable quota; receiver was not started')
        adapter = RemoteInference(config, 'unused', 'unused', root).start()
        state = HeadlessState(router)
        # No additional control HTTP listener is needed in headless mode.
        adapter.state = lambda: asyncio.run_coroutine_threadsafe(state.read(), loop).result(timeout=15)
        router.task(router.quota_refresh_loop())
        command, env = receiver_command(config, adapter, os.environ, receiver)
        with (runtime/'receiver.log').open('ab') as log:
            process = await asyncio.create_subprocess_exec(*command, cwd=root, env=env,
                stdin=subprocess.DEVNULL, stdout=log, stderr=log,
                creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
            print(json.dumps({'event': 'receiver_started', 'pid': process.pid, 'receiver': receiver}), flush=True)
            code = await process.wait()
            if code:
                raise RuntimeError(f'Native receiver exited with code {code}; see {runtime / "receiver.log"}')
    finally:
        if process and process.returncode is None:
            process.terminate()
            try:
                await asyncio.wait_for(process.wait(), 10)
            except asyncio.TimeoutError:
                process.kill()
                await process.wait()
        await router.close()
        if adapter:
            await asyncio.to_thread(adapter.close)
        owner.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=pathlib.Path, default=pathlib.Path(r'C:\_coding\ConversationAccounts'))
    parser.add_argument('--receiver', help='Configured account used for phone enrollment; inference still uses Auto/pins')
    parser.add_argument('--run', action='store_true', help='Start remote receiver; stop the desktop before using the same profile')
    args = parser.parse_args()
    config = json.loads((args.root/'bridge/router-config.json').read_text(encoding='utf-8-sig'))
    receiver = args.receiver or config['main']
    if receiver not in config['profiles']:
        parser.error('Receiver is not a configured profile')
    for path in [pathlib.Path(config['engine']), *[pathlib.Path(p['home'])/'auth.json' for p in config['profiles'].values()]]:
        if not path.is_file():
            parser.error('Required local file is missing: '+str(path))
    if args.run:
        asyncio.run(run(args.root, config, receiver))
    else:
        print(json.dumps({'configuration': 'valid', 'receiver': receiver,
            'accountCount': len(config['profiles']), 'started': False,
            'processes': ['Python quota/router supervisor', 'native Codex remote receiver']}))


if __name__ == '__main__':
    main()
