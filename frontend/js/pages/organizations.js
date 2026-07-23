  Auth.init();
  let profile = null;
  let allOrgs = [];

  Auth.requireAuth().then((p) => {
    profile = p;
    if (!profile) return;
    UI.setupHeader('/organizations.html');
    loadOrgs();
  });

  async function loadOrgs() {
    try {
      allOrgs = await Api.Orgs.browse();
      document.getElementById('orgs-loading').style.display = 'none';
      applyFilters();
    } catch (err) {
      document.getElementById('orgs-loading').innerHTML =
        `<div class="alert alert-error">Could not load organizations. Please refresh.</div>`;
    }
  }

  function applyFilters() {
    const search  = document.getElementById('filter-search').value.toLowerCase();
    const mineOnly = document.getElementById('filter-mine').checked;
    renderOrgs(allOrgs.filter(o =>
      (!search   || (o.name || '').toLowerCase().includes(search) || (o.type || '').toLowerCase().includes(search)) &&
      (!mineOnly || o.alreadyMember)
    ));
  }

  function renderOrgs(orgs) {
    const grid  = document.getElementById('orgs-grid');
    const empty = document.getElementById('orgs-empty');
    if (orgs.length === 0) {
      grid.style.display  = 'none';
      empty.style.display = 'block';
      return;
    }
    empty.style.display = 'none';
    grid.style.display  = 'grid';

    grid.innerHTML = '';
    orgs.forEach(org => grid.appendChild(orgCard(org)));
  }

  function orgCard(org) {
    const card = document.createElement('div');
    card.className = 'card event-card';

    const title = document.createElement('a');
    title.className = 'card-title';
    title.href = `/organization.html?id=${encodeURIComponent(org.id)}`;
    title.textContent = org.name || org.id;
    title.style.cssText = 'display:block;color:var(--green);text-decoration:none;';
    card.appendChild(title);

    if (org.type) {
      const badge = document.createElement('span');
      badge.className = 'event-badge';
      badge.textContent = org.type;
      card.appendChild(badge);
    }

    const actions = document.createElement('div');
    actions.style.cssText = 'display:flex;align-items:center;gap:.5rem;margin-top:.75rem;flex-wrap:wrap;';
    const view = document.createElement('a');
    view.className = 'btn btn-secondary btn-sm';
    view.href = `/organization.html?id=${encodeURIComponent(org.id)}`;
    view.textContent = 'View';
    actions.appendChild(view);

    if (org.alreadyMember) {
      const joined = document.createElement('span');
      joined.className = 'status status-approved';
      joined.textContent = 'Joined ✓';
      actions.appendChild(joined);

      const leaveBtn = document.createElement('button');
      leaveBtn.className = 'btn btn-secondary btn-sm';
      leaveBtn.textContent = 'Leave';
      leaveBtn.addEventListener('click', () => doLeave(org, card));
      actions.appendChild(leaveBtn);
    } else if (org.allowSelfJoin === false) {
      // Assign-only org: an admin creates the membership. Offering Join here would only
      // produce a 403. Checked against `=== false` so an older cached response, which
      // carries no such field, still renders Join exactly as it does today — the server
      // is the enforcement point either way.
      const note = document.createElement('span');
      note.className = 'status';
      note.style.cssText = 'background:var(--gray-200);color:var(--gray-600);';
      note.textContent = 'Admin-added';
      actions.appendChild(note);
    } else {
      const joinBtn = document.createElement('button');
      joinBtn.className = 'btn btn-primary btn-sm';
      joinBtn.textContent = 'Join';
      joinBtn.addEventListener('click', () => doJoin(org, card));
      actions.appendChild(joinBtn);
    }

    card.appendChild(actions);
    return card;
  }

  async function doJoin(org, card) {
    const btn = card.querySelector('button');
    btn.disabled = true;
    btn.textContent = 'Joining…';
    try {
      await Api.Memberships.join(org.id);
      org.alreadyMember = true;
      card.replaceWith(orgCard(org));
      showToast(`Joined ${org.name || org.id}.`, 'success');
    } catch (err) {
      btn.disabled = false;
      btn.textContent = 'Join';
      showToast(err.message || 'Could not join. Please try again.', 'error');
    }
  }

  async function doLeave(org, card) {
    const btn = card.querySelector('button');
    btn.disabled = true;
    btn.textContent = 'Leaving…';
    try {
      await Api.Memberships.leave(org.id);
      org.alreadyMember = false;
      card.replaceWith(orgCard(org));
      showToast(`Left ${org.name || org.id}.`, 'success');
    } catch (err) {
      btn.disabled = false;
      btn.textContent = 'Leave';
      showToast(err.message || 'Could not leave this organization.', 'error');
    }
  }

  ['filter-search', 'filter-mine'].forEach(id => {
    document.getElementById(id)?.addEventListener('input', applyFilters);
    document.getElementById(id)?.addEventListener('change', applyFilters);
  });

  // Shared toast (see UI.toast). Local alias keeps the call sites below unchanged.
  const showToast = UI.toast;
