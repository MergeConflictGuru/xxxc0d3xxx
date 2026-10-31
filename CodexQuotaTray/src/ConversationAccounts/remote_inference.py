"""Route auxiliary app-server inference without changing its remote-control login.

The native remote transport owns pairing, reconnects, approvals and events. Only
Responses and standalone web-search HTTP requests pass here. Credentials and message bodies are never logged.
"""
import http.server
import json
import pathlib
import secrets
import threading
import time
import tomllib
import urllib.error
import urllib.request
import uuid


class RoutingError(Exception):
    pass


def choose_account(state, thread, occupied=(), now=None):
    now = time.time() if now is None else now
    accounts = {a['id']: a for a in state['accounts']}
    pinned = state.get('assignments', {}).get(thread)
    if pinned:
        if pinned not in accounts:
            raise RoutingError('Pinned account is no longer configured')
        return pinned
    occupied = set(occupied) | {state['routes'][t] for t in state.get('busy', [])
                                if t != thread and t in state.get('routes', {})}
    ranked = []
    for account in accounts.values():
        quota = account.get('quota') or {}
        primary, weekly = quota.get('primary'), quota.get('secondary')
        if quota.get('error') or not primary:
            continue
        reset, remaining = primary.get('resetsAt'), primary.get('remaining', 0)
        if remaining <= 0 or not isinstance(reset, (int, float)) or reset <= now:
            continue
        if weekly and weekly.get('remaining', 0) <= 0:
            continue
        ranked.append((account['id'] not in occupied, remaining / ((reset-now)/3600), account['id']))
    if not ranked:
        raise RoutingError('No account has usable quota for this remote turn')
    return max(ranked)[2]


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class RemoteInference:
    def __init__(self, config, endpoint_path, control_token, root, opener=None):
        self.config = config
        self.endpoint_path = pathlib.Path(endpoint_path)
        self.control_token = control_token
        self.root = pathlib.Path(root)
        self.token = secrets.token_urlsafe(32)
        self.opener = opener or urllib.request.build_opener(NoRedirect)
        self.lock = threading.Lock()
        self.affinity = {}
        self.active = {}
        self.server = None

    def state(self):
        endpoint = json.loads(self.endpoint_path.read_text(encoding='utf-8-sig'))
        request = urllib.request.Request(f"http://127.0.0.1:{int(endpoint['port'])}/",
            json.dumps({'method': 'state', 'params': {}}).encode(),
            {'Content-Type': 'application/json', 'X-CA-Token': self.control_token})
        # Never send the control capability through a configured HTTP proxy.
        with urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect).open(request, timeout=10) as response:
            return json.load(response)['result']

    def select(self, thread, turn):
        uuid.UUID(thread)  # No arbitrary identifiers or paths from HTTP clients.
        state = self.state()
        now = time.time()
        with self.lock:
            self.affinity = {key: val for key, val in self.affinity.items() if now-val[1] < 3600}
            pinned = state.get('assignments', {}).get(thread)
            previous = self.affinity.get((thread, turn)) if turn else None
            account = pinned or (previous[0] if previous else None)
            if account is None:
                account = choose_account(state, thread, self.active.values(), now)
            if account not in self.config['profiles']:
                raise RoutingError('Selected account is no longer configured')
            if turn:
                self.affinity[(thread, turn)] = (account, now)
            lease = secrets.token_hex(12)
            self.active[lease] = account
        return account, lease

    def select_auxiliary(self):
        # Standalone search tools omit chat identity, including over remote control.
        # Select quota independently; never invent a chat route or guess its pin.
        state = self.state()
        with self.lock:
            account = choose_account(state, None, self.active.values())
            if account not in self.config['profiles']:
                raise RoutingError('Selected account is no longer configured')
            lease = secrets.token_hex(12)
            self.active[lease] = account
        return account, lease

    def release(self, lease):
        with self.lock:
            self.active.pop(lease, None)

    def log(self, **metadata):
        # IDs, status, and account labels only; never headers, tokens, or input.
        with self.lock:
            with (self.root / 'bridge/remote-routing.jsonl').open('a', encoding='utf-8') as stream:
                stream.write(json.dumps({'time': time.time(), **metadata}) + '\n')

    def start(self):
        adapter = self

        class Handler(http.server.BaseHTTPRequestHandler):
            protocol_version = 'HTTP/1.1'

            def log_message(self, *args):
                pass

            def fail(self, status, message):
                body = json.dumps({'error': {'message': message, 'type': 'remote_routing_error'}}).encode()
                self.send_response(status)
                self.send_header('Content-Type', 'application/json')
                self.send_header('Content-Length', str(len(body)))
                self.send_header('Connection', 'close')
                self.end_headers()
                self.wfile.write(body)
                self.close_connection = True

            def do_POST(self):
                lease, started = None, False
                try:
                    if self.headers.get('Host') != f'127.0.0.1:{self.server.server_port}' or self.headers.get('Origin'):
                        return self.fail(403, 'Local native app-server requests only')
                    path_token, _, operation = self.path.lstrip('/').partition('/')
                    authenticated = secrets.compare_digest(path_token, adapter.token)
                    if not authenticated and not secrets.compare_digest(self.headers.get('X-CA-Remote-Token', ''), adapter.token):
                        return self.fail(403, 'Invalid local request capability')
                    operation = '/' + operation if authenticated else self.path
                    if operation not in ('/responses', '/responses/compact', '/alpha/search'):
                        adapter.log(event='unsupported_operation', operation=operation.split('?', 1)[0][:160])
                        return self.fail(404, 'Unsupported inference operation')
                    length = int(self.headers.get('Content-Length', '0'))
                    if not 0 < length <= 256 * 1024 * 1024 or self.headers.get('Transfer-Encoding'):
                        return self.fail(400, 'Invalid request size')
                    body = self.rfile.read(length)
                    if len(body) != length:
                        return self.fail(400, 'Incomplete request')
                    thread = self.headers.get('thread-id') or self.headers.get('session-id')
                    metadata = json.loads(self.headers.get('x-codex-turn-metadata', '{}'))
                    turn = metadata.get('turn_id') or metadata.get('turnId')
                    if thread:
                        account, lease = adapter.select(thread, turn)
                    elif operation == '/alpha/search':
                        account, lease = adapter.select_auxiliary()
                    else:
                        return self.fail(400, 'Missing conversation identity')
                    auth_path = pathlib.Path(adapter.config['profiles'][account]['home']) / 'auth.json'
                    tokens = json.loads(auth_path.read_text(encoding='utf-8-sig'))['tokens']
                    if not tokens.get('access_token') or not tokens.get('account_id'):
                        raise RoutingError('Selected account requires sign-in')
                    excluded = {'authorization', 'chatgpt-account-id', 'host', 'connection',
                        'content-length', 'transfer-encoding', 'cookie', 'x-ca-remote-token',
                        'accept-encoding', 'proxy-authorization'}
                    headers = {k: v for k, v in self.headers.items() if k.lower() not in excluded}
                    headers.update({'Authorization': 'Bearer ' + tokens['access_token'],
                        'ChatGPT-Account-Id': tokens['account_id'], 'Accept-Encoding': 'identity'})
                    request = urllib.request.Request('https://chatgpt.com/backend-api/codex' + operation,
                        body, headers, method='POST')
                    adapter.log(thread=thread, turn=turn, account=account, event='request')
                    try:
                        response = adapter.opener.open(request, timeout=300)
                    except urllib.error.HTTPError as error:
                        response = error
                    with response:
                        adapter.log(thread=thread, turn=turn, account=account, event='response', status=response.code)
                        self.send_response(response.code)
                        for key, value in response.headers.items():
                            if key.lower() not in {'connection', 'transfer-encoding', 'content-length', 'set-cookie'}:
                                self.send_header(key, value)
                        self.send_header('Connection', 'close')
                        self.end_headers()
                        started = True
                        self.close_connection = True
                        while chunk := response.read1(65536):
                            self.wfile.write(chunk)
                            self.wfile.flush()
                except (BrokenPipeError, ConnectionResetError):
                    pass
                except Exception as error:
                    if not started:
                        self.fail(502, str(error) if isinstance(error, RoutingError) else 'Remote routing failed: ' + type(error).__name__)
                finally:
                    if lease:
                        adapter.release(lease)

            def do_GET(self):
                # The built-in provider falls back to full-context HTTP when
                # WebSocket upgrades are unavailable on the local transport.
                self.fail(426, 'Use HTTP Responses transport')

        self.server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        self.server.daemon_threads = True
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        return self

    def configure(self, arguments, env):
        env['OPENAI_BASE_URL'] = f'http://127.0.0.1:{self.server.server_port}/{self.token}'
        result = [*arguments, '-c', 'model_provider="openai"', '-c',
                  'openai_base_url='+json.dumps(env['OPENAI_BASE_URL'])]
        # Reuse explicit desktop approvals; never infer trust from a project path.
        # A profile's explicit setting (including an untrusted entry) takes priority.
        def projects(home):
            path = pathlib.Path(home) / 'config.toml'
            return tomllib.loads(path.read_text(encoding='utf-8-sig')).get('projects', {}) if path.exists() else {}
        if self.config.get('sharedStore'):
            shared = projects(self.config['sharedStore'])
            local = projects(env['CODEX_HOME'])
            normalize = lambda path: str(pathlib.Path(path)).replace('/', '\\').casefold().rstrip('\\')
            existing = {normalize(path) for path in local}
            for path, settings in shared.items():
                if settings.get('trust_level') == 'trusted' and normalize(path) not in existing:
                    result.extend(['-c', 'projects.'+json.dumps(path)+'.trust_level="trusted"'])
        return result

    def close(self):
        if self.server:
            self.server.shutdown()
            self.server.server_close()
