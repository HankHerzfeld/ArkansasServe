// guardian.js — the page a guardian's one-time link lands on (#20).
//
// STANDS ALONE. No auth.js, no api.js, no app shell: a guardian has no account, so the usual
// bootstrap would try to sign them in and fail. Everything here is plain fetch against two
// anonymous endpoints.
//
// THE TOKEN IS REMOVED FROM THE URL as soon as it is redeemed. It is spent by then, but a
// consumed credential sitting in the address bar still ends up in screenshots, shared links and
// browser history for no benefit.
//
// The working session lives in memory ONLY — not sessionStorage, not a cookie. It is short by
// design, and a guardian is likely to be on a shared family device.

'use strict';

(() => {
  let session = null;         // { token, expiresAt }
  let children = [];

  const $ = (id) => document.getElementById(id);

  function showLinkError(message) {
    $('intro').textContent = '';
    $('link-error-text').textContent = message;
    $('link-error').style.display = 'block';
  }

  // textContent everywhere — never innerHTML with server data.
  function el(tag, props = {}, ...kids) {
    const n = document.createElement(tag);
    Object.entries(props).forEach(([k, v]) => {
      if (v == null) return;
      if (k === 'class') n.className = v;
      else if (k === 'text') n.textContent = v;
      else if (k === 'style') n.style.cssText = v;
      else if (k.startsWith('on') && typeof v === 'function') n.addEventListener(k.slice(2), v);
      else n.setAttribute(k, v);
    });
    kids.filter(Boolean).forEach(c => n.appendChild(c));
    return n;
  }

  async function post(path, body) {
    const res = await fetch(path, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    let data = null;
    try { data = await res.json(); } catch { /* empty body on some errors */ }
    return { ok: res.ok, status: res.status, data };
  }

  // "Sat, 8 Aug 2026, 18:00 – Sun, 9 Aug 2026, 10:00" — the span is the point for an overnight
  // event, so the end is shown even when it is on another day.
  function formatWhen(evt) {
    const opts = { dateStyle: 'medium', timeStyle: 'short' };
    const s = evt.startDateTime ? new Date(evt.startDateTime) : null;
    const e = evt.endDateTime ? new Date(evt.endDateTime) : null;
    if (!s || isNaN(s)) return '';
    if (!e || isNaN(e)) return s.toLocaleString([], opts);
    return `${s.toLocaleString([], opts)} – ${e.toLocaleString([], opts)}`;
  }

  // Per-event approval (#20 carve-out). Some events need their own yes even when general consent
  // is on file; this is where the guardian gives it, so the refusal at sign-up has a button that
  // clears it rather than being a dead end.
  function renderEventApprovals(child, row) {
    const events = child.eventApprovals || [];
    if (!events.length) return;

    const withdrawn = String(child.consent?.status || '').toLowerCase() === 'revoked';

    row.appendChild(el('div', {
      text: 'Events needing your approval',
      style: 'font-weight:600;font-size:.9rem;margin-top:.9rem;padding-top:.7rem;border-top:1px solid var(--gray-200);',
    }));
    row.appendChild(el('div', {
      text: 'These events need their own approval, separately from general consent.',
      style: 'font-size:.8rem;color:var(--gray-600);margin-bottom:.5rem;',
    }));
    // Approving an event while consent is withdrawn would record a yes that changes nothing —
    // the withdrawal still blocks sign-up. Say so instead of letting them discover it later.
    if (withdrawn) {
      row.appendChild(el('div', {
        text: 'You have withdrawn consent for this organization, so approving a single event will not let them sign up. Give consent above first.',
        style: 'font-size:.8rem;color:var(--amber);margin-bottom:.5rem;',
      }));
    }

    events.forEach((evt) => {
      const approved = evt.approved === true;
      const box = el('div', { style: 'border:1px solid var(--gray-200);border-radius:var(--radius);padding:.55rem;margin-bottom:.45rem;' });

      box.appendChild(el('div', { text: evt.title || 'Event', style: 'font-weight:600;font-size:.9rem;' }));
      const when = formatWhen(evt);
      if (when) box.appendChild(el('div', { text: when, style: 'font-size:.8rem;color:var(--gray-600);' }));
      if (evt.reason) box.appendChild(el('div', { text: evt.reason, style: 'font-size:.8rem;color:var(--gray-600);margin:.2rem 0 .4rem;' }));

      box.appendChild(el('div', {
        text: approved ? 'Approved' : 'Not approved yet',
        style: `font-size:.85rem;font-weight:600;margin-bottom:.4rem;color:${approved ? 'var(--green)' : 'var(--gray-700)'};`,
      }));

      const status = el('div', { style: 'font-size:.8rem;margin-top:.4rem;display:none;' });
      const btn = el('button', {
        class: approved ? 'btn btn-secondary btn-sm' : 'btn btn-primary btn-sm',
        type: 'button',
        text: approved ? 'Withdraw approval' : 'Approve this event',
        onclick: async () => {
          btn.disabled = true;
          btn.textContent = approved ? 'Withdrawing…' : 'Approving…';
          const res = await post('/api/guardian/consent', {
            sessionToken: session?.token,
            minorUserId: child.minorUserId,
            organizationId: child.organizationId,
            action: approved ? 'revoke' : 'grant',
            eventId: evt.eventId,
          });

          if (!res.ok) {
            status.style.display = 'block';
            status.style.color = 'var(--red)';
            status.textContent = res.status === 401
              ? 'Your session has ended. Please ask the organization for a new link.'
              : (res.data?.error || res.data?.message || 'That could not be saved. Please try again.');
            btn.disabled = false;
            btn.textContent = approved ? 'Withdraw approval' : 'Approve this event';
            return;
          }

          evt.status = res.data?.status;
          evt.approved = String(res.data?.status || '').toLowerCase() === 'granted';
          renderChildren();
        },
      });

      box.appendChild(btn);
      box.appendChild(status);
      row.appendChild(box);
    });
  }

  function renderChildren() {
    const wrap = $('children');
    wrap.innerHTML = '';

    children.forEach((child) => {
      const active = child.consent?.active === true;
      const row = el('div', { style: 'border:1px solid var(--gray-200);border-radius:var(--radius);padding:.75rem;margin-bottom:.6rem;' });

      row.appendChild(el('div', { text: child.minorName || 'Volunteer', style: 'font-weight:600;' }));
      row.appendChild(el('div', {
        text: child.organizationName || child.organizationId,
        style: 'font-size:.85rem;color:var(--gray-600);margin-bottom:.5rem;',
      }));

      const state = el('div', {
        text: active ? 'Consent given' : 'No consent on file',
        style: `font-size:.9rem;margin-bottom:.6rem;color:${active ? 'var(--green)' : 'var(--gray-700)'};font-weight:600;`,
      });
      row.appendChild(state);

      const status = el('div', { style: 'font-size:.85rem;margin-top:.5rem;display:none;' });

      const btn = el('button', {
        class: active ? 'btn btn-secondary btn-sm' : 'btn btn-primary btn-sm',
        type: 'button',
        text: active ? 'Withdraw consent' : 'Give consent',
        onclick: async () => {
          btn.disabled = true;
          const wasActive = active;
          // Say the consequence BEFORE it happens, not after. Withdrawal cancels future
          // sign-ups, and a guardian should not discover that from the result message.
          if (wasActive && !window.confirm(
            'Withdrawing consent will cancel this person\'s upcoming registrations with this '
            + 'organization, and the organization will be told. Continue?')) {
            btn.disabled = false;
            return;
          }
          btn.textContent = wasActive ? 'Withdrawing…' : 'Recording…';
          const res = await post('/api/guardian/consent', {
            sessionToken: session?.token,
            minorUserId: child.minorUserId,
            organizationId: child.organizationId,
            action: wasActive ? 'revoke' : 'grant',
          });

          if (!res.ok) {
            status.style.display = 'block';
            status.style.color = 'var(--red)';
            // 401 here means the 30-minute session lapsed while they read — say so plainly
            // rather than showing a generic failure they cannot act on.
            status.textContent = res.status === 401
              ? 'Your session has ended. Please ask the organization for a new link.'
              : (res.data?.error || res.data?.message || 'That could not be saved. Please try again.');
            btn.disabled = false;
            btn.textContent = wasActive ? 'Withdraw consent' : 'Give consent';
            return;
          }

          child.consent = { active: res.data.active, status: res.data.status };
          const cancelled = res.data.registrationsCancelled || 0;
          renderChildren();
          const fresh = $('children').lastElementChild;
          if (cancelled > 0 && fresh) {
            fresh.appendChild(el('div', {
              text: `${cancelled} upcoming registration${cancelled === 1 ? '' : 's'} cancelled.`,
              style: 'font-size:.85rem;color:var(--gray-700);margin-top:.5rem;',
            }));
          }
        },
      });

      row.appendChild(btn);
      row.appendChild(status);
      renderEventApprovals(child, row);
      wrap.appendChild(row);
    });
  }

  async function start() {
    const params = new URLSearchParams(location.search);
    const token = params.get('token');
    if (!token) {
      showLinkError('This page needs the link that was emailed to you.');
      return;
    }

    const res = await post('/api/guardian/redeem', { token });

    // Strip the token whether or not it worked: on success it is spent, and on failure it is
    // worthless — either way it should not persist in history or a shared URL.
    history.replaceState(null, '', location.pathname);

    if (!res.ok) {
      showLinkError(res.data?.error || res.data?.message || 'This link is not valid.');
      return;
    }

    session = { token: res.data.sessionToken, expiresAt: res.data.sessionExpiresAt };
    children = res.data.children || [];

    $('intro').textContent = 'You can review and manage consent below.';
    $('guardian-details').textContent = [res.data.name, res.data.email].filter(Boolean).join(' · ');
    if (res.data.reason) {
      $('reason-line').textContent = `Why you were contacted: ${res.data.reason}`;
      $('reason-line').style.display = 'block';
    }

    const mins = Math.max(1, Math.round((new Date(session.expiresAt) - Date.now()) / 60000));
    $('session-note').textContent =
      `This page stays open for about ${mins} minutes. After that you'll need a new link.`;

    renderChildren();
    $('content').style.display = 'block';
  }

  start().catch(() => showLinkError('Something went wrong opening your link. Please try again.'));
})();
