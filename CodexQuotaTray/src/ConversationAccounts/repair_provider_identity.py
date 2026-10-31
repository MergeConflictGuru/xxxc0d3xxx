"""Repair the retired ca_remote provider identity in saved local chats."""
import json
import pathlib
import sqlite3
import time

def repair(shared, profiles, backup):
    backup = pathlib.Path(backup) / str(time.time_ns())
    backup.mkdir(parents=True)
    homes = [pathlib.Path(shared), *map(pathlib.Path, profiles)]
    changed = 0
    seen = set()
    for home in homes:
        for folder in ('sessions', 'archived_sessions'):
            for path in (home / folder).rglob('*.jsonl'):
                identity = str(path.resolve()).casefold()
                if identity in seen:
                    continue
                seen.add(identity)
                with path.open('rb') as stream:
                    line = stream.readline()
                try:
                    value = json.loads(line)
                except (ValueError, UnicodeError):
                    continue
                if value.get('type') != 'session_meta' or value.get('payload', {}).get('model_provider') != 'ca_remote':
                    continue
                # Keep complete originals before changing only the metadata header.
                original = path.read_bytes()
                (backup / (str(changed)+'.jsonl')).write_bytes(original)
                value['payload']['model_provider'] = 'openai'
                replacement = json.dumps(value, ensure_ascii=False).encode() + b'\n' + original[len(line):]
                with path.open('r+b') as stream:
                    if stream.read() != original:
                        raise RuntimeError('History changed during repair; retry when idle')
                    stream.seek(0); stream.write(replacement); stream.truncate()
                changed += 1
        database = home / 'state_5.sqlite'
        if database.exists():
            with sqlite3.connect(database, timeout=20) as db:
                rows = db.execute("select id,model_provider from threads where model_provider='ca_remote'").fetchall()
                (backup / (home.name+'-database-rows.json')).write_text(json.dumps(rows))
                db.execute("update threads set model_provider='openai' where model_provider='ca_remote'")
    return changed

if __name__ == '__main__':
    root = pathlib.Path(r'C:\_coding\ConversationAccounts')
    print('Repaired history headers:', repair(r'C:\Users\aiaiai\.codex', list((root/'profiles').glob('account-*')), root/'.app_tmp/provider-repair'))
