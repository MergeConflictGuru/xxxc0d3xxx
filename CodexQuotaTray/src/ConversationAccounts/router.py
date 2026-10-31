"""One desktop JSON-RPC connection, multiple isolated Codex app-server accounts."""
from __future__ import annotations

import asyncio
import copy
from contextlib import closing
import hashlib
import http.server
import json
import os
import pathlib
import secrets
import shutil
import sqlite3
import subprocess
import sys
import threading
import time
import uuid
import re
import urllib.error
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parent.parent



class RpcError(Exception):
    def __init__(self, message, code=-32001):
        super().__init__(message)
        self.code = code


def atomic_json(path, value):
    path = pathlib.Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
    temp.write_text(json.dumps(value, indent=2), encoding="utf-8")
    os.replace(temp, path)


class AccountRouter:
    def __init__(self, config, state_path, output, root=ROOT):
        self.config, self.path, self.output, self.root = config, pathlib.Path(state_path), output, pathlib.Path(root)
        self.main = config["main"]
        self.display_account = self.main
        self.default = config.get("default", self.main)
        self.default_selection = "unassigned"
        self.routes = {}
        if self.path.exists():
            saved = json.loads(self.path.read_text(encoding="utf-8"))
            self.default = saved.get("default", self.default)
            self.default_selection = "unassigned"  # Draft choices never become the default for later chats.
            self.routes = saved.get("routes", {})
        if config.get('conversationSource') in ('tray-codex-home', 'shared-codex-home'):
            global_home = pathlib.Path(config['sharedStore']).resolve()
            for route in self.routes.values():
                if 'assigned' not in route:
                    path = pathlib.Path((route.get('path') or '').removeprefix('\\\\?\\')).resolve()
                    route['assigned'] = global_home not in path.parents
        self.backends, self.pending, self.callbacks = {}, {}, {}
        self.start_locks = {}
        self.database_start_lock = asyncio.Lock()
        self.shared_resources_prepared = False
        self.selection_lock = asyncio.Lock()
        self.initialize_params = None
        self.busy, self.switching = set(), set()
        self.active_turns = {}
        self.forking_accounts = set()
        self.quotas = {}
        self.quota_lock = asyncio.Lock()
        self.reverse, self.pages = {}, {}
        self.sequence = 0
        self.closed = False
        self.background = set()
        self.remote_adapters = []
        self.refresh_reverse()

    def refresh_reverse(self):
        self.reverse = {(r["account"], r["id"]): virtual for virtual, r in self.routes.items()}

    def save(self):
        atomic_json(self.path, {"version": 1, "default": self.default, "defaultSelection": self.default_selection, "routes": self.routes})
        self.refresh_reverse()

    def unique(self, prefix):
        self.sequence += 1
        return f"ca:{prefix}:{self.sequence}"

    def task(self, coro):
        task = asyncio.create_task(coro)
        self.background.add(task)
        task.add_done_callback(self.background.discard)
        return task

    def profile(self, account):
        try:
            return self.config["profiles"][account]
        except KeyError:
            raise RpcError("Unknown account")

    @staticmethod
    def read_usage(profile):
        auth_path = pathlib.Path(profile['home']) / 'auth.json'
        auth = json.loads(auth_path.read_text(encoding='utf-8-sig'))
        tokens = auth.get('tokens') or {}
        access_token = tokens.get('access_token')
        account_id = tokens.get('account_id')
        if not access_token or not account_id:
            raise ValueError('Profile login is missing its access token or account ID')
        request = urllib.request.Request(
            'https://chatgpt.com/backend-api/wham/usage',
            headers={'Authorization': 'Bearer ' + access_token,
                     'ChatGPT-Account-Id': account_id,
                     'User-Agent': 'ConversationAccounts/1.0',
                     'Accept': 'application/json'})
        class NoRedirect(urllib.request.HTTPRedirectHandler):
            def redirect_request(self, request, response, code, message, headers, new_url):
                return None
        try:
            opener = urllib.request.build_opener(NoRedirect)
            with opener.open(request, timeout=20) as response:
                payload = json.loads(response.read().decode('utf-8'))
        except urllib.error.HTTPError as error:
            if error.code == 401:
                raise ValueError('Saved access token expired; sign in again to this account') from error
            raise ValueError(f'Quota request failed (HTTP {error.code})') from error
        response_account = payload.get('account_id')
        if response_account and response_account != account_id:
            raise ValueError('Usage response belongs to a different account')
        bucket = payload.get('rate_limit') or {}

        def window(value):
            if not isinstance(value, dict):
                return None
            used = value.get('used_percent')
            duration = value.get('limit_window_seconds')
            if not isinstance(used, (int, float)) or not isinstance(duration, (int, float)) or duration <= 0:
                return None
            reset = value.get('reset_at')
            return (int((duration + 59) // 60), {'remaining': max(0, min(100, 100 - used)),
                    'resetsAt': reset if isinstance(reset, (int, float)) else None}
                    )

        windows = [window(bucket.get('primary_window')), window(bucket.get('secondary_window'))]
        return {'name': profile.get('label', ''),
                'primary': next((item for item in windows if item and item[0] == 300), (None, None))[1],
                'secondary': next((item for item in windows if item and item[0] == 10080), (None, None))[1],
                'updated': time.time(), 'error': None}

    async def refresh_quotas(self):
        async with self.quota_lock:
            async def refresh(account, profile):
                try:
                    self.quotas[account] = await asyncio.to_thread(self.read_usage, profile)
                except Exception as error:
                    previous = self.quotas.get(account, {'name': profile.get('label', ''), 'primary': None, 'secondary': None, 'updated': None})
                    previous['error'] = str(error) if isinstance(error, ValueError) else 'Unable to read quota; check account credentials and network'
                    self.quotas[account] = previous
            await asyncio.gather(*(refresh(account, profile) for account, profile in self.config['profiles'].items()))

    async def quota_refresh_loop(self):
        while not self.closed:
            await self.refresh_quotas()
            for _ in range(120):
                if self.closed:
                    return
                await asyncio.sleep(1)

    async def start(self, account):
        # Serialize shared database initialization; normal RPC calls remain concurrent.
        async with self.database_start_lock:
            if self.config.get('shareResources') and not self.shared_resources_prepared:
                script = self.root / 'bridge/share-home.ps1'
                config_path = self.root / 'bridge/router-config.json'
                result = await asyncio.to_thread(subprocess.run,
                    ['powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', str(script), '-Config', str(config_path)],
                    stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                    creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
                if result.returncode:
                    raise RpcError('Shared home migration failed: ' + result.stderr.decode(errors='replace')[-1500:])
                self.shared_resources_prepared = True
            return await self.start_account(account)

    async def start_account(self, account):
        lock = self.start_locks.setdefault(account, asyncio.Lock())
        async with lock:
            if account in self.backends:
                process = self.backends[account]
                if process.returncode is None:
                    return process
                self.backends.pop(account, None)
            if self.initialize_params is None:
                raise RpcError("Client has not initialized", -32002)
            profile = self.profile(account)
            home = pathlib.Path(profile["home"]).resolve()
            if home == (pathlib.Path.home() / ".codex").resolve():
                raise RpcError("Live Codex home is forbidden")
            auth = json.loads((home / "auth.json").read_text(encoding="utf-8-sig"))
            account_id = auth.get("tokens", {}).get("account_id")
            if not account_id:
                raise RpcError("Account requires sign-in")
            fingerprint = hashlib.sha256(account_id.encode()).hexdigest()[:12]
            if fingerprint in self.config.get("protectedAccountFingerprints", []):
                raise RpcError("The original CLI account is excluded")
            env = os.environ.copy()
            env.pop('CA_TEST_STORE', None)
            env["CODEX_HOME"] = str(home)
            env["CODEX_CLI_PATH"] = self.config["engine"]
            for key in list(env):
                if key in (
                    "CODEX_THREAD_ID", "CODEX_INTERNAL_ORIGINATOR_OVERRIDE", "OPENAI_API_KEY", "CA_CONTROL_TOKEN"
                ):
                    env.pop(key, None)
            # Shared resource junctions replace the old per-account asset copies.
            # Keep the local adapter config: its desktop bridge paths differ from the CLI.
            command = [self.config["engine"], *self.config.get("engineArgs", []), *self.config.get("serverArgs", ["app-server"])]
            if self.config.get("sharedStore"):
                store = pathlib.Path(self.config["sharedStore"]).resolve()
                store.mkdir(parents=True, exist_ok=True)
                env['CODEX_SQLITE_HOME'] = str(store)
                env["CA_TEST_STORE"] = str(store)
                command = [self.config["engine"], *self.config.get("engineArgs", []),
                           "-c", "sqlite_home=" + json.dumps(str(store)), *self.config.get("serverArgs", ["app-server"])]
            # Remote control can be owned by any account worker, not just the
            # auxiliary receiver. Its internal turns bypass handle()/dispatch().
            if os.environ.get('CA_CONTROL_TOKEN'):
                from remote_inference import RemoteInference
                endpoint = os.environ.get('CA_CONTROL_ENDPOINT', str(self.root / 'bridge/control-endpoint.json'))
                adapter = RemoteInference(self.config, endpoint, os.environ['CA_CONTROL_TOKEN'], self.root).start()
                command = [command[0], *adapter.configure(command[1:], env)]
                self.remote_adapters.append(adapter)
            process = await asyncio.create_subprocess_exec(
                *command, cwd=str(self.root), env=env,
                stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE,
                stderr=asyncio.subprocess.DEVNULL,
                limit=self.config.get('backendMessageLimitBytes', 256 * 1024 * 1024),
                creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
            )
            self.backends[account] = process
            self.task(self.read_backend(account, process))
            try:
                result = await self.rpc_started(account, "initialize", self.initialize_params)
                if account == self.main:
                    self.initialization_result = result
                reported = result.get("codexHome")
                if reported and pathlib.Path(reported).resolve() != home:
                    raise RpcError("Backend reported the wrong isolated home")
                await self.write(account, {"method": "initialized", "params": {}})
                return process
            except BaseException:
                if process.returncode is None:
                    process.terminate()
                raise

    async def write(self, account, message):
        process = self.backends[account]
        if process.returncode is not None:
            raise RpcError("Assigned account backend disconnected", -32002)
        process.stdin.write((json.dumps(message, separators=(",", ":")) + "\n").encode())
        await process.stdin.drain()

    async def rpc_started(self, account, method, params):
        request_id = self.unique("request")
        future = asyncio.get_running_loop().create_future()
        self.pending[(account, request_id)] = future
        try:
            await self.write(account, {"id": request_id, "method": method, "params": params})
            return await asyncio.wait_for(future, self.config.get("rpcTimeoutSeconds", 60))
        finally:
            self.pending.pop((account, request_id), None)

    async def rpc(self, account, method, params):
        await self.start(account)
        if method in ('thread/resume', 'thread/read', 'thread/turns/list', 'thread/items/list', 'thread/fork', 'turn/start', 'turn/steer') and self.config.get('sharedStore'):
            self.prepare_lineage(account, params.get('threadId'), params.get('path'))
        return await self.rpc_started(account, method, params)

    @staticmethod
    def link_history(source, target):
        source, target = pathlib.Path(source), pathlib.Path(target)
        target.parent.mkdir(parents=True, exist_ok=True)
        if target.exists() and os.path.samefile(source, target):
            return
        temporary = target.with_name(target.name + '.' + uuid.uuid4().hex + '.importing')
        try:
            try:
                os.link(source, temporary)
            except OSError as error:
                if getattr(error, 'winerror', None) != 17 and error.errno != 18:
                    raise
                shutil.copy2(source, temporary)
            temporary.replace(target)
        except OSError as error:
            if error.errno == 28 or getattr(error, 'winerror', None) == 112:
                raise RpcError('Not enough disk space to prepare conversation history. Free space and retry.') from error
            raise
        finally:
            if temporary.exists():
                temporary.unlink()

    def prepare_lineage(self, account, thread_id, rollout_path=None):
        store = pathlib.Path(self.config['sharedStore']).resolve()
        database = store / 'state_5.sqlite'
        if not database.exists() or not thread_id:
            return
        home = pathlib.Path(self.profile(account)['home']).resolve()
        seen = set()
        lineage_index = None
        def sources(thread):
            nonlocal lineage_index
            if lineage_index is None:
                lineage_index = {}
                homes = [store, *[pathlib.Path(profile['home']).resolve() for profile in self.config['profiles'].values()]]
                for source_home in homes:
                    for folder in ('sessions', 'archived_sessions'):
                        for candidate in (source_home / folder).rglob('*.jsonl'):
                            match = re.search(r'[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}', candidate.name)
                            if match:
                                lineage_index.setdefault(match[0], []).append(candidate)
            return lineage_index.get(thread, [])
        with closing(sqlite3.connect(database.as_uri() + '?mode=ro', uri=True)) as db:
            def resolve(thread):
                row = db.execute('select rollout_path from threads where id=?', (thread,)).fetchone()
                return pathlib.Path(row[0].removeprefix('\\\\?\\')) if row else None
            def visit(thread, path, ancestor=False):
                path = pathlib.Path(str(path).removeprefix('\\\\?\\')) if path else resolve(thread)
                if not path or not path.is_file():
                    return
                identity = str(path.resolve()).lower()
                if identity in seen:
                    return
                seen.add(identity)
                with path.open(encoding='utf-8') as stream:
                    metadata = json.loads(stream.readline()).get('payload', {})
                parent = (metadata.get('history_base') or {}).get('thread_id') or metadata.get('forked_from_id')
                if parent:
                    visit(parent, resolve(parent), True)
                    # Rebased paginated histories may refer to an older rollout with
                    # the same ID; the database points only to the newest rollout.
                    for original in sources(parent):
                            if original.resolve() == path.resolve():
                                continue
                            visit(parent, original, True)
                            dependency = home / 'sessions/lineage' / original.name
                            if dependency.resolve() == original.resolve():
                                continue
                            dependency.parent.mkdir(parents=True, exist_ok=True)
                            if not dependency.exists() or dependency.stat().st_size < original.stat().st_size:
                                self.link_history(original, dependency)
                if home not in path.resolve().parents:
                    # Only dependency snapshots are copied; authoritative rollouts stay global.
                    target = home / 'sessions/lineage' / path.name
                    target.parent.mkdir(parents=True, exist_ok=True)
                    if not target.exists() or target.stat().st_size < path.stat().st_size:
                        self.link_history(path, target)
            visit(thread_id, rollout_path)

    def translate(self, value, account, outgoing=False, context=""):
        if isinstance(value, list):
            return [self.translate(v, account, outgoing, context) for v in value]
        if not isinstance(value, dict):
            return value
        result = {}
        thread_object = context == "thread" or ("id" in value and any(k in value for k in ("cwd", "preview", "createdAt")))
        for key, item in value.items():
            identity = key in ("threadId", "parentThreadId", "rootThreadId", "conversationId") or (key == "id" and thread_object)
            if identity and isinstance(item, str):
                if outgoing:
                    route = self.routes.get(item)
                    result[key] = route["id"] if route and route["account"] == account else item
                else:
                    result[key] = self.reverse.get((account, item), item)
            else:
                result[key] = self.translate(item, account, outgoing, key)
        return result

    def remember(self, account, thread, virtual=None):
        physical = thread["id"]
        known = self.reverse.get((account, physical))
        virtual = virtual or known or physical
        existing = self.routes.get(virtual)
        if existing and (existing["account"], existing["id"]) != (account, physical):
            raise RpcError("Conversation identity collision")
        self.routes[virtual] = {"assigned": False, **(existing or {}), "account": account, "id": physical, "path": thread.get("path"), "cwd": thread.get("cwd")}
        self.save()
        return virtual

    async def read_backend(self, account, process):
        try:
            while line := await process.stdout.readline():
                message = json.loads(line)
                if "method" not in message:
                    future = self.pending.get((account, message.get("id")))
                    if future and not future.done():
                        if "error" in message:
                            error = message["error"]
                            future.set_exception(RpcError(error.get("message", "Backend error"), error.get("code", -32001)))
                        else:
                            future.set_result(message.get("result"))
                    continue
                params = message.get("params") or {}
                thread = params.get("threadId") or (params.get("thread") or {}).get("id")
                if message["method"] == "thread/started" and account in self.forking_accounts:
                    continue
                if thread and message["method"] == "thread/started" and (account, thread) not in self.reverse:
                    self.remember(account, params["thread"])
                virtual = self.reverse.get((account, thread)) if thread else None
                # Do not leak stale events from a previous owner after an account switch.
                if thread and virtual is None:
                    if "id" in message:
                        await self.write(account, {"id": message["id"], "error": {"code": -32001, "message": "Unknown conversation ownership"}})
                    continue
                if not thread and message['method'] == 'account/rateLimits/updated':
                    if account == self.display_account:
                        self.output(message)
                    continue
                # Attestation is a device service implemented by the desktop, not
                # account-global UI state. Each worker must be able to request it.
                device_callback = message["method"] == "attestation/generate" and "id" in message
                if not thread and account != self.main and not device_callback:
                    if "id" in message:
                        await self.write(account, {"id": message["id"], "error": {"code": -32601, "message": "Worker-global requests are unsupported"}})
                    continue
                if virtual and message["method"] == "turn/started":
                    self.busy.add(virtual)
                    self.active_turns[virtual] = params.get('turn', {}).get('id')
                if virtual and message["method"] == "turn/completed":
                    self.busy.discard(virtual)
                    self.active_turns.pop(virtual, None)
                result = self.translate(message, account)
                if "id" in message:
                    callback = self.unique("callback")
                    self.callbacks[callback] = (account, message["id"])
                    result["id"] = callback
                self.output(result)
        except (OSError, ValueError, asyncio.IncompleteReadError):
            pass
        finally:
            # Fail the UI immediately on a broken protocol stream; waiting for
            # a still-running backend here can otherwise leave responses spinning.
            for virtual in list(self.busy):
                if self.routes.get(virtual, {}).get('account') != account:
                    continue
                self.busy.discard(virtual)
                turn_id = self.active_turns.pop(virtual, None)
                if turn_id and not self.closed:
                    self.output({'method': 'turn/completed', 'params': {
                        'threadId': virtual, 'turn': {'id': turn_id, 'items': [],
                        'status': 'failed', 'error': {'message': 'Assigned account backend disconnected. Send again or switch accounts.',
                        'codexErrorInfo': 'other', 'additionalDetails': None}}}})
            for (owner, _), future in list(self.pending.items()):
                if owner == account and not future.done():
                    future.set_exception(RpcError("Assigned account backend disconnected", -32002))
            self.callbacks = {key: pair for key, pair in self.callbacks.items() if pair[0] != account}
            if not self.closed and self.backends.get(account) is process:
                self.backends.pop(account, None)
                if process.returncode is None:
                    try:
                        process.terminate()
                    except ProcessLookupError:
                        pass

    async def initialize(self, params):
        if self.initialize_params is not None:
            raise RpcError("Already initialized")
        self.initialize_params = copy.deepcopy(params)
        await self.start(self.main)
        # Preserve the actual engine's initialization response without initializing it twice.
        result = copy.deepcopy(self.initialization_result)
        if self.config.get('conversationSource') in ('tray-codex-home', 'shared-codex-home'):
            result['codexHome'] = str(pathlib.Path(self.config['sharedStore']).resolve())
        return result

    async def discover(self, virtual):
        if virtual in self.routes:
            return self.routes[virtual]
        if self.config.get("sharedStore"):
            result = await self.rpc(self.main, "thread/read", {"threadId": virtual, "includeTurns": False})
            thread = result["thread"]
            owner = self.owner_from_path(thread.get("path"))
            self.remember(owner, thread)
            return self.routes[virtual]
        # Unknown UUIDs are queried read-only across isolated stores, never guessed to the main account.
        found = []
        for account in self.config["profiles"]:
            try:
                result = await self.rpc(account, "thread/read", {"threadId": virtual, "includeTurns": False})
                if result.get("thread"):
                    found.append((account, result["thread"]))
            except RpcError:
                continue
        if len(found) != 1:
            raise RpcError("Conversation ownership could not be uniquely resolved")
        self.remember(*found[0])
        return self.routes[virtual]

    def owner_from_path(self, path):
        if path:
            resolved = pathlib.Path(path).resolve()
            for account, profile in self.config["profiles"].items():
                if pathlib.Path(profile["home"]).resolve() in resolved.parents:
                    return account
        return self.main

    async def list_threads(self, params):
        cursor = params.get("cursor")
        signature = json.dumps({k: v for k, v in params.items() if k != "cursor"}, sort_keys=True)
        if cursor:
            page = self.pages.pop(cursor, None)
            if not page or page["signature"] != signature:
                raise RpcError("Conversation list cursor expired; refresh the list")
        else:
            accounts = [self.main] if self.config.get("sharedStore") else self.config["profiles"]
            page = {"signature": signature, "accounts": {a: {"buffer": [], "cursor": None, "first": True} for a in accounts}}
        limit = min(max(params.get("limit") or 50, 1), 100)
        results = []
        key = "updatedAt" if params.get("sortKey") == "updated_at" else "createdAt"
        descending = params.get("sortDirection", "desc") != "asc"
        while len(results) < limit:
            async def fill(account, state):
                if not state["buffer"] and (state["first"] or state["cursor"]):
                    query = {**params, "limit": limit, "cursor": state["cursor"]}
                    if self.config.get('conversationSource') in ('tray-codex-home', 'shared-codex-home'):
                        query['useStateDbOnly'] = True
                    try:
                        response = await self.rpc(account, "thread/list", query)
                    except RpcError as error:
                        if not query.get('originators') or 'originator filtering is not supported by the local app-server' not in str(error):
                            raise
                        query.pop('originators', None)
                        response = await self.rpc(account, "thread/list", query)
                    state["first"] = False
                    state["cursor"] = response.get("nextCursor")
                    for thread in response.get("data", []):
                        physical = thread["id"]
                        # Keep superseded physical histories as rollback copies, hidden from the combined list.
                        if any({"account": account, "id": physical} in r.get("previous", []) for r in self.routes.values()):
                            continue
                        owner = self.routes.get(physical, {}).get("account", self.owner_from_path(thread.get("path"))) if self.config.get("sharedStore") else account
                        self.remember(owner, thread)
                        state["buffer"].append(self.translate(thread, owner, context="thread"))
            await asyncio.gather(*(fill(a, state) for a, state in page["accounts"].items()))
            candidates = [(a, s["buffer"][0]) for a, s in page["accounts"].items() if s["buffer"]]
            if not candidates:
                if any(s["cursor"] for s in page["accounts"].values()):
                    continue
                break
            account, item = sorted(candidates, key=lambda pair: (pair[1].get(key, 0), pair[1]["id"]), reverse=descending)[0]
            results.append(item)
            page["accounts"][account]["buffer"].pop(0)
        more = any(s["buffer"] or s["cursor"] or s["first"] for s in page["accounts"].values())
        next_cursor = "ca-list:" + uuid.uuid4().hex if more else None
        if more:
            self.pages[next_cursor] = page
            while len(self.pages) > 100:
                self.pages.pop(next(iter(self.pages)))
        return {"data": results, "nextCursor": next_cursor}

    async def switch(self, virtual, target, *, pin=True):
        route = await self.discover(virtual)
        if target == 'unassigned':
            route['assigned'] = False
            self.save()
            await self.refresh_display_account(await self.choose_account(virtual))
            return {'threadId': virtual, 'account': route['account'], 'assigned': False}
        self.profile(target)
        if route["account"] == target:
            route['assigned'] = pin
            self.save()
            await self.refresh_display_account(target)
            return {"threadId": virtual, "account": target}
        if virtual in self.busy or virtual in self.switching:
            raise RpcError("Wait for this conversation's current response to finish before switching accounts")
        self.switching.add(virtual)
        self.forking_accounts.add(target)
        try:
            if self.config.get("sharedStore"):
                path = route.get('path')
                if not path or not pathlib.Path(path).is_file():
                    source = await self.rpc(route['account'], 'thread/read', {'threadId': route['id'], 'includeTurns': False})
                    path = source['thread'].get('path') or path
                if not path or not pathlib.Path(path).is_file():
                    raise RpcError('This conversation is not saved yet. Send its first message before switching accounts.')
                resume_params = {'threadId': route['id'], 'path': path, 'excludeTurns': True}
                roots = self.project_roots(virtual)
                if roots:
                    resume_params['runtimeWorkspaceRoots'] = roots
                if route.get('cwd'):
                    resume_params['cwd'] = route['cwd']
                await self.start(target)
                try:
                    await self.rpc(route["account"], "thread/unsubscribe", {"threadId": route["id"]})
                except RpcError as error:
                    if 'thread not loaded' not in str(error).lower():
                        raise
                try:
                    resumed = await self.rpc(target, "thread/resume", resume_params)
                except BaseException:
                    await self.rpc(route["account"], "thread/resume", resume_params)
                    raise
                if resumed['thread']['id'] != route['id']:
                    raise RpcError('Backend resumed a different conversation; account assignment was not changed')
                self.routes[virtual] = {**route, "account": target, 'assigned': pin, 'path': resumed['thread'].get('path') or path}
                self.save()
                self.pages.clear()
                self.output({"method": "thread/started", "params": {"thread": self.translate(resumed["thread"], target, context="thread")}})
                await self.refresh_display_account(target)
                return {"threadId": virtual, "account": target, "historyPreserved": True}
            source = await self.rpc(route["account"], "thread/read", {"threadId": route["id"], "includeTurns": False})
            path = source["thread"].get("path")
            if not path:
                raise RpcError("This conversation has no persisted history to transfer")
            fork = await self.rpc(target, "thread/fork", {"threadId": route["id"], "path": path, "cwd": source["thread"].get("cwd")})
            thread = fork["thread"]
            previous = [*route.get("previous", []), {"account": route["account"], "id": route["id"]}]
            # thread/started may have tentatively registered the fork's physical ID.
            self.routes.pop(thread["id"], None)
            self.routes[virtual] = {"account": target, 'assigned': pin, "id": thread["id"], "path": thread.get("path"), "cwd": thread.get("cwd"), "previous": previous}
            self.save()
            self.pages.clear()
            return {"threadId": virtual, "account": target, "historyPreserved": True}
        finally:
            self.switching.discard(virtual)
            self.forking_accounts.discard(target)

    async def refresh_display_account(self, account):
        self.display_account = account
        try:
            limits = await self.rpc(account, 'account/rateLimits/read', {})
            self.output({'method': 'account/rateLimits/updated', 'params': limits})
        except RpcError:
            pass

    async def control(self, method, params):
        if method == "state":
            return {"main": self.main, "default": self.default, "defaultSelection": self.default_selection,
                    "accounts": [{"id": a, "label": p.get("label", a), "color": p.get("color", "#888888"),
                                  'quota': self.quotas.get(a)} for a, p in self.config["profiles"].items()],
                    'assignments': {v: r['account'] for v, r in self.routes.items() if r.get('assigned', True)},
                    "routes": {v: r["account"] for v, r in self.routes.items()}, "busy": list(self.busy), "modelTurnsEnabled": not self.config.get("blockModelTurns", True)}
        if method == "select":
            if params['account'] == 'unassigned':
                self.default_selection = 'unassigned'
                self.save()
                return {'account': 'unassigned'}
            self.profile(params["account"])
            self.default_selection = params['account']
            self.default = params["account"]
            self.save()
            await self.refresh_display_account(self.default)
            return {"account": self.default}
        if method == "switch":
            return await self.switch(params["threadId"], params["account"])
        raise RpcError("Unknown account control operation", -32601)

    def project_roots(self, thread):
        try:
            state = json.loads((pathlib.Path(self.config['sharedStore']) / '.codex-global-state.json').read_text())
            assignment = state.get('thread-project-assignments', {}).get(thread, {})
            project = state.get('local-projects', {}).get(assignment.get('projectId'), {})
            return [str(pathlib.Path(path).resolve()) for path in project.get('rootPaths', []) if pathlib.Path(path).is_dir()]
        except (OSError, ValueError, KeyError):
            return []

    async def archive_in_store(self, method, params, home):
        """Use Codex's own archive operation with the home owning the rollout."""
        if method not in ('thread/archive', 'thread/unarchive', 'thread/fork'):
            raise RpcError('Unsupported store maintenance operation')
        env = os.environ.copy()
        env['CODEX_HOME'] = str(home)
        env['CODEX_SQLITE_HOME'] = str(pathlib.Path(self.config['sharedStore']).resolve())
        for key in list(env):
            if key.startswith('CODEX_APP_TOOLS_') or key in ('CODEX_THREAD_ID', 'OPENAI_API_KEY', 'CA_CONTROL_TOKEN'):
                env.pop(key, None)
        command = [self.config['engine'], *self.config.get('engineArgs', []),
                   '-c', 'sqlite_home=' + json.dumps(env['CODEX_SQLITE_HOME']),
                   *self.config.get('serverArgs', ['app-server'])]
        process = await asyncio.create_subprocess_exec(*command, cwd=str(self.root), env=env,
            stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.DEVNULL,
            limit=64 * 1024 * 1024, creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
        async def send(value):
            process.stdin.write((json.dumps(value) + '\n').encode())
            await process.stdin.drain()
        async def call(request_id, operation, arguments):
            await send({'id': request_id, 'method': operation, 'params': arguments})
            while True:
                line = await asyncio.wait_for(process.stdout.readline(), self.config.get('rpcTimeoutSeconds', 90))
                if not line:
                    raise RpcError('Conversation maintenance backend disconnected')
                message = json.loads(line)
                if message.get('id') == request_id and 'method' not in message:
                    if message.get('error'):
                        raise RpcError(message['error'].get('message', 'Archive failed'), message['error'].get('code', -32001))
                    return message.get('result')
                if 'id' in message and 'method' in message:
                    await send({'id': message['id'], 'error': {'code': -32601, 'message': 'Maintenance only'}})
        try:
            await call('archive:init', 'initialize', {'clientInfo': {'name': 'conversation_archive', 'version': '1'}, 'capabilities': {'experimentalApi': True}})
            await send({'method': 'initialized', 'params': {}})
            return await call('archive:operation', method, params)
        finally:
            if process.returncode is None:
                process.terminate()
            await process.wait()

    async def choose_account(self, thread):
        if not self.quotas or any(not value.get('updated') or time.time() - value['updated'] > 120 for value in self.quotas.values()):
            await self.refresh_quotas()
        state = await self.control('state', {})
        now = time.time()
        occupied = {self.routes[t]['account'] for t in self.busy | self.switching
                    if t != thread and t in self.routes}
        candidates = []
        for account in state['accounts']:
            quota = account.get('quota') or {}
            primary, weekly = quota.get('primary'), quota.get('secondary')
            if not primary:
                continue
            remaining = primary.get('remaining', 0)
            reset = primary.get('resetsAt')
            if remaining <= 0 or not isinstance(reset, (int, float)) or reset <= now:
                continue
            if weekly and weekly.get('remaining', 0) <= 0:
                continue
            density = remaining / ((reset - now) / 3600)
            candidates.append((account['id'] not in occupied, density, account['id']))
        if not candidates:
            if not any(account.get('quota') for account in state['accounts']):
                idle = [account['id'] for account in state['accounts'] if account['id'] not in occupied]
                return self.default if self.default in idle or not idle else idle[0]
            raise RpcError('No account has usable quota data. Check account sign-in and network; quota refresh runs in the multi-account app.')
        return max(candidates)[2]

    async def handle(self, method, params):
        if method == 'turn/start' and params.get('threadId') and not self.config.get('blockModelTurns', True):
            thread = params['threadId']
            route = await self.discover(thread)
            if not route.get('assigned', True):
                # Reserve the chosen backend before allowing another send to rank accounts.
                async with self.selection_lock:
                    if thread in self.busy:
                        raise RpcError('This conversation already has an active response')
                    target = await self.choose_account(thread)
                    if route['account'] != target:
                        # A new thread has no rollout to resume on another backend yet.
                        # Its initial backend was selected by quota at thread/start.
                        # Keep that backend until the first turn persists its history.
                        path = route.get('path')
                        if path and pathlib.Path(path).is_file():
                            await self.switch(thread, target, pin=False)
                    self.routes[thread]['assigned'] = False
                    self.save()
                    self.busy.add(thread)
                try:
                    return await self.dispatch(method, params)
                except BaseException:
                    self.busy.discard(thread)
                    raise
        return await self.dispatch(method, params)

    async def dispatch(self, method, params):
        # Consume a draft's explicit choice once, before awaiting any backend work.
        # A later draft always starts in Auto; concurrent starts cannot inherit it.
        start_selection = self.default_selection if method == 'thread/start' else None
        if method == 'thread/start':
            self.default_selection = 'unassigned'
            self.save()
        if method == "initialize":
            return await self.initialize(params)
        if self.initialize_params is None:
            raise RpcError("Client has not initialized", -32002)
        if method == "thread/list":
            return await self.list_threads(params)
        if method == 'account/rateLimits/read':
            return await self.rpc(self.display_account, method, params)
        if method == "thread/loaded/list":
            responses = await asyncio.gather(*(self.rpc(a, method, {}) for a in self.backends))
            data = []
            for account, response in zip(list(self.backends), responses):
                data.extend(self.reverse.get((account, t), t) for t in response.get("data", []))
            return {"data": data, "nextCursor": None}
        if method.startswith("router/"):
            return await self.control(method.split("/", 1)[1], params)
        thread = params.get("threadId")
        if thread:
            if thread in self.switching:
                raise RpcError("This conversation is switching accounts; try again shortly")
            route = await self.discover(thread)
            account = route["account"]
            if method in ('thread/archive', 'thread/unarchive', 'thread/fork') and self.config.get('sharedStore'):
                if thread in self.busy and method != 'thread/fork':
                    raise RpcError('Wait for the current response to finish before archiving')
                path = pathlib.Path((route.get('path') or '').removeprefix('\\\\?\\')).resolve()
                homes = [pathlib.Path(self.config['sharedStore']).resolve(),
                         *[pathlib.Path(profile['home']).resolve() for profile in self.config['profiles'].values()]]
                owner_home = next((home for home in homes if home in path.parents
                                   and path.relative_to(home).parts[0] in ('sessions', 'archived_sessions')), None)
                if owner_home is None:
                    raise RpcError('Cannot locate the conversation history home for archiving')
                result = await self.archive_in_store(method, {**params, 'threadId': route['id']}, owner_home)
                if method == 'thread/fork' and isinstance(result.get('thread'), dict):
                    fork = result['thread']
                    self.remember(account, fork)
                    self.routes[fork['id']]['assigned'] = route.get('assigned', False)
                    self.save()
                self.pages.clear()
                if method in ('thread/archive', 'thread/unarchive'):
                    self.output({'method': 'thread/archived' if method == 'thread/archive' else 'thread/unarchived',
                                 'params': {'threadId': thread}})
                return self.translate(result, account)
        elif params.get("threadIds"):
            routes = [await self.discover(t) for t in params["threadIds"]]
            owners = {r["account"] for r in routes}
            if len(owners) != 1:
                raise RpcError("A batch cannot span multiple account backends")
            account = owners.pop()
        else:
            account = (start_selection if start_selection != "unassigned" else await self.choose_account(None)) if method == "thread/start" else self.main
        if self.config.get('sharedStore') and method in ('thread/start', 'thread/resume'):
            params = {**params, 'config': {**(params.get('config') or {}),
                      'sqlite_home': str(pathlib.Path(self.config['sharedStore']).resolve())}}
            if method == 'thread/resume' and thread and not params.get('path') and route.get('path'):
                params['path'] = route['path']
            if method == 'thread/resume' and thread and 'runtimeWorkspaceRoots' not in params:
                roots = self.project_roots(thread)
                if roots:
                    params['runtimeWorkspaceRoots'] = roots
        if self.config.get("blockModelTurns", True) and method in ("turn/start", "turn/steer", "thread/realtime/start"):
            raise RpcError("Model turns are disabled during protocol validation")
        if method.startswith(("turn/", "item/")) and not thread:
            raise RpcError("Conversation operation requires an explicit threadId")
        if method == "turn/start" and thread:
            self.busy.add(thread)
        try:
            result = await self.rpc(account, method, self.translate(params, account, outgoing=True))
        except BaseException:
            if method == "turn/start" and thread:
                self.busy.discard(thread)
            raise
        if isinstance(result, dict) and isinstance(result.get("thread"), dict):
            self.remember(account, result["thread"])
            if method == 'thread/resume' and self.display_account != account:
                await self.refresh_display_account(account)
            if method == 'thread/start':
                self.routes[result['thread']['id']]['assigned'] = start_selection != 'unassigned'
                self.save()
        if method == 'turn/start' and thread and isinstance(result, dict):
            turn = result.get('turn') or {}
            if thread in self.busy and turn.get('id'):
                self.active_turns[thread] = turn['id']
        return self.translate(result, account)

    async def receive(self, message):
        if "method" not in message:
            callback = self.callbacks.pop(message.get("id"), None)
            if callback:
                account, request_id = callback
                await self.write(account, {**message, "id": request_id})
            return
        if message["method"] == "initialized":
            return  # Each backend is initialized exactly once by start().
        request_id = message.get("id")
        try:
            result = await self.handle(message["method"], message.get("params") or {})
            if request_id is not None:
                self.output({"id": request_id, "result": result})
        except Exception as error:
            if request_id is not None:
                self.output({"id": request_id, "error": {"code": getattr(error, "code", -32603), "message": str(error) if isinstance(error, RpcError) else "Router failure: " + type(error).__name__}})

    async def close(self):
        self.closed = True
        for process in self.backends.values():
            if process.returncode is None:
                process.stdin.close()
        for process in self.backends.values():
            try:
                await asyncio.wait_for(process.wait(), 5)
            except asyncio.TimeoutError:
                process.kill()
        for task in list(self.background):
            task.cancel()
        for adapter in self.remote_adapters:
            await asyncio.to_thread(adapter.close)


def serve_control(router, loop, token, endpoint_path):
    class Handler(http.server.BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass

        def do_POST(self):
            if self.headers.get("Host", "").split(":")[0] != "127.0.0.1" or not secrets.compare_digest(self.headers.get("X-CA-Token", ""), token):
                self.send_error(403)
                return
            try:
                length = int(self.headers.get("Content-Length", "0"))
                if not 0 < length <= 8192:
                    raise ValueError("Invalid request length")
                data = json.loads(self.rfile.read(length))
                future = asyncio.run_coroutine_threadsafe(router.control(data["method"], data.get("params") or {}), loop)
                payload = {"result": future.result(timeout=120)}
                status = 200
            except Exception as error:
                payload = {"error": str(error) if isinstance(error, RpcError) else type(error).__name__}
                status = 400
            body = json.dumps(payload).encode()
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    atomic_json(endpoint_path, {"port": server.server_address[1], "pid": os.getpid()})
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server


def is_gui_router(arguments, token):
    return (bool(token) and 'app-server' in arguments
            and 'features.prefer_mxc=true' in arguments)


def claim_control_owner(endpoint_path):
    """Keep one routing owner per desktop launch, including auxiliary app servers."""
    import msvcrt
    path = pathlib.Path(str(endpoint_path) + '.owner.lock')
    path.parent.mkdir(parents=True, exist_ok=True)
    handle = path.open('a+b')
    handle.seek(0)
    try:
        msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
    except OSError:
        handle.close()
        return None
    return handle


async def main():
    if os.environ.get('CA_PERSISTENT_RECORD') and 'app-server' in sys.argv[1:]:
        from persistent import relay
        record = json.loads(pathlib.Path(os.environ['CA_PERSISTENT_RECORD']).read_text())
        await relay(record)
        return 0
    config = json.loads((ROOT / "bridge/router-config.json").read_text(encoding="utf-8"))
    token = os.environ.get("CA_CONTROL_TOKEN")
    endpoint_path = pathlib.Path(os.environ.get("CA_CONTROL_ENDPOINT", str(ROOT / "bridge/control-endpoint.json")))
    owns_gui_control = is_gui_router(sys.argv[1:], token)
    owner = claim_control_owner(endpoint_path) if owns_gui_control else None
    if owner is None:
        env = os.environ.copy()
        env["CODEX_CLI_PATH"] = config["engine"]
        adapter = None
        arguments = sys.argv[1:]
        # Auxiliary native app servers receive phone requests internally, outside
        # our stdio router. Route their inference while preserving remote auth.
        if token and 'app-server' in arguments:
            from remote_inference import RemoteInference
            adapter = RemoteInference(config, endpoint_path, token, ROOT).start()
            arguments = adapter.configure(arguments, env)
        for key in list(env):
            if key.startswith('CA_CONTROL_'):
                env.pop(key, None)
        try:
            return subprocess.call([config["engine"], *arguments], env=env)
        finally:
            if adapter:
                adapter.close()

    def output(value):
        sys.stdout.write(json.dumps(value, separators=(",", ":")) + "\n")
        sys.stdout.flush()

    config["serverArgs"] = sys.argv[1:]
    router = AccountRouter(config, ROOT / "bridge/thread-routes.json", output)
    router.task(router.quota_refresh_loop())
    server = None
    # Desktop starts additional app servers for tools without prefer_mxc. They
    # share the launch environment but are not the GUI's account-routing owner.
    if token and owns_gui_control:
        server = serve_control(router, asyncio.get_running_loop(), token, endpoint_path)
    try:
        while line := await asyncio.to_thread(sys.stdin.readline):
            try:
                message = json.loads(line)
            except ValueError:
                output({"id": None, "error": {"code": -32700, "message": "Invalid JSON"}})
                continue
            router.task(router.receive(message))
    finally:
        if server:
            server.shutdown()
        await router.close()
        owner.close()
    return 0


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))

