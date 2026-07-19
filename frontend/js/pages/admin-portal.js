
  Auth.init();
  let profile = null;
  let activeApproval = null;
  let approvedTodayCount = 0;

  // Approvals is School/JDC work, and ui.js's PAGE_SCOPE narrows the switcher to those orgs.
  // An OrganizationAdmin of a Community Organization only therefore has NO org to view here.
  // Loading anyway would send a null orgId and render either an error or someone else's
  // queue, so the page states the reason instead. The scope bar says the same thing.
  function loadAll() {
    if (Scope.filteredEmpty || !Scope.activeOrgId) { showNoSchoolState(); return; }
    loadApprovals();
    loadReport();
    loadApprovalPolicy();
  }

  // The markup's own "All caught up!" text, kept so the no-school message below can be
  // swapped in and back out — otherwise switching to a school afterwards would show
  // "you don't administer one" over an empty-but-correct queue.
  const APPROVALS_EMPTY_DEFAULT = document.getElementById('approvals-empty')?.textContent?.trim() || '';

  function showNoSchoolState() {
    document.getElementById('approvals-loading').style.display = 'none';
    document.getElementById('approvals-table').style.display   = 'none';
    document.getElementById('approval-policy-card').style.display = 'none';
    const empty = document.getElementById('approvals-empty');
    empty.style.display = 'block';
    empty.textContent = 'Service hours are approved by a school or juvenile-service organization. '
      + 'You don\'t administer one, so there is nothing to approve here.';
  }

  Auth.requireAuth('OrganizationAdmin').then(async (p) => {
    profile = p;
    if (!profile) return;
    await UI.setupHeader('/admin-portal.html');
    // Reload when a SuperAdmin switches the active organization.
    Scope.onChange(loadAll);
    loadAll();
  });

  // Refresh button (was an inline onclick; moved here so CSP can drop script 'unsafe-inline').
  document.getElementById('btn-refresh-approvals').addEventListener('click', () => loadApprovals());

  // ── Service-hour approval policy (#12) ──────────────────────────────────────
  // Only Schools/JDCs review hours, so the editor is shown for them (and when the active org's
  // type is unknown — membership admins carry none — so a school's own admin still sees it),
  // and hidden for a Community Organization. A GET 403 (not an org admin here) also hides it.
  let policyState = null;      // { default, byOrg:{orgId:policy}, byCategory:{cat:policy} }
  let orgDirectory = [];       // [{ id, name }] for naming/picking org rules

  function policyAppliesToActiveOrg() {
    const type = Scope.activeOrg && Scope.activeOrg()?.raw?.type;
    return !type || !Taxonomy.isOrganization(type);
  }

  async function loadApprovalPolicy() {
    const card = document.getElementById('approval-policy-card');
    if (!card) return;
    if (!policyAppliesToActiveOrg()) { card.style.display = 'none'; return; }

    card.style.display = '';
    const loading = document.getElementById('policy-loading');
    const bodyEl  = document.getElementById('policy-body');
    loading.style.display = 'block'; bodyEl.style.display = 'none';
    document.getElementById('policy-saved').style.display = 'none';

    const schoolId = Scope.activeOrgId;
    try {
      const [policy, orgs] = await Promise.all([
        Api.AdminBackend.approvalPolicy(schoolId),
        orgDirectory.length ? Promise.resolve(orgDirectory) : Api.Orgs.browse().catch(() => []),
        Categories.load().catch(() => {}), // effective list for the by-category grid (#10②)
      ]);
      orgDirectory = orgs || [];
      policyState = {
        default: policy?.default || 'approvalRequired',
        byOrg: { ...(policy?.byOrg || {}) },
        byCategory: { ...(policy?.byCategory || {}) },
      };
      renderPolicyEditor();
      loading.style.display = 'none';
      bodyEl.style.display = 'block';
    } catch {
      // Not an admin of this org (403), or load failed — hide rather than show a broken editor.
      card.style.display = 'none';
    }
  }

  function renderPolicyEditor() {
    document.getElementById('policy-default').value = policyState.default;

    const catWrap = document.getElementById('policy-categories');
    catWrap.innerHTML = '';
    // Effective list (canonical + approved-new, #10②), not the hardcoded one.
    (Categories.list().length ? Categories.list() : Taxonomy.SERVICE_CATEGORIES).forEach(cat => {
      const label = document.createElement('div');
      label.textContent = cat;
      label.style.fontSize = '.88rem';
      const sel = document.createElement('select');
      sel.dataset.cat = cat;
      sel.style.cssText = 'padding:.35rem .5rem;border:1px solid var(--gray-400);border-radius:var(--radius);';
      [['', 'Use default'], ['preapproved', 'Preapproved'], ['approvalRequired', 'Approval required']]
        .forEach(([v, l]) => { const o = document.createElement('option'); o.value = v; o.textContent = l; sel.appendChild(o); });
      sel.value = policyState.byCategory[cat] || '';
      catWrap.appendChild(label);
      catWrap.appendChild(sel);
    });

    renderOrgRules();
    renderOrgPicker();
  }

  function orgName(id) { return (orgDirectory.find(o => o.id === id)?.name) || id; }

  function renderOrgRules() {
    const wrap = document.getElementById('policy-org-rules');
    wrap.innerHTML = '';
    const entries = Object.entries(policyState.byOrg);
    if (!entries.length) {
      const empty = document.createElement('div');
      empty.style.cssText = 'font-size:.82rem;color:var(--gray-600);';
      empty.textContent = 'No organization-specific rules yet.';
      wrap.appendChild(empty);
      return;
    }
    entries.forEach(([orgId, pol]) => {
      const row = document.createElement('div');
      row.style.cssText = 'display:flex;align-items:center;gap:.5rem;';
      const name = document.createElement('span');
      name.style.cssText = 'flex:1;font-size:.88rem;';
      name.textContent = orgName(orgId);
      const badge = document.createElement('span');
      badge.style.cssText = 'font-size:.78rem;padding:.15rem .5rem;border-radius:999px;background:var(--gray-100);color:var(--gray-700);white-space:nowrap;';
      badge.textContent = pol === 'preapproved' ? 'Preapproved' : 'Approval required';
      const rm = document.createElement('button');
      rm.className = 'btn btn-secondary btn-sm';
      rm.type = 'button';
      rm.textContent = 'Remove';
      rm.addEventListener('click', () => { delete policyState.byOrg[orgId]; renderOrgRules(); renderOrgPicker(); });
      row.append(name, badge, rm);
      wrap.appendChild(row);
    });
  }

  function renderOrgPicker() {
    const pick = document.getElementById('policy-org-pick');
    pick.innerHTML = '';
    const used = new Set(Object.keys(policyState.byOrg));
    const avail = orgDirectory.filter(o => !used.has(o.id) && o.id !== Scope.activeOrgId);
    if (!avail.length) {
      const o = document.createElement('option');
      o.value = ''; o.textContent = 'No more organizations';
      pick.appendChild(o);
      return;
    }
    const blank = document.createElement('option');
    blank.value = ''; blank.textContent = 'Choose an organization…';
    pick.appendChild(blank);
    avail.forEach(o => { const opt = document.createElement('option'); opt.value = o.id; opt.textContent = o.name || o.id; pick.appendChild(opt); });
  }

  document.getElementById('policy-org-add')?.addEventListener('click', () => {
    if (!policyState) return;
    const id = document.getElementById('policy-org-pick').value;
    const pol = document.getElementById('policy-org-policy').value;
    if (!id) return;
    policyState.byOrg[id] = pol;
    renderOrgRules();
    renderOrgPicker();
  });

  document.getElementById('policy-save')?.addEventListener('click', async () => {
    if (!policyState) return;
    const note = document.getElementById('policy-saved');
    const byCategory = {};
    document.querySelectorAll('#policy-categories select').forEach(sel => { if (sel.value) byCategory[sel.dataset.cat] = sel.value; });
    const payload = { default: document.getElementById('policy-default').value, byOrg: policyState.byOrg, byCategory };

    const btn = document.getElementById('policy-save');
    btn.disabled = true; btn.textContent = 'Saving…';
    try {
      const saved = await Api.AdminBackend.setApprovalPolicy(Scope.activeOrgId, payload);
      policyState = { default: saved.default, byOrg: { ...(saved.byOrg || {}) }, byCategory: { ...(saved.byCategory || {}) } };
      note.className = 'alert alert-success';
      note.textContent = 'Approval policy saved.';
      note.style.display = 'block';
      setTimeout(() => { note.style.display = 'none'; }, 4000);
    } catch (err) {
      note.className = 'alert alert-error';
      note.textContent = err.message || 'Could not save the policy.';
      note.style.display = 'block';
    } finally {
      btn.disabled = false; btn.textContent = 'Save policy';
    }
  });

  async function loadApprovals() {
    document.getElementById('approvals-loading').style.display = 'block';
    document.getElementById('approvals-table').style.display   = 'none';
    document.getElementById('approvals-empty').style.display   = 'none';

    try {
      const approvals = await Api.Approvals.list(Scope.activeOrgId);
      document.getElementById('approvals-loading').style.display = 'none';
      document.getElementById('stat-pending').textContent = approvals.length;
      document.getElementById('stat-approved-today').textContent = approvedTodayCount;

      if (approvals.length === 0) {
        const empty = document.getElementById('approvals-empty');
        empty.textContent = APPROVALS_EMPTY_DEFAULT;
        empty.style.display = 'block';
        return;
      }

      document.getElementById('approvals-table').style.display = 'table';
      const tbody = document.getElementById('approvals-tbody');
      tbody.innerHTML = '';
      approvals.forEach(a => {
        const tr = document.createElement('tr');

        const tdName = document.createElement('td');
        const strong1 = document.createElement('strong');
        strong1.textContent = a.studentName;
        tdName.appendChild(strong1);
        tr.appendChild(tdName);

        const tdEvent = document.createElement('td');
        tdEvent.textContent = a.eventTitle;
        tr.appendChild(tdEvent);

        const tdOrg = document.createElement('td');
        tdOrg.style.cssText = 'font-size:.85rem;color:var(--gray-600);';
        tdOrg.textContent = a.organizationName;
        tr.appendChild(tdOrg);

        const tdDate = document.createElement('td');
        tdDate.textContent = new Date(a.serviceDate).toLocaleDateString();
        tr.appendChild(tdDate);

        const tdSubmitted = document.createElement('td');
        tdSubmitted.style.cssText = 'font-size:.85rem;color:var(--gray-600);';
        tdSubmitted.textContent = a.submittedAt ? new Date(a.submittedAt).toLocaleDateString() : '—';
        tr.appendChild(tdSubmitted);

        const tdHours = document.createElement('td');
        const strong2 = document.createElement('strong');
        strong2.textContent = `${a.hoursLogged}h`;
        tdHours.appendChild(strong2);
        tr.appendChild(tdHours);

        const tdStatus = document.createElement('td');
        const badge = document.createElement('span');
        badge.className = 'status status-pending';
        badge.textContent = 'Pending';
        tdStatus.appendChild(badge);
        tr.appendChild(tdStatus);

        const tdBtn = document.createElement('td');
        tdBtn.style.whiteSpace = 'nowrap';
        const approveBtn = document.createElement('button');
        approveBtn.className = 'btn btn-primary btn-sm';
        approveBtn.textContent = 'Approve';
        approveBtn.addEventListener('click', () => openReview(a, 'Approved'));
        const rejectBtn = document.createElement('button');
        rejectBtn.className = 'btn btn-danger btn-sm';
        rejectBtn.style.marginLeft = '.4rem';
        rejectBtn.textContent = 'Reject';
        rejectBtn.addEventListener('click', () => openReview(a, 'Rejected'));
        tdBtn.appendChild(approveBtn);
        tdBtn.appendChild(rejectBtn);
        tr.appendChild(tdBtn);

        tbody.appendChild(tr);
      });
    } catch (err) {
      document.getElementById('approvals-loading').innerHTML =
        `<div class="alert alert-error">Could not load approvals. Please refresh.</div>`;
    }
  }

  let activeIntent = 'Approved';

  // Drive every intent-dependent piece of the modal from one place so the
  // Approve/Reject "switch" link stays consistent with the confirm button.
  function setIntent(intent) {
    activeIntent = intent;
    const a = activeApproval;
    const rejecting = intent === 'Rejected';
    document.getElementById('review-modal-title').textContent =
      rejecting ? 'Reject service hours' : 'Approve service hours';
    document.getElementById('review-consequence').textContent = rejecting
      ? `Rejecting returns these ${a.hoursLogged}h to ${a.organizationName}. ${a.studentName} is notified with your reason.`
      : `Approving credits ${a.studentName} with ${a.hoursLogged}h and notifies them.`;
    document.getElementById('review-note-label').textContent =
      rejecting ? 'Reason for rejection (required)' : 'Note (optional)';
    const confirm = document.getElementById('review-confirm');
    confirm.textContent = rejecting ? 'Reject hours' : 'Approve hours';
    confirm.className = rejecting ? 'btn btn-danger' : 'btn btn-primary';
    document.getElementById('review-switch').textContent =
      rejecting ? '← Approve instead' : 'Reject instead →';
  }

  function openReview(approval, intent) {
    activeApproval = approval;
    const details = document.getElementById('review-details');
    details.innerHTML = '';
    const fields = [
      ['Student', approval.studentName],
      ['Logged by', approval.organizationName],
      ['Event', approval.eventTitle],
      ['Service date', new Date(approval.serviceDate).toLocaleDateString()],
      ['Submitted', approval.submittedAt ? new Date(approval.submittedAt).toLocaleDateString() : '—'],
      ['Hours', `${approval.hoursLogged}h`]
    ];
    fields.forEach(([label, value]) => {
      const strong = document.createElement('strong');
      strong.textContent = `${label}: `;
      details.appendChild(strong);
      details.appendChild(document.createTextNode(value));
      details.appendChild(document.createElement('br'));
    });

    document.getElementById('review-note').value = '';
    document.getElementById('review-error').style.display = 'none';
    setIntent(intent);
    document.getElementById('review-modal').classList.add('open');
  }

  document.getElementById('review-cancel').addEventListener('click', () =>
    document.getElementById('review-modal').classList.remove('open'));
  document.getElementById('review-switch').addEventListener('click', () =>
    setIntent(activeIntent === 'Rejected' ? 'Approved' : 'Rejected'));

  function showBanner(text) {
    const b = document.getElementById('review-banner');
    b.textContent = text;
    b.style.display = 'block';
    setTimeout(() => { b.style.display = 'none'; }, 5000);
  }

  async function submitReview() {
    const status = activeIntent;
    const errEl = document.getElementById('review-error');
    const note  = document.getElementById('review-note').value.trim();
    if (status === 'Rejected' && !note) {
      errEl.textContent = 'A reason is required when rejecting hours.';
      errEl.style.display = 'block';
      return;
    }
    errEl.style.display = 'none';

    const confirmBtn = document.getElementById('review-confirm');
    const original = confirmBtn.textContent;
    confirmBtn.disabled = true;
    confirmBtn.textContent = status === 'Rejected' ? 'Rejecting…' : 'Approving…';

    try {
      const a = activeApproval;
      await Api.ServiceLogs.review(a.serviceLogId, a.studentId, status, note);
      if (status === 'Approved') approvedTodayCount++;
      document.getElementById('review-modal').classList.remove('open');
      showBanner(status === 'Approved'
        ? `✓ Approved — ${a.hoursLogged}h credited to ${a.studentName}, who was notified.`
        : `Rejected — ${a.studentName} was notified with your reason.`);
      loadApprovals();
    } catch (err) {
      errEl.textContent = err.message || 'Review failed. Please try again.';
      errEl.style.display = 'block';
    } finally {
      confirmBtn.disabled = false;
      confirmBtn.textContent = original;
    }
  }

  document.getElementById('review-confirm').addEventListener('click', submitReview);

  // ── Service-hour report ───────────────────────────────────────────────────
  let lastReport = null;

  async function loadReport() {
    const loading = document.getElementById('report-loading');
    const empty   = document.getElementById('report-empty');
    const content = document.getElementById('report-content');
    loading.style.display = 'block';
    empty.style.display   = 'none';
    content.style.display = 'none';

    try {
      const from = document.getElementById('report-from').value || null;
      const to   = document.getElementById('report-to').value   || null;
      const report = await Api.Reports.serviceHours({ from, to, schoolId: Scope.activeOrgId });
      lastReport = report;
      loading.style.display = 'none';
      renderLogDetail(report.logs || []);

      const students = report.students || [];
      if (students.length === 0) {
        empty.style.display = 'block';
        empty.textContent = 'No students found for your school yet.';
        return;
      }

      const tbody = document.getElementById('report-tbody');
      tbody.innerHTML = '';
      let totalApproved = 0, totalPending = 0;
      students.forEach(s => {
        totalApproved += s.approvedHours;
        totalPending  += s.pendingHours;

        const tr = document.createElement('tr');
        appendCell(tr, s.name, true);
        appendCell(tr, s.grade || '—');
        appendCell(tr, s.approvedHours.toFixed(1));
        appendCell(tr, s.pendingHours.toFixed(1));
        appendCell(tr, String(s.eventsAttended));
        tbody.appendChild(tr);
      });

      const range = (report.from || report.to)
        ? `${report.from ? new Date(report.from).toLocaleDateString() : '—'} → ${report.to ? new Date(report.to).toLocaleDateString() : '—'}`
        : 'All time';
      document.getElementById('report-summary-line').textContent =
        `${students.length} students · ${totalApproved.toFixed(1)} approved hrs · ${totalPending.toFixed(1)} pending · ${range}`;

      content.style.display = 'block';
    } catch (err) {
      loading.style.display = 'none';
      empty.style.display = 'block';
      empty.textContent = 'Could not load the report. Please try again.';
    }
  }

  // Per-log detail with a Void action (OrganizationAdmin+). Voiding hard-deletes an
  // already-approved log — the only way to reverse an erroneous/duplicate entry, since
  // Approvals can only reject items still pending. Rebuilt on each report load.
  function renderLogDetail(logs) {
    let box = document.getElementById('report-log-detail');
    if (!box) {
      box = document.createElement('div');
      box.id = 'report-log-detail';
      box.style.cssText = 'margin-top:1.5rem;';
      document.getElementById('report-content').appendChild(box);
    }
    box.innerHTML = '';
    if (!logs.length) return;

    const canVoid = Scope.isSuperAdmin
      || Auth.adminRank(Scope.activeOrg()?.adminLevel) >= Auth.adminRank('OrganizationAdmin');

    const heading = document.createElement('div');
    heading.className = 'card-title';
    heading.textContent = 'Approved logs';
    box.appendChild(heading);

    const table = document.createElement('table');
    table.style.cssText = 'width:100%;font-size:.9rem;';
    const head = document.createElement('thead');
    head.innerHTML = `<tr><th>Student</th><th>Event</th><th>Date</th><th>Hours</th>${canVoid ? '<th></th>' : ''}</tr>`;
    table.appendChild(head);
    const tbody = document.createElement('tbody');
    logs.forEach(l => {
      const tr = document.createElement('tr');
      appendCell(tr, l.studentName, true);
      appendCell(tr, l.eventTitle || '—');
      appendCell(tr, new Date(l.serviceDate).toLocaleDateString());
      appendCell(tr, Number(l.hoursLogged).toFixed(1));
      if (canVoid) {
        const td = document.createElement('td');
        const btn = document.createElement('button');
        btn.className = 'btn btn-danger btn-sm';
        btn.textContent = 'Void';
        btn.addEventListener('click', () => voidLog(l, btn));
        td.appendChild(btn);
        tr.appendChild(td);
      }
      tbody.appendChild(tr);
    });
    table.appendChild(tbody);
    box.appendChild(table);
  }

  async function voidLog(l, btn) {
    if (!l.id || !l.studentId) { showBanner('This log cannot be voided (missing id).'); return; }
    if (!window.confirm(`Void ${Number(l.hoursLogged).toFixed(1)}h for ${l.studentName} (${l.eventTitle})? This permanently removes the approved hours.`)) return;
    btn.disabled = true; btn.textContent = 'Voiding…';
    try {
      await Api.ServiceLogs.voidLog(l.id, l.studentId);
      showBanner(`Voided ${Number(l.hoursLogged).toFixed(1)}h for ${l.studentName}.`);
      await loadReport();
    } catch (err) {
      btn.disabled = false; btn.textContent = 'Void';
      window.alert(err.message || 'Could not void this log. You may not have permission.');
    }
  }

  function appendCell(tr, text, bold) {
    const td = document.createElement('td');
    if (bold) {
      const strong = document.createElement('strong');
      strong.textContent = text;
      td.appendChild(strong);
    } else {
      td.textContent = text;
    }
    tr.appendChild(td);
  }

  // ── CSV export ────────────────────────────────────────────────────────────
  function toCsv(rows) {
    return rows.map(row => row.map(cell => {
      const v = cell == null ? '' : String(cell);
      return /[",\n\r]/.test(v) ? `"${v.replace(/"/g, '""')}"` : v;
    }).join(',')).join('\r\n');
  }

  function downloadCsv(filename, csv) {
    const blob = new Blob([csv], { type: 'text/csv;charset=utf-8;' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
  }

  function stamp() {
    return new Date().toISOString().slice(0, 10);
  }

  document.getElementById('report-run').addEventListener('click', loadReport);
  document.getElementById('report-clear').addEventListener('click', () => {
    document.getElementById('report-from').value = '';
    document.getElementById('report-to').value   = '';
    loadReport();
  });

  document.getElementById('export-summary').addEventListener('click', () => {
    if (!lastReport) return;
    const rows = [['Student', 'Grade', 'Email', 'Approved Hours', 'Pending Hours', 'Events Attended']];
    (lastReport.students || []).forEach(s =>
      rows.push([s.name, s.grade || '', s.email || '', s.approvedHours, s.pendingHours, s.eventsAttended]));
    downloadCsv(`service-hours-summary-${stamp()}.csv`, toCsv(rows));
  });

  document.getElementById('export-detail').addEventListener('click', () => {
    if (!lastReport) return;
    const rows = [['Student', 'Event', 'Organization', 'Service Date', 'Hours']];
    (lastReport.logs || []).forEach(l =>
      rows.push([l.studentName, l.eventTitle, l.organizationName, new Date(l.serviceDate).toLocaleDateString(), l.hoursLogged]));
    downloadCsv(`service-hours-detail-${stamp()}.csv`, toCsv(rows));
  });

  // Print / Save as PDF: fill the print-only header (org, range, generated date) so the saved
  // document is self-describing, then let the @media print rules render a clean roster.
  document.getElementById('report-print').addEventListener('click', () => {
    if (!lastReport) return;
    const org = Scope.activeOrg && Scope.activeOrg();
    document.getElementById('print-org-name').textContent = (org && org.name) || 'Organization';
    const from = lastReport.from ? new Date(lastReport.from).toLocaleDateString() : null;
    const to   = lastReport.to   ? new Date(lastReport.to).toLocaleDateString()   : null;
    document.getElementById('print-range').textContent =
      'Date range: ' + ((from || to) ? `${from || '—'} → ${to || '—'}` : 'All dates');
    document.getElementById('print-generated').textContent = 'Generated: ' + new Date().toLocaleString();
    window.print();
  });
