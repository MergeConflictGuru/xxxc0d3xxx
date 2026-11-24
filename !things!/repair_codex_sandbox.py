"""Provision the supported Windows sandbox; run from an interactive desktop."""
import argparse,datetime,getpass,json,os,pathlib,queue,shutil,subprocess,sys,threading,time

def repair():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--user',help='Windows account used to run Codex')
    parser.add_argument('--pause',action='store_true')
    args=parser.parse_args()
    if args.user and getpass.getuser().casefold()!=args.user.casefold():
        command=subprocess.list2cmdline([sys.executable,str(pathlib.Path(__file__).resolve()),'--user',args.user,'--pause'])
        return subprocess.call(['runas.exe','/savecred','/user:'+args.user,command])
    home=pathlib.Path(os.environ.get('CODEX_HOME',str(pathlib.Path.home()/'.codex'))).resolve()
    candidates=[home/'plugins/.plugin-appserver/codex.exe']
    cache=pathlib.Path(os.environ.get('LOCALAPPDATA',str(pathlib.Path.home()/'AppData/Local')))/'OpenAI/Codex/bin'
    candidates+=sorted(cache.glob('*/codex.exe'),key=lambda p:p.stat().st_mtime,reverse=True)
    engine=next((p for p in candidates if p.is_file()),None)
    if engine is None:raise RuntimeError('Open the installed Codex app once to prepare its CLI, then retry.')
    print('Windows account:',getpass.getuser(),flush=True)
    print('Codex data:',home,flush=True)
    backup=home/'chat-repair-backups'/('sandbox-'+datetime.datetime.now().strftime('%Y%m%d-%H%M%S-%f'))
    backup.mkdir(parents=True)
    shutil.copy2(home/'config.toml',backup/'config.toml')
    events=queue.Queue()
    with (backup/'setup-stderr.txt').open('w') as error:
        process=subprocess.Popen([str(engine),'--no-daemon','-c','windows.sandbox="elevated"','app-server','--stdio'],
            cwd=home,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=error,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW)
        def read():
            for line in process.stdout:
                try:events.put(json.loads(line))
                except ValueError:pass
            events.put(None)
        threading.Thread(target=read,daemon=True).start()
        def send(value):process.stdin.write(json.dumps(value)+'\n');process.stdin.flush()
        def call(number,method,params):
            send(dict(id=number,method=method,params=params));deadline=time.monotonic()+30
            while True:
                value=events.get(timeout=max(.01,deadline-time.monotonic()))
                if value is None:raise RuntimeError('Codex app-server exited. See '+str(backup/'setup-stderr.txt'))
                if value.get('id')!=number:continue
                if 'error' in value:raise RuntimeError(str(value['error']))
                return value['result']
        try:
            call(1,'initialize',dict(clientInfo=dict(name='windows_sandbox_repair',version='1'),capabilities=dict(experimentalApi=True)))
            send(dict(method='initialized'))
            readiness=call(2,'windowsSandbox/readiness',None)
            if readiness['status']!='ready':
                print('Approve the Windows administrator prompt to install/update the sandbox.',flush=True)
                call(3,'windowsSandbox/setupStart',dict(mode='elevated',cwd=str(home)))
                deadline=time.monotonic()+180
                while True:
                    value=events.get(timeout=max(.01,deadline-time.monotonic()))
                    if value is None:raise RuntimeError('Codex exited before setup completed.')
                    if value.get('method')!='windowsSandbox/setupCompleted':continue
                    (backup/'setup-result.json').write_text(json.dumps(value['params'],indent=2))
                    if not value['params']['success']:raise RuntimeError(value['params'].get('error','Windows sandbox setup failed.'))
                    break
            call(4,'config/value/write',dict(keyPath='windows.sandbox',value='elevated',mergeStrategy='replace'))
            print('Windows sandbox configured: elevated (sandbox commands are NOT administrator processes).',flush=True)
            print('Restart Codex GUI and CLI, then retry the failed conversation.',flush=True)
            print('Configuration backup:',backup,flush=True)
        finally:
            try:process.stdin.close()
            except OSError:pass
            try:process.wait(timeout=2)
            except subprocess.TimeoutExpired:process.kill();process.wait(timeout=2)
    return 0

if __name__=='__main__':
    try:code=repair()
    except Exception as error:
        print('REPAIR FAILED:',str(error) or type(error).__name__,flush=True)
        print('No sandbox restrictions were disabled.',flush=True)
        code=1
    if '--pause' in sys.argv:
        try:input('Press Enter to close...')
        except EOFError:pass
    raise SystemExit(code)
