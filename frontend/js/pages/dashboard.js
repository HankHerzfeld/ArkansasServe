
  Auth.init();
  Auth.requireAuth().then((profile) => {
    if (!profile) return;
    document.getElementById('greeting').textContent = `Welcome, ${profile.name.split(' ')[0]}`;

    const rank = {
      Student: 0,
      EventAdmin: 1,
      GroupAdmin: 2,
      OrganizationAdmin: 3,
      SuperAdmin: 4,
    };

    function toAdminLevel(user) {
      return user?.adminLevel || profile.adminLevel || 'Student';
    }

    // ── Profile card ─────────────────────────────────────────────────────────
    let profileUser = null;
    let profileMemberships = [];

    const LEVEL_LABEL = {
      Student: 'Volunteer', EventAdmin: 'Event Admin', GroupAdmin: 'Group Admin',
      OrganizationAdmin: 'Org Admin', SuperAdmin: 'Super Admin',
    };
    const prettyLevel = (lvl) => LEVEL_LABEL[lvl] || lvl || 'Volunteer';

    function renderProfile(user, memberships) {
      const name = user.displayName || profile.name || user.email || '';
      document.getElementById('profile-card').style.display = 'block';
      document.getElementById('profile-name').textContent = name;
      document.getElementById('profile-email').textContent = user.email || '';
      document.getElementById('profile-avatar').textContent = (name || '?').trim().charAt(0).toUpperCase();

      const levelWrap = document.getElementById('profile-level');
      levelWrap.innerHTML = '';
      const badge = document.createElement('span');
      badge.className = 'event-badge';
      badge.textContent = prettyLevel(user.adminLevel);
      levelWrap.appendChild(badge);

      // Progressive: only render fields that have a value.
      const details = document.getElementById('profile-details');
      details.innerHTML = '';
      const rows = [];
      if (user.phone) rows.push(['Phone', user.phone]);
      if (user.grade) rows.push(['Grade', user.grade]);
      rows.forEach(([label, val]) => {
        const wrap = document.createElement('div');
        const l = document.createElement('div');
        l.style.cssText = 'color:var(--gray-600);font-size:.75rem;';
        l.textContent = label;
        const v = document.createElement('div');
        v.textContent = val;
        wrap.appendChild(l); wrap.appendChild(v);
        details.appendChild(wrap);
      });

      const orgsWrap = document.getElementById('profile-orgs');
      orgsWrap.innerHTML = '';
      if (memberships && memberships.length) {
        const h = document.createElement('div');
        h.style.cssText = 'color:var(--gray-600);font-size:.75rem;margin-bottom:.4rem;';
        h.textContent = memberships.length > 1 ? 'Organizations' : 'Organization';
        orgsWrap.appendChild(h);
        const list = document.createElement('div');
        list.style.cssText = 'display:flex;gap:.5rem;flex-wrap:wrap;';
        memberships.forEach(m => {
          const chip = document.createElement('span');
          chip.className = 'event-badge';
          chip.textContent = `${m.organizationName || m.organizationId} · ${prettyLevel(m.adminLevel)}`;
          list.appendChild(chip);
        });
        orgsWrap.appendChild(list);
      }

      // Editable if you're an org admin, or your org allows self-edit (backend
      // enforces the real per-org rule; this is a UI hint).
      const canEdit = rank[user.adminLevel] >= rank.OrganizationAdmin
        || (memberships && memberships.length ? memberships.some(m => m.allowProfileSelfEdit !== false) : true);
      document.getElementById('btn-edit-profile').style.display = canEdit ? 'inline-block' : 'none';
    }

    document.getElementById('btn-edit-profile').addEventListener('click', () => {
      document.getElementById('edit-name').value  = profileUser?.displayName || '';
      document.getElementById('edit-phone').value = profileUser?.phone || '';
      document.getElementById('edit-grade').value = profileUser?.grade || '';
      document.getElementById('profile-modal-error').style.display = 'none';
      document.getElementById('profile-modal').classList.add('open');
    });
    document.getElementById('profile-cancel').addEventListener('click', () =>
      document.getElementById('profile-modal').classList.remove('open'));
    document.getElementById('profile-save').addEventListener('click', async () => {
      const btn = document.getElementById('profile-save');
      const err = document.getElementById('profile-modal-error');
      const name = document.getElementById('edit-name').value.trim();
      if (!name) { err.textContent = 'Name is required.'; err.style.display = 'block'; return; }
      btn.disabled = true; btn.textContent = 'Saving…'; err.style.display = 'none';
      try {
        const updated = await Api.Users.updateMe({
          displayName: name,
          phone: document.getElementById('edit-phone').value.trim() || null,
          grade: document.getElementById('edit-grade').value.trim() || null,
        });
        profileUser = updated;
        Auth.setResolvedLevelFromUser(updated);
        renderProfile(updated, profileMemberships);
        document.getElementById('greeting').textContent = `Welcome, ${(updated.displayName || '').split(' ')[0] || 'back'}`;
        document.getElementById('profile-modal').classList.remove('open');
      } catch (e) {
        err.textContent = e.message || 'Could not save your profile.';
        err.style.display = 'block';
      } finally {
        btn.disabled = false; btn.textContent = 'Save';
      }
    });

    async function loadDashboard() {
      try {
        const currentUser = await Api.Users.getMe();
        Auth.setResolvedLevelFromUser(currentUser);
        const adminLevel = toAdminLevel(currentUser);

        await UI.setupHeader('/dashboard.html', currentUser);

        const memberships = await Api.Memberships.list().catch(() => []);
        profileUser = currentUser;
        profileMemberships = memberships;
        renderProfile(currentUser, memberships);

        if (adminLevel !== 'Student') {
          document.getElementById('stat-total').textContent = '—';
          document.getElementById('stat-pending').textContent = '—';
          document.getElementById('stat-events').textContent = '—';
          document.getElementById('log-loading').style.display = 'none';
          document.getElementById('log-empty').style.display = 'block';
          document.getElementById('log-empty').textContent =
            adminLevel === 'SuperAdmin'
              ? 'SuperAdmin tools are available above. Use Platform Admin and Admin Backend to manage executive features.'
              : 'Admin users can use the Admin Backend for scoped management tasks.';
          return;
        }

        const { logs, totalApprovedHours } = await Api.ServiceLogs.myLogs();

        // Stats
        document.getElementById('stat-total').textContent   = totalApprovedHours.toFixed(1);
        document.getElementById('stat-pending').textContent = logs.filter(l => l.status === 'Pending').reduce((s, l) => s + l.hoursLogged, 0).toFixed(1);
        document.getElementById('stat-events').textContent  = logs.filter(l => l.status === 'Approved').length;

        // Table
        document.getElementById('log-loading').style.display = 'none';
        if (logs.length === 0) {
          document.getElementById('log-empty').style.display = 'block';
          return;
        }

        document.getElementById('log-table').style.display = 'table';
        const tbody = document.getElementById('log-tbody');
        tbody.innerHTML = '';
        logs.forEach(log => {
          const tr = document.createElement('tr');

          const tdDate = document.createElement('td');
          tdDate.textContent = new Date(log.serviceDate).toLocaleDateString();
          tr.appendChild(tdDate);

          const tdEvent = document.createElement('td');
          tdEvent.textContent = log.eventTitle;
          tr.appendChild(tdEvent);

          const tdOrg = document.createElement('td');
          tdOrg.textContent = log.organizationName;
          tr.appendChild(tdOrg);

          const tdHours = document.createElement('td');
          tdHours.textContent = String(log.hoursLogged);
          tr.appendChild(tdHours);

          const tdStatus = document.createElement('td');
          const statusSpan = document.createElement('span');
          statusSpan.className = `status status-${log.status.toLowerCase()}`;
          statusSpan.textContent = log.status;
          tdStatus.appendChild(statusSpan);
          tr.appendChild(tdStatus);

          const tdNote = document.createElement('td');
          tdNote.style.cssText = 'font-size:.8rem;color:var(--gray-600);';
          tdNote.textContent = log.reviewNote || '';
          tr.appendChild(tdNote);

          tbody.appendChild(tr);
        });
      } catch (err) {
        document.getElementById('log-loading').innerHTML =
          `<div class="alert alert-error">Failed to load your service history. Please refresh.</div>`;
      }
    }

    loadDashboard();
  });
