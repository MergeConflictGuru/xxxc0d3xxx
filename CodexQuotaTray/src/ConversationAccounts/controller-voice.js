/* Foreground-only standard gamepad controls. Uses the app's existing buttons. */
(function (root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  else if (root.location?.origin === 'app://-' && root.location.pathname === '/index.html' &&
           !/avatar-overlay|detached-window/.test(root.location.search)) {
    root.conversationControllerVoice?.dispose();
    root.conversationControllerVoice = api.install(root);
  }
})(globalThis, function () {
  'use strict';
  const mapping = Object.freeze({0: 'send', 1: 'cancel', 8: 'dictate', 9: 'voice'});

  function createDriver({perform, invalidate = () => {}}) {
    let previous = new Set(), armed = false, active = false, identity = null, context = null;
    const state = {controller: null, unsupported: [], foreground: false, lastAction: null};
    function reset() {
      previous = new Set(); armed = false; active = false; identity = null;
      invalidate();
    }
    function update(pads, foreground, contextKey) {
      const connected = Array.from(pads || []).filter(p => p && p.connected !== false);
      const pad = connected.find(p => p.mapping === 'standard' && p.buttons.length >= 10);
      state.controller = pad ? pad.id : null;
      state.unsupported = connected.filter(p => p.mapping !== 'standard').map(p => p.id);
      state.foreground = foreground;
      if (!foreground || !pad) {
        if (active || identity !== null) reset();
        return;
      }
      const key = pad.index + ':' + pad.id;
      const pressed = new Set(Object.keys(mapping).map(Number).filter(i => pad.buttons[i]?.pressed));
      if (!active || identity !== key || context !== contextKey) {
        invalidate(); active = true; identity = key; context = contextKey;
        previous = pressed; armed = pressed.size === 0;
        return; // A newly discovered or refocused pad must first release its buttons.
      }
      if (!armed) {
        if (pressed.size === 0) armed = true;
        previous = pressed;
        return;
      }
      if (pressed.size > 1) {
        armed = false; previous = pressed;
        return; // No combinations, accidental chords, or queued actions.
      }
      const fresh = [...pressed].find(i => !previous.has(i));
      previous = pressed;
      if (fresh !== undefined) {
        state.lastAction = mapping[fresh];
        perform(mapping[fresh]);
      }
    }
    return {update, reset, status: () => ({...state, armed})};
  }

  function createActions(doc, win, {isForeground, contextKey, report = () => {}}) {
    let pending = null, timer = null, generation = 0;
    function cancelPending() {
      generation++;
      if (timer !== null) win.clearTimeout(timer);
      timer = null; pending = null;
    }
    function visible(element) {
      if (!element || element.closest('[inert],[aria-hidden="true"]')) return false;
      const rect = element.getBoundingClientRect();
      const style = win.getComputedStyle(element);
      return rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && style.display !== 'none';
    }
    function composer() {
      if (!isForeground() || [...doc.querySelectorAll('[role="dialog"],[role="alertdialog"]')].some(visible)) return null;
      const editors = [...doc.querySelectorAll('[contenteditable="true"][role="textbox"],textarea')]
        .filter(e => visible(e) && !e.disabled && e.getAttribute('aria-disabled') !== 'true');
      const focused = editors.find(e => e === doc.activeElement || e.contains(doc.activeElement));
      const editor = focused || (editors.length === 1 ? editors[0] : null);
      const scope = editor?.closest('[data-codex-composer-root]') || editor?.closest('[data-composer-body]');
      return scope ? {editor, scope, key: contextKey()} : null;
    }
    function buttons(c) {
      return [...c.scope.querySelectorAll('button')].filter(visible);
    }
    function label(b) { return (b.getAttribute('aria-label') || b.title || b.textContent || '').trim(); }
    function button(c, names, enabled = true) {
      return buttons(c).find(b => names.includes(label(b)) &&
        (!enabled || (!b.disabled && b.getAttribute('aria-disabled') !== 'true' && b.getAttribute('aria-busy') !== 'true')));
    }
    const dictationLabels = ['Stop dictation', 'Cancel dictation', 'Cancel transcription',
      'Finishing dictation', 'Starting dictation; click to cancel', 'Preparing dictation'];
    function isDictating(c) { return !!button(c, dictationLabels, false); }
    function same(c) {
      const now = composer();
      return now && now.key === c.key && now.editor === c.editor && now.scope === c.scope;
    }
    function clear(c) {
      if (!same(c)) return false;
      if (c.editor.tagName === 'TEXTAREA') {
        const setter = Object.getOwnPropertyDescriptor(win.HTMLTextAreaElement.prototype, 'value').set;
        setter.call(c.editor, '');
        c.editor.dispatchEvent(new win.Event('input', {bubbles: true}));
        return true;
      }
      if (!c.editor.textContent) return true;
      // Let the editor observe the native edit so its state and undo history stay consistent.
      c.editor.focus({preventScroll: true});
      const range = doc.createRange(); range.selectNodeContents(c.editor);
      const selection = win.getSelection(); selection.removeAllRanges(); selection.addRange(range);
      return doc.execCommand('delete');
    }
    function sendWhenReady(c, ticket) {
      if (pending !== ticket) return;
      if (!same(c)) { cancelPending(); return; }
      if (Date.now() - ticket.started > 120000) {
        cancelPending(); report('Transcription took too long. Your draft has not been sent.', true); return;
      }
      if (button(c, ['Retry dictation'], false)) {
        cancelPending(); report('Transcription failed. Your draft has not been sent.', true); return;
      }
      const stop = button(c, ['Stop dictation']);
      if (stop && !ticket.stopRequested) { ticket.stopRequested = true; stop.click(); }
      if (!isDictating(c)) {
        const send = button(c, ['Send', 'Send prompt', 'Send message']);
        const text = c.editor.tagName === 'TEXTAREA' ? c.editor.value : c.editor.textContent;
        if (send && text.trim()) {
          cancelPending(); send.click(); report('Message sent'); return;
        }
      }
      timer = win.setTimeout(() => sendWhenReady(c, ticket), 100);
    }
    function perform(action) {
      const c = composer();
      if (!c) { report('Select a chat before using controller voice controls.', true); return false; }
      if (action === 'cancel') {
        cancelPending();
        const abort = button(c, ['Cancel dictation', 'Cancel transcription', 'Starting dictation; click to cancel']);
        if (isDictating(c) && !abort) {
          report('Dictation cannot be canceled in this state yet.', true); return false;
        }
        if (abort) {
          abort.click();
          // Cancellation can restore the pre-dictation text on the next React render.
          const token = generation;
          win.setTimeout(() => {
            if (token === generation && same(c)) {
              const cleared = clear(c);
              report(cleared ? 'Unsent draft cleared' : 'The draft could not be cleared.', !cleared);
            }
          }, 0);
        } else {
          const cleared = clear(c);
          report(cleared ? 'Unsent draft cleared' : 'The draft could not be cleared.', !cleared);
          return cleared;
        }
        return true;
      }
      if (action === 'voice') {
        cancelPending();
        const stop = button(c, ['Stop voice chat']);
        if (stop) { stop.click(); report('Voice chat stopped'); return true; }
        if (isDictating(c)) { report('Stop dictation before starting voice chat.', true); return false; }
        const start = button(c, ['Start voice chat', 'Start new voice chat']);
        if (start) { start.click(); report('Voice chat requested'); return true; }
        report('Voice chat is unavailable in this chat.', true); return false;
      }
      if (action === 'dictate') {
        cancelPending();
        const toggle = button(c, ['Stop dictation', 'Starting dictation; click to cancel', 'Dictate']);
        if (toggle) { toggle.click(); report('Dictation toggled'); return true; }
        report(button(c, ['Stop voice chat']) ? 'Stop voice chat before starting dictation.' :
          'Dictation is busy or unavailable.', true); return false;
      }
      if (action === 'send') {
        if (pending) return false;
        if (isDictating(c)) {
          const stop = button(c, ['Stop dictation']);
          const ticket = {started: Date.now(), stopRequested: !!stop};
          if (stop) stop.click();
          pending = ticket;
          timer = win.setTimeout(() => sendWhenReady(c, ticket), 100);
          report('Finishing dictation before sending'); return true;
        }
        const send = button(c, ['Send', 'Send prompt', 'Send message']);
        if (send) { send.click(); report('Message sent'); return true; }
        report('There is no message ready to send.', true); return false;
      }
      return false;
    }
    return {perform, cancelPending, inspect: () => {
      const c = composer();
      return c ? {available: true, buttons: buttons(c).map(label), dictating: isDictating(c)} : {available: false};
    }};
  }

  function install(win) {
    const doc = win.document;
    let disposed = false, frame = null, toast = null, toastTimer = null, result = null;
    const foreground = () => doc.visibilityState === 'visible' && doc.hasFocus();
    const contextKey = () => win.location.href + '|' +
      (doc.querySelector('[data-app-action-sidebar-thread-active="true"][data-app-action-sidebar-thread-id]')
        ?.getAttribute('data-app-action-sidebar-thread-id') || '');
    function report(message, error = false) {
      result = {message, error};
      if (!error || !foreground()) return;
      if (!toast) {
        toast = doc.createElement('div'); toast.setAttribute('role', 'status');
        toast.style.cssText = 'position:fixed;bottom:130px;left:50%;transform:translateX(-50%);z-index:2147483647;' +
          'padding:8px 14px;border-radius:8px;background:#262626;color:white;font:13px system-ui;pointer-events:none;max-width:420px';
        doc.body.appendChild(toast);
      }
      toast.textContent = message;
      win.clearTimeout(toastTimer);
      toastTimer = win.setTimeout(() => { toast?.remove(); toast = null; }, 3500);
    }
    const actions = createActions(doc, win, {isForeground: foreground, contextKey, report});
    const driver = createDriver({perform: action => {
      try { actions.perform(action); }
      catch (_) { actions.cancelPending(); report('Controller action could not be completed.', true); }
    }, invalidate: actions.cancelPending});
    function tick() {
      if (disposed) return;
      try { driver.update(win.navigator.getGamepads?.() || [], foreground(), contextKey()); }
      catch (_) { driver.reset(); result = {error: true, message: 'Gamepad input is unavailable.'}; }
      frame = win.requestAnimationFrame(tick);
    }
    const blur = () => driver.reset();
    const visibility = () => { if (!foreground()) driver.reset(); };
    win.addEventListener('blur', blur);
    doc.addEventListener('visibilitychange', visibility);
    // Editing or clicking while transcription finishes cancels automatic submission.
    const interaction = event => { if (event.isTrusted) actions.cancelPending(); };
    doc.addEventListener('pointerdown', interaction, true);
    doc.addEventListener('keydown', interaction, true);
    tick();
    return Object.freeze({version: 1, status: () => ({...driver.status(), result, composer: actions.inspect()}),
      dispose: () => {
        disposed = true; win.cancelAnimationFrame(frame); win.clearTimeout(toastTimer);
        driver.reset(); toast?.remove();
        win.removeEventListener('blur', blur); doc.removeEventListener('visibilitychange', visibility);
        doc.removeEventListener('pointerdown', interaction, true); doc.removeEventListener('keydown', interaction, true);
      }});
  }
  return {createDriver, createActions, install, mapping};
});
