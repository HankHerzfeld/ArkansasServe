
  Auth.init();
  let profile = null;
  let activeApproval = null;
  let approvedTodayCount = 0;

  Auth.requireAuth('OrganizationAdmin').then(async (p) => {
    profile = p;
    if (!profile) return;
    await UI.setupHeader('/admin-portal.html');
    // Reload when a SuperAdmin switches the active organization.
    Scope.onChange(() => { loadApprovals(); loadReport(); });
    loadApprovals();
    loadReport();
  });

  // Refresh button (was an inline onclick; moved here so CSP can drop script 'unsafe-inline').
  document.getElementById('btn-refresh-approvals').addEventListener('click', () => loadApprovals());

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
        document.getElementById('approvals-empty').style.display = 'block';
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
