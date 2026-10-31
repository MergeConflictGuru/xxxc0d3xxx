"""Validate and label the multi-account tool's own credential profiles."""
import base64
import json
import os
import pathlib
import uuid


def write_json(path, value):
    path = pathlib.Path(path)
    temporary = path.with_name(path.name + '.' + uuid.uuid4().hex + '.tmp')
    try:
        temporary.write_text(json.dumps(value, indent=2), encoding='utf-8')
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def identity(data):
    auth = json.loads(data.decode('utf-8-sig'))
    tokens = auth['tokens']
    encoded = tokens['id_token'].split('.')[1]
    claims = json.loads(base64.urlsafe_b64decode(encoded + '=' * (-len(encoded) % 4)))
    email = claims.get('email') or claims.get('https://api.openai.com/profile', {}).get('email')
    account = tokens.get('account_id') or claims.get('https://api.openai.com/auth', {}).get('chatgpt_account_id') or claims.get('sub') or email
    if not email or not account:
        raise ValueError('Profile auth is missing account identity')
    return email.lower() + '|' + account, email


def sync(root):
    """Use profile homes as the source of truth; never read tray settings/backups."""
    root = pathlib.Path(root)
    config_path = root / 'bridge/router-config.json'
    config = json.loads(config_path.read_text(encoding='utf-8-sig'))
    profiles = config.get('profiles')
    if not isinstance(profiles, dict) or not profiles:
        raise RuntimeError('No OpenAI account profiles are configured')

    seen = set()
    for account, profile in profiles.items():
        home = pathlib.Path(profile['home']).resolve()
        auth_path = home / 'auth.json'
        try:
            key, email = identity(auth_path.read_bytes())
        except (OSError, ValueError, KeyError, IndexError, TypeError) as error:
            raise RuntimeError(f'Account profile {account} has no valid local auth.json') from error
        if key in seen:
            raise RuntimeError(f'Duplicate OpenAI login identity in account profile {account}')
        seen.add(key)
        profile['home'] = str(home)
        profile['label'] = email
        profile.pop('sourceBackup', None)
        profile.pop('sourceSha256', None)

    if config.get('main') not in profiles:
        raise RuntimeError('The configured primary account is missing')
    config['sharedStore'] = str(pathlib.Path(config['sharedStore']).resolve())
    config['conversationSource'] = 'shared-codex-home'
    config.pop('traySettingsPath', None)
    write_json(config_path, config)

    bridge_path = root / 'bridge/bridge-config.json'
    bridge = json.loads(bridge_path.read_text(encoding='utf-8-sig'))
    bridge['allowedHomes'] = [profile['home'] for profile in profiles.values()]
    bridge['guiHome'] = config['sharedStore']
    write_json(bridge_path, bridge)
    print(f"Validated {len(profiles)} locally managed OpenAI account profiles")
