
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

    // ── First-login intake (#22–#24) ─────────────────────────────────────────
    const intakeType = document.getElementById('intake-type');
    const intakeDob  = document.getElementById('intake-dob');

    // Whole-year age from a yyyy-MM-dd string, or null if missing/invalid/future.
    // Mirrors the server's IntakeValidation.TryComputeAge so the wizard shows exactly
    // the fields the backend will require.
    function ageFromDob(dob) {
      if (!dob) return null;
      const d = new Date(dob + 'T00:00:00');
      if (isNaN(d)) return null;
      const now = new Date();
      let age = now.getFullYear() - d.getFullYear();
      const m = now.getMonth() - d.getMonth();
      if (m < 0 || (m === 0 && now.getDate() < d.getDate())) age--;
      return age < 0 ? null : age;
    }
    const isMinorDob = (dob) => { const a = ageFromDob(dob); return a !== null && a < 18; };

    function syncIntakeSections() {
      const t = intakeType.value;
      document.getElementById('intake-student').style.display = t === 'Student' ? 'block' : 'none';
      document.getElementById('intake-adult').style.display   = t === 'AdultVolunteer' ? 'block' : 'none';
      // Guardian consent is gated on age (DOB), independent of the chosen type.
      document.getElementById('intake-guardian').style.display = isMinorDob(intakeDob.value) ? 'block' : 'none';
    }
    intakeType.addEventListener('change', syncIntakeSections);
    intakeDob.addEventListener('input', syncIntakeSections);

    function openIntake(user) {
      document.getElementById('intake-first').value = user.firstName || '';
      document.getElementById('intake-last').value  = user.lastName || '';
      intakeDob.max = new Date().toISOString().slice(0, 10); // no future birth dates
      intakeDob.value = user.dateOfBirth ? String(user.dateOfBirth).slice(0, 10) : '';
      intakeType.value = user.personType || '';
      document.getElementById('intake-grade').value = user.grade || '';
      syncIntakeSections();
      document.getElementById('intake-error').style.display = 'none';
      document.getElementById('intake-modal').classList.add('open');
    }
    document.getElementById('intake-later').addEventListener('click', () =>
      document.getElementById('intake-modal').classList.remove('open'));

    document.getElementById('intake-save').addEventListener('click', async () => {
      const btn = document.getElementById('intake-save');
      const err = document.getElementById('intake-error');
      const val = (id) => document.getElementById(id).value.trim();
      const first = val('intake-first'), last = val('intake-last'), personType = intakeType.value;
      const dob = intakeDob.value;
      const minor = isMinorDob(dob);

      const missing = [];
      if (!first) missing.push('first name');
      if (!last) missing.push('last name');
      if (ageFromDob(dob) === null) missing.push('date of birth');
      if (!personType) missing.push('type');
      if (minor) {
        if (!val('intake-guardian-name')) missing.push('guardian name');
        if (!val('intake-guardian-email') && !val('intake-guardian-phone')) missing.push('guardian email or phone');
        if (!document.getElementById('intake-consent').checked) missing.push('guardian consent');
      }
      if (personType === 'Student') {
        if (!val('intake-grade')) missing.push('grade');
      } else if (personType === 'AdultVolunteer') {
        if (!val('intake-emergency-name')) missing.push('emergency contact name');
        if (!val('intake-emergency-phone')) missing.push('emergency contact phone');
      }
      if (missing.length) {
        err.textContent = `Please provide: ${missing.join(', ')}.`;
        err.style.display = 'block';
        return;
      }

      btn.disabled = true; btn.textContent = 'Saving…'; err.style.display = 'none';
      try {
        const payload = { firstName: first, lastName: last, personType, dateOfBirth: dob };
        if (minor) {
          Object.assign(payload, {
            guardianName: val('intake-guardian-name'),
            guardianEmail: val('intake-guardian-email') || null,
            guardianPhone: val('intake-guardian-phone') || null,
            guardianConsent: document.getElementById('intake-consent').checked,
          });
        }
        if (personType === 'Student') {
          payload.grade = val('intake-grade');
        } else {
          Object.assign(payload, {
            affiliation: val('intake-affiliation') || null,
            emergencyContactName: val('intake-emergency-name'),
            emergencyContactPhone: val('intake-emergency-phone'),
          });
        }
        const updated = await Api.Users.updateMe(payload);
        profileUser = updated;
        Auth.setResolvedLevelFromUser(updated);
        renderProfile(updated, profileMemberships);
        document.getElementById('greeting').textContent = `Welcome, ${(updated.firstName || updated.displayName || 'back')}`;
        document.getElementById('intake-modal').classList.remove('open');
      } catch (e) {
        err.textContent = e.message || 'Could not save your profile.';
        err.style.display = 'block';
      } finally {
        btn.disabled = false; btn.textContent = 'Save & continue';
      }
    });

    async function loadDashboard() {
      try {
        const currentUser = await Api.Users.getMe();
        Auth.setResolvedLevelFromUser(currentUser);
        // Re-set the greeting from the authoritative profile name; the initial paint
        // used the token claim, which can be stale/"unknown".
        const greetName = (currentUser.displayName || currentUser.firstName || profile.name || '').trim().split(' ')[0] || 'back';
        document.getElementById('greeting').textContent = `Welcome, ${greetName}`;
        const adminLevel = toAdminLevel(currentUser);

        await UI.setupHeader('/dashboard.html', currentUser);

        const memberships = await Api.Memberships.list().catch(() => []);
        profileUser = currentUser;
        profileMemberships = memberships;
        renderProfile(currentUser, memberships);

        // First-login: prompt to complete intake when the profile isn't complete.
        if (currentUser.profileComplete === false) openIntake(currentUser);

        // First-login onboarding: a self-registered student not yet in any organization is
        // guided to join one. Skip the (necessarily empty) stats + log history.
        if (adminLevel === 'Student' && (!memberships || memberships.length === 0)) {
          document.getElementById('onboarding-card').style.display = 'block';
          document.getElementById('stats-row').style.display = 'none';
          document.getElementById('log-card').style.display = 'none';
          return;
        }

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
        document.getElementById('stat-total').textContent   = (totalApprovedHours ?? 0).toFixed(1);
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
          tdEvent.textContent = log.eventTitle || '—';
          tr.appendChild(tdEvent);

          const tdOrg = document.createElement('td');
          tdOrg.textContent = log.organizationName || '—';
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
