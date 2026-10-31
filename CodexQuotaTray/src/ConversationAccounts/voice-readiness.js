/* Recover missing server rollout data in Electron's separate voice window. */
(function (root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  else if (root.location?.origin === 'app://-' && root.location.pathname === '/index.html') {
    root.conversationVoiceReadiness?.dispose();
    root.conversationVoiceReadiness = api.install(root);
  }
})(globalThis, function () {
  'use strict';
  function createRepair() {
    let identity = null, attempts = 0, pending = false, nextAttempt = 0, generation = 0;
    const state = {phase: 'waiting', attempts: 0, capability: null, cacheWriteFailed: false};
    async function check({client, auth, capability, now = Date.now()}) {
      state.capability = capability;
      if (!client || auth.authLoading || !auth.userId || !auth.authenticatedAccountId) return;
      const user = client.getContext()?.user;
      const account = user?.customIDs?.account_id || user?.custom?.account_id;
      // Never refresh or adopt another login's rollout values.
      if (user?.userID !== auth.userId || account !== auth.authenticatedAccountId) {
        state.phase = 'identity-not-ready'; return;
      }
      const key = auth.userId + '|' + auth.authenticatedAccountId;
      if (identity !== key) {
        identity = key; attempts = 0; nextAttempt = 0; generation++;
      }
      const gate = client.getFeatureGate('2380644311', {disableExposureLog: true});
      if (gate.details?.reason !== 'NoValues') {
        state.phase = capability?.isCapable ? 'ready' : 'server-disabled'; return;
      }
      if (pending || now < nextAttempt || attempts >= 3) return;
      pending = true; attempts++; state.attempts = attempts; state.phase = 'refreshing';
      const current = generation;
      try {
        const result = await client.refreshValuesAsync();
        if (current !== generation) return;
        const updated = client.getFeatureGate('2380644311', {disableExposureLog: true});
        // A payload too large for localStorage can still be valid in memory.
        state.cacheWriteFailed = /quota|storage|setItem/i.test(result?.error?.message || '');
        state.phase = updated.details?.reason === 'NoValues' ? 'missing-values' :
          updated.value ? 'refreshed' : 'server-disabled';
      } catch (_) {
        if (current === generation) state.phase = 'refresh-failed';
      } finally {
        pending = false; nextAttempt = now + attempts * 15000;
      }
    }
    return {check, status: () => ({...state}), dispose: () => { generation++; }};
  }
  function findStore(doc) {
    let found;
    const seen = new WeakSet();
    function scan(value, depth) {
      if (!value || typeof value !== 'object' || depth < 0 || seen.has(value)) return;
      seen.add(value);
      if (typeof value.get === 'function' && typeof value.watch === 'function' && value.query) {
        found = value; return;
      }
      for (const key of ['memoizedState', 'next', 'current', 'value']) {
        try { scan(value[key], depth - 1); } catch (_) {}
      }
      if (Array.isArray(value)) for (const item of value) scan(item, depth - 1);
    }
    for (const element of doc.querySelectorAll('button,[role="textbox"],#root')) {
      let fiber = element[Object.keys(element).find(key => key.startsWith('__reactFiber'))];
      for (let i = 0; fiber && i < 40 && !found; i++, fiber = fiber.return) scan(fiber.memoizedState, 8);
      if (found) break;
    }
    return found;
  }
  function install(win) {
    const repair = createRepair();
    let disposed = false, busy = false, shared = null;
    async function tick() {
      if (disposed || busy) return;
      busy = true;
      try {
        if (!shared) {
          const boot = [...win.document.scripts].map(s => s.src).find(p => /\/index-[\w-]+\.js$/.test(p));
          if (!boot) return;
          const text = await (await win.fetch(boot)).text();
          const name = text.match(/app-shared-[a-f0-9]+\.js/)?.[0];
          if (!name) return;
          shared = await import(new URL(name, boot).href);
          if (!shared.GIt || !shared.xNt || !shared.Nk) throw Error('Unsupported voice readiness adapter');
        }
        const store = findStore(win.document);
        if (!store || disposed) return;
        await repair.check({client: store.get(shared.GIt), auth: store.get(shared.xNt),
          capability: store.get(shared.Nk, {name: 'voice.realtime'})});
      } catch (_) {
        // Voice readiness must never take down account controls or normal chats.
      } finally { busy = false; }
    }
    const timer = win.setInterval(tick, 3000);
    tick();
    return Object.freeze({status: repair.status, dispose: () => {
      disposed = true; win.clearInterval(timer); repair.dispose();
    }});
  }
  return {createRepair, install};
});
