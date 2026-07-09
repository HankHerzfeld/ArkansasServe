
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

        const tdHours = document.createElement('td');
        const strong2 = document.createElement('strong');
        strong2.textContent = `${a.hoursLogged}h`;
        tdHours.appendChild(strong2);
        tr.appendChild(tdHours);

        const tdBtn = document.createElement('td');
        const btn = document.createElement('button');
        btn.className = 'btn btn-primary btn-sm';
        btn.textContent = 'Review';
        btn.addEventListener('click', () => openReview(a));
        tdBtn.appendChild(btn);
        tr.appendChild(tdBtn);

        tbody.appendChild(tr);
      });
    } catch (err) {
      document.getElementById('approvals-loading').innerHTML =
        `<div class="alert alert-error">Could not load approvals. Please refresh.</div>`;
    }
  }

  function openReview(approval) {
    activeApproval = approval;
    document.getElementById('review-modal-title').textContent = `Review: ${approval.eventTitle}`;
    const details = document.getElementById('review-details');
    details.innerHTML = '';

    const fields = [
      ['Student', approval.studentName],
      ['Organization', approval.organizationName],
      ['Event', approval.eventTitle],
      ['Date', new Date(approval.serviceDate).toLocaleDateString()],
      ['Hours submitted', String(approval.hoursLogged)]
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
    document.getElementById('review-modal').classList.add('open');
  }

  document.getElementById('review-cancel').addEventListener('click', () =>
    document.getElementById('review-modal').classList.remove('open'));

  async function submitReview(status) {
    const errEl = document.getElementById('review-error');
    const note  = document.getElementById('review-note').value.trim();
    if (status === 'Rejected' && !note) {
      errEl.textContent = 'A note is required when rejecting hours.';
      errEl.style.display = 'block';
      return;
    }
    errEl.style.display = 'none';

    const approveBtn = document.getElementById('review-approve');
    const rejectBtn  = document.getElementById('review-reject');
    approveBtn.disabled = rejectBtn.disabled = true;

    try {
      await Api.ServiceLogs.review(activeApproval.serviceLogId, activeApproval.studentId, status, note);
      if (status === 'Approved') approvedTodayCount++;
      document.getElementById('review-modal').classList.remove('open');
      loadApprovals();
    } catch (err) {
      errEl.textContent = err.message || 'Review failed. Please try again.';
      errEl.style.display = 'block';
    } finally {
      approveBtn.disabled = rejectBtn.disabled = false;
    }
  }

  document.getElementById('review-approve').addEventListener('click', () => submitReview('Approved'));
  document.getElementById('review-reject').addEventListener('click',  () => submitReview('Rejected'));

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
