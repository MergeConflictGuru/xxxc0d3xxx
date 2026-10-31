'use strict';
// Loaded only by the separate, patched desktop copy.
const { app, ipcMain } = require('electron');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const crypto = require('node:crypto');
const root = process.env.CA_PROJECT_ROOT;
if (!root || !process.env.CA_CONTROL_TOKEN) throw new Error('Launch this desktop using Conversation Accounts');
const channel = 'conversation-accounts:control-v1';
const allowed = new Set(['state', 'select', 'switch']);
const ui = fs.readFileSync(path.join(root, 'bridge', 'accounts-ui.js'), 'utf8');
const verified = new Set();

function request(method, params) {
  return new Promise((resolve, reject) => {
    let endpoint;
    try { endpoint = JSON.parse(fs.readFileSync(path.join(root, 'bridge', 'control-endpoint.json'), 'utf8')); }
    catch { reject(new Error('Account router is starting. Try again shortly.')); return; }
    const body = JSON.stringify({ method, params });
    const req = http.request({ hostname: '127.0.0.1', port: endpoint.port, path: '/', method: 'POST',
      headers: { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(body), 'X-CA-Token': process.env.CA_CONTROL_TOKEN } }, res => {
      let data = '';
      res.setEncoding('utf8');
      res.on('data', part => { data += part; if (data.length > 2 * 1024 * 1024) req.destroy(new Error('Router response too large')); });
      res.on('end', () => {
        try { const result = JSON.parse(data); if (result.error) reject(new Error(result.error)); else resolve(result.result); }
        catch (error) { reject(error); }
      });
    });
    req.setTimeout(120000, () => req.destroy(new Error('Account operation timed out')));
    req.on('error', reject);
    req.end(body);
  });
}

ipcMain.handle(channel, async (event, method, params = {}) => {
  if (!event.senderFrame?.url?.startsWith('app://-/')) throw new Error('Untrusted account-control origin');
  if (!allowed.has(method)) throw new Error('Unknown account operation');
  if (method !== 'state' && (typeof params.account !== 'string' || params.account.length > 100)) throw new Error('Invalid account');
  if (method === 'switch' && (typeof params.threadId !== 'string' || !/^[0-9a-f-]{36}$/i.test(params.threadId))) throw new Error('Invalid conversation');
  return request(method, params);
});

app.on('web-contents-created', (_event, contents) => {
  if (contents.getType() !== 'window') return;
  const inject = async () => {
    if (!contents.getURL().startsWith('app://-/')) return;
    const url = new URL(contents.getURL());
    const initialRoute = url.searchParams.get('initialRoute') || '';
    if (url.pathname !== '/index.html' || initialRoute.startsWith('/avatar-overlay') || initialRoute.startsWith('/detached-window')) return;
    try { await contents.executeJavaScript(ui); }
    catch (error) { console.error('[ConversationAccounts] UI initialization failed:', error.message); }
    if (process.env.CA_VERIFY_UI === '1' && !verified.has(contents.id)) {
      verified.add(contents.id);
      setTimeout(async () => {
        try {
          const result = await contents.executeJavaScript(`(async () => {
            const state = await window.conversationAccounts.state();
            const host = document.querySelector('#conversation-accounts-controls');
            const model = [...document.querySelectorAll('[data-codex-intelligence-trigger],[data-composer-navigation-target="model"],button[aria-haspopup="menu"]')].find(e => e.getBoundingClientRect().width > 0 && e.getBoundingClientRect().height > 0 && (e.hasAttribute('data-codex-intelligence-trigger') || /GPT-[0-9]/i.test(e.textContent)));
            return { accountCount: state.accounts.length, main: state.main, default: state.default,
              controlPresent: !!host, pickerCount: host?.querySelectorAll('select').length,
              optionCount: host?.querySelector('#account')?.options.length,
              modelTurnsEnabled: state.modelTurnsEnabled, url: location.href,
              quotaOptions: [...(host?.querySelector('#account')?.options || [])].map(o => o.textContent),
              coloredConversations: document.querySelectorAll('[data-conversation-account]').length,
              besideModel: !!host && !!model && (host.nextElementSibling === model || host.nextElementSibling?.contains(model)),
              hitTest: (() => { const select = host?.querySelector('#account'); if (!select) return null; const r = select.getBoundingClientRect(); const x = r.x + r.width / 2, y = r.y + r.height / 2; return {hostReceivesPointer: host.contains(document.elementFromPoint(x, y)), selectReceivesPointer: document.elementFromPoint(x, y) === select, pointerEvents: getComputedStyle(select).pointerEvents, disabled: select.disabled}; })(),
              controlBounds: host?.getBoundingClientRect().toJSON(), modelBounds: model?.getBoundingClientRect().toJSON() };
          })()`);
          result.sameRow = !!result.controlBounds && !!result.modelBounds && Math.abs(result.controlBounds.y - result.modelBounds.y) < 4;
          result.passed = result.controlPresent && result.pickerCount === 1 && result.accountCount > 0 && result.optionCount >= result.accountCount && result.besideModel && result.sameRow && result.controlBounds.width > 0 && result.hitTest?.selectReceivesPointer && !result.hitTest.disabled;
          fs.writeFileSync(path.join(root, 'bridge', 'ui-verification.json'), JSON.stringify(result, null, 2));
          const preview = await contents.capturePage();
          fs.writeFileSync(path.join(root, 'bridge', 'ui-preview.png'), preview.toPNG());
          console.log('[ConversationAccounts] UI verification', result.passed ? 'passed' : 'failed', JSON.stringify(result));
        } catch (error) {
          fs.writeFileSync(path.join(root, 'bridge', 'ui-verification.json'), JSON.stringify({ error: error.message }, null, 2));
        }
      }, 12000);
    }
  };
  contents.on('dom-ready', inject);
  contents.on('did-finish-load', inject);
});
