  Auth.init();
  let profile = null;
  let currentEvent = null;

  // Orgs whose roster the viewer may sign up as a group. Mirrors scope.js's rule, which is
  // the app's existing answer to "which orgs can this person act in":
  //   SuperAdmin → every tenant (they can act in any org; their own membership is the
  //                arkansas-serve-root host org, whose roster is admins, not volunteers —
  //                so memberships alone would leave them with an empty list and no way out)
  //   everyone else → the orgs they hold EventAdmin+ in
  // Membership-based, not token-based: a membership admin carries no admin claim on their
  // token, which is exactly the mistake Finding 9 documented.
  let groupAdminOrgs = [];
  // OrganizationAdmin+ in THIS event's org — the level required to delete a whole series.
  let canDeleteSeries = false;
  // The reserved platform/host partition. Matches platform-admin.js, which hardcodes the
  // same id to hide Delete for it. Not a joinable org and never a volunteer roster.
  const ROOT_ORG_ID = 'arkansas-serve-root';

  const params = new URLSearchParams(location.search);
  const eventId = params.get('id');
  const orgId = params.get('organizationId') || '';

  Auth.requireAuth().then((p) => {
    profile = p;
    if (!profile) return;
    UI.setupHeader('/events.html');
    loadEvent();
  });

  async function loadGroupAdminOrgs() {
    try {
      // NOT profile.adminLevel. `profile` here comes from Auth.requireAuth(), which is built
      // from the TOKEN — and this org's SuperAdmin reads as "Student" there, because their
      // role comes from a membership and a membership grants no token claim. That is Finding
      // 9's trap verbatim, and it silently made this whole feature a no-op for the person
      // most likely to use it. /users/me reports the strongest level across all memberships
      // (Finding 2), which is what scope.js asks and therefore what this must ask too.
      const me = await Api.Users.getMe();

      // Deleting a whole series is destructive, so it needs OrganizationAdmin+ IN THE
      // EVENT'S OWN ORG — a stricter test than the group button's EventAdmin+ anywhere, and
      // the same level single-event delete requires. Asked from the same source, for the
      // same reason as above.
      if (me.adminLevel === 'SuperAdmin') {
        canDeleteSeries = true;
      } else {
        const mine = (await Api.Memberships.list().catch(() => []))
          .find(m => m.organizationId === (currentEvent && currentEvent.organizationId));
        canDeleteSeries = !!mine && Auth.adminRank(mine.adminLevel) >= Auth.adminRank('OrganizationAdmin');
      }

      if (me.adminLevel === 'SuperAdmin') {
        const tenants = await Api.Admin.getTenants().catch(() => []);
        groupAdminOrgs = (tenants || [])
          // The host org is never a useful choice: its roster is platform admins, not
          // volunteers, so it is always empty here — and since the public "Arkansas Serve"
          // org shares its name, offering both puts two identical entries in the picker with
          // no way to tell them apart. The org directory omits it for the same reason.
          .filter(t => t.id !== ROOT_ORG_ID)
          .map(t => ({ id: t.id, name: t.name || t.id }));
      } else {
        const memberships = await Api.Memberships.list();
        groupAdminOrgs = (memberships || [])
          .filter(m => Auth.adminRank(m.adminLevel) >= Auth.adminRank('EventAdmin'))
          // Name only — never fall back to the id, which would print a raw tenant GUID in
          // the picker. The API omits memberships whose org no longer exists.
          .map(m => ({ id: m.organizationId, name: m.organizationName }));
      }
    } catch (err) {
      // Non-fatal: without this the group button simply doesn't appear, and the event page
      // itself must still render.
      groupAdminOrgs = [];
    }
  }

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
      await loadGroupAdminOrgs();
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
    // An occurrence looks exactly like a one-off event, which is the point — but someone
    // editing or deleting it should know eleven siblings exist.
    if (evt.seriesId) badges.appendChild(elem('span', { class: 'event-badge', text: '🔁 ' + describeRecurrence(evt.recurrence) }));
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
    // Per-shift when the event has shifts: the Shifts section below breaks the same number
    // down, so reading the overall counter here would contradict it (see availability.js).
    if (!Availability.isUncapped(evt)) addFact('Spots left', Availability.label(evt));
    root.appendChild(section('Details', facts));

    // Shifts (only when present).
    if (evt.shifts && evt.shifts.length) {
      const list = elem('div', { style: 'display:flex;flex-direction:column;gap:.4rem;' });
      evt.shifts.forEach(s => {
        const row = elem('div', { style: 'display:flex;justify-content:space-between;gap:1rem;padding:.5rem .7rem;border:1px solid var(--gray-200);border-radius:var(--radius);font-size:.9rem;flex-wrap:wrap;' });
        const parts = [s.label];
        if (s.startDateTime) parts.push(new Date(s.startDateTime).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' }));
        row.appendChild(elem('div', { text: parts.filter(Boolean).join(' · ') }));
        const left = Availability.shiftRemaining(s);
        const spots = left === Availability.UNCAPPED ? 'Open' : `${left} spot${left === 1 ? '' : 's'} left`;
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

    // Group registration is offered to whoever can act for a roster, which is a different
    // question from the viewer's own sign-up state above: an admin may register their people
    // whether or not they are personally signed up. Shown only once we know they hold
    // EventAdmin+ somewhere, so the button never appears just to 403.
    if (evt.status === 'Open' && groupAdminOrgs.length > 0) {
      const gbtn = elem('button', { class: 'btn btn-secondary', text: 'Register a group' });
      gbtn.addEventListener('click', () => openGroup(evt));
      actions.appendChild(gbtn);
    }

    // Day-of check-in overview (roster, manual check-in, walk-ins, the posted QR). Offered to
    // an admin who can act in THIS event's org — the same membership test the group button uses
    // (groupAdminOrgs holds the orgs the viewer is EventAdmin+ in; a super gets every tenant).
    if (groupAdminOrgs.some(o => o.id === evt.organizationId)) {
      actions.appendChild(elem('a', {
        class: 'btn btn-secondary',
        href: `/event-checkin.html?e=${encodeURIComponent(evt.id)}&o=${encodeURIComponent(evt.organizationId)}`,
        text: 'Day-of check-in',
      }));
    }

    // Only on an occurrence, and only for someone who could delete these events one by one
    // anyway. Deliberately last and btn-danger: it removes dates the viewer is not looking at.
    if (evt.seriesId && canDeleteSeries) {
      const dbtn = elem('button', { class: 'btn btn-danger', text: 'Delete series' });
      dbtn.addEventListener('click', () => deleteSeries(evt, dbtn));
      actions.appendChild(dbtn);
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
        const remaining = Availability.shiftRemaining(s);
        const full = remaining === 0;
        const left = remaining === Availability.UNCAPPED ? '' : ` (${remaining} left)`;
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
    // bottom/right are safe-area aware: the page is edge-to-edge, so a flat 1.5rem would
    // put the toast under the home indicator (portrait) or the notch (landscape).
    toast.style.cssText = 'position:fixed;bottom:max(1.5rem,env(safe-area-inset-bottom));right:max(1.5rem,env(safe-area-inset-right));z-index:999;max-width:320px;box-shadow:0 4px 12px rgba(0,0,0,.15);';
    toast.textContent = message;
    document.body.appendChild(toast);
    setTimeout(() => toast.remove(), 4000);
  }

  // ── Recurring series ──────────────────────────────────────────────────────

  const DAY_NAMES = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];

  // Reads the rule back in the words an admin chose it with. Falls back to a plain label
  // rather than guessing: a badge reading "Repeats undefined" is worse than one reading
  // "Repeats".
  function describeRecurrence(r) {
    if (!r || !r.frequency) return 'Repeats';
    const f = String(r.frequency).toLowerCase();
    if (f === 'daily') return 'Repeats daily';
    if (f === 'weekly') {
      const days = (r.daysOfWeek || []).map(d => DAY_NAMES[d]).filter(Boolean);
      return days.length ? `Repeats weekly on ${days.join(', ')}` : 'Repeats weekly';
    }
    if (f === 'monthly') return r.byNthWeekday ? 'Repeats monthly (same weekday)' : 'Repeats monthly (same date)';
    return 'Repeats';
  }

  // Deleting a whole series is destructive across dates the admin may not be looking at, so
  // the server refuses when anyone is signed up and reports the count. That 409 message is
  // the confirmation text — it knows the real number, and this does not.
  async function deleteSeries(evt, btn) {
    const proceed = (msg) => window.confirm(msg);
    if (!proceed(`Delete every date in this series?\n\n"${evt.title}"\n\nThis cannot be undone.`)) return;

    btn.disabled = true;
    const original = btn.textContent;
    btn.textContent = 'Deleting…';
    try {
      const res = await Api.Events.deleteSeries(evt.seriesId, evt.organizationId, false);
      showToast(`Deleted ${res.occurrencesDeleted} date${res.occurrencesDeleted === 1 ? '' : 's'}.`, 'success');
      window.location.href = '/events.html';
    } catch (err) {
      // The refusal is expected whenever people are signed up. Show the server's own
      // sentence — it carries the count — and only then offer to force.
      const msg = err.message || '';
      if (/signed up/i.test(msg) && proceed(`${msg}\n\nDelete anyway?`)) {
        try {
          const res = await Api.Events.deleteSeries(evt.seriesId, evt.organizationId, true);
          showToast(`Deleted ${res.occurrencesDeleted} date${res.occurrencesDeleted === 1 ? '' : 's'}, cancelling ${res.registrationsRemoved} sign-up${res.registrationsRemoved === 1 ? '' : 's'}.`, 'success');
          window.location.href = '/events.html';
          return;
        } catch (forceErr) {
          showToast(forceErr.message || 'Could not delete the series.', 'error');
        }
      } else if (!/signed up/i.test(msg)) {
        showToast(msg || 'Could not delete the series.', 'error');
      }
      btn.disabled = false;
      btn.textContent = original;
    }
  }

  // ── Group registration (EventAdmin+) ──────────────────────────────────────
  // Two steps: choose people, then answer the event's questions FOR EACH of them.
  // Step 2 is skipped entirely when the event asks nothing, which is the common case.

  const group = { evt: null, orgId: null, roster: [], selected: new Set(), answers: {}, shiftId: null, step: 1 };

  function groupEls() {
    return {
      modal:   document.getElementById('group-modal'),
      title:   document.getElementById('group-modal-title'),
      details: document.getElementById('group-modal-details'),
      fields:  document.getElementById('group-fields'),
      error:   document.getElementById('group-error'),
      back:    document.getElementById('group-back'),
      next:    document.getElementById('group-next'),
      confirm: document.getElementById('group-confirm'),
    };
  }

  function groupError(msg) {
    const { error } = groupEls();
    if (!msg) { error.style.display = 'none'; error.textContent = ''; return; }
    error.textContent = msg;
    error.style.display = 'block';
  }

  async function openGroup(evt) {
    group.evt = evt;
    // Default to the event's own org when the viewer can act there — for a SuperAdmin the
    // list is every tenant, and its first entry is arbitrary (quite possibly the
    // arkansas-serve-root host org, whose roster is admins rather than volunteers, i.e. an
    // empty list and an apparently broken dialog). The org hosting the event is the far
    // likelier intent; otherwise fall back to the first they can act in.
    group.orgId = groupAdminOrgs.some(o => o.id === evt.organizationId)
      ? evt.organizationId
      : (groupAdminOrgs[0]?.id || null);
    group.selected = new Set();
    group.answers = {};
    group.shiftId = null;
    group.step = 1;
    const { modal, title, details } = groupEls();
    title.textContent = 'Register a group';
    details.textContent = evt.title || '';
    groupError(null);
    modal.classList.add('open');
    await loadRoster();
  }

  function closeGroup() { groupEls().modal.classList.remove('open'); }

  async function loadRoster() {
    const { fields } = groupEls();
    fields.textContent = 'Loading roster…';
    try {
      group.roster = await Api.Volunteers.list({ organizationId: group.orgId });
    } catch (err) {
      group.roster = [];
      groupError('Could not load that organization\'s roster.');
    }
    renderGroupStep();
  }

  function personName(p) {
    return p.displayName
      || [p.firstName, p.lastName].filter(Boolean).join(' ')
      || p.email
      || p.id;
  }

  function renderGroupStep() {
    const { fields, back, next, confirm } = groupEls();
    fields.innerHTML = '';
    const questions = group.evt.signupQuestions || [];

    if (group.step === 1) {
      // With no questions there is no step 2, so step 1 submits directly.
      back.style.display = 'none';
      next.style.display = questions.length ? '' : 'none';
      confirm.style.display = questions.length ? 'none' : '';

      // Org picker only when there is a genuine choice to make.
      if (groupAdminOrgs.length > 1) {
        const g = elem('div', { class: 'form-group' });
        g.appendChild(elem('label', { for: 'group-org', text: 'Register people from' }));
        const sel = elem('select', { id: 'group-org' });
        groupAdminOrgs.forEach(o => {
          const opt = elem('option', { value: o.id, text: o.name });
          if (o.id === group.orgId) opt.setAttribute('selected', 'true');
          sel.appendChild(opt);
        });
        sel.addEventListener('change', async () => {
          group.orgId = sel.value;
          group.selected = new Set();
          await loadRoster();
        });
        g.appendChild(sel);
        fields.appendChild(g);
      }

      if (group.evt.shifts && group.evt.shifts.length) {
        const g = elem('div', { class: 'form-group' });
        g.appendChild(elem('label', { for: 'group-shift', text: 'Choose a shift *' }));
        const sel = elem('select', { id: 'group-shift' });
        sel.appendChild(elem('option', { value: '', text: 'Select…' }));
        group.evt.shifts.forEach(s => {
          const remaining = Availability.shiftRemaining(s);
          const full = remaining === 0;
          const when = s.startDateTime ? ` — ${new Date(s.startDateTime).toLocaleString([], { dateStyle: 'short', timeStyle: 'short' })}` : '';
          const opt = elem('option', {
            value: s.id,
            text: `${s.label}${when}${full ? ' — FULL' : remaining === Availability.UNCAPPED ? '' : ` (${remaining} left)`}`,
          });
          if (full) opt.setAttribute('disabled', 'true');
          sel.appendChild(opt);
        });
        g.appendChild(sel);
        fields.appendChild(g);
      }

      const already = new Set();
      if (group.evt.myRegistration) already.add(group.evt.myRegistration.memberId);

      const head = elem('div', { style: 'display:flex;justify-content:space-between;align-items:baseline;gap:1rem;' });
      head.appendChild(elem('label', { text: 'Who is coming?' }));
      const count = elem('span', { id: 'group-count', class: 'event-badge' });
      head.appendChild(count);
      fields.appendChild(head);

      if (!group.roster.length) {
        fields.appendChild(elem('p', { text: 'No one on this roster yet.', style: 'color:var(--gray-600);font-size:.9rem;' }));
      } else {
        // Scroll container, matching how the tables are contained elsewhere: a long roster
        // must not make the dialog itself grow past the frame.
        const list = elem('div', { style: 'max-height:16rem;overflow-y:auto;border:1px solid var(--gray-200);border-radius:var(--radius);padding:.25rem;' });
        group.roster.forEach(p => {
          const row = elem('label', { style: 'display:flex;align-items:center;gap:.5rem;padding:.4rem .5rem;cursor:pointer;' });
          const cb = elem('input', { type: 'checkbox', value: p.id });
          cb.checked = group.selected.has(p.id);
          cb.addEventListener('change', () => {
            if (cb.checked) group.selected.add(p.id); else group.selected.delete(p.id);
            updateGroupCount();
          });
          row.appendChild(cb);
          const who = elem('div');
          who.appendChild(elem('div', { text: personName(p), style: 'font-size:.9rem;' }));
          if (p.email) who.appendChild(elem('div', { text: p.email, style: 'font-size:.75rem;color:var(--gray-600);' }));
          row.appendChild(who);
          list.appendChild(row);
        });
        fields.appendChild(list);
      }
      updateGroupCount();
      return;
    }

    // ── Step 2: the per-person answer grid ──────────────────────────────────
    back.style.display = '';
    next.style.display = 'none';
    confirm.style.display = '';

    fields.appendChild(elem('p', {
      text: 'Answer for each person. An admin answers on their behalf, so these are recorded per volunteer rather than shared.',
      style: 'font-size:.85rem;color:var(--gray-600);margin-bottom:.5rem;',
    }));

    const selected = group.roster.filter(p => group.selected.has(p.id));
    selected.forEach(p => {
      const card = elem('div', { style: 'border:1px solid var(--gray-200);border-radius:var(--radius);padding:.6rem .75rem;margin-bottom:.5rem;' });
      card.appendChild(elem('div', { text: personName(p), style: 'font-weight:600;font-size:.9rem;margin-bottom:.35rem;color:var(--green);' }));
      questions.forEach(q => {
        const g = elem('div', { class: 'form-group', style: 'margin-bottom:.4rem;' });
        g.appendChild(elem('label', { text: q.label + (q.required ? ' *' : ''), style: 'font-size:.8rem;' }));
        const input = elem('input', { type: 'text' });
        input.value = (group.answers[p.id] || {})[q.id] || '';
        input.addEventListener('input', () => {
          group.answers[p.id] = group.answers[p.id] || {};
          group.answers[p.id][q.id] = input.value;
        });
        g.appendChild(input);
        card.appendChild(g);
      });
      fields.appendChild(card);
    });
  }

  function updateGroupCount() {
    const el = document.getElementById('group-count');
    if (el) el.textContent = `${group.selected.size} selected`;
    const { next, confirm } = groupEls();
    const none = group.selected.size === 0;
    next.disabled = none;
    confirm.disabled = none;
  }

  document.getElementById('group-cancel')?.addEventListener('click', closeGroup);
  document.getElementById('group-back')?.addEventListener('click', () => {
    group.step = 1; groupError(null); renderGroupStep();
  });
  document.getElementById('group-next')?.addEventListener('click', () => {
    if (!validateGroupStep1()) return;
    group.step = 2; groupError(null); renderGroupStep();
  });
  document.getElementById('group-confirm')?.addEventListener('click', submitGroup);

  function validateGroupStep1() {
    groupError(null);
    if (group.selected.size === 0) { groupError('Choose at least one person.'); return false; }
    if (group.evt.shifts && group.evt.shifts.length) {
      const shift = document.getElementById('group-shift')?.value;
      if (!shift) { groupError('Choose a shift.'); return false; }
      group.shiftId = shift;
    }
    return true;
  }

  async function submitGroup() {
    // Re-check step 1 even when submitting straight from it (no questions), so the shift
    // requirement can't be skipped by the shorter path.
    if (group.step === 1 && !validateGroupStep1()) return;
    groupError(null);
    const { confirm } = groupEls();
    confirm.disabled = true;
    const original = confirm.textContent;
    confirm.textContent = 'Registering…';

    const registrants = [...group.selected].map(memberId => ({
      memberId,
      answers: Object.entries(group.answers[memberId] || {})
        .filter(([, v]) => v && v.trim())
        .map(([questionId, answer]) => ({ questionId, answer })),
    }));

    try {
      const res = await Api.Registrations.createGroup({
        eventId: group.evt.id,
        organizationId: group.evt.organizationId,
        shiftId: group.shiftId || null,
        registrantOrganizationId: group.orgId,
        registrants,
      });
      closeGroup();
      await loadEvent();
      showToast(`Registered ${res.registered} ${res.registered === 1 ? 'person' : 'people'}.`, 'success');
    } catch (err) {
      // The server's message is the useful one here — "Only 3 spots left", or which person
      // is already registered — so it is surfaced verbatim rather than replaced.
      groupError(err.message || 'Could not register this group.');
      confirm.disabled = false;
      confirm.textContent = original;
    }
  }
