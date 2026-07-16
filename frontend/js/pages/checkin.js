// checkin.js — student self-check-in (#14). Reached by scanning the QR an event admin posts,
// which carries ?e=<eventId>&o=<orgId>&c=<code>. Also supports typing the code by hand when the
// student arrives at /checkin.html without one. Requires sign-in (Entra); a scanned link that
// isn't authenticated bounces through login and returns here.

(() => {
  'use strict';

  Auth.init();

  const params = new URLSearchParams(location.search);
  const eventId = params.get('e') || '';
  const orgId = params.get('o') || '';
  const codeFromLink = (params.get('c') || '').trim();

  const loading = () => document.getElementById('checkin-loading');
  const resultBox = () => document.getElementById('checkin-result');
  const manualBox = () => document.getElementById('checkin-manual');

  Auth.requireAuth().then((profile) => {
    if (!profile) return; // requireAuth redirects to login when there's no session
    UI.setupHeader('/dashboard.html');

    if (!eventId || !orgId) {
      showError('This check-in link is incomplete. Please scan the event’s QR code again, or ask an admin for the current code.', { allowManual: false });
      return;
    }

    if (codeFromLink) {
      attempt(codeFromLink);
    } else {
      showManual();
    }
  });

  async function attempt(code) {
    show(loading());
    hide(resultBox());
    hide(manualBox());
    try {
      const res = await Api.CheckIn.self(eventId, { organizationId: orgId, code });
      showSuccess(res);
    } catch (err) {
      // The server messages are specific and worth surfacing verbatim: expired code, not
      // registered, missing a required tag. Offer manual re-entry so a stale link can be retried.
      showError(err.message || 'Could not check you in.', { allowManual: true });
    }
  }

  function showSuccess(res) {
    hide(loading());
    hide(manualBox());
    const already = res && res.alreadyCheckedIn && res.checkedInAt;
    const when = res && res.checkedInAt ? new Date(res.checkedInAt) : null;
    const title = res && res.title ? res.title : 'your event';
    const box = resultBox();
    box.innerHTML = '';
    box.appendChild(el('div', { style: 'font-size:2.5rem;line-height:1;margin-bottom:.5rem;', text: '✅' }));
    box.appendChild(el('div', { class: 'modal-title', style: 'margin-bottom:.25rem;', text: already ? 'You’re already checked in' : 'You’re checked in' }));
    box.appendChild(el('p', { style: 'color:var(--gray-600);', text: `for ${title}.` }));
    if (when) box.appendChild(el('p', { style: 'color:var(--gray-600);font-size:.85rem;margin-top:.25rem;', text: `Checked in at ${when.toLocaleString()}.` }));
    const back = el('a', { class: 'btn btn-primary', href: '/dashboard.html', text: 'Go to my dashboard', style: 'margin-top:1rem;' });
    box.appendChild(back);
    show(box);
  }

  function showError(message, { allowManual }) {
    hide(loading());
    const box = resultBox();
    box.innerHTML = '';
    box.appendChild(el('div', { style: 'font-size:2.5rem;line-height:1;margin-bottom:.5rem;', text: '⚠️' }));
    box.appendChild(el('div', { class: 'modal-title', style: 'margin-bottom:.25rem;', text: 'Not checked in' }));
    box.appendChild(el('p', { style: 'color:var(--gray-600);', text: message }));
    show(box);
    if (allowManual) showManual(); else hide(manualBox());
  }

  function showManual() {
    hide(loading());
    const box = manualBox();
    const input = document.getElementById('checkin-code');
    const errBox = document.getElementById('checkin-manual-error');
    const submit = document.getElementById('checkin-submit');

    if (codeFromLink) input.value = codeFromLink;
    hide(errBox);

    // Wire once.
    if (!submit.dataset.wired) {
      submit.dataset.wired = '1';
      const go = async () => {
        const code = (input.value || '').trim().toUpperCase();
        hide(errBox);
        if (!code) { errBox.textContent = 'Enter the code shown at the event.'; show(errBox); return; }
        if (!eventId || !orgId) { errBox.textContent = 'This check-in link is incomplete. Scan the event’s QR code again.'; show(errBox); return; }
        submit.disabled = true;
        try {
          const res = await Api.CheckIn.self(eventId, { organizationId: orgId, code });
          showSuccess(res);
        } catch (err) {
          errBox.textContent = err.message || 'Could not check you in.';
          show(errBox);
        } finally {
          submit.disabled = false;
        }
      };
      submit.addEventListener('click', go);
      input.addEventListener('keydown', (e) => { if (e.key === 'Enter') go(); });
    }

    show(box);
    input.focus();
  }

  // ── tiny DOM helper (matches the elem() style used across pages) ────────────
  function el(tag, opts = {}) {
    const node = document.createElement(tag);
    for (const [k, v] of Object.entries(opts)) {
      if (k === 'text') node.textContent = v;
      else if (k === 'class') node.className = v;
      else node.setAttribute(k, v);
    }
    return node;
  }
  function show(node) { if (node) node.style.display = ''; }
  function hide(node) { if (node) node.style.display = 'none'; }
})();
