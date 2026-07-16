// event-checkin.js — the admin's live day-of check-in overview (#14). Reached from an event's
// "Day-of check-in" button with ?e=<eventId>&o=<orgId>. Shows the roster grouped by shift with
// check-in state, lets an EventAdmin+ toggle attendance and add walk-ins, and mints the posted
// self check-in code. "Live" = a light poll of the roster; the real-time/offline layer is #15.

(() => {
  'use strict';

  Auth.init();

  const params = new URLSearchParams(location.search);
  const eventId = params.get('e') || '';
  const orgId = params.get('o') || '';

  const POLL_MS = 8000;
  let latest = null;        // last roster payload
  let pollTimer = null;
  let mutating = false;     // pause polling/re-render while a write is in flight
  let codeInfo = null;      // { code, url, expiresAt } once minted

  Auth.requireAuth().then((profile) => {
    if (!profile) return;
    UI.setupHeader('/events.html');

    const back = document.getElementById('back-link');
    if (eventId) back.href = `/event.html?id=${encodeURIComponent(eventId)}&organizationId=${encodeURIComponent(orgId)}`;

    if (!eventId || !orgId) return fail('This check-in link is incomplete — no event was specified.');

    wireCodePanel();
    wireWalkIn();
    load().then(() => { pollTimer = setInterval(poll, POLL_MS); });
  });

  async function load() {
    try {
      latest = await Api.CheckIn.roster(eventId, orgId);
      render();
      show('checkin-root'); hide('checkin-loading');
    } catch (err) {
      fail(err.message || 'Could not load the roster.');
    }
  }

  async function poll() {
    if (mutating || document.hidden) return;
    try {
      latest = await Api.CheckIn.roster(eventId, orgId);
      render();
    } catch { /* transient; next tick retries */ }
  }

  function render() {
    if (!latest) return;
    text('ci-title', latest.title || 'Event');
    text('ci-count', `${latest.checkedInCount} / ${latest.totalCount}`);
    const when = latest.startDateTime ? new Date(latest.startDateTime) : null;
    text('ci-when', when ? when.toLocaleString() : '');

    renderShiftSelect(latest.shifts || []);
    renderRoster(latest);
  }

  // ── Roster (grouped by shift when the event has them) ───────────────────────
  function renderRoster(data) {
    const root = document.getElementById('ci-roster');
    root.innerHTML = '';
    const regs = data.registrations || [];
    if (regs.length === 0) {
      root.appendChild(el('div', { class: 'card', style: 'text-align:center;color:var(--gray-600);', text: 'Nobody is registered yet. Add walk-ins above as they arrive.' }));
      return;
    }

    const shifts = data.shifts || [];
    if (shifts.length > 0) {
      const byShift = new Map(shifts.map(s => [s.Id, []]));
      const noShift = [];
      for (const r of regs) (byShift.has(r.shiftId) ? byShift.get(r.shiftId) : noShift).push(r);
      for (const s of shifts) root.appendChild(groupCard(s.Label || 'Shift', byShift.get(s.Id) || []));
      if (noShift.length) root.appendChild(groupCard('No shift', noShift));
    } else {
      root.appendChild(groupCard(null, regs));
    }
  }

  function groupCard(label, rows) {
    const card = el('div', { class: 'card', style: 'margin-bottom:1rem;' });
    if (label) {
      const inCount = rows.filter(r => r.checkedInAt).length;
      card.appendChild(el('div', { class: 'modal-title', style: 'margin-bottom:.5rem;font-size:1rem;',
        text: `${label} — ${inCount}/${rows.length} in` }));
    }
    if (rows.length === 0) {
      card.appendChild(el('p', { style: 'color:var(--gray-600);font-size:.9rem;margin:0;', text: 'No one in this shift.' }));
      return card;
    }
    const list = el('div', { style: 'display:flex;flex-direction:column;gap:.4rem;' });
    for (const r of rows) list.appendChild(rosterRow(r));
    card.appendChild(list);
    return card;
  }

  function rosterRow(r) {
    const row = el('div', { style: 'display:flex;align-items:center;justify-content:space-between;gap:.75rem;padding:.4rem 0;border-bottom:1px solid var(--gray-100);' });

    const left = el('div', { style: 'display:flex;align-items:center;gap:.5rem;flex-wrap:wrap;' });
    left.appendChild(el('span', { text: r.studentName || 'Unknown', style: 'font-weight:600;' }));
    if (r.checkedInAt) left.appendChild(el('span', { class: 'event-badge', text: '✓ in', style: 'background:var(--green);color:#fff;' }));
    if (r.crossOrg) left.appendChild(el('span', { class: 'event-badge', text: 'other org', title: 'Registered from another organization', style: 'opacity:.75;' }));
    row.appendChild(left);

    const btn = el('button', {
      class: r.checkedInAt ? 'btn btn-secondary' : 'btn btn-primary',
      text: r.checkedInAt ? 'Undo' : 'Check in',
      style: 'flex:0 0 auto;',
    });
    btn.addEventListener('click', () => toggle(r, btn));
    row.appendChild(btn);
    return row;
  }

  async function toggle(r, btn) {
    const want = !r.checkedInAt;
    btn.disabled = true; mutating = true;
    try {
      const res = await Api.CheckIn.set(eventId, { organizationId: orgId, registrationId: r.id, checkedIn: want });
      // Update in place from server truth, then re-render for the counts.
      r.checkedInAt = res.checkedInAt;
      recount();
      render();
    } catch (err) {
      // blockCheckIn refusals and cancelled-reg conflicts come back here verbatim.
      alertBanner(err.message || 'Could not update check-in.');
    } finally {
      btn.disabled = false; mutating = false;
    }
  }

  function recount() {
    if (!latest) return;
    latest.checkedInCount = (latest.registrations || []).filter(r => r.checkedInAt).length;
  }

  // ── Walk-in ─────────────────────────────────────────────────────────────────
  function renderShiftSelect(shifts) {
    const wrap = document.getElementById('wi-shift-wrap');
    const sel = document.getElementById('wi-shift');
    if (!shifts || shifts.length === 0) { wrap.style.display = 'none'; return; }
    // Rebuild only if the option set changed, so we don't clobber a selection mid-typing.
    const want = shifts.map(s => s.Id).join('|');
    if (sel.dataset.sig === want) { wrap.style.display = ''; return; }
    sel.dataset.sig = want;
    sel.innerHTML = '';
    for (const s of shifts) sel.appendChild(el('option', { value: s.Id, text: s.Label || 'Shift' }));
    wrap.style.display = '';
  }

  function wireWalkIn() {
    const btn = document.getElementById('wi-add');
    const err = document.getElementById('wi-error');
    btn.addEventListener('click', async () => {
      hide('wi-error');
      const first = val('wi-first'), last = val('wi-last'), email = val('wi-email');
      if (!first && !last) { err.textContent = 'Enter at least a first or last name.'; show('wi-error'); return; }
      const body = { organizationId: orgId, firstName: first || null, lastName: last || null };
      if (email) body.email = email;
      const shiftWrap = document.getElementById('wi-shift-wrap');
      if (shiftWrap.style.display !== 'none') body.shiftId = val('wi-shift');

      btn.disabled = true; mutating = true;
      try {
        await Api.CheckIn.walkIn(eventId, body);
        ['wi-first', 'wi-last', 'wi-email'].forEach(id => { document.getElementById(id).value = ''; });
        await load(); // refresh roster + counts
      } catch (e) {
        err.textContent = e.message || 'Could not add the walk-in.'; show('wi-error');
      } finally {
        btn.disabled = false; mutating = false;
      }
    });
  }

  // ── Self check-in code ──────────────────────────────────────────────────────
  function wireCodePanel() {
    document.getElementById('ci-mint').addEventListener('click', mint);
    document.getElementById('ci-rotate').addEventListener('click', mint);
    document.getElementById('ci-copy').addEventListener('click', async () => {
      if (!codeInfo) return;
      try { await navigator.clipboard.writeText(codeInfo.url); alertBanner('Check-in link copied.', 'success'); }
      catch { alertBanner('Copy failed — select the link and copy it manually.'); }
    });
  }

  async function mint() {
    const mintBtn = document.getElementById('ci-mint');
    const rotateBtn = document.getElementById('ci-rotate');
    mintBtn.disabled = true; rotateBtn.disabled = true;
    try {
      codeInfo = await Api.CheckIn.mintCode(eventId, orgId);
      text('ci-code', codeInfo.code);
      const link = document.getElementById('ci-code-url');
      link.href = codeInfo.url; link.textContent = codeInfo.url;
      const exp = codeInfo.expiresAt ? new Date(codeInfo.expiresAt) : null;
      text('ci-code-expiry', exp ? `Valid until ${exp.toLocaleString()}.` : '');
      show('ci-code-panel');
      mintBtn.textContent = 'Regenerate code';
    } catch (err) {
      alertBanner(err.message || 'Could not create a check-in code.');
    } finally {
      mintBtn.disabled = false; rotateBtn.disabled = false;
    }
  }

  // ── helpers ─────────────────────────────────────────────────────────────────
  function fail(msg) {
    hide('checkin-loading'); hide('checkin-root');
    const box = document.getElementById('checkin-error');
    box.textContent = msg; box.style.display = 'block';
    if (pollTimer) clearInterval(pollTimer);
  }

  // Reuses the per-page toast style used elsewhere (event.js), safe-area aware.
  function alertBanner(message, type = 'error') {
    const t = document.createElement('div');
    t.className = `alert alert-${type}`;
    t.style.cssText = 'position:fixed;bottom:max(1.5rem,env(safe-area-inset-bottom));right:max(1.5rem,env(safe-area-inset-right));z-index:999;max-width:320px;box-shadow:0 4px 12px rgba(0,0,0,.15);';
    t.textContent = message;
    document.body.appendChild(t);
    setTimeout(() => t.remove(), 4000);
  }

  function el(tag, opts = {}) {
    const node = document.createElement(tag);
    for (const [k, v] of Object.entries(opts)) {
      if (k === 'text') node.textContent = v;
      else if (k === 'class') node.className = v;
      else node.setAttribute(k, v);
    }
    return node;
  }
  const val = (id) => (document.getElementById(id).value || '').trim();
  const text = (id, v) => { const n = document.getElementById(id); if (n) n.textContent = v; };
  const show = (id) => { const n = document.getElementById(id); if (n) n.style.display = ''; };
  const hide = (id) => { const n = document.getElementById(id); if (n) n.style.display = 'none'; };
})();
