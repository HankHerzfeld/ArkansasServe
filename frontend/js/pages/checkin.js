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

  // The code most recently tried, so the waiver prompt can retry check-in without a re-scan.
  let lastCode = null;

  async function attempt(code) {
    lastCode = code;
    show(loading());
    hide(resultBox());
    hide(manualBox());
    try {
      const res = await Api.CheckIn.self(eventId, { organizationId: orgId, code });
      showSuccess(res);
    } catch (err) {
      // A missing waiver is the ONE refusal here the volunteer can clear themselves (#19), and
      // they are standing at the venue — so offer to sign it now rather than sending them to
      // find an admin. Everything else (expired code, not registered) falls through unchanged.
      const signable = await selfSignableBlockers();
      if (signable.length) { showWaiverPrompt(signable); return; }

      // The server messages are specific and worth surfacing verbatim: expired code, not
      // registered, missing a required tag. Offer manual re-entry so a stale link can be retried.
      showError(err.message || 'Could not check you in.', { allowManual: true });
    }
  }

  // Credentials this org lets the volunteer sign themselves that are currently blocking check-in.
  // Any failure here returns nothing: a lookup problem must not turn one refusal into a worse one.
  async function selfSignableBlockers() {
    try {
      const data = await Api.Tags.mine(orgId);
      return (data.tags || []).filter(t => t.canSelfAttest && !t.current
        && String(t.enforcement || '').toLowerCase() === 'blockcheckin');
    } catch {
      return [];
    }
  }

  function showWaiverPrompt(tags) {
    hide(loading());
    hide(manualBox());
    const box = resultBox();
    box.innerHTML = '';
    box.appendChild(el('div', { style: 'font-size:2.5rem;line-height:1;margin-bottom:.5rem;', text: '📝' }));
    box.appendChild(el('div', { class: 'modal-title', style: 'margin-bottom:.25rem;', text: 'One thing first' }));
    // Deliberately NOT the server's refusal text. That message ends "an event admin can record
    // it", which is true in general but flatly contradicts the tick-boxes below offering to do it
    // themselves — which is the whole point of being prompted here.
    box.appendChild(el('p', {
      style: 'color:var(--gray-600);',
      text: 'This organization needs your agreement before you can check in. You can do that right here.',
    }));

    // One tick per credential — a single "I agree to everything" box would be a worse record of
    // what was actually agreed to.
    const rows = tags.map((t) => {
      const row = el('div', { style: 'text-align:left;border:1px solid var(--gray-200);border-radius:var(--radius);padding:.6rem;margin:.6rem 0;' });
      const label = el('label', { style: 'display:flex;gap:.5rem;align-items:flex-start;font-weight:600;cursor:pointer;' });
      const cb = el('input', { type: 'checkbox' });
      label.appendChild(cb);
      label.appendChild(el('span', { text: `I have read and agree to the ${t.label}.` }));
      row.appendChild(label);
      if (t.description) {
        row.appendChild(el('div', { style: 'font-size:.85rem;color:var(--gray-600);margin-top:.3rem;', text: t.description }));
      }
      box.appendChild(row);
      return { cb, tag: t };
    });

    const status = el('div', { style: 'font-size:.85rem;color:var(--red);margin-top:.5rem;display:none;' });
    const go = el('button', { class: 'btn btn-primary', style: 'margin-top:.5rem;', text: 'Agree and check in' });
    go.addEventListener('click', async () => {
      if (!rows.every(r => r.cb.checked)) {
        status.style.display = 'block';
        status.textContent = 'Please tick each box to agree.';
        return;
      }
      go.disabled = true;
      go.textContent = 'Saving…';
      status.style.display = 'none';
      try {
        for (const r of rows) await Api.Tags.attest(r.tag.id, orgId);
      } catch (e) {
        go.disabled = false;
        go.textContent = 'Agree and check in';
        status.style.display = 'block';
        status.textContent = e.message || 'That could not be saved. Please try again.';
        return;
      }
      attempt(lastCode); // straight back into check-in — no re-scan
    });

    box.appendChild(go);
    box.appendChild(status);
    show(box);
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
