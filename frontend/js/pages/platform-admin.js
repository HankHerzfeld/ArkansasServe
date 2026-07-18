
  Auth.init();
  Auth.requireAuth('SuperAdmin').then((profile) => {
    if (!profile) return;
    UI.setupHeader('/platform-admin.html');
    loadTenants();
    setupTabs();
  });

  // ── Tabs ────────────────────────────────────────────────────────────────
  function setupTabs() {
    document.querySelectorAll('.tab-btn').forEach(btn => {
      btn.addEventListener('click', () => {
        document.querySelectorAll('.tab-btn').forEach(b => b.classList.remove('active'));
        btn.classList.add('active');
        const tab = btn.dataset.tab;
        document.getElementById('tab-tenants').style.display   = tab === 'tenants'   ? 'block' : 'none';
        document.getElementById('tab-approvals').style.display = tab === 'approvals' ? 'block' : 'none';
        document.getElementById('tab-roles').style.display     = tab === 'roles'     ? 'block' : 'none';
        if (tab === 'approvals') loadAllApprovals();
        if (tab === 'roles') loadMatrix();
      });
    });
  }

  // ── Tenants ─────────────────────────────────────────────────────────────
  async function loadTenants() {
    try {
      const tenants = await Api.Admin.getTenants();
      document.getElementById('tenants-loading').style.display = 'none';
      if (tenants.length === 0) {
        document.getElementById('tenants-empty').style.display = 'block';
        return;
      }
      document.getElementById('tenants-table').style.display = 'table';
      const tbody = document.getElementById('tenants-tbody');
      tbody.innerHTML = '';
      tenants.forEach(t => {
        const tr = document.createElement('tr');

        const tdName = document.createElement('td');
        const strong = document.createElement('strong');
        strong.textContent = t.name;
        tdName.appendChild(strong);
        tr.appendChild(tdName);

        const tdType = document.createElement('td');
        tdType.style.textTransform = 'capitalize';
        tdType.textContent = t.type;
        tr.appendChild(tdType);

        const tdDomain = document.createElement('td');
        tdDomain.style.cssText = 'font-size:.85rem;color:var(--gray-600);';
        tdDomain.textContent = t.ssoDomain || t.googleWorkspaceDomain || '—';
        tr.appendChild(tdDomain);

        const tdStatus = document.createElement('td');
        const statusSpan = document.createElement('span');
        statusSpan.className = `status status-${t.status === 'active' ? 'approved' : 'rejected'}`;
        statusSpan.textContent = t.status;
        tdStatus.appendChild(statusSpan);
        tr.appendChild(tdStatus);

        const tdDate = document.createElement('td');
        tdDate.style.fontSize = '.85rem';
        tdDate.textContent = t.contractStartDate ? new Date(t.contractStartDate).toLocaleDateString() : '—';
        tr.appendChild(tdDate);

        const tdActions = document.createElement('td');
        tdActions.style.whiteSpace = 'nowrap';
        const editBtn = document.createElement('button');
        editBtn.className = 'btn btn-secondary btn-sm';
        editBtn.textContent = 'Edit';
        editBtn.addEventListener('click', () => openTenantModal(t));
        tdActions.appendChild(editBtn);
        // The root tenant is protected server-side; don't offer Delete for it.
        if (t.id !== 'arkansas-serve-root') {
          const delBtn = document.createElement('button');
          delBtn.className = 'btn btn-danger btn-sm';
          delBtn.style.marginLeft = '.4rem';
          delBtn.textContent = 'Delete';
          delBtn.addEventListener('click', () => openDeleteTenant(t));
          tdActions.appendChild(delBtn);
        }
        tr.appendChild(tdActions);

        tbody.appendChild(tr);
      });
    } catch (err) {
      document.getElementById('tenants-loading').innerHTML =
        `<div class="alert alert-error">Could not load tenants.</div>`;
    }
  }

  // ── Create / edit tenant modal ──────────────────────────────────────────
  let editingTenantId = null;
  // The org being edited keeps its type: the field is create-only, so on save the type comes
  // from the stored record rather than the (disabled) input. Held here because the tenant
  // list itself is fetched into function-local scope and is not reachable from the handler.
  let editingTenantType = null;

  // Fields that can't change after creation (the settings API doesn't update
  // type/domains); shown but disabled in edit mode.
  const CREATE_ONLY_FIELDS = ['tenant-type', 'tenant-sso', 'tenant-google', 'tenant-contract'];
  function setCreateOnlyDisabled(disabled) {
    CREATE_ONLY_FIELDS.forEach(id => { document.getElementById(id).disabled = disabled; });
  }

  function openTenantModal(tenant) {
    editingTenantId = tenant ? tenant.id : null;
    editingTenantType = tenant ? tenant.type : null;
    const editing = !!tenant;
    document.querySelector('#tenant-modal .modal-title').textContent = editing ? 'Edit organization' : 'Add Tenant';
    document.getElementById('tenant-save').textContent = editing ? 'Save changes' : 'Add Tenant';
    document.getElementById('tenant-name').value     = tenant?.name || '';
    // Filled from the shared vocabulary, and matched case-insensitively: live data holds both
    // "organization" and "Organization", and a strict match would show an existing org's type
    // as blank — i.e. the form appearing to lose the answer.
    Taxonomy.fillSelect(document.getElementById('tenant-type'), Taxonomy.ORG_TYPES, tenant?.type || '');
    // Effective vocabulary (canonical + approved-new, #10②); "Other" opens a propose input, and
    // a still-pending stored value selects Other and pre-fills it.
    const scSel = document.getElementById('tenant-service-category');
    const scInput = document.getElementById('tenant-service-category-propose');
    Categories.fillSelect(scSel, tenant?.serviceCategory || '', 'No category').then(() => {
      Categories.wirePropose(scSel, scInput, {
        hintEl: document.getElementById('tenant-service-category-hint'),
        pendingValue: tenant?.serviceCategory,
      });
    });
    document.getElementById('tenant-faith-based').checked = !!tenant?.faithBased;
    syncTenantTaxonomyVisibility();
    document.getElementById('tenant-sso').value      = tenant?.ssoDomain || '';
    document.getElementById('tenant-google').value   = tenant?.googleWorkspaceDomain || '';
    document.getElementById('tenant-email').value    = tenant?.contactEmail || '';
    document.getElementById('tenant-contract').value = tenant?.contractStartDate ? tenant.contractStartDate.slice(0, 10) : '';
    document.getElementById('tenant-status').value   = tenant?.status || 'active';
    document.getElementById('tenant-status-group').style.display = editing ? 'block' : 'none';
    setCreateOnlyDisabled(editing);
    document.getElementById('tenant-error').style.display = 'none';
    document.getElementById('tenant-modal').classList.add('open');
  }

  // Service category and the faith flag only apply to a Community Organization; a school's
  // "service category" is a question with no good answer, and asking anyway is how "Other"
  // becomes the most popular value in a taxonomy. Mirrors the server, which nulls a category
  // on any non-organization regardless of what the form sends.
  function syncTenantTaxonomyVisibility() {
    const isOrg = Taxonomy.isOrganization(document.getElementById('tenant-type').value);
    document.getElementById('tenant-service-category-wrap').style.display = isOrg ? '' : 'none';
    document.getElementById('tenant-faith-wrap').style.display = isOrg ? '' : 'none';
  }
  document.getElementById('tenant-type').addEventListener('change', syncTenantTaxonomyVisibility);

  document.getElementById('btn-new-tenant').addEventListener('click', () => openTenantModal(null));
  document.getElementById('tenant-cancel').addEventListener('click', () =>
    document.getElementById('tenant-modal').classList.remove('open'));

  document.getElementById('tenant-save').addEventListener('click', async () => {
    const errEl = document.getElementById('tenant-error');
    errEl.style.display = 'none';
    const name = document.getElementById('tenant-name').value.trim();
    const type = document.getElementById('tenant-type').value;
    const email = document.getElementById('tenant-email').value.trim() || null;
    if (!name || (!editingTenantId && !type)) {
      errEl.textContent = 'Name and type are required.';
      errEl.style.display = 'block';
      return;
    }
    const btn = document.getElementById('tenant-save');
    const original = btn.textContent;
    btn.disabled = true; btn.textContent = 'Saving…';
    try {
      // Taxonomy fields are only meaningful for a Community Organization, so they are only
      // SENT for one. The server enforces this too — it nulls a category on any other type
      // regardless — but sending them anyway would mean an admin's answer silently vanishing,
      // which reads as the form being broken.
      const isOrg = Taxonomy.isOrganization(editingTenantId ? editingTenantType : type);
      const taxonomy = isOrg ? {
        serviceCategory: Categories.valueFrom(document.getElementById('tenant-service-category'), document.getElementById('tenant-service-category-propose')) || '',
        faithBased:      document.getElementById('tenant-faith-based').checked,
      } : {};

      if (editingTenantId) {
        // Settings API updates name/status/contactEmail and the taxonomy; type/domains stay
        // fixed after creation.
        await Api.Admin.updateTenant(editingTenantId, {
          name,
          contactEmail: email,
          status: document.getElementById('tenant-status').value,
          ...taxonomy,
        });
      } else {
        await Api.Admin.createTenant({
          name,
          type,
          ssoDomain:             document.getElementById('tenant-sso').value.trim() || null,
          googleWorkspaceDomain: document.getElementById('tenant-google').value.trim() || null,
          contactEmail:          email,
          contractStartDate:     document.getElementById('tenant-contract').value || null,
          status: 'active',
          ...taxonomy,
        });
      }
      document.getElementById('tenant-modal').classList.remove('open');
      loadTenants();
    } catch (err) {
      errEl.textContent = err.message || 'Save failed.';
      errEl.style.display = 'block';
    } finally {
      btn.disabled = false; btn.textContent = original;
    }
  });

  // ── Delete organization (type-to-confirm) ───────────────────────────────
  let deletingTenant = null;
  const delInput   = document.getElementById('delete-tenant-input');
  const delConfirm = document.getElementById('delete-tenant-confirm');

  function openDeleteTenant(tenant) {
    deletingTenant = tenant;
    document.getElementById('delete-tenant-name').textContent = tenant.name;
    document.getElementById('delete-tenant-prompt').textContent = `Type "${tenant.name}" to confirm`;
    delInput.value = '';
    delConfirm.disabled = true;
    document.getElementById('delete-tenant-error').style.display = 'none';
    document.getElementById('delete-tenant-modal').classList.add('open');
    delInput.focus();
  }

  // Enable the button only on an exact (trimmed) name match.
  delInput.addEventListener('input', () => {
    delConfirm.disabled = !deletingTenant || delInput.value.trim() !== deletingTenant.name.trim();
  });

  document.getElementById('delete-tenant-cancel').addEventListener('click', () =>
    document.getElementById('delete-tenant-modal').classList.remove('open'));

  delConfirm.addEventListener('click', async () => {
    if (!deletingTenant) return;
    const errEl = document.getElementById('delete-tenant-error');
    errEl.style.display = 'none';
    delConfirm.disabled = true; delConfirm.textContent = 'Deleting…';
    try {
      const res = await Api.Admin.deleteTenant(deletingTenant.id, deletingTenant.name);
      document.getElementById('delete-tenant-modal').classList.remove('open');
      alert(`Deleted "${deletingTenant.name}" — removed ${res.events} event(s) and ${res.members} member account(s).`);
      loadTenants();
    } catch (err) {
      errEl.textContent = err.message || 'Delete failed.';
      errEl.style.display = 'block';
    } finally {
      delConfirm.disabled = false; delConfirm.textContent = 'Delete permanently';
    }
  });

  // ── All Approvals (platform-wide) ────────────────────────────────────────
  async function loadAllApprovals() {
    document.getElementById('all-approvals-loading').style.display = 'block';
    document.getElementById('all-approvals-table').style.display   = 'none';
    document.getElementById('all-approvals-empty').style.display   = 'none';
    try {
      // Platform admin gets all approvals (no schoolId filter)
      const approvals = await Api.Approvals.list();
      document.getElementById('all-approvals-loading').style.display = 'none';
      if (approvals.length === 0) {
        document.getElementById('all-approvals-empty').style.display = 'block';
        return;
      }
      document.getElementById('all-approvals-table').style.display = 'table';
      const appTbody = document.getElementById('all-approvals-tbody');
      appTbody.innerHTML = '';
      approvals.forEach(a => {
        const tr = document.createElement('tr');

        const tdStudent = document.createElement('td');
        tdStudent.textContent = a.studentName;
        tr.appendChild(tdStudent);

        const tdSchool = document.createElement('td');
        tdSchool.style.cssText = 'font-size:.8rem;color:var(--gray-600);';
        tdSchool.textContent = a.schoolId;
        tr.appendChild(tdSchool);

        const tdEvent = document.createElement('td');
        tdEvent.textContent = a.eventTitle;
        tr.appendChild(tdEvent);

        const tdOrg = document.createElement('td');
        tdOrg.style.fontSize = '.85rem';
        tdOrg.textContent = a.organizationName;
        tr.appendChild(tdOrg);

        const tdDate = document.createElement('td');
        tdDate.textContent = new Date(a.serviceDate).toLocaleDateString();
        tr.appendChild(tdDate);

        const tdHours = document.createElement('td');
        const hoursStrong = document.createElement('strong');
        hoursStrong.textContent = `${a.hoursLogged}h`;
        tdHours.appendChild(hoursStrong);
        tr.appendChild(tdHours);

        appTbody.appendChild(tr);
      });
    } catch (err) {
      document.getElementById('all-approvals-loading').innerHTML =
        `<div class="alert alert-error">Could not load approvals.</div>`;
    }
  }

  // ── Role matrix ─────────────────────────────────────────────────────────
  // Server-side filtered (org + name/email search) and paginated. Each row is
  // one membership document; "Load more" follows the continuation token.
  const LEVELS = ['Student', 'EventAdmin', 'GroupAdmin', 'OrganizationAdmin', 'SuperAdmin'];
  const MATRIX_PAGE_SIZE = 50;
  let matrixItems = [];
  let matrixToken = null;
  let matrixLoading = false;

  async function loadMatrix() {
    clearMatrixError();
    // Populate the level + org selects once (assign form + filter dropdown).
    const levelSel = document.getElementById('assign-level');
    if (!levelSel.options.length) {
      LEVELS.forEach(l => { const o = document.createElement('option'); o.value = l; o.textContent = l; levelSel.appendChild(o); });
    }
    try {
      const tenants = await Api.Admin.getTenants();
      const orgSel = document.getElementById('assign-org');
      orgSel.innerHTML = '';
      tenants.forEach(t => { const o = document.createElement('option'); o.value = t.id; o.textContent = t.name; orgSel.appendChild(o); });

      const filterSel = document.getElementById('matrix-org');
      const current = filterSel.value; // preserve the active filter across reloads
      filterSel.innerHTML = '<option value="">All organizations</option>';
      tenants.forEach(t => { const o = document.createElement('option'); o.value = t.id; o.textContent = t.name; filterSel.appendChild(o); });
      filterSel.value = current;
    } catch (err) {
      console.error('[matrix] tenant load failed', err); // non-fatal
    }

    matrixItems = [];
    matrixToken = null;
    await fetchMatrixPage(true);
  }

  async function fetchMatrixPage(reset) {
    if (matrixLoading) return;
    matrixLoading = true;
    const loading = document.getElementById('matrix-loading');
    const moreBtn = document.getElementById('matrix-loadmore');
    if (reset) loading.style.display = 'block';
    moreBtn.disabled = true; moreBtn.textContent = 'Loading…';
    try {
      const res = await Api.Matrix.list({
        organizationId: document.getElementById('matrix-org').value || undefined,
        search: document.getElementById('matrix-search').value.trim() || undefined,
        continuationToken: reset ? undefined : matrixToken,
        pageSize: MATRIX_PAGE_SIZE,
      });
      matrixItems = reset ? (res.items || []) : matrixItems.concat(res.items || []);
      matrixToken = res.continuationToken || null;
      renderMatrix();
    } catch (err) {
      document.getElementById('matrix-table').style.display = 'none';
      const empty = document.getElementById('matrix-empty');
      empty.style.display = 'block';
      empty.textContent = 'Could not load the role matrix.';
      console.error('[matrix] load failed', err);
    } finally {
      loading.style.display = 'none';
      moreBtn.disabled = false; moreBtn.textContent = 'Load more';
      matrixLoading = false;
    }
  }

  function renderMatrix() {
    const table = document.getElementById('matrix-table');
    const empty = document.getElementById('matrix-empty');
    const tbody = document.getElementById('matrix-tbody');
    const moreWrap = document.getElementById('matrix-loadmore-wrap');
    tbody.innerHTML = '';

    matrixItems.forEach(item => {
      const tr = document.createElement('tr');

      const tdP = document.createElement('td');
      const strong = document.createElement('strong');
      strong.textContent = item.displayName || item.email;
      tdP.appendChild(strong);
      const sub = document.createElement('div');
      sub.style.cssText = 'font-size:.8rem;color:var(--gray-600);';
      sub.textContent = item.email + (item.externalId ? '' : ' · managed');
      tdP.appendChild(sub);
      tr.appendChild(tdP);

      appendCell(tr, item.organizationName);

      const tdL = document.createElement('td');
      const sel = document.createElement('select');
      sel.style.cssText = 'padding:.2rem .4rem;font-size:.85rem;';
      LEVELS.forEach(l => { const o = document.createElement('option'); o.value = l; o.textContent = l; if (l === item.adminLevel) o.selected = true; sel.appendChild(o); });
      sel.addEventListener('change', () => changeLevel(item, sel));
      tdL.appendChild(sel);
      tr.appendChild(tdL);

      appendCell(tr, (item.groupIds || []).join(', '));

      const tdA = document.createElement('td');
      const rm = document.createElement('button');
      rm.className = 'btn btn-danger btn-sm';
      rm.textContent = 'Remove';
      rm.addEventListener('click', () => removeMembership(item, rm));
      tdA.appendChild(rm);
      tr.appendChild(tdA);

      tbody.appendChild(tr);
    });

    if (matrixItems.length === 0) { table.style.display = 'none'; empty.style.display = 'block'; empty.textContent = 'No matching assignments.'; }
    else { table.style.display = 'table'; empty.style.display = 'none'; }
    moreWrap.style.display = matrixToken ? 'block' : 'none';
  }

  function appendCell(tr, text) {
    const td = document.createElement('td');
    td.textContent = text;
    tr.appendChild(td);
  }

  // Surface a matrix action failure inline (auto-hides). The API layer already
  // extracts the server's error message, so err.message is user-meaningful.
  let matrixErrorTimer = null;
  function showMatrixError(msg) {
    const el = document.getElementById('matrix-error');
    el.textContent = msg;
    el.style.display = 'block';
    clearTimeout(matrixErrorTimer);
    matrixErrorTimer = setTimeout(() => { el.style.display = 'none'; }, 6000);
  }
  function clearMatrixError() {
    const el = document.getElementById('matrix-error');
    el.style.display = 'none';
    el.textContent = '';
  }

  async function changeLevel(item, sel) {
    sel.disabled = true;
    clearMatrixError();
    try {
      await Api.Matrix.assign({ email: item.email, organizationId: item.organizationId, adminLevel: sel.value, externalId: item.externalId || null, groupIds: item.groupIds || [] });
      item.adminLevel = sel.value;
    } catch (err) {
      sel.value = item.adminLevel; // revert the select to the last saved level
      showMatrixError(`Couldn't change ${item.displayName || item.email}'s level: ${err.message || 'please try again.'}`);
    } finally {
      sel.disabled = false;
    }
  }

  async function removeMembership(item, btn) {
    btn.disabled = true;
    clearMatrixError();
    try {
      await Api.Matrix.unassign(item.userId, item.organizationId);
      await loadMatrix();
    } catch (err) {
      btn.disabled = false;
      showMatrixError(`Couldn't remove ${item.displayName || item.email} from ${item.organizationName}: ${err.message || 'please try again.'}`);
    }
  }

  document.getElementById('btn-assign').addEventListener('click', async () => {
    const status = document.getElementById('assign-status');
    const email = document.getElementById('assign-email').value.trim();
    const name  = document.getElementById('assign-name').value.trim();
    const org   = document.getElementById('assign-org').value;
    const level = document.getElementById('assign-level').value;
    const groups = document.getElementById('assign-groups').value.split(',').map(s => s.trim()).filter(Boolean);
    if (!email || !org || !level) { status.textContent = 'Email, organization, and level are required.'; return; }
    status.textContent = 'Assigning...';
    try {
      await Api.Matrix.assign({ email, displayName: name || undefined, organizationId: org, adminLevel: level, groupIds: groups });
      status.textContent = 'Assigned';
      document.getElementById('assign-email').value = '';
      document.getElementById('assign-name').value = '';
      document.getElementById('assign-groups').value = '';
      await loadMatrix();
    } catch (err) {
      status.textContent = err.message || 'Failed to assign';
    }
  });

  // Server-side filters: reload the first page on org change or (debounced) search.
  let matrixSearchTimer = null;
  document.getElementById('matrix-search').addEventListener('input', () => {
    clearTimeout(matrixSearchTimer);
    matrixSearchTimer = setTimeout(loadMatrix, 300);
  });
  document.getElementById('matrix-org').addEventListener('change', loadMatrix);
  document.getElementById('matrix-loadmore').addEventListener('click', () => fetchMatrixPage(false));
