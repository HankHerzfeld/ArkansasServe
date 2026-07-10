
  'use strict';

  Auth.init();

  const state = {
    tenantId: '',
    currentUser: null,
    users: [],
    vols: [],
    groups: [],
    isSuperAdmin: false,
  };

  const adminLevels = ['Student', 'EventAdmin', 'GroupAdmin', 'OrganizationAdmin', 'SuperAdmin'];

  const PERSON_TYPE_LABEL = { Student: 'Student', AdultVolunteer: 'Adult volunteer', Staff: 'Staff' };
  const personTypeLabel = (t) => PERSON_TYPE_LABEL[t] || '';

  function splitCsv(text) {
    return text
      .split(',')
      .map(x => x.trim())
      .filter(Boolean);
  }

  function userRank(level) {
    const rank = {
      Student: 0,
      EventAdmin: 1,
      GroupAdmin: 2,
      OrganizationAdmin: 3,
      SuperAdmin: 4,
    };
    return rank[level] || 0;
  }

  function clearChildren(element) {
    while (element.firstChild) element.removeChild(element.firstChild);
  }

  function createTextCell(text) {
    const td = document.createElement('td');
    td.textContent = text;
    return td;
  }

  // #24: reference people by name with a context sub-line; email is the hover
  // tiebreaker (title) so two same-named people are still distinguishable.
  function createNameCell(user) {
    const td = document.createElement('td');
    const name = document.createElement('div');
    name.style.fontWeight = '600';
    name.textContent = user.displayName || user.email || '(no name)';
    td.appendChild(name);

    const bits = [personTypeLabel(user.personType), user.grade ? `Grade ${user.grade}` : ''].filter(Boolean);
    if (bits.length) {
      const ctx = document.createElement('div');
      ctx.style.cssText = 'font-size:.75rem;color:var(--gray-600);';
      ctx.textContent = bits.join(' · ');
      td.appendChild(ctx);
    }
    if (user.email) td.title = user.email; // email-on-hover tiebreaker
    return td;
  }

  // Shared name/email substring match used by both table filters.
  function matchesQuery(user, q) {
    if (!q) return true;
    const hay = `${user.displayName || ''} ${user.firstName || ''} ${user.lastName || ''} ${user.email || ''}`.toLowerCase();
    return hay.includes(q);
  }

  function showAdminSections() {
    document.getElementById('tenant-settings-card').style.display = 'block';
    document.getElementById('groups-card').style.display = 'block';
    document.getElementById('users-card').style.display = 'block';
  }

  async function loadContext() {
    const profile = await Auth.requireAuth();
    if (!profile) return;

    // Only a genuine context/auth failure should bounce to the dashboard —
    // a failing sub-section (groups/users) must not redirect the whole page.
    let context;
    try {
      context = await Api.AdminBackend.context();
    } catch (err) {
      window.location.href = '/dashboard.html';
      return;
    }

    state.currentUser = context.user;
    state.contextTenant = context.tenant; // full tenant for the active org
    state.logoDisplayUrl = context.logoDisplayUrl || null; // signed preview for the current logo
    const level = context.user.adminLevel || 'Student';
    if (userRank(level) === 0) {
      window.location.href = '/dashboard.html';
      return;
    }

    state.isSuperAdmin = !!context.canManageDemoUsers;
    showAdminSections();

    if (state.isSuperAdmin) {
      document.getElementById('demo-users-card').style.display = 'block';
      await safe(loadDemoUsers);
      await safe(initDbConsole);
      await safe(initCrawler);
    }

    // The shared scope switcher chooses which org's settings/groups/users to
    // manage — a SuperAdmin gets every tenant; an org admin gets their own org.
    // Groups are managed in full below, so the bar hides its group switcher here.
    await UI.setupHeader('/admin-backend.html', context.user, { showGroups: false });
    Scope.onChange(applyScope);
    applyScope(Scope.snapshot());
  }

  function applyScope(snap) {
    const org = snap.org;
    const summary = document.getElementById('scope-summary');
    if (!org) {
      summary.textContent = state.isSuperAdmin
        ? 'No tenants exist yet. Create one in Platform Admin first.'
        : 'No organization is associated with your account.';
      return;
    }
    state.tenantId = org.id;
    summary.textContent = state.isSuperAdmin
      ? `Managing ${org.name}.`
      : `Signed in as ${state.currentUser?.adminLevel || 'Admin'} for ${org.name}.`;
    // Prefer the full tenant from context (has the profile fields); scope.raw is
    // minimal for non-supers.
    const full = (state.contextTenant && state.contextTenant.id === org.id) ? state.contextTenant : org.raw;
    if (full) populateTenantForm(full);
    safe(loadGroups);
    safe(loadUsers);

    // Volunteers roster is available to GroupAdmin+ (and supers).
    const canVolunteers = state.isSuperAdmin || Auth.adminRank(org.adminLevel) >= Auth.adminRank('GroupAdmin');
    const volCard = document.getElementById('volunteers-card');
    if (canVolunteers) {
      volCard.style.display = 'block';
      populateVolGroupSelect(org.groups || []);
      safe(loadVolunteers);
    } else {
      volCard.style.display = 'none';
    }
    document.getElementById('import-panel').style.display = canVolunteers ? 'block' : 'none';
  }

  // Runs a section loader without letting its failure redirect the whole page.
  async function safe(fn) {
    try {
      await fn();
    } catch (err) {
      console.error('[admin-backend]', err);
    }
  }

  function populateTenantForm(tenant) {
    document.getElementById('tenant-name').value = tenant.name || '';
    document.getElementById('tenant-status').value = tenant.status || 'active';
    document.getElementById('tenant-rbac').checked = !!tenant.rbacEnabled;
    // Defaults to allowed when the field is absent.
    document.getElementById('tenant-allow-groupadmin-volunteers').checked = tenant.allowGroupAdminAddVolunteers !== false;
    document.getElementById('tenant-allow-profile-self-edit').checked = tenant.allowProfileSelfEdit !== false;
    document.getElementById('tenant-description').value   = tenant.description || '';
    document.getElementById('tenant-mission').value       = tenant.mission || '';
    document.getElementById('tenant-website').value       = tenant.website || '';
    document.getElementById('tenant-logo').value          = tenant.logoUrl || '';
    document.getElementById('tenant-logo-blob').value     = tenant.logoBlobName || '';
    // Preview the current logo using the signed display URL (org-logos is private).
    const logoPreview = document.getElementById('tenant-logo-preview');
    const logoShown = state.logoDisplayUrl || tenant.logoUrl || '';
    if (logoShown) { logoPreview.src = logoShown; logoPreview.style.display = 'block'; }
    else { logoPreview.removeAttribute('src'); logoPreview.style.display = 'none'; }
    document.getElementById('tenant-logo-progress').style.display = 'none';
    document.getElementById('tenant-contact-email').value = tenant.contactEmail || '';
    document.getElementById('tenant-contact-phone').value = tenant.contactPhone || '';
    document.getElementById('tenant-address').value       = tenant.address || '';
  }

  function populateVolGroupSelect(groups) {
    const sel = document.getElementById('vol-group');
    clearChildren(sel);
    const none = document.createElement('option');
    none.value = '';
    none.textContent = 'No group';
    sel.appendChild(none);
    (groups || []).forEach(g => {
      const o = document.createElement('option');
      o.value = g.id;
      o.textContent = g.name;
      sel.appendChild(o);
    });
  }

  async function loadVolunteers() {
    state.vols = await Api.Volunteers.list({ organizationId: state.tenantId });
    renderVolunteers();
  }

  function renderVolunteers() {
    const table = document.getElementById('volunteers-table');
    const empty = document.getElementById('volunteers-empty');
    const tbody = document.getElementById('volunteers-tbody');
    const panel = document.getElementById('log-service-panel');
    const count = document.getElementById('volf-count');
    clearChildren(tbody);

    const all = state.vols || [];
    if (!all.length) {
      table.style.display = 'none';
      empty.style.display = 'block';
      empty.textContent = 'No volunteers yet.';
      panel.style.display = 'none';
      count.textContent = '';
      return;
    }
    panel.style.display = 'block';

    const q = (document.getElementById('volf-search').value || '').trim().toLowerCase();
    const type = document.getElementById('volf-type').value;
    const status = document.getElementById('volf-status').value;
    const rows = all.filter(v => {
      if (type && (v.personType || '') !== type) return false;
      if (status === 'managed' && !v.isManaged) return false;
      if (status === 'active' && v.isManaged) return false;
      return matchesQuery(v, q);
    });

    count.textContent = `${rows.length} of ${all.length}`;
    if (!rows.length) {
      table.style.display = 'none';
      empty.style.display = 'block';
      empty.textContent = 'No volunteers match your filters.';
      return;
    }
    table.style.display = 'table';
    empty.style.display = 'none';

    rows.forEach(v => {
      const tr = document.createElement('tr');
      const tdChk = document.createElement('td');
      const chk = document.createElement('input');
      chk.type = 'checkbox';
      chk.className = 'vol-check';
      chk.value = v.id;
      tdChk.appendChild(chk);
      tr.appendChild(tdChk);
      tr.appendChild(createNameCell(v));
      tr.appendChild(createTextCell(personTypeLabel(v.personType) || '—'));
      tr.appendChild(createTextCell(v.email || ''));
      tr.appendChild(createTextCell((v.groupIds || []).join(', ')));
      tr.appendChild(createTextCell(v.isManaged ? 'managed (no login yet)' : 'active'));
      tbody.appendChild(tr);
    });
  }

  async function loadGroups() {
    const groups = await Api.AdminBackend.tenantGroups(state.tenantId);
    state.groups = groups;

    const table = document.getElementById('groups-table');
    const empty = document.getElementById('groups-empty');
    const tbody = document.getElementById('groups-tbody');
    clearChildren(tbody);

    if (!groups.length) {
      table.style.display = 'none';
      empty.style.display = 'block';
      return;
    }

    table.style.display = 'table';
    empty.style.display = 'none';

    groups.forEach(group => {
      const tr = document.createElement('tr');
      tr.appendChild(createTextCell(group.name || ''));
      tr.appendChild(createTextCell(group.status || 'active'));
      tr.appendChild(createTextCell(group.id || ''));
      tbody.appendChild(tr);
    });
  }

  async function loadUsers() {
    state.users = await Api.AdminBackend.users(state.tenantId);
    renderUsers();
  }

  function renderUsers() {
    const table = document.getElementById('users-table');
    const empty = document.getElementById('users-empty');
    const tbody = document.getElementById('users-tbody');
    const count = document.getElementById('users-count');
    clearChildren(tbody);

    const all = state.users || [];
    if (!all.length) {
      table.style.display = 'none';
      empty.style.display = 'block';
      empty.textContent = 'No users in scope.';
      count.textContent = '';
      return;
    }

    const q = (document.getElementById('users-search').value || '').trim().toLowerCase();
    const type = document.getElementById('users-filter-type').value;
    const level = document.getElementById('users-filter-level').value;
    const rows = all.filter(u => {
      if (type && (u.personType || '') !== type) return false;
      if (level && (u.adminLevel || 'Student') !== level) return false;
      return matchesQuery(u, q);
    });

    count.textContent = `${rows.length} of ${all.length}`;
    if (!rows.length) {
      table.style.display = 'none';
      empty.style.display = 'block';
      empty.textContent = 'No users match your filters.';
      return;
    }

    table.style.display = 'table';
    empty.style.display = 'none';

    rows.forEach(user => {
      const tr = document.createElement('tr');
      tr.appendChild(createNameCell(user));
      tr.appendChild(createTextCell(user.email || ''));

      const levelTd = document.createElement('td');
      const levelSelect = document.createElement('select');
      adminLevels.forEach(level => {
        const option = document.createElement('option');
        option.value = level;
        option.textContent = level;
        levelSelect.appendChild(option);
      });
      levelSelect.value = user.adminLevel || 'Student';
      levelTd.appendChild(levelSelect);
      tr.appendChild(levelTd);

      const groupTd = document.createElement('td');
      const groupInput = document.createElement('input');
      groupInput.type = 'text';
      groupInput.value = (user.groupIds || []).join(', ');
      groupTd.appendChild(groupInput);
      tr.appendChild(groupTd);

      const eventTd = document.createElement('td');
      const eventInput = document.createElement('input');
      eventInput.type = 'text';
      eventInput.value = (user.eventAdminEventIds || []).join(', ');
      eventTd.appendChild(eventInput);
      tr.appendChild(eventTd);

      const saveTd = document.createElement('td');
      const saveBtn = document.createElement('button');
      saveBtn.className = 'btn btn-primary btn-sm';
      saveBtn.textContent = 'Save';
      saveBtn.addEventListener('click', async () => {
        saveBtn.disabled = true;
        try {
          await Api.AdminBackend.updateUserAccess(user.id, {
            tenantId: state.tenantId,
            adminLevel: levelSelect.value,
            organizationId: state.tenantId,
            groupIds: splitCsv(groupInput.value),
            eventAdminEventIds: splitCsv(eventInput.value),
          });
          saveBtn.textContent = 'Saved';
          setTimeout(() => { saveBtn.textContent = 'Save'; }, 800);
        } catch {
          saveBtn.textContent = 'Error';
          setTimeout(() => { saveBtn.textContent = 'Save'; }, 1200);
        } finally {
          saveBtn.disabled = false;
        }
      });
      saveTd.appendChild(saveBtn);
      tr.appendChild(saveTd);

      tbody.appendChild(tr);
    });
  }

  // Populate the level filter once and wire both table toolbars to re-render.
  (function wireTableFilters() {
    const levelSel = document.getElementById('users-filter-level');
    adminLevels.forEach(level => {
      const opt = document.createElement('option');
      opt.value = level;
      opt.textContent = level;
      levelSel.appendChild(opt);
    });
    ['users-search', 'users-filter-type', 'users-filter-level'].forEach(id =>
      document.getElementById(id).addEventListener('input', renderUsers));
    ['volf-search', 'volf-type', 'volf-status'].forEach(id =>
      document.getElementById(id).addEventListener('input', renderVolunteers));
  })();

  async function loadDemoUsers() {
    const users = await Api.AdminBackend.demoUsers();
    const table = document.getElementById('demo-users-table');
    const empty = document.getElementById('demo-users-empty');
    const tbody = document.getElementById('demo-users-tbody');
    clearChildren(tbody);

    if (!users.length) {
      table.style.display = 'none';
      empty.style.display = 'block';
      return;
    }

    table.style.display = 'table';
    empty.style.display = 'none';

    users.forEach(user => {
      const tr = document.createElement('tr');
      tr.appendChild(createTextCell(user.displayName || ''));
      tr.appendChild(createTextCell(user.demoUserType || user.adminLevel || ''));
      tr.appendChild(createTextCell(user.email || ''));
      tbody.appendChild(tr);
    });
  }

  // SAS-gated logo upload: fetch a short-lived write token, PUT straight to Blob Storage,
  // then keep the returned blob name (the API signs it into a display URL on read).
  document.getElementById('tenant-logo-file').addEventListener('change', async (e) => {
    const file = e.target.files[0];
    if (!file) return;
    const prog = document.getElementById('tenant-logo-progress');
    if (!state.tenantId) { prog.style.display = 'block'; prog.textContent = 'Select a tenant first.'; return; }
    prog.style.display = 'block'; prog.textContent = 'Uploading…';
    try {
      const { sasUrl, blobName } = await Api.AdminBackend.logoUploadToken(state.tenantId, file.name);
      await fetch(sasUrl, { method: 'PUT', headers: { 'x-ms-blob-type': 'BlockBlob', 'Content-Type': file.type }, body: file });
      document.getElementById('tenant-logo-blob').value = blobName;
      document.getElementById('tenant-logo').value = ''; // an uploaded logo supersedes an external URL
      // No inline object-URL preview (keeps CSP img-src free of blob:); the signed logo
      // renders on the next load. The org directory/profile show it immediately.
      prog.textContent = '✅ Uploaded — click Save to apply';
    } catch (err) {
      prog.textContent = '❌ Upload failed';
    }
  });

  document.getElementById('btn-save-tenant').addEventListener('click', async () => {
    const status = document.getElementById('tenant-save-status');
    if (!state.tenantId) {
      status.textContent = 'Select a tenant first.';
      return;
    }
    status.textContent = 'Saving...';
    try {
      const updated = await Api.AdminBackend.updateTenant(state.tenantId, {
        name: document.getElementById('tenant-name').value.trim(),
        status: document.getElementById('tenant-status').value,
        rbacEnabled: document.getElementById('tenant-rbac').checked,
        allowGroupAdminAddVolunteers: document.getElementById('tenant-allow-groupadmin-volunteers').checked,
        allowProfileSelfEdit: document.getElementById('tenant-allow-profile-self-edit').checked,
        description:  document.getElementById('tenant-description').value.trim(),
        mission:      document.getElementById('tenant-mission').value.trim(),
        website:      document.getElementById('tenant-website').value.trim(),
        logoUrl:      document.getElementById('tenant-logo').value.trim(),
        logoBlobName: document.getElementById('tenant-logo-blob').value,
        contactEmail: document.getElementById('tenant-contact-email').value.trim(),
        contactPhone: document.getElementById('tenant-contact-phone').value.trim(),
        address:      document.getElementById('tenant-address').value.trim(),
      });
      const org = Scope.activeOrg();
      if (org && updated) {
        org.raw = updated;
        state.contextTenant = updated;
        if (updated.name) org.name = updated.name;
      }
      status.textContent = 'Saved';
    } catch (err) {
      status.textContent = err.message || 'Failed';
    }
  });

  document.getElementById('btn-add-group').addEventListener('click', async () => {
    const name = document.getElementById('new-group-name').value.trim();
    if (!name) return;
    if (!state.tenantId) return;
    const btn = document.getElementById('btn-add-group');
    btn.disabled = true;
    try {
      await Api.AdminBackend.createTenantGroup(state.tenantId, { name, status: 'active' });
      document.getElementById('new-group-name').value = '';
      await loadGroups();
    } catch (err) {
      console.error('[admin-backend] add group failed', err);
    } finally {
      btn.disabled = false;
    }
  });

  document.getElementById('btn-reset-demo-users').addEventListener('click', async () => {
    const status = document.getElementById('demo-reset-status');
    status.textContent = 'Resetting...';
    try {
      await Api.AdminBackend.resetDemoUsers();
      await loadDemoUsers();
      status.textContent = 'Reset complete';
    } catch {
      status.textContent = 'Reset failed';
    }
  });

  // ── Database Console (SuperAdmin) ─────────────────────────────────────────
  async function initDbConsole() {
    try {
      const containers = await Api.Db.containers();
      const select = document.getElementById('db-container');
      clearChildren(select);
      containers.forEach(name => {
        const option = document.createElement('option');
        option.value = name;
        option.textContent = name;
        select.appendChild(option);
      });
      document.getElementById('db-console-card').style.display = 'block';
    } catch {
      // SuperAdmin-only tool; stay hidden if it can't initialize.
    }
  }

  document.getElementById('btn-run-query').addEventListener('click', async () => {
    const status  = document.getElementById('db-status');
    const results = document.getElementById('db-results');
    const container = document.getElementById('db-container').value;
    const query     = document.getElementById('db-query').value.trim();
    const maxItems  = parseInt(document.getElementById('db-max').value, 10) || 50;

    if (!query) return;
    status.textContent = 'Running...';
    results.style.display = 'none';

    try {
      const res = await Api.Db.query(container, query, maxItems);
      status.textContent = `${res.count} row(s)`;
      results.textContent = JSON.stringify(res.rows, null, 2);
      results.style.display = 'block';
    } catch (err) {
      status.textContent = err.message || 'Query failed';
    }
  });

  document.getElementById('btn-add-volunteer').addEventListener('click', async () => {
    const status = document.getElementById('vol-status');
    const first = document.getElementById('vol-first').value.trim();
    const last = document.getElementById('vol-last').value.trim();
    const personType = document.getElementById('vol-type').value;
    const email = document.getElementById('vol-email').value.trim();
    const groupId = document.getElementById('vol-group').value;
    if (!first || !last || !email) {
      status.textContent = 'First name, last name, and email are required.';
      return;
    }
    if (!state.tenantId) return;
    const btn = document.getElementById('btn-add-volunteer');
    btn.disabled = true;
    status.textContent = 'Adding...';
    try {
      await Api.Volunteers.create({
        firstName: first,
        lastName: last,
        personType,
        email,
        organizationId: state.tenantId,
        groupIds: groupId ? [groupId] : [],
      });
      document.getElementById('vol-first').value = '';
      document.getElementById('vol-last').value = '';
      document.getElementById('vol-email').value = '';
      document.getElementById('vol-group').value = '';
      status.textContent = 'Added';
      await loadVolunteers();
    } catch (err) {
      status.textContent = err.message || 'Failed to add volunteer';
    } finally {
      btn.disabled = false;
    }
  });

  // ── Log service (bulk, auto-approved) ─────────────────────────────────────
  let logMatchTimer = null;
  document.getElementById('log-event-title').addEventListener('input', () => {
    document.getElementById('log-event-id').value = '';
    clearTimeout(logMatchTimer);
    const q = document.getElementById('log-event-title').value.trim();
    const box = document.getElementById('log-event-matches');
    if (q.length < 2) { box.innerHTML = ''; return; }
    logMatchTimer = setTimeout(async () => {
      try {
        const matches = await Api.Events.match(state.tenantId, q);
        box.innerHTML = '';
        if (!matches.length) return;
        const label = document.createElement('span');
        label.style.color = 'var(--gray-600)';
        label.textContent = 'Reuse existing: ';
        box.appendChild(label);
        matches.forEach(m => {
          const btn = document.createElement('button');
          btn.type = 'button';
          btn.className = 'btn btn-secondary btn-sm';
          btn.style.margin = '.15rem';
          btn.textContent = m.title + (m.organizationId !== state.tenantId ? ' (shared)' : '');
          btn.addEventListener('click', () => {
            document.getElementById('log-event-id').value = m.id;
            document.getElementById('log-event-title').value = m.title;
            box.textContent = 'Using existing event: ' + m.title;
          });
          box.appendChild(btn);
        });
      } catch { /* suggestions are best-effort */ }
    }, 300);
  });

  document.getElementById('btn-log-service').addEventListener('click', async () => {
    const status = document.getElementById('log-service-status');
    const ids = Array.from(document.querySelectorAll('.vol-check:checked')).map(c => c.value);
    if (!ids.length) { status.textContent = 'Select at least one volunteer above.'; return; }

    const title = document.getElementById('log-event-title').value.trim();
    const eventId = document.getElementById('log-event-id').value;
    const date = document.getElementById('log-date').value;
    const hours = Number(document.getElementById('log-hours').value);
    if (!title) { status.textContent = 'An event/activity name is required.'; return; }
    if (!date || !hours) { status.textContent = 'Date and hours are required.'; return; }

    const btn = document.getElementById('btn-log-service');
    btn.disabled = true;
    status.textContent = 'Logging...';
    try {
      const payload = {
        organizationId: state.tenantId,
        volunteerIds: ids,
        hoursLogged: hours,
        serviceDate: new Date(date).toISOString(),
        note: document.getElementById('log-note').value.trim() || null,
        visibility: document.getElementById('log-visibility').value,
      };
      if (eventId) { payload.eventId = eventId; payload.eventTitle = title; }
      else { payload.newEventTitle = title; }

      const res = await Api.ServiceLogs.bulkCreate(payload);
      status.textContent = `Logged ${hours}h for ${res.count} volunteer(s).`;
      document.querySelectorAll('.vol-check:checked').forEach(c => { c.checked = false; });
      document.getElementById('log-event-title').value = '';
      document.getElementById('log-event-id').value = '';
      document.getElementById('log-event-matches').innerHTML = '';
      document.getElementById('log-note').value = '';
    } catch (err) {
      status.textContent = err.message || 'Failed to log service';
    } finally {
      btn.disabled = false;
    }
  });

  // ── CSV import ────────────────────────────────────────────────────────────
  let importRows = [];

  document.getElementById('import-file').addEventListener('change', async (e) => {
    const file = e.target.files[0];
    const status = document.getElementById('import-status');
    document.getElementById('btn-import').disabled = true;
    document.getElementById('import-results').style.display = 'none';
    if (!file) return;
    try {
      importRows = parseCsv(await file.text());
      if (!importRows.length) { status.textContent = 'No data rows found.'; return; }
      status.textContent = `${importRows.length} row(s) ready to import.`;
      document.getElementById('btn-import').disabled = false;
    } catch {
      status.textContent = 'Could not read the file.';
    }
  });

  document.getElementById('btn-import').addEventListener('click', async () => {
    const status = document.getElementById('import-status');
    if (!importRows.length || !state.tenantId) return;
    const btn = document.getElementById('btn-import');
    btn.disabled = true;
    status.textContent = 'Importing...';
    try {
      const res = await Api.ServiceLogs.import({ organizationId: state.tenantId, rows: importRows });
      status.textContent = `Imported ${res.imported}, failed ${res.failed}.`;
      renderImportResults(res.results || []);
      await loadVolunteers();
    } catch (err) {
      status.textContent = err.message || 'Import failed';
    } finally {
      btn.disabled = false;
    }
  });

  function renderImportResults(results) {
    const box = document.getElementById('import-results');
    box.innerHTML = '';
    box.style.display = 'block';
    const failures = results.filter(r => !r.ok);
    if (!failures.length) { box.textContent = 'All rows imported successfully.'; return; }
    const title = document.createElement('div');
    title.style.cssText = 'color:var(--red);margin-bottom:.25rem;';
    title.textContent = `${failures.length} row(s) failed:`;
    box.appendChild(title);
    failures.forEach(f => {
      const line = document.createElement('div');
      line.textContent = `Row ${f.row} (${f.email || 'no email'}): ${f.message}`;
      box.appendChild(line);
    });
  }

  function parseCsv(text) {
    const lines = text.split(/\r?\n/).filter(l => l.trim().length);
    if (!lines.length) return [];
    const header = splitCsvLine(lines[0]).map(h => h.trim().toLowerCase());
    const idx = (n) => header.indexOf(n);
    const rows = [];
    for (let i = 1; i < lines.length; i++) {
      const c = splitCsvLine(lines[i]);
      const at = (n) => { const j = idx(n); return j >= 0 ? (c[j] || '').trim() : ''; };
      rows.push({ email: at('email'), name: at('name'), hours: Number(at('hours')) || 0, date: at('date'), event: at('event') });
    }
    return rows;
  }

  function splitCsvLine(line) {
    const out = [];
    let cur = '';
    let q = false;
    for (let i = 0; i < line.length; i++) {
      const ch = line[i];
      if (q) {
        if (ch === '"') { if (line[i + 1] === '"') { cur += '"'; i++; } else q = false; }
        else cur += ch;
      } else if (ch === '"') {
        q = true;
      } else if (ch === ',') {
        out.push(cur);
        cur = '';
      } else {
        cur += ch;
      }
    }
    out.push(cur);
    return out;
  }

  // ── Event Crawler (SuperAdmin) ────────────────────────────────────────────

  async function initCrawler() {
    // Only SuperAdmins see the crawler card.
    if (!state.isSuperAdmin) return;
    document.getElementById('crawler-card').style.display = 'block';
    await safe(loadCrawlerQueue);
  }

  async function loadCrawlerQueue() {
    const table = document.getElementById('crawler-queue-table');
    const empty = document.getElementById('crawler-queue-empty');
    const tbody = document.getElementById('crawler-queue-tbody');
    const count = document.getElementById('queue-count');
    clearChildren(tbody);

    let drafts;
    try {
      drafts = await Api.Crawler.queue();
    } catch (err) {
      empty.textContent = err.message || 'Failed to load queue.';
      empty.style.display = 'block';
      table.style.display = 'none';
      return;
    }

    count.textContent = drafts.length ? `(${drafts.length})` : '';

    if (!drafts.length) {
      table.style.display = 'none';
      empty.style.display = 'block';
      return;
    }

    table.style.display = 'table';
    empty.style.display = 'none';

    drafts.forEach(evt => {
      const tr = document.createElement('tr');

      // Title
      tr.appendChild(createTextCell(evt.title || '(Untitled)'));

      // Source badge
      const sourceTd = document.createElement('td');
      const badge = document.createElement('span');
      badge.style.cssText =
        'display:inline-block;padding:.15rem .45rem;border-radius:var(--radius);' +
        'font-size:.75rem;font-weight:600;background:var(--gray-200);color:var(--gray-700);';
      badge.textContent = evt.crawlerSourceName || 'External';
      sourceTd.appendChild(badge);
      tr.appendChild(sourceTd);

      // Date
      const date = evt.startDateTime
        ? new Date(evt.startDateTime).toLocaleDateString(undefined, { dateStyle: 'medium' })
        : '—';
      tr.appendChild(createTextCell(date));

      // Location
      tr.appendChild(createTextCell(evt.location || '—'));

      // Organization name
      tr.appendChild(createTextCell(evt.organizationName || '—'));

      // Contact info
      const contactTd = document.createElement('td');
      if (evt.contactEmail) {
        const a = document.createElement('a');
        a.href = `mailto:${evt.contactEmail}`;
        a.textContent = evt.contactEmail;
        a.style.display = 'block';
        contactTd.appendChild(a);
      }
      if (evt.contactPhone) {
        const span = document.createElement('span');
        span.textContent = evt.contactPhone;
        span.style.display = 'block';
        contactTd.appendChild(span);
      }
      if (evt.contactUrl && /^https?:\/\//i.test(evt.contactUrl)) {
        const a = document.createElement('a');
        a.href = evt.contactUrl;
        a.target = '_blank';
        a.rel = 'noopener noreferrer';
        a.textContent = 'Org website';
        a.style.display = 'block';
        contactTd.appendChild(a);
      }
      if (!evt.contactEmail && !evt.contactPhone && !evt.contactUrl) {
        contactTd.textContent = '—';
      }
      tr.appendChild(contactTd);

      // Attribution / source link
      const attrTd = document.createElement('td');
      const attrSpan = document.createElement('span');
      attrSpan.style.cssText = 'font-size:.8rem;color:var(--gray-600);display:block;';
      attrSpan.textContent = 'Added from ';
      attrLink.href = /^https?:\/\//i.test(evt.crawlerSourceUrl || '') ? evt.crawlerSourceUrl : '#';
      attrLink.rel = 'noopener noreferrer';
      attrLink.textContent = evt.crawlerSourceName || 'external source';
      attrSpan.appendChild(attrLink);
      attrTd.appendChild(attrSpan);
      tr.appendChild(attrTd);

      // Actions
      const actionTd = document.createElement('td');
      actionTd.style.whiteSpace = 'nowrap';

      const publishBtn = document.createElement('button');
      publishBtn.className = 'btn btn-primary btn-sm';
      publishBtn.textContent = 'Publish';
      publishBtn.style.marginRight = '.35rem';
      publishBtn.addEventListener('click', async () => {
        publishBtn.disabled = true;
        dismissBtn.disabled = true;
        try {
          await Api.Crawler.publish(evt.id);
          tr.remove();
          const remaining = document.getElementById('crawler-queue-tbody').querySelectorAll('tr').length;
          document.getElementById('queue-count').textContent = remaining ? `(${remaining})` : '';
          if (!remaining) {
            table.style.display = 'none';
            empty.style.display = 'block';
          }
        } catch (err) {
          publishBtn.disabled = false;
          dismissBtn.disabled = false;
          publishBtn.textContent = 'Error';
          setTimeout(() => { publishBtn.textContent = 'Publish'; }, 1500);
        }
      });

      const dismissBtn = document.createElement('button');
      dismissBtn.className = 'btn btn-secondary btn-sm';
      dismissBtn.textContent = 'Dismiss';
      dismissBtn.addEventListener('click', async () => {
        dismissBtn.disabled = true;
        publishBtn.disabled = true;
        try {
          await Api.Crawler.dismiss(evt.id);
          tr.remove();
          const remaining = document.getElementById('crawler-queue-tbody').querySelectorAll('tr').length;
          document.getElementById('queue-count').textContent = remaining ? `(${remaining})` : '';
          if (!remaining) {
            table.style.display = 'none';
            empty.style.display = 'block';
          }
        } catch {
          dismissBtn.disabled = false;
          publishBtn.disabled = false;
        }
      });

      actionTd.appendChild(publishBtn);
      actionTd.appendChild(dismissBtn);
      tr.appendChild(actionTd);

      tbody.appendChild(tr);
    });
  }

  async function runCrawl(isDryRun) {
    const status = document.getElementById('crawl-status');
    const summary = document.getElementById('crawl-summary');
    const sources = Array.from(document.querySelectorAll('.crawler-source:checked')).map(c => c.value);
    if (!sources.length) { status.textContent = 'Select at least one source.'; return; }

    const runBtn = document.getElementById('btn-run-crawl');
    const dryBtn = document.getElementById('btn-dry-run');
    runBtn.disabled = true;
    dryBtn.disabled = true;
    status.textContent = isDryRun ? 'Previewing...' : 'Crawling — this may take a minute...';
    summary.style.display = 'none';

    try {
      const res = await Api.Crawler.run(sources, isDryRun);
      if (isDryRun) {
        summary.textContent = `Dry run: would fetch ${res.fetched ?? 0} event(s) from selected sources (nothing was saved).`;
      } else {
        summary.textContent = `Imported ${res.imported} new event(s), skipped ${res.skipped} duplicate(s).` +
          (res.errors?.length ? ` ${res.errors.length} error(s) — see browser console.` : '');
        if (res.errors?.length) console.error('[Crawler] errors:', res.errors);
        if (res.imported > 0) await safe(loadCrawlerQueue);
      }
      summary.style.display = 'block';
      status.textContent = '';
    } catch (err) {
      status.textContent = err.message || 'Crawl failed';
    } finally {
      runBtn.disabled = false;
      dryBtn.disabled = false;
    }
  }

  document.getElementById('btn-run-crawl').addEventListener('click', () => runCrawl(false));
  document.getElementById('btn-dry-run').addEventListener('click',  () => runCrawl(true));
  document.getElementById('btn-refresh-queue').addEventListener('click', () => safe(loadCrawlerQueue));

  loadContext();
