
  Auth.init();
  let profile = null;
  let orgEvents = [];
  let activeLogEventId = null;

  Auth.requireAuth('EventAdmin').then(async (p) => {
    profile = p;
    if (!profile) return;
    await UI.setupHeader('/org-portal.html');
    // Reload the event list when a SuperAdmin switches the active organization.
    Scope.onChange(() => loadOrgEvents());
    loadOrgEvents();
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

  function renderOrgEvents(events) {
    const list  = document.getElementById('events-list');
    const empty = document.getElementById('events-empty');
    if (events.length === 0) { empty.style.display = 'block'; return; }
    empty.style.display = 'none';
    list.innerHTML = '';
    events.forEach(evt => {
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

      card.appendChild(actions);
      list.appendChild(card);
    });
  }

  // ── New / Edit event ────────────────────────────────────────────────────
  document.getElementById('btn-new-event').addEventListener('click', () => openEventModal());

  // ── Shift + question builders ──────────────────────────────────────────────
  function newId() { return (crypto.randomUUID ? crypto.randomUUID() : 'x' + Date.now() + Math.random().toString(16).slice(2)); }

  function buildShiftRow(s = {}) {
    const row = document.createElement('div');
    row.className = 'shift-row';
    row.dataset.id = s.id || newId();
    row.style.cssText = 'display:grid;grid-template-columns:1.2fr 1fr 1fr .7fr auto;gap:.5rem;align-items:center;';
    const label = document.createElement('input'); label.className = 's-label'; label.placeholder = 'Label (e.g. Morning)'; label.value = s.label || '';
    const start = document.createElement('input'); start.className = 's-start'; start.type = 'datetime-local'; start.value = s.startDateTime ? s.startDateTime.slice(0, 16) : '';
    const end   = document.createElement('input'); end.className = 's-end'; end.type = 'datetime-local'; end.value = s.endDateTime ? s.endDateTime.slice(0, 16) : '';
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

  function openEventModal(evt = null) {
    document.getElementById('event-modal-title').textContent = evt ? 'Edit Event' : 'New Event';
    document.getElementById('edit-event-id').value   = evt?.id || '';
    document.getElementById('edit-org-id').value     = evt?.organizationId || '';
    document.getElementById('evt-title').value       = evt?.title || '';
    document.getElementById('evt-description').value = evt?.description || '';
    document.getElementById('evt-start').value       = evt?.startDateTime?.slice(0,16) || '';
    document.getElementById('evt-end').value         = evt?.endDateTime?.slice(0,16) || '';
    document.getElementById('evt-location').value    = evt?.location || '';
    document.getElementById('evt-hours').value       = evt?.hoursValue ?? 2;
    document.getElementById('evt-slots').value       = evt?.maxSlots ?? 0;
    document.getElementById('evt-category').value    = evt?.category || '';
    document.getElementById('evt-tags').value          = (evt?.tags || []).join(', ');
    document.getElementById('evt-requirements').value  = evt?.requirements || '';
    document.getElementById('evt-external-url').value  = evt?.externalUrl || '';
    document.getElementById('evt-contact-name').value  = evt?.contactName || '';
    document.getElementById('evt-contact-email').value = evt?.contactEmail || '';
    document.getElementById('evt-contact-phone').value = evt?.contactPhone || '';
    document.getElementById('evt-photo-blob').value  = evt?.photoBlobName || '';

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

    document.getElementById('event-form-error').style.display = 'none';
    document.getElementById('event-modal').classList.add('open');
  }

  async function openEdit(eventId) {
    const evt = orgEvents.find(e => e.id === eventId);
    if (evt) openEventModal(evt);
  }

  document.getElementById('event-modal-cancel').addEventListener('click', () => {
    document.getElementById('event-modal').classList.remove('open');
  });

  document.getElementById('evt-photo').addEventListener('change', async (e) => {
    const file = e.target.files[0];
    if (!file) return;
    document.getElementById('upload-progress').style.display = 'block';
    try {
      const { sasUrl, blobName } = await Api.Events.uploadToken(file.name);
      await fetch(sasUrl, { method: 'PUT', headers: { 'x-ms-blob-type': 'BlockBlob', 'Content-Type': file.type }, body: file });
      // Persist the stable blob name; the API signs it into a read SAS at display time.
      document.getElementById('evt-photo-blob').value = blobName;
      document.getElementById('upload-progress').textContent = '✅ Photo uploaded';
    } catch (err) {
      document.getElementById('upload-progress').textContent = '❌ Upload failed';
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
    const payload = {
      title,
      description:   document.getElementById('evt-description').value,
      startDateTime: new Date(document.getElementById('evt-start').value).toISOString(),
      endDateTime:   new Date(document.getElementById('evt-end').value).toISOString(),
      location:      document.getElementById('evt-location').value,
      hoursValue:    Number(document.getElementById('evt-hours').value),
      maxSlots:      Number(document.getElementById('evt-slots').value),
      category:      document.getElementById('evt-category').value,
      tags:          document.getElementById('evt-tags').value.split(',').map(s => s.trim()).filter(Boolean),
      requirements:  document.getElementById('evt-requirements').value.trim() || null,
      externalUrl:   document.getElementById('evt-external-url').value.trim() || null,
      contactName:   document.getElementById('evt-contact-name').value.trim() || null,
      contactEmail:  document.getElementById('evt-contact-email').value.trim() || null,
      contactPhone:  document.getElementById('evt-contact-phone').value.trim() || null,
      groupId:       document.getElementById('evt-group').value || null,
      photoBlobName: document.getElementById('evt-photo-blob').value || null,
      shifts:          readShifts(),
      signupQuestions: readQuestions(),
    };
    const editId = document.getElementById('edit-event-id').value;
    const orgId  = document.getElementById('edit-org-id').value;
    const btn = document.getElementById('event-modal-save');
    btn.disabled = true; btn.textContent = 'Saving…';
    try {
      if (editId) await Api.Events.update(editId, { ...payload, organizationId: orgId });
      else        await Api.Events.create({ ...payload, organizationId: Scope.activeOrgId });
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
