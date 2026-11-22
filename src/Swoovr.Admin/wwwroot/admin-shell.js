// Progressive enhancement only: no session, permission or form state is held here.
(() => {
  const narrow = matchMedia('(max-width: 800px)');
  let dispose;
  function initialize() {
    dispose?.();
    const sidebar = document.querySelector('#navigation-drawer .admin-sidebar');
    const home = document.querySelector('#sidebar-home');
    const drawer = document.querySelector('#navigation-drawer');
    const trigger = document.querySelector('[data-drawer-open]');
    if (!sidebar || !home || !drawer || !trigger) return;
    document.documentElement.classList.add('shell-ready');
    const close = () => drawer.close();
    const onClose = () => {
      trigger.setAttribute('aria-expanded', 'false');
      if (narrow.matches) trigger.focus();
    };
    const open = () => { drawer.showModal(); trigger.setAttribute('aria-expanded', 'true'); };
    const backdrop = event => { if (event.target === drawer) close(); };
    const resize = () => {
      if (drawer.open) close();
    };
    trigger.addEventListener('click', open);
    const closeButton = sidebar.querySelector('[data-drawer-close]');
    closeButton.addEventListener('click', close);
    drawer.addEventListener('close', onClose);
    drawer.addEventListener('click', backdrop);
    narrow.addEventListener('change', resize);
    resize();
    dispose = () => {
      narrow.removeEventListener('change', resize);
      drawer.removeEventListener('close', onClose);
      drawer.removeEventListener('click', backdrop);
      trigger.removeEventListener('click', open);
      closeButton.removeEventListener('click', close);
    };
  }
  // Leave the native top layer before enhanced navigation replaces the dialog's DOM.
  document.addEventListener('click', event => {
    if (event.target.closest?.('#navigation-drawer a[href]')) {
      const drawer = document.querySelector('#navigation-drawer');
      if (drawer?.open) drawer.close();
    }
  }, true);
  initialize();
  // The layout is replaced during enhanced SSR navigation as well as full-page posts.
  document.addEventListener('DOMContentLoaded', () => window.Blazor?.addEventListener('enhancedload', initialize), {once:true});
  document.addEventListener('keydown', event => {
    if (event.key === 'Escape') document.querySelector('.staff-menu[open]')?.removeAttribute('open');
  });
})();

// Copy-to-clipboard for reference buttons (data-copy). Progressive enhancement only.
// Issue 412: the async Clipboard API can be refused (Safari, an unfocused document, browser settings) and the old code
// only changed the tooltip, so nothing visibly happened. Fall back to a selection copy placed inside the nearest
// dialog (a modal dialog makes the rest of the page inert, so a textarea on <body> can't be selected), show a visible
// result, and when both ways fail offer the full value, selected, so ⌘C / Ctrl+C works.
(() => {
  function legacyCopy(button, text) {
    const host = button.closest('dialog') ?? document.body;
    const area = document.createElement('textarea');
    area.value = text; area.setAttribute('readonly', ''); area.setAttribute('aria-hidden', 'true');
    area.style.cssText = 'position:fixed;top:0;left:0;width:1px;height:1px;opacity:0;pointer-events:none;';
    host.append(area);
    area.select(); area.setSelectionRange(0, text.length);
    let ok = false;
    try { ok = document.execCommand('copy'); } catch { ok = false; }
    area.remove();
    button.focus({ preventScroll: true });
    return ok;
  }
  // Last resort: the visible <code> is often a shortened id (first 8 characters), so never select that. Show the full
  // data-copy value in a read-only field beside the button, focused and selected for ⌘C / Ctrl+C. It goes away once
  // copied or when focus leaves it.
  function offerManualCopy(button, text) {
    button.parentElement?.querySelector('[data-copy-manual]')?.remove();
    const field = document.createElement('input');
    field.type = 'text'; field.readOnly = true; field.value = text; field.className = 'ui-copy-manual';
    field.dataset.copyManual = ''; field.setAttribute('aria-label', 'Reference to copy'); field.size = Math.min(40, Math.max(8, text.length));
    button.after(field);
    const done = () => setTimeout(() => field.remove(), 0);
    field.addEventListener('copy', done, { once: true });
    field.addEventListener('blur', done, { once: true });
    field.focus({ preventScroll: true }); field.select(); field.setSelectionRange(0, text.length);
  }
  function show(button, state) {
    clearTimeout(button._copyTimer);
    button.dataset.copied = state;
    button.title = state === 'ok' ? 'Copied' : 'Copy failed \u2014 press \u2318C or Ctrl+C';
    let status = button.nextElementSibling?.matches?.('[data-copy-status]') ? button.nextElementSibling : null;
    if (!status) { status = document.createElement('span'); status.className = 'ui-copy-status'; status.dataset.copyStatus = ''; status.setAttribute('role', 'status'); button.after(status); }
    status.textContent = state === 'ok' ? 'Copied' : 'Press \u2318C to copy';
    button._copyTimer = setTimeout(() => { delete button.dataset.copied; button.title = 'Copy'; status.remove(); }, state === 'ok' ? 1500 : 4000);
  }
  document.addEventListener('click', async event => {
    const button = event.target.closest?.('button[data-copy]');
    if (!button) return;
    event.preventDefault();
    const text = button.dataset.copy;
    let ok = false;
    if (navigator.clipboard?.writeText && window.isSecureContext) {
      try { await navigator.clipboard.writeText(text); ok = true; } catch { ok = false; }
    }
    if (!ok) ok = legacyCopy(button, text);
    show(button, ok ? 'ok' : 'failed');
    if (!ok) offerManualCopy(button, text);
  });
})();
