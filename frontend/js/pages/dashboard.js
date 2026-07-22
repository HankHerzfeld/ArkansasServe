
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

    // Must match PolicyVersions.Current on the server and the version rendered in
    // terms.html / privacy.html. Bump all four together when the documents change —
    // notably when counsel signs off and they stop being drafts.
    //
    // Drift here fails safe rather than silently: the server validates the version it is
    // sent and rejects anything that isn't current, so a stale cached copy of this file
    // cannot record consent to wording nobody was shown — it gets a "reload the page" 400.
    const POLICY_VERSION = '2026-07-14-draft';

    // Acceptance is recorded as a version, not a flag, so re-issuing the documents
    // re-prompts everyone who only accepted the older text.
    const needsPolicyAcceptance = (user) => user?.acceptedPolicyVersion !== POLICY_VERSION;

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
      // Grade is a school-grade level — meaningful only for a Student, and only worth surfacing to
      // a plain volunteer. An org-level admin (e.g. the SuperAdmin owner, whose account still
      // carried a stray "Grade 17" from test setup) never shows it, whatever their personType.
      if (user.grade && user.personType === 'Student'
          && Auth.adminRank(user.adminLevel) < Auth.adminRank('OrganizationAdmin')) {
        rows.push(['Grade', user.grade]);
      }
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
        // Deliberately empty: the per-organization cards below now carry the org list with
        // real content (your role, your hours there, who oversees you, admin figures). The
        // chips that used to sit here said the same names twice on one screen.
      } else {
        // No assigned or joined organization. Say so plainly and point somewhere useful —
        // this state used to render nothing at all.
        const empty = document.createElement('div');
        empty.style.cssText = 'color:var(--gray-600);font-size:.8rem;';
        empty.textContent = 'No assigned or joined organization. ';
        const link = document.createElement('a');
        link.href = '/organizations.html';
        link.textContent = 'Browse organizations';
        link.style.cssText = 'color:var(--green);font-weight:600;';
        empty.appendChild(link);
        orgsWrap.appendChild(empty);
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
      // Never pre-tick consent: it must be an action the person takes each time the
      // documents change. Only hide the block once they've accepted THIS version.
      const policyRow = document.getElementById('intake-policy').closest('div');
      const accepted = needsPolicyAcceptance(user) === false;
      policyRow.style.display = accepted ? 'none' : 'block';
      document.getElementById('intake-policy').checked = false;
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
      // Required whenever it's on screen — i.e. until they've accepted this version.
      const policyPending = needsPolicyAcceptance(profileUser);
      if (policyPending && !document.getElementById('intake-policy').checked) {
        missing.push('agreement to the Terms & Privacy Policy');
      }
      if (missing.length) {
        err.textContent = `Please provide: ${missing.join(', ')}.`;
        err.style.display = 'block';
        return;
      }

      btn.disabled = true; btn.textContent = 'Saving…'; err.style.display = 'none';
      try {
        const payload = { firstName: first, lastName: last, personType, dateOfBirth: dob };
        // Only sent when they actually ticked it now. The server stamps the timestamp and
        // rejects any version that isn't the one in force.
        if (policyPending) payload.acceptedPolicyVersion = POLICY_VERSION;
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

    // ── Per-organization cards ────────────────────────────────────────────────
    // One card per membership, whose CONTENT depends on the person's role in THAT org.
    // A person is routinely an admin in one org and a plain volunteer in another, so a
    // single global "admin view vs volunteer view" would be wrong for one of them.

    function el(tag, props = {}, ...kids) {
      const n = document.createElement(tag);
      Object.entries(props).forEach(([k, v]) => {
        if (v == null) return;
        if (k === 'class') n.className = v;
        else if (k === 'text') n.textContent = v;   // textContent only — never innerHTML with data
        else if (k === 'style') n.style.cssText = v;
        else n.setAttribute(k, v);
      });
      kids.filter(Boolean).forEach(c => n.appendChild(c));
      return n;
    }

    // A labelled number that links where the admin would act on it.
    function statTile(number, label, href) {
      const box = el(href ? 'a' : 'div', {
        href,
        style: 'flex:1;min-width:7.5rem;padding:.6rem .75rem;border:1px solid var(--gray-200);'
             + 'border-radius:var(--radius);text-decoration:none;color:inherit;display:block;',
      });
      box.appendChild(el('div', { text: String(number), style: 'font-size:1.35rem;font-weight:700;color:var(--green);' }));
      box.appendChild(el('div', { text: label, style: 'font-size:.75rem;color:var(--gray-600);' }));
      return box;
    }

    // Everything the admin half of a card needs, fetched per org. Each call is guarded
    // by the role it actually requires and degrades to null rather than failing the card:
    // a 403 from one stat must not blank the others.
    async function loadAdminStats(orgId, myRank) {
      const q = (cond, fn) => (cond ? fn().catch(() => null) : Promise.resolve(null));
      const [approvals, assigned, events, roster] = await Promise.all([
        q(myRank >= Auth.adminRank('OrganizationAdmin'), () => Api.Approvals.list(orgId)),
        q(myRank >= Auth.adminRank('EventAdmin'),        () => Api.Assignments.mine(orgId)),
        q(myRank >= Auth.adminRank('EventAdmin'),        () => Api.Events.listOrgEvents(orgId)),
        // /manage/volunteers is GroupAdmin+ — asking as an EventAdmin would only ever 403.
        q(myRank >= Auth.adminRank('GroupAdmin'),        () => Api.Volunteers.list({ organizationId: orgId })),
      ]);
      return { approvals, assigned, events, roster };
    }

    async function renderOrgCards(memberships, logs) {
      const wrap = document.getElementById('org-cards');
      const section = document.getElementById('org-section');
      if (!wrap || !section || !memberships || !memberships.length) return;

      section.style.display = 'block';
      wrap.innerHTML = '';

      // Who oversees me, keyed by org. Volunteer-side mirror of #13 (Api.Assignments.mine).
      const overseerRows = await Api.Assignments.overseers().catch(() => []);
      const overseersByOrg = Object.fromEntries((overseerRows || []).map(r => [r.organizationId, r.admins || []]));

      // Collected so the overview's "Awaiting Your Approval" can total the SAME figures the
      // cards show, rather than re-fetching them and risking a different number on one screen.
      const adminStatPromises = [];

      for (const m of memberships) {
        const orgId = m.organizationId;
        const myRank = Auth.adminRank(m.adminLevel);
        const isAdminHere = myRank >= Auth.adminRank('EventAdmin');

        const card = el('div', { class: 'card', style: 'margin-bottom:1rem;' });

        // Header: org name, your role there, and the org's kind.
        const head = el('div', { style: 'display:flex;align-items:center;gap:.6rem;flex-wrap:wrap;margin-bottom:.75rem;' });
        head.appendChild(el('a', {
          href: `/organization.html?id=${encodeURIComponent(orgId)}`,
          text: m.organizationName,
          style: 'font-weight:600;font-size:1.05rem;color:var(--green);text-decoration:none;',
        }));
        if (m.adminLevel && m.adminLevel !== 'Student') {
          head.appendChild(el('span', { class: 'status status-approved', text: prettyLevel(m.adminLevel) }));
        }
        if (m.type) {
          head.appendChild(el('span', { text: Taxonomy.orgTypeLabel(m.type), style: 'font-size:.75rem;color:var(--gray-600);' }));
        }
        card.appendChild(head);

        // ── Admin half ────────────────────────────────────────────────────────
        if (isAdminHere) {
          const tiles = el('div', { style: 'display:flex;gap:.5rem;flex-wrap:wrap;margin-bottom:.6rem;' });
          tiles.appendChild(el('div', { text: 'Loading…', style: 'font-size:.85rem;color:var(--gray-600);' }));
          card.appendChild(tiles);

          // Fill in when the per-org calls land, so one slow org can't hold up the others.
          const p = loadAdminStats(orgId, myRank);
          adminStatPromises.push(p);
          p.then(({ approvals, assigned, events, roster }) => {
            tiles.innerHTML = '';
            if (approvals) {
              tiles.appendChild(statTile(approvals.length, 'Awaiting approval', '/admin-portal.html'));
            }
            if (assigned) {
              tiles.appendChild(statTile(assigned.length, 'Assigned to me', '/org-portal.html'));
            }
            if (events) {
              const upcoming = events.filter(e => new Date(e.startDateTime) >= new Date());
              tiles.appendChild(statTile(upcoming.length, 'Upcoming events', '/org-portal.html'));
            }
            if (roster) {
              // "Volunteers", not "Members": GetVolunteersByTenantAsync filters
              // AdminLevel == "Student", so admins are excluded. Labelling this "Members"
              // read as 1 in an org holding four people.
              tiles.appendChild(statTile(roster.length, 'Volunteers', '/admin-backend.html'));
            }
            if (!tiles.children.length) {
              tiles.appendChild(el('div', {
                text: 'No admin figures available for this organization.',
                style: 'font-size:.85rem;color:var(--gray-600);',
              }));
            }
          });
        }

        // ── Volunteer half ────────────────────────────────────────────────────
        // Hours are keyed on schoolId — the org that APPROVED them, i.e. the membership
        // that credited the person. Hours served at an outside org still count here,
        // matching how approval actually works; the history table below names the host.
        const mine = (logs || []).filter(l => l.schoolId === orgId);
        const approvedHrs = mine.filter(l => l.status === 'Approved').reduce((s, l) => s + l.hoursLogged, 0);
        const pendingHrs  = mine.filter(l => l.status === 'Pending').reduce((s, l) => s + l.hoursLogged, 0);
        const overseers   = overseersByOrg[orgId] || [];

        // Shown when they have hours here, when they're not an admin here (a plain
        // volunteer card would otherwise be empty), or when someone oversees them.
        if (mine.length || !isAdminHere || overseers.length) {
          const vol = el('div', { style: 'font-size:.9rem;color:var(--gray-700);' });
          vol.appendChild(el('div', {
            text: mine.length
              ? `${approvedHrs.toFixed(1)} hours approved here${pendingHrs > 0 ? ` · ${pendingHrs.toFixed(1)} pending` : ''}`
              : 'No service hours logged here yet.',
          }));
          if (overseers.length) {
            const line = el('div', { style: 'margin-top:.35rem;font-size:.85rem;color:var(--gray-600);' });
            line.appendChild(document.createTextNode(overseers.length > 1 ? 'Overseen by: ' : 'Overseen by '));
            overseers.forEach((a, i) => {
              if (i) line.appendChild(document.createTextNode(', '));
              line.appendChild(el('a', { href: `mailto:${a.email}`, text: a.name }));
            });
            vol.appendChild(line);
          }
          card.appendChild(vol);
        }

        wrap.appendChild(card);
      }

      // Hours credited by an org the person is NOT a member of belong to no card, so the
      // cards would silently sum to less than the "Total Approved Hours" tile above — two
      // numbers on one screen that don't reconcile. Happens when a membership is removed or
      // its org is deleted (live example: an approved log whose schoolId names a tenant that
      // no longer exists). Say so rather than letting the arithmetic look wrong; the history
      // table below lists the logs themselves.
      const orgIds = new Set(memberships.map(m => m.organizationId));
      const unmatched = (logs || []).filter(l => l.status === 'Approved' && !orgIds.has(l.schoolId));
      if (unmatched.length) {
        const hrs = unmatched.reduce((s, l) => s + l.hoursLogged, 0);
        wrap.appendChild(el('div', {
          style: 'font-size:.85rem;color:var(--gray-600);margin:-.25rem 0 1rem;',
          text: `${hrs.toFixed(1)} approved hour${hrs === 1 ? '' : 's'} were credited by an organization `
              + `you're no longer a member of. They count towards your total above and appear in your history below.`,
        }));
      }

      // Overview tile: hours waiting on this person across every org they can approve in.
      // Only appears when they actually approve somewhere — for a pure volunteer it would
      // always read 0, which is noise rather than information.
      if (adminStatPromises.length) {
        const all = await Promise.all(adminStatPromises);
        const totals = all.map(s => s.approvals).filter(Boolean);
        if (totals.length) {
          document.getElementById('stat-approvals').textContent = String(totals.reduce((s, a) => s + a.length, 0));
          document.getElementById('stat-card-approvals').style.display = '';
        }
      }
    }

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

        // First-login: prompt to complete intake when the profile isn't complete, and also
        // whenever the Terms/Privacy version they accepted isn't the one in force — that's
        // what makes re-issuing the documents (e.g. once counsel signs off on the drafts)
        // re-prompt existing users rather than only catching new ones.
        if (currentUser.profileComplete === false || needsPolicyAcceptance(currentUser)) {
          openIntake(currentUser);
        }

        // First-login onboarding: a self-registered student not yet in any organization is
        // guided to join one. Skip the (necessarily empty) stats + log history.
        if (adminLevel === 'Student' && (!memberships || memberships.length === 0)) {
          document.getElementById('onboarding-card').style.display = 'block';
          document.getElementById('stats-row').style.display = 'none';
          document.getElementById('log-card').style.display = 'none';
          return;
        }

        // Everyone gets their real hours now, admins included. This previously short-circuited
        // for any non-Student and printed "Admin users can use the Admin Backend" over three
        // em-dashes — so an admin who also volunteers was told their own hours didn't exist,
        // and an admin who doesn't got a home screen with nothing on it.
        const { logs, totalApprovedHours } = await Api.ServiceLogs.myLogs().catch(() => ({ logs: [], totalApprovedHours: 0 }));

        // Per-org cards, in parallel with the personal history below.
        renderOrgCards(memberships, logs).catch(err => console.error('[dashboard] org cards', err));

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
