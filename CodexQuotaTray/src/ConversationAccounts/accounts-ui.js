(() => {
  if (window.__conversationAccountsUI) return;
  window.__conversationAccountsUI = true;
  const api = window.conversationAccounts;
  if (!api) return;
  const host = document.createElement('span');
  host.id = 'conversation-accounts-controls';
  host.className = 'no-drag';
  host.contentEditable = 'false';
  host.style.cssText = 'display:inline-flex;align-items:center;min-width:0;flex:0 1 auto;margin-inline:4px;vertical-align:middle;pointer-events:auto;position:relative;z-index:2;-webkit-app-region:no-drag';
  const shadow = host;
  shadow.innerHTML = `<style>
    #conversation-accounts-controls{font:12px/1.4 system-ui,sans-serif;color-scheme:dark light;pointer-events:auto;-webkit-app-region:no-drag}
    #conversation-accounts-controls select{font:inherit;color:light-dark(#555,#bbb);background:transparent;border:0;border-left:3px solid var(--account,#888);border-radius:5px;padding:3px 4px;max-width:180px;height:28px;cursor:pointer;box-sizing:border-box;pointer-events:auto;-webkit-app-region:no-drag}
    #conversation-accounts-controls select:hover{background:light-dark(#eee,#333)}#conversation-accounts-controls select:disabled{opacity:.5;cursor:wait}
    #conversation-accounts-controls option{background:light-dark(white,#232323);color:light-dark(#222,#eee)}
    #conversation-accounts-controls .error{color:#e45b5b;margin-left:3px;font-weight:bold;cursor:help}#conversation-accounts-controls [hidden]{display:none!important}
  </style><select id="account" aria-label="Conversation account"></select><span class="error" role="alert" hidden>!</span>`;
  const select = shadow.querySelector('#account'), errorBadge = shadow.querySelector('.error');
  // The composer's bubbling mouse handlers focus its editor and cancel native controls.
  // Keep the native select's default behavior, but don't send its input to those handlers.
  for (const type of ['pointerdown', 'mousedown', 'click', 'keydown', 'keyup']) {
    host.addEventListener(type, event => event.stopPropagation());
  }
  const accountStyle = document.createElement('style');
  accountStyle.textContent = `
    [data-conversation-account][data-conversation-account-mode=pinned]{box-shadow:inset 4px 0 var(--conversation-account-color)!important;border-radius:4px}
    [data-conversation-account] :is([class*="animate-spin"],[role="progressbar"]){color:var(--conversation-account-color)!important}
  `;
  document.documentElement.appendChild(accountStyle);
  let state = null, pending = false, interacting = false, operationError = null, lastOptions = '';
  select.addEventListener('pointerdown', () => { interacting = true; });
  select.addEventListener('blur', () => { interacting = false; render(); });
  function mount() {
    const visible = button => {
      const rect = button.getBoundingClientRect();
      return rect.width > 0 && rect.height > 0 && getComputedStyle(button).visibility !== 'hidden';
    };
    const model = [...document.querySelectorAll('[data-codex-intelligence-trigger],[data-composer-navigation-target="model"]')].find(visible)
      || [...document.querySelectorAll('button[aria-haspopup="menu"]')].find(button => visible(button) && /GPT-[0-9]/i.test(button.textContent));
    let anchor = model;
    while (anchor?.parentElement?.tagName === 'SPAN' && anchor.parentElement.classList.contains('contents')) anchor = anchor.parentElement;
    if (anchor && anchor.previousElementSibling !== host) anchor.insertAdjacentElement('beforebegin', host);
    if (anchor) Object.assign(anchor.parentElement.style, {display: 'inline-flex', flexDirection: 'row', flexWrap: 'nowrap', alignItems: 'center', minWidth: '0'});
    if (!model && host.isConnected) host.remove();
  }
  function currentThread() {
    const active = document.querySelector('[data-app-action-sidebar-thread-active="true"][data-app-action-sidebar-thread-id]');
    const id = active?.getAttribute('data-app-action-sidebar-thread-id')?.match(/[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}/i)?.[0];
    const headerId = window.__conversationAccountsCurrentThread;
    const pathId = location.pathname.match(/[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}/i)?.[0];
    // Draft/sidebar IDs can be temporary client UUIDs, not app-server thread IDs.
    return [headerId, id, pathId, savedRowThread(active)].find(candidate => candidate && state?.routes[candidate]) || null;
  }
  function savedRowThread(row) {
    // New-chat rows keep a temporary client key after their first response.
    // Read the row component's resolved conversationId, validating it against
    // the backend routes; never infer an owner from a title or client UUID.
    const key = row && Object.keys(row).find(key => key.startsWith('__reactFiber'));
    let fiber = key && row[key];
    for (let depth = 0; fiber && depth < 20; depth++, fiber = fiber.return) {
      const id = fiber.memoizedProps?.conversationId;
      if (typeof id === 'string' && state?.routes[id]) return id;
    }
    return null;
  }
  function message(text, error = false) {
    host.title = select.title = text;
    errorBadge.hidden = !error;
    errorBadge.title = text;
  }
  function decorate() {
    if (!state) return;
    for (const link of document.querySelectorAll('a[href],[data-conversation-id],[data-thread-id],[data-app-action-sidebar-thread-id]')) {
      let identity = [link.getAttribute('href'), link.getAttribute('data-conversation-id'), link.getAttribute('data-thread-id'), link.getAttribute('data-app-action-sidebar-thread-id')].filter(Boolean).join(' ');
      try { identity = decodeURIComponent(identity); } catch {}
      const ids = identity.match(/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/ig) || [];
      const routes = state.routes || {};
      const assignments = state.assignments || {};
      const id = ids.find(id => routes[id] || assignments[id]) || savedRowThread(link);
      const account = state.accounts.find(a => a.id === (assignments[id] || routes[id]));
      if (!account) {
        if (link.dataset.conversationAccount) { delete link.dataset.conversationAccount; delete link.dataset.conversationAccountMode; link.style.removeProperty('--conversation-account-color'); link.removeAttribute('title'); }
        continue;
      }
      const pinned = !!assignments[id];
      link.dataset.conversationAccountMode = pinned ? 'pinned' : 'auto';
      link.style.setProperty('--conversation-account-color', account.color);
      link.dataset.conversationAccount = account.id;
      const usage = pinned ? 'Pinned account' : (state.busy || []).includes(id) ? 'Auto · currently using' : 'Auto · last used';
      link.title = `${usage}: ${account.label} · ${link.textContent.trim().slice(0, 100)}`;
    }
  }
  function accountText(account) {
    const windowText = window => {
      if (!window || !Number.isFinite(window.remaining)) return '—% —h';
      const percent = Math.round(window.remaining * 10) / 10;
      const hours = Number.isFinite(window.resetsAt) ? `${Math.max(0, (window.resetsAt * 1000 - Date.now()) / 3600000).toFixed(1)}h` : '—h';
      return `${percent}% ${hours}`;
    };
    return `${account.quota?.name || account.label.split('@')[0]} ${windowText(account.quota?.primary)}`;
  }
  function render() {
    mount();
    if (!state) return;
    const thread = currentThread(), assigned = (state.assignments || state.routes)[thread];
    const defaultAccount = state.accounts.find(account => account.id === state.default);
    const labels = state.accounts.map(account => [account.id, accountText(account)]);
    labels.unshift(['unassigned', 'Auto · best quota']);
    const options = JSON.stringify(labels);
    if (lastOptions !== options && !pending && !interacting) {
      select.replaceChildren(...labels.map(([value, text]) => {
        const option = document.createElement('option'); option.value = value; option.textContent = text; return option;
      }));
      lastOptions = options;
    }
    if (!pending && !interacting) select.value = thread ? assigned || 'unassigned' : state.defaultSelection || 'unassigned';
    select.disabled = pending || !!(thread && state.busy.includes(thread));
    const actualAccount = thread ? assigned || state.routes[thread] : state.default;
    const account = state.accounts.find(account => account.id === actualAccount);
    host.style.setProperty('--account', account?.color || '#888');
    const usage = !thread ? (state.defaultSelection && state.defaultSelection !== 'unassigned' ? `New conversation pinned to ${account?.label}.` : 'Auto: choose an idle account on send, then rank by quota density.')
      : state.busy.includes(thread) ? `Currently using ${account?.label || 'an unknown account'}.`
      : assigned ? `Pinned to ${account?.label}.`
      : `Auto: last used ${account?.label || 'an unknown account'}. On send, prefer an idle account, then quota density.`;
    if (!pending) message(operationError || usage, !!operationError);
    decorate();
  }
  async function refresh() {
    try { state = await api.state(); render(); }
    catch (error) { message(error.message || String(error), true); }
  }
  select.onchange = async () => {
    const target = select.value, thread = currentThread();
    interacting = false;
    pending = true; operationError = null; select.disabled = true;
    message('Switching account?');
    try {
      if (thread) await api.switch(thread, target);
      else await api.select(target);
    } catch (error) { operationError = `Account change failed: ${error.message || String(error)}`; }
    finally { pending = false; await refresh(); }
  };
  document.addEventListener('click', event => {
    const button = event.target.closest?.('button,a,[role="button"]');
    if (button && /^(new chat|new conversation)$/i.test((button.getAttribute('aria-label') || button.textContent).trim())) {
      window.__conversationAccountsCurrentThread = null;
      operationError = null;
      if(state)state.defaultSelection='unassigned';
      void api.select('unassigned').then(refresh).catch(error=>message(error.message,true));
      render();
    }
  }, true);
  let scheduled = false;
  new MutationObserver(() => {
    if (!scheduled) { scheduled = true; setTimeout(() => { scheduled = false; render(); }, 100); }
  }).observe(document.body || document.documentElement, { childList: true, subtree: true });
  setInterval(refresh, 3000);
  window.addEventListener('conversation-accounts:thread', () => { operationError = null; render(); refresh(); });
  mount();
  refresh();
})();
