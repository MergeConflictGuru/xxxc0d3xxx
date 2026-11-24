"""Merge an offline sidebar backup without replacing current chats, then repair rollouts."""
import argparse
import datetime
import json
import pathlib
import sqlite3
import subprocess
import sys

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('source', type=pathlib.Path)
p.add_argument('--codex-home', type=pathlib.Path, default=pathlib.Path.home()/'.codex')
p.add_argument('--apply', action='store_true')
p.add_argument('--delete-source', action='store_true')
a = p.parse_args()
source = a.source.resolve(strict=True)
home = a.codex_home.resolve(strict=True)
assert source != home/'state_5.sqlite', 'Source must be an offline backup'
old = sqlite3.connect(source.as_uri()+'?mode=ro&immutable=1', uri=True)
assert old.execute('pragma quick_check').fetchone() == ('ok',)
db = sqlite3.connect(home/'state_5.sqlite', timeout=30)
ids = {r[0] for r in old.execute('select id from threads')}
existing = {r[0] for r in db.execute('select id from threads')}
print('Backup threads:', len(ids), 'New sidebar entries:', len(ids-existing), flush=True)
tables = ['projects', 'project_roots', 'thread_sections', 'threads',
          'thread_dynamic_tools', 'thread_spawn_edges', 'thread_attachments']
available = {r[0] for r in old.execute("select name from sqlite_master where type='table'")}
if not a.apply:
    sys.exit(0)
backup = home/'chat-repair-backups'/datetime.datetime.now().strftime('%Y%m%d-%H%M%S-%f')
backup.mkdir(parents=True)
with sqlite3.connect(backup/'state_5.sqlite') as snap:
    db.backup(snap)
with sqlite3.connect(backup/'imported-source.sqlite') as snap:
    old.backup(snap)
(backup/'import.json').write_text(json.dumps({'source':str(source), 'added_thread_ids':sorted(ids-existing)}, indent=2))
with db:
    for table in tables:
        if table not in available:
            continue
        dest = {r[1] for r in db.execute(f'pragma table_info("{table}")')}
        cols = [r[1] for r in old.execute(f'pragma table_info("{table}")') if r[1] in dest]
        names = ','.join('"'+c+'"' for c in cols)
        rows = old.execute(f'SELECT {names} FROM "{table}"').fetchall()
        db.executemany(f'INSERT OR IGNORE INTO "{table}" ({names}) VALUES ({",".join("?" for _ in cols)})', rows)
assert ids <= {r[0] for r in db.execute('select id from threads')}
assert existing <= {r[0] for r in db.execute('select id from threads')}
assert db.execute('pragma quick_check').fetchone() == ('ok',)
db.close()
old.close()
subprocess.run([sys.executable, str(pathlib.Path(__file__).with_name('repair_codex_chats.py')),
                '--codex-home', str(home), '--keep-workspace-roots', '--skip-command-check'], check=True)
with sqlite3.connect(home/'state_5.sqlite') as check:
    missing = [(r[0],r[1]) for r in check.execute('select id,rollout_path from threads') if not pathlib.Path(r[1]).is_file()]
with sqlite3.connect(home/'thread_history_1.sqlite') as history:
    recoverable = [tid for tid, _ in missing if history.execute(
        'select 1 from thread_history_projection_state where thread_id=? and next_rollout_ordinal>=2', (tid,)).fetchone()]
assert not recoverable, f'{len(recoverable)} recoverable rollout files remain missing; source retained'
(backup/'unavailable-rollouts.json').write_text(json.dumps(missing, indent=2))
print('Verified sidebar merge and recoverable rollout files. Unavailable history:',len(missing), flush=True)
print('Rollback:', backup, flush=True)
if a.delete_source:
    source.unlink()
    print('Deleted source backup:', source, flush=True)
