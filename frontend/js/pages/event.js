  Auth.init();
  let profile = null;
  let currentEvent = null;

  const params = new URLSearchParams(location.search);
  const eventId = params.get('id');
  const orgId = params.get('organizationId') || '';

  Auth.requireAuth().then((p) => {
    profile = p;
    if (!profile) return;
    UI.setupHeader('/events.html');
    loadEvent();
  });

  async function loadEvent() {
    const loading = document.getElementById('event-loading');
    const errBox = document.getElementById('event-error');
    if (!eventId) {
      loading.style.display = 'none';
      errBox.style.display = 'block';
      errBox.textContent = 'No event specified.';
      return;
    }
    try {
      currentEvent = await Api.Events.get(eventId, orgId);
      loading.style.display = 'none';
      renderEvent(currentEvent);
    } catch (err) {
      loading.style.display = 'none';
      errBox.style.display = 'block';
      errBox.textContent = 'Could not load this event. It may have been removed.';
    }
  }

  // Small helpers — textContent only.
  function elem(tag, props = {}, ...kids) {
    const n = document.createElement(tag);
    Object.entries(props).forEach(([k, v]) => {
      if (v == null) return;
      if (k === 'class') n.className = v;
      else if (k === 'text') n.textContent = v;
      else n.setAttribute(k, v);
    });
    kids.forEach(c => { if (c != null) n.appendChild(typeof c === 'string' ? document.createTextNode(c) : c); });
    return n;
  }
  // A titled section that only renders when it has content.
  function section(title, body) {
    if (!body) return null;
    const wrap = elem('div', { class: 'card' });
    wrap.appendChild(elem('div', { class: 'card-title', text: title }));
    wrap.appendChild(typeof body === 'string' ? elem('p', { text: body, style: 'white-space:pre-wrap;font-size:.95rem;' }) : body);
    return wrap;
  }

  function renderEvent(evt) {
    const root = document.getElementById('event-detail');
    root.style.display = 'block';
    root.innerHTML = '';

    // Hero image (only if present).
    if (evt.photoUrl) {
      const img = elem('img', { src: evt.photoUrl, alt: evt.title, style: 'width:100%;max-height:320px;object-fit:cover;border-radius:var(--radius);margin-bottom:1rem;' });
      root.appendChild(img);
    }

    // Header card: title, org, status, category + tags.
    const header = elem('div', { class: 'card' });
    header.appendChild(elem('h1', { text: evt.title, style: 'font-size:1.6rem;color:var(--green);' }));
    if (evt.organizationName) header.appendChild(elem('p', { text: evt.organizationName, style: 'color:var(--gray-600);margin-top:.15rem;' }));

    const badges = elem('div', { style: 'display:flex;gap:.4rem;flex-wrap:wrap;margin-top:.6rem;' });
    const status = elem('span', { class: `status status-${(evt.status || 'open').toLowerCase()}`, text: evt.status || 'Open' });
    badges.appendChild(status);
    if (evt.category) badges.appendChild(elem('span', { class: 'event-badge', text: evt.category }));
    (evt.tags || []).forEach(t => badges.appendChild(elem('span', { class: 'event-badge', text: t })));
    header.appendChild(badges);
    root.appendChild(header);

    // Facts card: date, location (+map), hours, spots. Each row only if known.
    const facts = elem('div', { style: 'display:flex;gap:1.75rem;flex-wrap:wrap;font-size:.95rem;' });
    const addFact = (label, valueNode) => {
      if (valueNode == null) return;
      const f = elem('div');
      f.appendChild(elem('div', { text: label, style: 'color:var(--gray-600);font-size:.75rem;' }));
      f.appendChild(typeof valueNode === 'string' ? elem('div', { text: valueNode }) : valueNode);
      facts.appendChild(f);
    };
    if (evt.startDateTime) {
      const when = new Date(evt.startDateTime).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' })
        + (evt.endDateTime ? ` – ${new Date(evt.endDateTime).toLocaleTimeString([], { timeStyle: 'short' })}` : '');
      addFact('When', when);
    }
    if (evt.location) {
      const mapLink = elem('a', { href: `https://www.google.com/maps/search/?api=1&query=${encodeURIComponent(evt.location)}`, target: '_blank', rel: 'noopener', text: evt.location });
      addFact('Where', mapLink);
    }
    if (evt.hoursValue != null) addFact('Service credit', `${evt.hoursValue} hour${evt.hoursValue === 1 ? '' : 's'}`);
    if (evt.maxSlots > 0) addFact('Spots left', String(Math.max(0, evt.maxSlots - (evt.currentSlots || 0))));
    root.appendChild(section('Details', facts));

    // Shifts (only when present).
    if (evt.shifts && evt.shifts.length) {
      const list = elem('div', { style: 'display:flex;flex-direction:column;gap:.4rem;' });
      evt.shifts.forEach(s => {
        const row = elem('div', { style: 'display:flex;justify-content:space-between;gap:1rem;padding:.5rem .7rem;border:1px solid var(--gray-200);border-radius:var(--radius);font-size:.9rem;flex-wrap:wrap;' });
        const parts = [s.label];
        if (s.startDateTime) parts.push(new Date(s.startDateTime).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' }));
        row.appendChild(elem('div', { text: parts.filter(Boolean).join(' · ') }));
        const spots = s.capacity > 0 ? `${Math.max(0, s.capacity - (s.filled || 0))} spot${(s.capacity - (s.filled || 0)) === 1 ? '' : 's'} left` : 'Open';
        row.appendChild(elem('div', { text: spots, style: 'color:var(--gray-600);' }));
        list.appendChild(row);
      });
      root.appendChild(section('Shifts', list));
    }

    // Description / requirements (only if present).
    const desc = section('About this event', evt.description);
    if (desc) root.appendChild(desc);
    const req = section('What to know', evt.requirements);
    if (req) root.appendChild(req);

    // Contact (only rows with data).
    const contactRows = [];
    if (evt.contactName)  contactRows.push(['Contact', evt.contactName]);
    if (evt.contactEmail) contactRows.push(['Email', evt.contactEmail]);
    if (evt.contactPhone) contactRows.push(['Phone', evt.contactPhone]);
    if (contactRows.length) {
      const grid = elem('div', { style: 'display:flex;gap:1.75rem;flex-wrap:wrap;font-size:.95rem;' });
      contactRows.forEach(([l, v]) => {
        const c = elem('div');
        c.appendChild(elem('div', { text: l, style: 'color:var(--gray-600);font-size:.75rem;' }));
        if (l === 'Email') c.appendChild(elem('a', { href: `mailto:${v}`, text: v }));
        else c.appendChild(elem('div', { text: v }));
        grid.appendChild(c);
      });
      root.appendChild(section('Contact', grid));
    }

    // Actions: external link + sign-up.
    const actions = elem('div', { style: 'display:flex;gap:.5rem;flex-wrap:wrap;margin-top:.5rem;' });
    if (evt.externalUrl) {
      actions.appendChild(elem('a', { class: 'btn btn-secondary', href: evt.externalUrl, target: '_blank', rel: 'noopener', text: 'More info ↗' }));
    }
    if (evt.myRegistration) {
      // Already signed up: show the state + a cancel affordance instead of a Sign Up
      // button that would only 409 on the server's already-registered guard.
      actions.appendChild(elem('span', { class: 'event-badge', text: "✓ You're signed up", style: 'align-self:center;' }));
      const cancelBtn = elem('button', { class: 'btn btn-secondary', text: 'Cancel sign-up' });
      cancelBtn.addEventListener('click', async () => {
        cancelBtn.disabled = true; cancelBtn.textContent = 'Cancelling…';
        try {
          await Api.Registrations.cancel(evt.myRegistration.id, evt.id);
          await loadEvent();
          showToast('Your sign-up was cancelled.', 'success');
        } catch (err) {
          cancelBtn.disabled = false; cancelBtn.textContent = 'Cancel sign-up';
          showToast(err.message || 'Could not cancel. Please try again.', 'error');
        }
      });
      actions.appendChild(cancelBtn);
    } else if (profile.adminLevel === 'Student' && (evt.status === 'Open')) {
      const btn = elem('button', { class: 'btn btn-primary', text: 'Sign Up' });
      btn.addEventListener('click', () => openSignup(evt));
      actions.appendChild(btn);
    }
    if (actions.children.length) root.appendChild(actions);
  }

  // Sign-up modal — builds a shift selector and any custom questions.
  function openSignup(evt) {
    document.getElementById('modal-event-title').textContent   = `Sign Up: ${evt.title}`;
    document.getElementById('modal-event-details').textContent = evt.location ? `📍 ${evt.location}` : '';

    const fields = document.getElementById('signup-fields');
    fields.innerHTML = '';

    if (evt.shifts && evt.shifts.length) {
      const g = elem('div', { class: 'form-group' });
      g.appendChild(elem('label', { for: 'signup-shift', text: 'Choose a shift *' }));
      const sel = elem('select', { id: 'signup-shift' });
      sel.appendChild(elem('option', { value: '', text: 'Select…' }));
      evt.shifts.forEach(s => {
        const full = s.capacity > 0 && (s.filled || 0) >= s.capacity;
        const left = s.capacity > 0 ? ` (${Math.max(0, s.capacity - (s.filled || 0))} left)` : '';
        const when = s.startDateTime ? ` — ${new Date(s.startDateTime).toLocaleString([], { dateStyle: 'short', timeStyle: 'short' })}` : '';
        const opt = elem('option', { value: s.id, text: `${s.label}${when}${full ? ' — FULL' : left}` });
        if (full) opt.setAttribute('disabled', 'true');
        sel.appendChild(opt);
      });
      g.appendChild(sel);
      fields.appendChild(g);
    }

    (evt.signupQuestions || []).forEach(q => {
      const g = elem('div', { class: 'form-group' });
      g.appendChild(elem('label', { for: `q-${q.id}`, text: q.label + (q.required ? ' *' : '') }));
      let input;
      if (q.type === 'choice' && (q.options || []).length) {
        input = elem('select', { id: `q-${q.id}` });
        input.appendChild(elem('option', { value: '', text: 'Select…' }));
        q.options.forEach(o => input.appendChild(elem('option', { value: o, text: o })));
      } else {
        input = elem('input', { id: `q-${q.id}`, type: 'text' });
      }
      input.className = 'signup-q';
      input.setAttribute('data-qid', q.id);
      input.setAttribute('data-q', q.label);
      g.appendChild(input);
      fields.appendChild(g);
    });

    document.getElementById('signup-modal').classList.add('open');
  }
  document.getElementById('modal-cancel').addEventListener('click', () =>
    document.getElementById('signup-modal').classList.remove('open'));
  document.getElementById('modal-confirm').addEventListener('click', async () => {
    const btn = document.getElementById('modal-confirm');

    let shiftId = null;
    const shiftSel = document.getElementById('signup-shift');
    if (shiftSel) {
      shiftId = shiftSel.value;
      if (!shiftId) { showToast('Please choose a shift.', 'error'); return; }
    }

    const questions = currentEvent.signupQuestions || [];
    const answers = [];
    let missing = null;
    document.querySelectorAll('.signup-q').forEach(inp => {
      const qid = inp.getAttribute('data-qid');
      const q = questions.find(x => x.id === qid);
      const val = (inp.value || '').trim();
      if (q && q.required && !val && !missing) missing = q.label;
      if (val) answers.push({ questionId: qid, question: inp.getAttribute('data-q'), answer: val });
    });
    if (missing) { showToast(`Please answer: ${missing}`, 'error'); return; }

    btn.disabled = true; btn.textContent = 'Signing up…';
    try {
      await Api.Registrations.create({ eventId: currentEvent.id, organizationId: currentEvent.organizationId, shiftId, answers });
      document.getElementById('signup-modal').classList.remove('open');
      await loadEvent();
      showToast("You're signed up! We'll see you there.", 'success');
    } catch (err) {
      showToast(err.message || 'Sign-up failed. Please try again.', 'error');
    } finally {
      btn.disabled = false; btn.textContent = 'Confirm Sign-Up';
    }
  });

  function showToast(message, type = 'success') {
    const toast = document.createElement('div');
    toast.className = `alert alert-${type}`;
    toast.style.cssText = 'position:fixed;bottom:1.5rem;right:1.5rem;z-index:999;max-width:320px;box-shadow:0 4px 12px rgba(0,0,0,.15);';
    toast.textContent = message;
    document.body.appendChild(toast);
    setTimeout(() => toast.remove(), 4000);
  }
