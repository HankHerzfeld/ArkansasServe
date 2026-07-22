
  Auth.init();
  let profile = null;
  let orgEvents = [];
  let activeLogEventId = null;

  Auth.requireAuth('EventAdmin').then(async (p) => {
    profile = p;
    if (!profile) return;
    await UI.setupHeader('/org-portal.html');
    // Reload the event list when a SuperAdmin switches the active organization.
    Scope.onChange(() => { loadOrgEvents(); loadMyAssignees(); });
    loadOrgEvents();
    loadMyAssignees();
  });

  // ── My assigned volunteers (#13) ────────────────────────────────────────────
  async function loadMyAssignees() {
    const card = document.getElementById('assignees-card');
    if (!card || !Scope.activeOrgId) { if (card) card.style.display = 'none'; return; }
    try {
      const list = await Api.Assignments.mine(Scope.activeOrgId);
      renderAssignees(list || []);
    } catch {
      card.style.display = 'none'; // caller isn't an admin in this org, or the load failed
    }
  }

  function renderAssignees(list) {
    const card = document.getElementById('assignees-card');
    const empty = document.getElementById('assignees-empty');
    const body = document.getElementById('assignees-body');
    const tbody = document.getElementById('assignees-tbody');
    card.style.display = '';
    tbody.innerHTML = '';
    if (!list.length) { empty.style.display = 'block'; body.style.display = 'none'; return; }
    empty.style.display = 'none'; body.style.display = 'block';

    list.forEach(v => {
      const tr = document.createElement('tr');
      const name = document.createElement('td'); name.textContent = v.name || v.email || v.id; tr.appendChild(name);
      const hrs = document.createElement('td'); hrs.textContent = (v.totalApprovedHours ?? 0); tr.appendChild(hrs);
      tr.appendChild(prefCell(v, 'notifyOnHours'));
      tr.appendChild(prefCell(v, 'notifyOnApproval'));
      tbody.appendChild(tr);
    });
  }

  function prefCell(v, key) {
    const td = document.createElement('td');
    const cb = document.createElement('input');
    cb.type = 'checkbox';
    cb.checked = !!v[key];
    cb.addEventListener('change', async () => {
      cb.disabled = true;
      try {
        await Api.Assignments.setPrefs(v.id, Scope.activeOrgId, { [key]: cb.checked });
        v[key] = cb.checked;
      } catch {
        cb.checked = !cb.checked; // revert on failure
      } finally {
        cb.disabled = false;
      }
    });
    td.appendChild(cb);
    return td;
  }

  document.getElementById('assignee-send')?.addEventListener('click', async () => {
    const ta = document.getElementById('assignee-message');
    const status = document.getElementById('assignee-send-status');
    const msg = (ta.value || '').trim();
    if (!msg) return;
    const btn = document.getElementById('assignee-send');
    btn.disabled = true;
    try {
      const res = await Api.Assignments.notify({ organizationId: Scope.activeOrgId, message: msg });
      ta.value = '';
      status.style.color = 'var(--gray-600)';
      status.textContent = `Sent to ${res.sent} volunteer${res.sent === 1 ? '' : 's'}.`;
      status.style.display = 'block';
      setTimeout(() => { status.style.display = 'none'; }, 4000);
    } catch (err) {
      status.style.color = 'var(--danger,#b91c1c)';
      status.textContent = err.message || 'Could not send.';
      status.style.display = 'block';
    } finally {
      btn.disabled = false;
    }
  });

  async function loadOrgEvents() {
    try {
      orgEvents = await Api.Events.listOrgEvents(Scope.activeOrgId, Scope.activeGroupId);
      document.getElementById('events-loading').style.display = 'none';
      renderOrgEvents(orgEvents);
    } catch (err) {
      document.getElementById('events-loading').innerHTML =
        `<div class="alert alert-error">Could not load events.</div>`;
    }
  }

  // Hard-delete an event (and its sign-ups). Service logs already earned are kept.
  async function deleteEvent(evt) {
    const signups = evt.currentSlots || 0;
    const warn = signups > 0 ? `\n\n${signups} volunteer sign-up(s) will also be removed.` : '';
    if (!window.confirm(`Delete "${evt.title}"? This cannot be undone.${warn}`)) return;
    try {
      await Api.Events.delete(evt.id, evt.organizationId || Scope.activeOrgId);
      await loadOrgEvents();
    } catch (err) {
      window.alert(err.message || 'Could not delete the event. You may not have permission.');
    }
  }

  function renderOrgEvents(events) {
    const list     = document.getElementById('events-list');
    const empty    = document.getElementById('events-empty');
    const pastWrap = document.getElementById('past-events-wrap');
    const pastList = document.getElementById('past-events-list');

    // Past = auto-archived by the nightly ArchivePastEvents timer. Kept out of the main list but
    // still actionable here (Log Hours after the event); the empty state shows only when the org
    // has no events at all, active or past.
    const active = events.filter(e => e.status !== 'Archived');
    const past   = events.filter(e => e.status === 'Archived');

    list.innerHTML = '';
    empty.style.display = (active.length === 0 && past.length === 0) ? 'block' : 'none';
    active.forEach(evt => list.appendChild(buildEventCard(evt)));

    pastList.innerHTML = '';
    if (past.length) {
      document.getElementById('past-events-count').textContent = String(past.length);
      past.forEach(evt => pastList.appendChild(buildEventCard(evt)));
      pastWrap.style.display = '';
    } else {
      pastWrap.style.display = 'none';
    }
  }

  function buildEventCard(evt) {
    const card = document.createElement('div');
    card.className = 'card';
    card.style.cssText = 'display:flex;justify-content:space-between;align-items:center;flex-wrap:wrap;gap:1rem;';

    const info = document.createElement('div');
    const title = document.createElement('div');
    title.className = 'card-title';
    title.textContent = evt.title;
    info.appendChild(title);

    const meta = document.createElement('div');
    meta.className = 'card-meta';
    meta.textContent = `📅 ${new Date(evt.startDateTime).toLocaleString([], { dateStyle:'medium', timeStyle:'short' })} · 📍 ${evt.location} · ${evt.currentSlots} signed up`;
    info.appendChild(meta);

    const status = document.createElement('span');
    status.className = `status status-${evt.status.toLowerCase()}`;
    status.textContent = evt.status;
    info.appendChild(status);

    card.appendChild(info);

    const actions = document.createElement('div');
    actions.style.cssText = 'display:flex;gap:.5rem;flex-wrap:wrap;';

    const editBtn = document.createElement('button');
    editBtn.className = 'btn btn-secondary btn-sm';
    editBtn.textContent = 'Edit';
    editBtn.addEventListener('click', () => openEdit(evt.id));
    actions.appendChild(editBtn);

    const logBtn = document.createElement('button');
    logBtn.className = 'btn btn-primary btn-sm';
    logBtn.textContent = 'Log Hours';
    logBtn.addEventListener('click', () => openLogHours(evt.id, evt.title));
    actions.appendChild(logBtn);

    // Delete is destructive, so it's gated to OrganizationAdmin+ in the active org
    // (mirrors the backend); the backend enforces the real rule regardless.
    const canDelete = Scope.isSuperAdmin
      || Auth.adminRank(Scope.activeOrg()?.adminLevel) >= Auth.adminRank('OrganizationAdmin');
    if (canDelete) {
      const delBtn = document.createElement('button');
      delBtn.className = 'btn btn-danger btn-sm';
      delBtn.textContent = 'Delete';
      delBtn.addEventListener('click', () => deleteEvent(evt));
      actions.appendChild(delBtn);
    }

    card.appendChild(actions);
    return card;
  }

  // Expand/collapse the past-events section.
  document.getElementById('past-events-toggle')?.addEventListener('click', () => {
    const btn = document.getElementById('past-events-toggle');
    const listEl = document.getElementById('past-events-list');
    const open = listEl.style.display !== 'none';
    listEl.style.display = open ? 'none' : '';
    btn.setAttribute('aria-expanded', String(!open));
    const count = document.getElementById('past-events-count')?.textContent || '0';
    btn.innerHTML = `${open ? '▸' : '▾'} Past events (<span id="past-events-count">${count}</span>)`;
  });

  // ── New / Edit event ────────────────────────────────────────────────────
  document.getElementById('btn-new-event').addEventListener('click', () => openEventModal());

  // ── Shift + question builders ──────────────────────────────────────────────
  function newId() { return (crypto.randomUUID ? crypto.randomUUID() : 'x' + Date.now() + Math.random().toString(16).slice(2)); }

  // An ISO/UTC instant → the value a <input type="datetime-local"> expects, in the viewer's
  // LOCAL time. Slicing the raw ISO string (as this form used to) put the UTC clock time in the
  // field — a 10:00am Central event opened showing 3:00pm — and the save path reads the field
  // back as local via `new Date(value)`, so the two disagreed. Building from the local getters
  // closes the round-trip.
  function toLocalInput(iso) {
    if (!iso) return '';
    const d = new Date(iso);
    if (isNaN(d)) return '';
    const p = (n) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}`;
  }

  function buildShiftRow(s = {}) {
    const row = document.createElement('div');
    row.className = 'shift-row';
    row.dataset.id = s.id || newId();
    row.style.cssText = 'display:grid;grid-template-columns:1.2fr 1fr 1fr .7fr auto;gap:.5rem;align-items:center;';
    const label = document.createElement('input'); label.className = 's-label'; label.placeholder = 'Label (e.g. Morning)'; label.value = s.label || '';
    const start = document.createElement('input'); start.className = 's-start'; start.type = 'datetime-local'; start.value = toLocalInput(s.startDateTime);
    const end   = document.createElement('input'); end.className = 's-end'; end.type = 'datetime-local'; end.value = toLocalInput(s.endDateTime);
    const cap   = document.createElement('input'); cap.className = 's-cap'; cap.type = 'number'; cap.min = '0'; cap.placeholder = 'Cap'; cap.value = (s.capacity != null ? s.capacity : '');
    const rm    = document.createElement('button'); rm.type = 'button'; rm.className = 'btn btn-danger btn-sm'; rm.textContent = '✕';
    rm.addEventListener('click', () => row.remove());
    [label, start, end, cap, rm].forEach(el => row.appendChild(el));
    document.getElementById('evt-shifts').appendChild(row);
  }

  function buildQuestionRow(q = {}) {
    const row = document.createElement('div');
    row.className = 'q-row';
    row.dataset.id = q.id || newId();
    row.style.cssText = 'display:grid;grid-template-columns:1.4fr .8fr auto 1.4fr auto;gap:.5rem;align-items:center;';
    const label = document.createElement('input'); label.className = 'q-label'; label.placeholder = 'Question'; label.value = q.label || '';
    const type  = document.createElement('select'); type.className = 'q-type';
    [['text', 'Text'], ['choice', 'Choice']].forEach(([v, t]) => { const o = document.createElement('option'); o.value = v; o.textContent = t; type.appendChild(o); });
    type.value = q.type || 'text';
    const reqWrap = document.createElement('label'); reqWrap.style.cssText = 'font-size:.8rem;white-space:nowrap;';
    const req = document.createElement('input'); req.type = 'checkbox'; req.className = 'q-req'; req.checked = !!q.required;
    reqWrap.appendChild(req); reqWrap.appendChild(document.createTextNode(' Required'));
    const opts = document.createElement('input'); opts.className = 'q-options'; opts.placeholder = 'Option A, Option B'; opts.value = (q.options || []).join(', ');
    opts.style.display = type.value === 'choice' ? '' : 'none';
    type.addEventListener('change', () => { opts.style.display = type.value === 'choice' ? '' : 'none'; });
    const rm = document.createElement('button'); rm.type = 'button'; rm.className = 'btn btn-danger btn-sm'; rm.textContent = '✕';
    rm.addEventListener('click', () => row.remove());
    [label, type, reqWrap, opts, rm].forEach(el => row.appendChild(el));
    document.getElementById('evt-questions').appendChild(row);
  }

  function readShifts() {
    return [...document.querySelectorAll('#evt-shifts .shift-row')].map(row => {
      const start = row.querySelector('.s-start').value;
      const end   = row.querySelector('.s-end').value;
      const cap   = row.querySelector('.s-cap').value;
      return {
        id: row.dataset.id,
        label: row.querySelector('.s-label').value.trim(),
        startDateTime: start ? new Date(start).toISOString() : null,
        endDateTime: end ? new Date(end).toISOString() : null,
        capacity: cap ? Number(cap) : 0,
      };
    }).filter(s => s.label);
  }

  function readQuestions() {
    return [...document.querySelectorAll('#evt-questions .q-row')].map(row => {
      const type = row.querySelector('.q-type').value;
      return {
        id: row.dataset.id,
        label: row.querySelector('.q-label').value.trim(),
        type,
        required: row.querySelector('.q-req').checked,
        options: type === 'choice' ? row.querySelector('.q-options').value.split(',').map(o => o.trim()).filter(Boolean) : [],
      };
    }).filter(q => q.label);
  }

  document.getElementById('evt-add-shift').addEventListener('click', () => buildShiftRow());
  document.getElementById('evt-add-question').addEventListener('click', () => buildQuestionRow());

  // ── ZIP → city/county auto-fill (#16) ──────────────────────────────────────
  function setZipHint(msg, kind) {
    const el = document.getElementById('evt-zip-hint');
    if (!el) return;
    el.textContent = msg || '';
    el.style.display = msg ? 'block' : 'none';
    el.style.color = kind === 'error' ? 'var(--danger, #b91c1c)' : 'var(--gray-600)';
  }

  // Guards against a slow earlier lookup landing after a newer one (out-of-order responses).
  let zipLookupSeq = 0;
  async function autofillFromZip() {
    const zip5 = (document.getElementById('evt-zip').value || '').replace(/\D/g, '').slice(0, 5);
    if (zip5.length !== 5) { setZipHint(''); return; }
    const seq = ++zipLookupSeq;
    setZipHint('Looking up ZIP…');
    try {
      const r = await Api.Geo.lookupZip(zip5);
      if (seq !== zipLookupSeq) return;
      document.getElementById('evt-city').value   = r.city;
      document.getElementById('evt-county').value = r.county;
      setZipHint(`Auto-filled ${r.city}, ${r.county} County. Edit if the venue differs.`);
    } catch {
      if (seq !== zipLookupSeq) return;
      // Leave any city/county the admin already typed; just tell them it wasn't recognised.
      setZipHint('ZIP not in the Arkansas list — enter city and county by hand.', 'error');
    }
  }
  document.getElementById('evt-zip').addEventListener('change', autofillFromZip);
  document.getElementById('evt-zip').addEventListener('input', (e) => {
    if ((e.target.value || '').replace(/\D/g, '').length === 5) autofillFromZip();
  });

  // ── Address autocomplete + real coordinates (#17) ───────────────────────────
  // LAYERED ON TOP of the ZIP path above, never replacing it. #16's bundled dataset resolves a
  // ZIP to its CENTROID, so every event in one ZIP shares a point; a geocoded street address
  // gives the actual venue. But the ZIP path needs no key, no network and no billing, so it
  // stays the floor: if Maps is unconfigured or blocked, the form behaves exactly as before.
  //
  // Precision is recorded so the map can be honest about which it has — pre-#17 events keep
  // centroid coordinates until someone re-saves them, so the dataset is mixed by design.
  let addressPrecision = null;   // 'exact' once geocoded; null = ZIP centroid / unknown

  function applyPlace(p) {
    if (p.lat != null && p.lng != null) {
      document.getElementById('evt-latitude').value  = p.lat;
      document.getElementById('evt-longitude').value = p.lng;
      addressPrecision = 'exact';
    }
    // Only fill what Google actually returned — never blank a value the admin typed.
    if (p.zip)    document.getElementById('evt-zip').value    = p.zip;
    if (p.city)   document.getElementById('evt-city').value   = p.city;
    if (p.county) document.getElementById('evt-county').value = p.county;
    setZipHint(`Located ${p.formatted || 'this address'}. Edit any field if the venue differs.`);
  }

  Maps.attachAutocomplete(document.getElementById('evt-location'), applyPlace)
    .then(ok => { if (!ok) console.info('[org-portal] maps unavailable — ZIP lookup still active'); });

  // Typed an address without picking a suggestion? Geocode it on blur so coordinates are still
  // captured. Silent on failure: the ZIP path has already filled city/county.
  document.getElementById('evt-location').addEventListener('blur', async () => {
    const addr = document.getElementById('evt-location').value.trim();
    if (!addr || addressPrecision === 'exact') return;
    const p = await Maps.geocode(addr).catch(() => null);
    if (p) applyPlace(p);
  });

  function openEventModal(evt = null) {
    document.getElementById('event-modal-title').textContent = evt ? 'Edit Event' : 'New Event';
    document.getElementById('edit-event-id').value   = evt?.id || '';
    document.getElementById('edit-org-id').value     = evt?.organizationId || '';
    document.getElementById('evt-title').value       = evt?.title || '';
    document.getElementById('evt-description').value = evt?.description || '';
    document.getElementById('evt-start').value       = toLocalInput(evt?.startDateTime);
    document.getElementById('evt-end').value         = toLocalInput(evt?.endDateTime);
    document.getElementById('evt-location').value    = evt?.location || '';
    document.getElementById('evt-zip').value          = evt?.zip || '';
    document.getElementById('evt-city').value         = evt?.city || '';
    document.getElementById('evt-county').value       = evt?.county || '';
    document.getElementById('evt-latitude').value     = evt?.latitude ?? '';
    document.getElementById('evt-longitude').value    = evt?.longitude ?? '';
    // Reset per-open: a stored coordinate may be a #16 ZIP centroid rather than a geocoded
    // address, and we cannot tell which from the value alone. Treating it as unknown lets the
    // blur handler re-geocode and upgrade it; treating it as exact would freeze the centroid in.
    addressPrecision = null;
    setZipHint('');
    document.getElementById('evt-hours').value       = evt?.hoursValue ?? 2;
    document.getElementById('evt-slots').value       = evt?.maxSlots ?? 0;
    document.getElementById('evt-category').value    = evt?.category || '';
    document.getElementById('evt-tags').value          = (evt?.tags || []).join(', ');
    document.getElementById('evt-requirements').value  = evt?.requirements || '';
    document.getElementById('evt-external-url').value  = evt?.externalUrl || '';
    document.getElementById('evt-contact-name').value  = evt?.contactName || '';
    document.getElementById('evt-contact-email').value = evt?.contactEmail || '';
    document.getElementById('evt-contact-phone').value = evt?.contactPhone || '';
    document.getElementById('evt-contact-url').value   = evt?.contactUrl || '';
    document.getElementById('evt-photo-blob').value  = evt?.photoBlobName || '';
    // Header-image preview: show the current photo when editing; clear the file picker so a
    // re-open never re-triggers the crop modal with a stale selection.
    const photoPv = document.getElementById('evt-photo-preview');
    const photoPvWrap = document.getElementById('evt-photo-preview-wrap');
    if (evt?.photoUrl) { photoPv.src = evt.photoUrl; photoPvWrap.style.display = ''; }
    else { photoPv.removeAttribute('src'); photoPvWrap.style.display = 'none'; }
    document.getElementById('evt-photo').value = '';

    // External (informational) listing (MVP). One flag drives which fields are shown.
    document.getElementById('evt-external').checked   = evt?.listingType === 'external';
    document.getElementById('evt-host-name').value    = evt?.hostOrganizationName || '';
    document.getElementById('evt-host-url').value     = evt?.hostOrganizationUrl || '';
    syncListingType();

    const groupSel = document.getElementById('evt-group');
    groupSel.innerHTML = '';
    const noneOpt = document.createElement('option');
    noneOpt.value = '';
    noneOpt.textContent = 'No group';
    groupSel.appendChild(noneOpt);
    (Scope.activeGroups() || []).forEach(g => {
      const o = document.createElement('option');
      o.value = g.id;
      o.textContent = g.name;
      groupSel.appendChild(o);
    });
    groupSel.value = evt?.groupId || '';

    document.getElementById('evt-shifts').innerHTML = '';
    (evt?.shifts || []).forEach(buildShiftRow);
    document.getElementById('evt-questions').innerHTML = '';
    (evt?.signupQuestions || []).forEach(buildQuestionRow);

    // Categories come from the effective vocabulary (canonical + approved-new, #10②). A stored
    // value that is still a pending proposal selects "Other" and pre-fills the propose input.
    const catSel = document.getElementById('evt-category');
    const catInput = document.getElementById('evt-category-propose');
    Categories.fillSelect(catSel, evt?.category || '', 'Select…').then(() => {
      Categories.wirePropose(catSel, catInput, {
        hintEl: document.getElementById('evt-category-hint'),
        pendingValue: evt?.category,
      });
    });

    // Recurrence is a create-time choice only. An occurrence is an ordinary event once it
    // exists, and editing one never re-expands its series (decided), so offering these
    // controls on edit would promise something the server does not do.
    resetRecurrenceForm(!evt);

    document.getElementById('event-form-error').style.display = 'none';
    document.getElementById('event-modal').classList.add('open');
  }

  // ── Listing type (informational vs registerable) ───────────────────────────
  // An informational listing is a one-sided post hosted on behalf of an outside org: no
  // sign-up, so every registration-shaped control is hidden. Hiding (not removing) keeps the
  // values intact if the admin toggles back before saving; the save handler zeroes them.
  function syncListingType() {
    const external = document.getElementById('evt-external').checked;
    document.getElementById('evt-host-fields').style.display    = external ? '' : 'none';
    document.getElementById('evt-hours-group').style.display     = external ? 'none' : '';
    document.getElementById('evt-slots-group').style.display     = external ? 'none' : '';
    document.getElementById('evt-shifts-group').style.display    = external ? 'none' : '';
    document.getElementById('evt-questions-group').style.display = external ? 'none' : '';
    // Recurrence shows only when CREATING a registerable event: it is create-only already
    // (hidden on edit), and an informational listing never repeats a sign-up series.
    const isCreate = !document.getElementById('edit-event-id').value;
    document.getElementById('evt-recurrence-group').style.display = (isCreate && !external) ? '' : 'none';
  }
  document.getElementById('evt-external').addEventListener('change', syncListingType);

  // ── Recurrence controls ───────────────────────────────────────────────────

  const DAY_LABELS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];

  function resetRecurrenceForm(isCreate) {
    const group = document.getElementById('evt-recurrence-group');
    group.style.display = isCreate ? '' : 'none';
    document.getElementById('evt-repeat').checked = false;
    document.getElementById('evt-recurrence-fields').style.display = 'none';
    document.getElementById('evt-repeat-freq').value = 'weekly';
    document.getElementById('evt-repeat-endmode').value = 'count';
    document.getElementById('evt-repeat-count').value = '4';
    document.getElementById('evt-repeat-until').value = '';
    document.getElementById('evt-repeat-monthly-mode').value = 'dayofmonth';
    document.getElementById('evt-repeat-preview').textContent = '';
    buildDayCheckboxes();
    syncRecurrenceVisibility();
  }

  function buildDayCheckboxes() {
    const wrap = document.getElementById('evt-repeat-days');
    wrap.innerHTML = '';
    DAY_LABELS.forEach((label, i) => {
      const l = document.createElement('label');
      l.style.cssText = 'display:flex;align-items:center;gap:.25rem;font-size:.85rem;';
      const cb = document.createElement('input');
      cb.type = 'checkbox'; cb.className = 'evt-repeat-day'; cb.value = String(i);
      cb.addEventListener('change', refreshRecurrencePreview);
      l.appendChild(cb);
      l.appendChild(document.createTextNode(label));
      wrap.appendChild(l);
    });
    syncStartDayChecked();
  }

  // The start's own weekday is always an occurrence (the server rejects a rule that excludes
  // it), so it is ticked and locked rather than left as a trap the admin springs on submit.
  function syncStartDayChecked() {
    const startVal = document.getElementById('evt-start').value;
    if (!startVal) return;
    const dow = new Date(startVal).getDay();
    document.querySelectorAll('.evt-repeat-day').forEach(cb => {
      const isStartDay = Number(cb.value) === dow;
      if (isStartDay) { cb.checked = true; cb.disabled = true; cb.title = "The start date's own day"; }
      else if (cb.disabled) { cb.disabled = false; cb.title = ''; }
    });
  }

  function syncRecurrenceVisibility() {
    const on = document.getElementById('evt-repeat').checked;
    document.getElementById('evt-recurrence-fields').style.display = on ? '' : 'none';
    const freq = document.getElementById('evt-repeat-freq').value;
    document.getElementById('evt-repeat-days-wrap').style.display = freq === 'weekly' ? '' : 'none';
    document.getElementById('evt-repeat-monthly-wrap').style.display = freq === 'monthly' ? '' : 'none';
    const endmode = document.getElementById('evt-repeat-endmode').value;
    document.getElementById('evt-repeat-count-wrap').style.display = endmode === 'count' ? '' : 'none';
    document.getElementById('evt-repeat-until-wrap').style.display = endmode === 'until' ? '' : 'none';
  }

  // Builds the rule exactly as the server expects it, so the preview and the create send the
  // same object. Returns null when repeat is off.
  function readRecurrence() {
    if (!document.getElementById('evt-repeat').checked) return null;
    const freq = document.getElementById('evt-repeat-freq').value;
    const rule = { frequency: freq };

    if (freq === 'weekly') {
      rule.daysOfWeek = [...document.querySelectorAll('.evt-repeat-day')]
        .filter(cb => cb.checked).map(cb => Number(cb.value));
    }
    if (freq === 'monthly') {
      rule.byNthWeekday = document.getElementById('evt-repeat-monthly-mode').value === 'nthweekday';
    }
    if (document.getElementById('evt-repeat-endmode').value === 'count') {
      rule.count = Number(document.getElementById('evt-repeat-count').value) || 0;
    } else {
      const d = document.getElementById('evt-repeat-until').value;
      // Send an instant at midday local, not midnight: the server compares `until` on the
      // local calendar day, and a midnight-local value is the safest thing to round-trip
      // through a date input either way. Midday keeps it unambiguous in every zone.
      rule.until = d ? new Date(`${d}T12:00:00`).toISOString() : null;
    }
    return rule;
  }

  let previewTimer = null;
  function refreshRecurrencePreview() {
    syncRecurrenceVisibility();
    syncStartDayChecked();
    clearTimeout(previewTimer);
    previewTimer = setTimeout(doPreview, 250);
  }

  async function doPreview() {
    const out = document.getElementById('evt-repeat-preview');
    const rule = readRecurrence();
    const start = document.getElementById('evt-start').value;
    if (!rule) { out.textContent = ''; return; }
    if (!start) { out.textContent = 'Pick a start date and time to preview the dates.'; return; }

    out.textContent = 'Working out the dates…';
    try {
      const res = await Api.Events.previewRecurrence({
        startDateTime: new Date(start).toISOString(),
        endDateTime: new Date(document.getElementById('evt-end').value || start).toISOString(),
        recurrence: rule,
      });
      out.innerHTML = '';
      const head = document.createElement('div');
      head.style.cssText = 'font-weight:600;color:var(--green);margin-bottom:.25rem;';
      head.textContent = `${res.count} date${res.count === 1 ? '' : 's'}:`;
      out.appendChild(head);
      const list = document.createElement('div');
      list.style.cssText = 'max-height:8rem;overflow-y:auto;';
      res.occurrences.forEach(o => {
        const d = document.createElement('div');
        // Rendered in the viewer's own zone from the real UTC instants the server will
        // store — so a DST shift would be visible here, before anything is created.
        d.textContent = new Date(o.startDateTime).toLocaleString([], { dateStyle: 'full', timeStyle: 'short' });
        list.appendChild(d);
      });
      out.appendChild(list);
    } catch (err) {
      out.textContent = err.message || 'Could not work out those dates.';
    }
  }

  ['evt-repeat', 'evt-repeat-freq', 'evt-repeat-endmode', 'evt-repeat-count',
   'evt-repeat-until', 'evt-repeat-monthly-mode', 'evt-start', 'evt-end'].forEach(id => {
    document.getElementById(id)?.addEventListener('change', refreshRecurrencePreview);
    document.getElementById(id)?.addEventListener('input', refreshRecurrencePreview);
  });

  async function openEdit(eventId) {
    const evt = orgEvents.find(e => e.id === eventId);
    if (evt) openEventModal(evt);
  }

  document.getElementById('event-modal-cancel').addEventListener('click', () => {
    document.getElementById('event-modal').classList.remove('open');
  });

  // ── Header image: crop + resize before upload ──────────────────────────────
  // Choosing a file opens a crop modal (drag to pan, slider to zoom) framed at the header's
  // banner ratio. On confirm the selection is rendered to a fixed 1600×600 canvas — which also
  // downscales large photos — and that JPEG is what gets uploaded, so what the admin frames is
  // exactly what renders as the hero, and no multi-MB original is stored.
  const CROP_FRAME = { w: 480, h: 180 };   // on-screen canvas (8:3 banner)
  const CROP_OUT   = { w: 1600, h: 600 };  // stored resolution
  const crop = { img: null, scale: 1, min: 1, x: 0, y: 0, dragging: false, lastX: 0, lastY: 0 };

  function cropCanvas() { return document.getElementById('crop-canvas'); }

  // Keep the image covering the whole frame — never let a drag/zoom expose empty edges.
  function clampCrop() {
    const iw = crop.img.width * crop.scale, ih = crop.img.height * crop.scale;
    crop.x = Math.min(0, Math.max(CROP_FRAME.w - iw, crop.x));
    crop.y = Math.min(0, Math.max(CROP_FRAME.h - ih, crop.y));
  }

  function drawCrop() {
    const cv = cropCanvas(); const ctx = cv.getContext('2d');
    ctx.clearRect(0, 0, cv.width, cv.height);
    ctx.drawImage(crop.img, crop.x, crop.y, crop.img.width * crop.scale, crop.img.height * crop.scale);
  }

  function openCropModal(file) {
    const url = URL.createObjectURL(file);
    const img = new Image();
    img.onload = () => {
      URL.revokeObjectURL(url);
      crop.img = img;
      // Cover-fit is the floor: min scale makes the image just fill the frame.
      crop.min = Math.max(CROP_FRAME.w / img.width, CROP_FRAME.h / img.height);
      crop.scale = crop.min;
      crop.x = (CROP_FRAME.w - img.width * crop.scale) / 2;
      crop.y = (CROP_FRAME.h - img.height * crop.scale) / 2;
      document.getElementById('crop-zoom').value = '1';
      document.getElementById('crop-progress').style.display = 'none';
      drawCrop();
      document.getElementById('crop-modal').classList.add('open');
    };
    img.onerror = () => window.alert('Could not read that image.');
    img.src = url;
  }

  document.getElementById('evt-photo').addEventListener('change', (e) => {
    const file = e.target.files[0];
    if (file) openCropModal(file);
  });

  // Zoom: slider 1..3 → scale min..min×3, holding the frame centre fixed.
  document.getElementById('crop-zoom').addEventListener('input', (e) => {
    if (!crop.img) return;
    const newScale = crop.min * Number(e.target.value);
    const cx = CROP_FRAME.w / 2, cy = CROP_FRAME.h / 2, ratio = newScale / crop.scale;
    crop.x = cx - (cx - crop.x) * ratio;
    crop.y = cy - (cy - crop.y) * ratio;
    crop.scale = newScale;
    clampCrop(); drawCrop();
  });

  // Drag to pan (pointer events cover mouse + touch).
  (function wireCropDrag() {
    const cv = cropCanvas();
    const pt = (e) => { const r = cv.getBoundingClientRect(); return { x: (e.clientX - r.left) * (cv.width / r.width), y: (e.clientY - r.top) * (cv.height / r.height) }; };
    cv.addEventListener('pointerdown', (e) => { if (!crop.img) return; crop.dragging = true; const p = pt(e); crop.lastX = p.x; crop.lastY = p.y; cv.setPointerCapture(e.pointerId); cv.style.cursor = 'grabbing'; });
    cv.addEventListener('pointermove', (e) => { if (!crop.dragging) return; const p = pt(e); crop.x += p.x - crop.lastX; crop.y += p.y - crop.lastY; crop.lastX = p.x; crop.lastY = p.y; clampCrop(); drawCrop(); });
    const end = () => { crop.dragging = false; cv.style.cursor = 'grab'; };
    cv.addEventListener('pointerup', end); cv.addEventListener('pointercancel', end);
  })();

  document.getElementById('crop-cancel').addEventListener('click', () => {
    document.getElementById('crop-modal').classList.remove('open');
    document.getElementById('evt-photo').value = '';
  });

  document.getElementById('crop-confirm').addEventListener('click', async () => {
    if (!crop.img) return;
    const btn = document.getElementById('crop-confirm');
    const prog = document.getElementById('crop-progress');
    btn.disabled = true; prog.style.display = 'block'; prog.textContent = 'Processing…';
    try {
      // Same crop rectangle, scaled up to the output resolution → resizes big photos too.
      const out = document.createElement('canvas'); out.width = CROP_OUT.w; out.height = CROP_OUT.h;
      const r = CROP_OUT.w / CROP_FRAME.w;
      out.getContext('2d').drawImage(crop.img, crop.x * r, crop.y * r, crop.img.width * crop.scale * r, crop.img.height * crop.scale * r);
      const blob = await new Promise(res => out.toBlob(res, 'image/jpeg', 0.85));
      prog.textContent = 'Uploading…';
      const { sasUrl, blobName } = await Api.Events.uploadToken('header.jpg');
      await fetch(sasUrl, { method: 'PUT', headers: { 'x-ms-blob-type': 'BlockBlob', 'Content-Type': 'image/jpeg' }, body: blob });
      document.getElementById('evt-photo-blob').value = blobName;
      // Immediate preview straight from the canvas we just rendered.
      document.getElementById('evt-photo-preview').src = out.toDataURL('image/jpeg', 0.85);
      document.getElementById('evt-photo-preview-wrap').style.display = '';
      document.getElementById('crop-modal').classList.remove('open');
      document.getElementById('evt-photo').value = '';
    } catch (err) {
      prog.textContent = '❌ Upload failed — please try again.';
    } finally {
      btn.disabled = false;
    }
  });

  document.getElementById('event-modal-save').addEventListener('click', async () => {
    const errEl = document.getElementById('event-form-error');
    errEl.style.display = 'none';
    const title = document.getElementById('evt-title').value.trim();
    const start = document.getElementById('evt-start').value;
    const end   = document.getElementById('evt-end').value;
    if (!title || !start || !end) {
      errEl.textContent = 'Title, start date, and end date are required.';
      errEl.style.display = 'block';
      return;
    }
    // Informational (external) listing: attribution is the whole point, so the host org is
    // required; the registration-shaped fields are zeroed below rather than sent stale.
    const external = document.getElementById('evt-external').checked;
    const hostName = document.getElementById('evt-host-name').value.trim();
    if (external && !hostName) {
      errEl.textContent = 'For an informational listing, name the organization hosting it.';
      errEl.style.display = 'block';
      return;
    }
    const payload = {
      title,
      description:   document.getElementById('evt-description').value,
      startDateTime: new Date(document.getElementById('evt-start').value).toISOString(),
      endDateTime:   new Date(document.getElementById('evt-end').value).toISOString(),
      location:      document.getElementById('evt-location').value,
      zip:           document.getElementById('evt-zip').value.replace(/\D/g, '').slice(0, 5) || null,
      city:          document.getElementById('evt-city').value.trim() || null,
      county:        document.getElementById('evt-county').value.trim() || null,
      // null (not 0) when absent, so the server keeps whatever #16's ZIP lookup resolved rather
      // than pinning the event to the Gulf of Guinea.
      latitude:      parseFloat(document.getElementById('evt-latitude').value)  || null,
      longitude:     parseFloat(document.getElementById('evt-longitude').value) || null,
      // Registration-shaped fields are meaningless on an informational listing; zero them so a
      // display-only post never carries a stray hours credit or capacity.
      hoursValue:    external ? 0 : Number(document.getElementById('evt-hours').value),
      maxSlots:      external ? 0 : Number(document.getElementById('evt-slots').value),
      category:      Categories.valueFrom(document.getElementById('evt-category'), document.getElementById('evt-category-propose')),
      tags:          document.getElementById('evt-tags').value.split(',').map(s => s.trim()).filter(Boolean),
      requirements:  document.getElementById('evt-requirements').value.trim() || null,
      externalUrl:   document.getElementById('evt-external-url').value.trim() || null,
      contactName:   document.getElementById('evt-contact-name').value.trim() || null,
      contactEmail:  document.getElementById('evt-contact-email').value.trim() || null,
      contactPhone:  document.getElementById('evt-contact-phone').value.trim() || null,
      contactUrl:    document.getElementById('evt-contact-url').value.trim() || null,
      listingType:          external ? 'external' : 'hosted',
      hostOrganizationName: external ? hostName : null,
      hostOrganizationUrl:  external ? (document.getElementById('evt-host-url').value.trim() || null) : null,
      groupId:       document.getElementById('evt-group').value || null,
      photoBlobName: document.getElementById('evt-photo-blob').value || null,
      shifts:          external ? [] : readShifts(),
      signupQuestions: external ? [] : readQuestions(),
    };
    const editId = document.getElementById('edit-event-id').value;
    const orgId  = document.getElementById('edit-org-id').value;
    const btn = document.getElementById('event-modal-save');
    btn.disabled = true; btn.textContent = 'Saving…';
    try {
      if (editId) {
        // Never sent on update: editing one occurrence must not re-expand its series.
        await Api.Events.update(editId, { ...payload, organizationId: orgId });
      } else {
        // A series responds with a summary rather than a single event; the caller does not
        // need it, since loadOrgEvents() below shows the created dates in the list — which
        // is this page's existing idiom (it has no toast).
        await Api.Events.create({ ...payload, organizationId: Scope.activeOrgId, recurrence: external ? null : readRecurrence() });
      }
      document.getElementById('event-modal').classList.remove('open');
      loadOrgEvents();
    } catch (err) {
      errEl.textContent = err.message || 'Save failed.';
      errEl.style.display = 'block';
    } finally {
      btn.disabled = false; btn.textContent = 'Save Event';
    }
  });

  // ── Log hours ───────────────────────────────────────────────────────────
  async function openLogHours(eventId, eventTitle) {
    activeLogEventId = eventId;
    document.getElementById('log-event-name').textContent = eventTitle;
    document.getElementById('log-roster-table').style.display = 'none';
    document.getElementById('log-roster-loading').style.display = 'block';
    document.getElementById('log-error').style.display = 'none';
    document.getElementById('log-modal').classList.add('open');

    try {
      const registrations = await Api.Events.registrations(eventId);
      document.getElementById('log-roster-loading').style.display = 'none';

      if (registrations.length === 0) {
        document.getElementById('log-roster-loading').innerHTML =
          '<p style="color:var(--gray-600);">No students have signed up for this event.</p>';
        document.getElementById('log-roster-loading').style.display = 'block';
        return;
      }

      document.getElementById('log-roster-table').style.display = 'table';
      const rosterTbody = document.getElementById('log-roster-tbody');
      rosterTbody.innerHTML = '';
      registrations.forEach(reg => {
        const tr = document.createElement('tr');

        const tdName = document.createElement('td');
        tdName.textContent = reg.studentName;
        tr.appendChild(tdName);

        const tdSchool = document.createElement('td');
        tdSchool.style.cssText = 'font-size:.8rem;color:var(--gray-600);';
        tdSchool.textContent = reg.schoolId;
        tr.appendChild(tdSchool);

        const tdCheck = document.createElement('td');
        const checkbox = document.createElement('input');
        checkbox.type = 'checkbox';
        checkbox.className = 'attend-check';
        checkbox.dataset.student = reg.userId;
        checkbox.dataset.name = reg.studentName;
        checkbox.dataset.school = reg.schoolId;
        checkbox.checked = true;
        tdCheck.appendChild(checkbox);
        tr.appendChild(tdCheck);

        const tdHours = document.createElement('td');
        const hoursInput = document.createElement('input');
        hoursInput.type = 'number';
        hoursInput.className = 'hours-input';
        hoursInput.dataset.student = reg.userId;
        hoursInput.min = '0.5';
        hoursInput.step = '0.5';
        hoursInput.value = '2';
        hoursInput.style.cssText = 'width:70px;padding:.3rem;border:1px solid var(--gray-400);border-radius:4px;';
        tdHours.appendChild(hoursInput);
        tr.appendChild(tdHours);

        rosterTbody.appendChild(tr);
      });
    } catch (err) {
      document.getElementById('log-roster-loading').style.display = 'none';
      document.getElementById('log-error').textContent = 'Could not load roster.';
      document.getElementById('log-error').style.display = 'block';
    }
  }

  document.getElementById('log-modal-cancel').addEventListener('click', () =>
    document.getElementById('log-modal').classList.remove('open'));

  document.getElementById('log-modal-submit').addEventListener('click', async () => {
    const btn = document.getElementById('log-modal-submit');
    btn.disabled = true; btn.textContent = 'Submitting…';
    const errEl = document.getElementById('log-error');
    errEl.style.display = 'none';

    const attending = [...document.querySelectorAll('.attend-check:checked')];
    if (attending.length === 0) {
      errEl.textContent = 'Mark at least one student as attended.';
      errEl.style.display = 'block';
      btn.disabled = false; btn.textContent = 'Submit Hours';
      return;
    }

    try {
      for (const checkbox of attending) {
        const studentId   = checkbox.dataset.student;
        const studentName = checkbox.dataset.name;
        const schoolId    = checkbox.dataset.school;
        const hours       = Number(document.querySelector(`.hours-input[data-student="${studentId}"]`).value);
        await Api.ServiceLogs.create({
          studentId, studentName, schoolId,
          eventId: activeLogEventId,
          hoursLogged: hours,
          serviceDate: new Date().toISOString(),
        });
      }
      document.getElementById('log-modal').classList.remove('open');
      alert(`Hours submitted for ${attending.length} student(s). Their school admin will now review and approve.`);
    } catch (err) {
      errEl.textContent = err.message || 'Submission failed. Please try again.';
      errEl.style.display = 'block';
    } finally {
      btn.disabled = false; btn.textContent = 'Submit Hours';
    }
  });
