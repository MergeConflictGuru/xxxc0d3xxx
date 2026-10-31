(() => {
  if (location.origin !== 'app://-' || location.pathname !== '/index.html' || window.__caDevtoolsV3) return;
  window.__caDevtoolsV3 = true;
  const style = document.createElement('style');
  style.textContent = '#conversation-accounts-devtools{display:none!important}';
  document.documentElement.appendChild(style);
  const openInspector = __CA_OPEN_DEVTOOLS__;
  window.addEventListener('keydown', event => {
    if (event.key === 'F12') {
      event.preventDefault(); event.stopImmediatePropagation(); openInspector();
    }
  }, true);
})();
