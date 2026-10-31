(() => {
  if (!window.conversationAccounts || window.__conversationAccountsQuotaGate?.persistent) return;
  // Local routing eligibility is separate from the GUI login's quota. Keep its
  // usage query and credit balances untouched; workers still enforce real quota.
  window.__conversationAccountsQuotaGate?.restore();
  let installing = false;
  function threadId(state) {
    const row = document.querySelector('[data-app-action-sidebar-thread-active="true"]');
    const candidates = [window.__conversationAccountsCurrentThread, row?.getAttribute('data-app-action-sidebar-thread-id')?.replace(/^local:/, '')];
    let fiber = row?.[Object.keys(row).find(k => k.startsWith('__reactFiber'))];
    for (let n = 0; fiber && n < 25; n++, fiber = fiber.return) candidates.push(fiber.memoizedProps?.conversationId);
    return candidates.find(id => state.routes?.[id]) || null;
  }
  function hasQuota(state, thread) {
    const pinned = thread ? state.assignments?.[thread] : state.defaultSelection;
    const candidates = pinned && pinned !== 'unassigned' ? state.accounts.filter(a => a.id === pinned) : state.accounts;
    const now = Date.now() / 1000;
    const usable = candidates.some(a => {
      const q = a.quota;
      return q?.primary && q?.secondary && [q.primary, q.secondary].every(w => w.remaining > 0 || (w.resetsAt && w.resetsAt <= now));
    });
    if (usable) return true;
    return candidates.length && candidates.every(a => a.quota?.primary && a.quota?.secondary) ? false : null;
  }
  async function install() {
    const paths = performance.getEntriesByType('resource').map(r => r.name);
    if (!paths.some(p => /\/app-primary-[a-f0-9]+\.js$/.test(p))) {
      const boot = [...document.scripts].map(s => s.src).find(p => /\/index-.*\.js$/.test(p));
      if (!boot) return;
      const source = await (await fetch(boot)).text();
      let names = source.match(/app-(?:initial|primary|shared)-[a-f0-9]+\.js/g) || [];
      const initial = names.find(n => n.startsWith('app-initial-'));
      if (initial) names.push(...((await (await fetch(new URL(initial, boot))).text()).match(/app-(?:primary|shared)-[a-f0-9]+\.js/g) || []));
      paths.push(...names.map(n => new URL(n, boot).href));
    }
    const sharedPath = paths.find(p => /\/app-shared-[a-f0-9]+\.js$/.test(p));
    const primaryPath = paths.find(p => /\/app-primary-[a-f0-9]+\.js$/.test(p));
    if (!sharedPath || !primaryPath) return;
    const shared = await import(sharedPath), primary = await import(primaryPath), gate = primary.W_;
    if (!gate?.resolve || !gate.scope || typeof shared.don !== 'function' || typeof shared.aon !== 'function') throw Error('Unsupported GPT quota adapter version');
    const editor = [...document.querySelectorAll('[contenteditable=true]')].find(e => e.getBoundingClientRect().width > 0);
    const node = editor?.parentElement;
    let fiber = node?.[Object.keys(node).find(k => k.startsWith('__reactFiber'))], map, record;
    for (; fiber; fiber = fiber.return) {
      const value = fiber.memoizedProps?.value;
      if (!(value instanceof Map)) continue;
      record = [...value.values()].find(v => v?.token === gate.scope);
      if (record) { map = value; break; }
    }
    if (!record) return;
    const original = {...gate}, available = shared.don(gate.scope, null);
    const routedGate = shared.aon(gate.scope, ({get}) => {
      const usable = get(available);
      return usable === null ? get(original) : !usable;
    });
    gate.resolve = (...args) => routedGate.resolve(...args);
    const signal = available.resolve(record, map);
    const style = document.createElement('style');
    style.textContent = 'aside[data-mu-quota-available]{display:none!important}';
    document.documentElement.appendChild(style);
    window.__conversationAccountsQuotaGate = {
      persistent: true,
      update(state, thread = threadId(state)) {
        const usable = hasQuota(state, thread);
        record.store.set(signal, usable);
        for (const banner of document.querySelectorAll('aside')) {
          if (banner.querySelector('h3')?.textContent === 'You’re out of Codex and Work usage') banner.toggleAttribute('data-mu-quota-available', usable === true);
        }
        return usable === true;
      },
      blocked() { return record.store.get(routedGate.resolve(record, map)); },
      restore() { gate.resolve = original.resolve; delete window.__conversationAccountsQuotaGate; }
    };
    window.dispatchEvent(new Event('resize'));
  }
  async function refresh() {
    if (installing) return;
    installing = true;
    try {
      if (!window.__conversationAccountsQuotaGate) await install();
      if (window.__conversationAccountsQuotaGate) window.__conversationAccountsQuotaGate.update(await window.conversationAccounts.state());
    } catch (error) { console.warn('Multi-user quota adapter:', error.message); }
    finally { installing = false; }
  }
  setInterval(refresh, 3000);
  window.addEventListener('conversation-accounts:thread', refresh);
  document.addEventListener('change', e => { if (e.target?.matches('#conversation-accounts-controls select')) setTimeout(refresh, 0); });
  refresh();
})();
