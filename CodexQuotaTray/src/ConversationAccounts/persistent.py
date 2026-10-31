"""Persistent native receiver and disposable desktop stdio clients (MU only)."""
import asyncio
import csv
import json
import os
import pathlib
import secrets
import socket
import sqlite3
import subprocess
import sys
import time
import uuid
from websockets.asyncio.client import connect
from router import AccountRouter, atomic_json, claim_control_owner, serve_control, RpcError
from remote_inference import RemoteInference


def runtime(root):
    return pathlib.Path(root)/'.app_tmp/persistent'


def protect(path):
    path.mkdir(parents=True, exist_ok=True)
    if os.name == 'nt':
        result = subprocess.run(['whoami','/user','/fo','csv','/nh'],capture_output=True,text=True,check=True,creationflags=subprocess.CREATE_NO_WINDOW)
        sid = next(csv.reader(result.stdout.splitlines()))[1]
        subprocess.run(['icacls',str(path),'/inheritance:r','/grant:r','*'+sid+':(OI)(CI)F','*S-1-5-18:(OI)(CI)F'],check=True,stdout=subprocess.DEVNULL,creationflags=subprocess.CREATE_NO_WINDOW)


class NativeJob:
    """Windows closes native receiver children if the supervisor crashes."""
    def __init__(self, pid):
        self.handle=None
        if os.name!='nt':return
        import ctypes as c
        from ctypes import wintypes as w
        class Limits(c.Structure):
            _fields_=[('process_time',c.c_int64),('job_time',c.c_int64),('flags',w.DWORD),('minimum',c.c_size_t),('maximum',c.c_size_t),('active',w.DWORD),('affinity',c.c_size_t),('priority',w.DWORD),('scheduling',w.DWORD)]
        class IO(c.Structure):
            _fields_=[(name,c.c_uint64) for name in ('reads','writes','other','read_bytes','write_bytes','other_bytes')]
        class Extended(c.Structure):
            _fields_=[('basic',Limits),('io',IO),('process_memory',c.c_size_t),('job_memory',c.c_size_t),('peak_process',c.c_size_t),('peak_job',c.c_size_t)]
        kernel=c.WinDLL('kernel32',use_last_error=True)
        kernel.CreateJobObjectW.argtypes=[c.c_void_p,w.LPCWSTR];kernel.CreateJobObjectW.restype=w.HANDLE
        kernel.SetInformationJobObject.argtypes=[w.HANDLE,c.c_int,c.c_void_p,w.DWORD]
        kernel.OpenProcess.argtypes=[w.DWORD,w.BOOL,w.DWORD];kernel.OpenProcess.restype=w.HANDLE
        kernel.AssignProcessToJobObject.argtypes=[w.HANDLE,w.HANDLE]
        kernel.CloseHandle.argtypes=[w.HANDLE]
        job=kernel.CreateJobObjectW(None,None)
        if not job:raise c.WinError(c.get_last_error())
        native=None
        try:
            limits=Extended();limits.basic.flags=0x2000
            if not kernel.SetInformationJobObject(job,9,c.byref(limits),c.sizeof(limits)):raise c.WinError(c.get_last_error())
            native=kernel.OpenProcess(0x0101,False,pid)
            if not native or not kernel.AssignProcessToJobObject(job,native):raise c.WinError(c.get_last_error())
            self.handle=job;self.kernel=kernel
        except BaseException:
            kernel.CloseHandle(job);raise
        finally:
            if native:kernel.CloseHandle(native)

    def close(self):
        if self.handle:self.kernel.CloseHandle(self.handle);self.handle=None


def enrollment_profile(config):
    if config.get('remoteReceiver') in config['profiles']:
        return config['remoteReceiver']
    # Preserve the desktop's existing enabled enrollment, rather than its chat account.
    try:
        database = pathlib.Path(config['sharedStore'])/'state_5.sqlite'
        with sqlite3.connect(database.as_uri()+'?mode=ro',uri=True) as db:
            rows = db.execute('select account_id from remote_control_enrollments where remote_control_enabled=1 order by updated_at desc').fetchall()
        for (account_id,) in rows:
            for key, profile in config['profiles'].items():
                auth=json.loads((pathlib.Path(profile['home'])/'auth.json').read_text(encoding='utf-8-sig'))
                if auth.get('tokens',{}).get('account_id')==account_id:
                    return key
    except (OSError,ValueError,sqlite3.Error):
        pass
    return config['main']


async def rpc(ws, identity, method, params):
    await ws.send(json.dumps({'id':identity,'method':method,'params':params}))
    while True:
        value=json.loads(await asyncio.wait_for(ws.recv(),25))
        if value.get('id')==identity:
            if 'error' in value:
                raise RuntimeError(value['error'].get('message','Native RPC failed'))
            return value.get('result')


async def initialize(ws, name='Codex Desktop'):
    await rpc(ws,'persistent:init','initialize',{'clientInfo':{'name':name,'version':'1'},'capabilities':{'experimentalApi':True}})
    await ws.send(json.dumps({'method':'initialized','params':{}}))


def connection(record):
    return connect(record['url'],additional_headers={'Authorization':'Bearer '+record['token']},max_size=256*1024*1024,open_timeout=3)


async def healthy(record):
    try:
        async with connection(record) as ws:
            await initialize(ws)
            await rpc(ws,'persistent:health','config/read',{})
        return True
    except Exception:
        return False


class Inference(RemoteInference):
    def select(self, thread, turn):
        account, lease = super().select(thread, turn)
        try:
            asyncio.run_coroutine_threadsafe(self.accounts.note(thread, account, lease), self.loop).result(20)
            return account, lease
        except BaseException:
            self.release(lease)
            raise

    def release(self, lease):
        super().release(lease)
        asyncio.run_coroutine_threadsafe(self.accounts.finished(lease), self.loop)


class Accounts(AccountRouter):
    async def note(self, thread, account, lease):
        previous=self.routes.get(thread,{})
        self.routes[thread]={**previous,'id':thread,'account':account,'assigned':previous.get('assigned',False)}
        self.leases[lease]=thread
        self.busy.add(thread)
        self.save()

    async def finished(self, lease):
        thread=self.leases.pop(lease,None)
        if thread and thread not in self.leases.values():self.busy.discard(thread)

    async def control(self, method, params):
        if method=='stop-server':
            self.stop_event.set()
            return {'stopping':True}
        if method=='state':
            result=await super().control(method,params)
            return result
        if method=='select':
            account=params['account']
            if account!='unassigned': self.profile(account)
            self.default_selection=account
            return {'account':account}
        if method=='switch':
            thread=str(uuid.UUID(params['threadId']))
            account=params['account']
            if account!='unassigned': self.profile(account)
            if thread in self.busy:
                raise RpcError('Wait for the current turn to finish before switching accounts')
            previous=self.routes.get(thread,{})
            self.routes[thread]={**previous,'id':thread,'account':account if account!='unassigned' else previous.get('account',self.main),'assigned':account!='unassigned'}
            self.save()
            return {'threadId':thread,'account':account,'historyPreserved':True}
        if method=='new-thread':
            thread=str(uuid.UUID(params['threadId']))
            account=self.default_selection
            self.default_selection='unassigned'
            if account!='unassigned':
                self.routes[thread]={'id':thread,'account':account,'assigned':True}
                self.save()
            return {}
        raise RpcError('Unknown persistent account operation')


async def serve(root):
    root=pathlib.Path(root)
    folder=runtime(root)
    protect(folder)
    owner=claim_control_owner(folder/'server')
    if owner is None: return
    config=json.loads((root/'bridge/router-config.json').read_text(encoding='utf-8-sig'))
    receiver=enrollment_profile(config)
    accounts=Accounts(config,root/'bridge/thread-routes.json',lambda value:None,root)
    accounts.leases={}
    accounts.stop_event=asyncio.Event()
    adapter=None;process=None;control=None;job=None
    record_path=folder/'server.json'
    try:
        await accounts.refresh_quotas()
        loop=asyncio.get_running_loop()
        adapter=Inference(config,'unused','unused',root).start()
        adapter.accounts=accounts
        adapter.loop=loop
        adapter.state=lambda: asyncio.run_coroutine_threadsafe(accounts.control('state',{}),loop).result(20)
        accounts.adapter=adapter
        accounts.task(accounts.quota_refresh_loop())
        token=secrets.token_urlsafe(32)
        secret=folder/'native-token';secret.write_text(token)
        with socket.socket() as sock:
            sock.bind(('127.0.0.1',0));port=sock.getsockname()[1]
        env=os.environ.copy()
        for key in list(env):
            if key.startswith(('CA_CONTROL_','CODEX_APP_TOOLS_','CA_PERSISTENT_')) or key in ('OPENAI_API_KEY','CODEX_THREAD_ID','CODEX_INTERNAL_ORIGINATOR_OVERRIDE'):
                env.pop(key,None)
        env.update(CODEX_HOME=config['profiles'][receiver]['home'],CODEX_SQLITE_HOME=config['sharedStore'],CODEX_CLI_PATH=config['engine'])
        args=['app-server','--listen',f'ws://127.0.0.1:{port}','--ws-auth','capability-token','--ws-token-file',str(secret),'-c','sqlite_home='+json.dumps(config['sharedStore']),'-c','features.code_mode_host=true']
        command=[config['engine'],*adapter.configure(args,env)]
        with (folder/'native.log').open('ab') as log:
            process=await asyncio.create_subprocess_exec(*command,cwd=root,env=env,stdin=subprocess.DEVNULL,stdout=log,stderr=log,creationflags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0)
        job=NativeJob(process.pid)
        record={'url':f'ws://127.0.0.1:{port}','token':token,'pid':process.pid,'supervisorPid':os.getpid(),'receiver':receiver,'controlToken':secrets.token_urlsafe(32),'controlEndpoint':str(folder/'control.json')}
        for _ in range(120):
            if process.returncode is not None:raise RuntimeError('Native server exited during startup')
            if await healthy(record):break
            await asyncio.sleep(.25)
        else:raise RuntimeError('Native server did not become ready')
        control=serve_control(accounts,loop,record['controlToken'],pathlib.Path(record['controlEndpoint']))
        async with connection(record) as keeper:
            await initialize(keeper)
            if config.get('persistentRemote',True):
                await rpc(keeper,'persistent:remote','remoteControl/enable',None)
            atomic_json(record_path,record)
            async def drain():
                async for _ in keeper:pass
                raise RuntimeError('Native keeper disconnected')
            tasks=[asyncio.create_task(drain()),asyncio.create_task(process.wait()),asyncio.create_task(accounts.stop_event.wait())]
            await asyncio.wait(tasks,return_when=asyncio.FIRST_COMPLETED)
            for task in tasks:task.cancel()
    finally:
        if process and process.returncode is None:
            process.terminate()
            try:await asyncio.wait_for(process.wait(),10)
            except asyncio.TimeoutError:process.kill();await process.wait()
        if job:job.close()
        if control:await asyncio.to_thread(control.shutdown)
        await accounts.close()
        if adapter:await asyncio.to_thread(adapter.close)
        record_path.unlink(missing_ok=True)
        owner.close()


def legacy_desktop_running(root):
    path=pathlib.Path(root)/'bridge/desktop-process.json'
    if os.name!='nt' or not path.exists():return False
    value=json.loads(path.read_text(encoding='utf-8-sig'))
    if value.get('persistent'):return False
    result=subprocess.run(['tasklist','/FI',f"PID eq {int(value['pid'])}",'/FO','CSV','/NH'],capture_output=True,text=True,check=True,creationflags=subprocess.CREATE_NO_WINDOW)
    expected=pathlib.Path(value.get('app','ChatGPT.exe')).name.casefold()
    return any(row and row[0].casefold()==expected for row in csv.reader(result.stdout.splitlines()))


def config_engine(root):
    return json.loads((pathlib.Path(root)/'bridge/router-config.json').read_text(encoding='utf-8-sig'))['engine']


def ensure(root):
    root=pathlib.Path(root);folder=runtime(root);protect(folder)
    record_path=folder/'server.json'
    def read():
        try:
            record=json.loads(record_path.read_text())
            return record if asyncio.run(healthy(record)) else None
        except (OSError,ValueError):return None
    record=read()
    if record:return record
    if legacy_desktop_running(root):
        raise RuntimeError('Close the legacy MU desktop before starting the persistent receiver for the first time; its enrollment is still in use')
    lock=claim_control_owner(folder/'startup')
    if lock is None:
        for _ in range(240):
            time.sleep(.5)
            record=read()
            if record:return record
        raise RuntimeError('Persistent startup timed out')
    try:
        record=read()
        if record:return record
        startup_error=folder/'start-error.log'
        startup_error.unlink(missing_ok=True)
        failure=folder/'failure.json'
        failure.unlink(missing_ok=True)
        if os.name=='nt' and 'WindowsApps' in config_engine(root):
            command=['powershell','-NoProfile','-NonInteractive','-WindowStyle','Hidden','-ExecutionPolicy','Bypass','-File',str(root/'bridge/persistent_start.ps1'),'-Root',str(root)]
            result=subprocess.run(command,capture_output=True,text=True,creationflags=subprocess.CREATE_NO_WINDOW,timeout=45)
            if result.returncode:raise RuntimeError('Package supervisor launch failed: '+result.stderr[-1500:])
        else:
            with (folder/'supervisor.log').open('ab') as log:
                flags=(subprocess.DETACHED_PROCESS|subprocess.CREATE_NEW_PROCESS_GROUP|subprocess.CREATE_NO_WINDOW) if os.name=='nt' else 0
                subprocess.Popen([sys.executable,str(pathlib.Path(__file__).resolve()),'serve',str(root)],cwd=root,stdin=subprocess.DEVNULL,stdout=log,stderr=log,creationflags=flags,close_fds=True)
        for _ in range(240):
            time.sleep(.5)
            record=read()
            if record:return record
            if failure.exists():raise RuntimeError(json.loads(failure.read_text())['error'])
            if startup_error.exists():raise RuntimeError(startup_error.read_text(encoding='utf-8-sig'))
        raise RuntimeError('Persistent startup failed; see '+str(folder/'supervisor.log'))
    finally:lock.close()


async def relay(record):
    """Disposable stdin/stdout client: EOF disconnects; it never stops the server."""
    import urllib.request
    def control(method,params):
        endpoint=json.loads(pathlib.Path(record['controlEndpoint']).read_text())
        req=urllib.request.Request(f"http://127.0.0.1:{endpoint['port']}/",json.dumps({'method':method,'params':params}).encode(),{'X-CA-Token':record['controlToken'],'Content-Type':'application/json'})
        with urllib.request.build_opener(urllib.request.ProxyHandler({})).open(req,timeout=20) as response:return json.load(response)
    async with connection(record) as ws:
        starts=set()
        async def send():
            while line:=await asyncio.to_thread(sys.stdin.readline):
                value=json.loads(line)
                if value.get('method') in ('shutdown','exit'):
                    if 'id'in value:
                        sys.stdout.write(json.dumps({'id':value['id'],'error':{'code':-32001,'message':'Persistent server shutdown requires explicit server control'}})+'\n');sys.stdout.flush()
                    continue
                if value.get('method')=='remoteControl/disable':
                    value['method']='remoteControl/status/read'
                if value.get('method')=='thread/start':starts.add(value.get('id'))
                await ws.send(json.dumps(value))
        async def receive():
            async for line in ws:
                value=json.loads(line)
                if value.get('id') in starts:
                    starts.discard(value['id'])
                    thread=value.get('result',{}).get('thread',{}).get('id')
                    if thread:await asyncio.to_thread(control,'new-thread',{'threadId':thread})
                sys.stdout.write(line+'\n');sys.stdout.flush()
        tasks=[asyncio.create_task(send()),asyncio.create_task(receive())]
        done,pending=await asyncio.wait(tasks,return_when=asyncio.FIRST_COMPLETED)
        for task in pending:task.cancel()
        for task in done:task.result()



def stop(root):
    import urllib.request
    path=runtime(root)/'server.json'
    if not path.exists():return False
    record=json.loads(path.read_text())
    if not asyncio.run(healthy(record)):
        raise RuntimeError('Server record is stale; no process was terminated')
    endpoint=json.loads(pathlib.Path(record['controlEndpoint']).read_text())
    request=urllib.request.Request(f"http://127.0.0.1:{endpoint['port']}/",json.dumps({'method':'stop-server','params':{}}).encode(),{'X-CA-Token':record['controlToken'],'Content-Type':'application/json'})
    with urllib.request.build_opener(urllib.request.ProxyHandler({})).open(request,timeout=15) as response:
        if not json.load(response).get('result',{}).get('stopping'):raise RuntimeError('Server refused shutdown')
    for _ in range(60):
        if not path.exists():return True
        time.sleep(.25)
    raise RuntimeError('Server shutdown timed out')


if __name__=='__main__':
    if len(sys.argv)>1 and sys.argv[1]=='serve':
        try:
            asyncio.run(serve(sys.argv[2]))
        except BaseException as error:
            atomic_json(runtime(sys.argv[2])/'failure.json',{'error':type(error).__name__+': '+str(error)})
            raise
    else:raise SystemExit('Use launch.py --server-only or the MU launcher')

