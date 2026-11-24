import sqlite3,pathlib,json,datetime,argparse,os,ntpath,subprocess,threading,queue

def check_commands(home, projects):
 if not projects:return True
 cache=pathlib.Path(os.environ.get('LOCALAPPDATA',str(pathlib.Path.home()/'AppData/Local')))/'OpenAI/Codex/bin'
 engines=sorted(cache.glob('*/codex.exe'),key=lambda p:p.stat().st_mtime,reverse=True)
 if not engines:
  print('Command check unavailable: open the installed Codex app once to prepare its local CLI.',flush=True)
  return False
 process=None
 try:
  env=os.environ.copy();env['CODEX_HOME']=str(home)
  process=subprocess.Popen([str(engines[0]),'--no-daemon','app-server','--stdio'],cwd=home,env=env,
   stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.DEVNULL,text=True,encoding='utf-8',
   creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
  replies=queue.Queue()
  def read():
   for line in process.stdout:
    try:replies.put(json.loads(line))
    except ValueError:pass
   replies.put(None)
  threading.Thread(target=read,daemon=True).start()
  def send(message):
   process.stdin.write(json.dumps(message)+'\n');process.stdin.flush()
  def call(number,method,params):
   import time
   send({'id':number,'method':method,'params':params});deadline=time.monotonic()+10
   while True:
    message=replies.get(timeout=max(.01,deadline-time.monotonic()))
    if message is None:raise RuntimeError('Codex app-server exited')
    if message.get('id')!=number:continue
    if 'error' in message:raise RuntimeError(str(message['error']))
    return message['result']
  call(1,'initialize',{'clientInfo':{'name':'chat_repair','version':'1'},'capabilities':{'experimentalApi':True}})
  send({'method':'initialized'})
  shell=str(pathlib.Path(os.environ.get('SystemRoot',r'C:\Windows'))/'System32/WindowsPowerShell/v1.0/powershell.exe')
  for number,(name,roots) in enumerate(projects,2):
   result=call(number,'command/exec',{'command':[shell,'-NoProfile','-NonInteractive','-Command','Get-Location'],
    'cwd':roots[0],'timeoutMs':5000,'sandboxPolicy':{'type':'workspaceWrite','writableRoots':roots,'networkAccess':False}})
   if result['exitCode']!=0 or not result.get('stdout','').strip():raise RuntimeError('Get-Location failed for '+name)
   print('Command check PASS:',name,'(sandboxed Get-Location via Codex)',flush=True)
  return True
 except (OSError,RuntimeError,queue.Empty) as error:
  print('Command check FAILED:',str(error) or 'Codex command timed out',flush=True)
  return False
 finally:
  if process:
   try:process.stdin.close()
   except OSError:pass
   try:process.wait(timeout=2)
   except subprocess.TimeoutExpired:process.kill();process.wait(timeout=2)

parser=argparse.ArgumentParser(description='Recover Codex chats after copying the SQLite databases to another Windows user/computer.')
parser.add_argument('--codex-home',type=pathlib.Path,default=pathlib.Path(os.environ.get('CODEX_HOME',str(pathlib.Path.home()/'.codex'))))
parser.add_argument('--preview',action='store_true',help='Inspect only; do not change files')
parser.add_argument('--keep-workspace-roots',action='store_true',help='Skip the known restored Midious workspace-root repair')
parser.add_argument('--repair-legacy-workspace-roots',action='store_true',help='Opt in to the earlier unelevated-sandbox workspace-root workaround')
parser.add_argument('--skip-command-check',action='store_true',help='Repair metadata without the local Codex command check')
args=parser.parse_args()
root=args.codex_home.expanduser().resolve()
for name in ['state_5.sqlite','thread_history_1.sqlite']:
 if not (root/name).is_file():raise SystemExit('Missing database: '+str(root/name))
out=root/'chat-repair-backups'/datetime.datetime.now().strftime('%Y%m%d-%H%M%S-%f')
print('Codex data:',root,flush=True)
s=sqlite3.connect(root/'state_5.sqlite',timeout=30);s.row_factory=sqlite3.Row
h=sqlite3.connect(root/'thread_history_1.sqlite',timeout=30)
work=[];skipped=[]
workspace_work=[];command_projects=[]
# This restored Midious root was reproduced hanging even Get-Location under
# the Windows sandbox. Its C: project/parent roots work. Leave other projects
# and their deliberately selected cross-drive folders alone.
tables={r[0] for r in s.execute("select name from sqlite_master where type='table'")}
if {'projects','project_roots'} <= tables:
 for project in s.execute('select id,name from projects'):
  roots=list(s.execute('select position,path from project_roots where project_id=? order by position',(project['id'],)))
  if not roots:continue
  primary=ntpath.normcase(ntpath.normpath(roots[0]['path']))
  if not primary.endswith(r'\music-tools\midious2') or ntpath.splitdrive(primary)[0]=='q:':continue
  kept=[item['path'] for item in roots]
  for item in roots[1:]:
   if args.repair_legacy_workspace_roots and not args.keep_workspace_roots and ntpath.normcase(ntpath.normpath(item['path']))==r'q:\_coding':
    workspace_work.append({'project_id':project['id'],'project_name':project['name'],'position':item['position'],'path':item['path']})
    kept.remove(item['path'])
  command_projects.append((project['name'],kept))
for r in s.execute('select * from threads'):
 if pathlib.Path(r['rollout_path']).exists():continue
 old=h.execute('select next_rollout_byte_offset,next_rollout_ordinal from thread_history_projection_state where thread_id=?',(r['id'],)).fetchone()
 if not old or old[1]<2:
  skipped.append(r['id']);continue
 ts=datetime.datetime.fromtimestamp(r['created_at'],datetime.timezone.utc).isoformat(timespec='milliseconds').replace('+00:00','Z')
 p=root/('archived_sessions' if r['archived'] else 'sessions')/('rollout-'+ts[:19].replace(':','-')+'-'+r['id']+'.jsonl')
 payload={k:r[k] for k in ['id','cwd','originator','cli_version','source','model_provider','creator_user_id','creator_account_id'] if r[k] is not None}
 try:payload['source']=json.loads(payload['source'])
 except (ValueError,TypeError):pass
 payload.update(session_id=r['id'],timestamp=ts,history_mode='paginated')
 meta={'timestamp':ts,'ordinal':0,'type':'session_meta','payload':payload}
 end={'timestamp':ts,'ordinal':old[1]-1,'type':'event_msg','payload':{'type':'token_count','info':None,'rate_limits':None}}
 data=(json.dumps(meta)+'\n'+json.dumps(end)+'\n').encode()
 work.append({'id':r['id'],'old_path':r['rollout_path'],'new_path':str(p),'old_offset':old[0],'next_ordinal':old[1],'size':len(data),'data':data})
print('Missing rollouts to rebuild:',len(work),'\nRecords without usable SQLite sequence metadata:',len(skipped),flush=True)
print('Restored workspace roots to repair:',len(workspace_work),flush=True)
for item in workspace_work:print('Remove extra root from',item['project_name']+':',item['path'],flush=True)
if args.preview:raise SystemExit(0)
if not work and not workspace_work:
 raise SystemExit(0 if args.skip_command_check or check_commands(root,command_projects) else 1)
out.mkdir(parents=True)
# Snapshot the small state database through SQLite so its WAL is included.
backup=sqlite3.connect(out/'state_5.sqlite');s.backup(backup);backup.close()
(out/'repair-rollback.json').write_text(json.dumps([{k:v for k,v in w.items() if k!='data'} for w in work],indent=2))
(out/'workspace-roots-rollback.json').write_text(json.dumps(workspace_work,indent=2))
s.execute('BEGIN IMMEDIATE');h.execute('BEGIN IMMEDIATE');made=[]
try:
 for item in workspace_work:
  changed=s.execute('delete from project_roots where project_id=? and position=? and path=?',(item['project_id'],item['position'],item['path']))
  if changed.rowcount!=1:raise RuntimeError('Workspace roots changed during repair; rerun the preview.')
  s.execute('update projects set updated_at_ms=? where id=?',(int(datetime.datetime.now().timestamp()*1000),item['project_id']))
 for w in work:
  p=pathlib.Path(w['new_path']);p.parent.mkdir(exist_ok=True)
  if p.exists():raise RuntimeError('Destination exists '+str(p))
  p.write_bytes(w['data']);made.append(p)
  h.execute('update thread_history_projection_state set next_rollout_byte_offset=? where thread_id=?',(w['size'],w['id']))
  s.execute('update threads set rollout_path=? where id=?',(str(p),w['id']))
 h.commit();s.commit()
except:
 h.rollback();s.rollback()
 print('Repair interrupted. Recovery metadata:',out,flush=True)
 raise
for w in work:
 assert pathlib.Path(w['new_path']).stat().st_size==w['size']
 assert h.execute('select next_rollout_byte_offset,next_rollout_ordinal from thread_history_projection_state where thread_id=?',(w['id'],)).fetchone()==(w['size'],w['next_ordinal'])
print('REPAIRED',len(work),flush=True)
print('WORKSPACE ROOTS REPAIRED',len(workspace_work),flush=True)
print('State integrity',tuple(s.execute('pragma quick_check').fetchone()))
print('History integrity',h.execute('pragma quick_check').fetchone())
print('Project assignment coverage', [tuple(r) for r in s.execute('select p.name,count(t.id) from projects p left join threads t on t.project_id=p.id group by p.id')])

print('Backup and rollback metadata:',out)
print('Done. Reopen Codex if its sidebar has not refreshed.')
if not args.skip_command_check and not check_commands(root,command_projects):raise SystemExit(1)
