  Auth.init();
  let profile = null;
  let org = null;

  // A link built from an `arkansas-serve-root` membership (a SuperAdmin's dashboard card, a
  // bookmark) names the internal platform partition, whose profile 404s by design. Its public
  // face is the real `arkansas-serve` org, so resolve to that instead of showing an error for
  // a page that plainly exists. See js/orgs.js — the backend guard is deliberately untouched.
  const requestedOrgId = new URLSearchParams(location.search).get('id');
  const orgId = Orgs.canonicalOrgId(requestedOrgId);

  // Canonicalise the address bar so the URL matches what is on screen — otherwise sharing or
  // re-bookmarking from here would propagate the internal id again.
  if (requestedOrgId && orgId !== requestedOrgId) {
    const url = new URL(location.href);
    url.searchParams.set('id', orgId);
    history.replaceState(null, '', url);
  }

  Auth.requireAuth().then((p) => {
    profile = p;
    if (!profile) return;
    UI.setupHeader('/organizations.html');
    loadOrg();
  });

  async function loadOrg() {
    const loading = document.getElementById('org-loading');
    const errBox = document.getElementById('org-error');
    if (!orgId) {
      loading.style.display = 'none';
      errBox.style.display = 'block';
      errBox.textContent = 'No organization specified.';
      return;
    }
    try {
      org = await Api.Orgs.get(orgId);
      loading.style.display = 'none';
      renderOrg(org);
    } catch (err) {
      loading.style.display = 'none';
      errBox.style.display = 'block';
      errBox.textContent = 'Could not load this organization. It may have been removed.';
    }
  }

  function elem(tag, props = {}, ...kids) {
    const n = document.createElement(tag);
    Object.entries(props).forEach(([k, v]) => {
      if (v == null) return;
      if (k === 'class') n.className = v;
      else if (k === 'text') n.textContent = v;
      else n.setAttribute(k, v);
    });
    kids.forEach(c => { if (c != null) n.appendChild(typeof c === 'string' ? document.createTextNode(c) : c); });
    return n;
  }
  function section(title, body) {
    if (!body) return null;
    const wrap = elem('div', { class: 'card' });
    wrap.appendChild(elem('div', { class: 'card-title', text: title }));
    wrap.appendChild(typeof body === 'string' ? elem('p', { text: body, style: 'white-space:pre-wrap;font-size:.95rem;' }) : body);
    return wrap;
  }

  function renderOrg(o) {
    const root = document.getElementById('org-detail');
    root.style.display = 'block';
    root.innerHTML = '';

    // Header: logo + name + type + join/leave.
    const header = elem('div', { class: 'card' });
    const top = elem('div', { style: 'display:flex;justify-content:space-between;gap:1rem;flex-wrap:wrap;align-items:flex-start;' });
    const idBlock = elem('div', { style: 'display:flex;gap:1rem;align-items:center;' });
    if (o.logoUrl) {
      idBlock.appendChild(elem('img', { src: o.logoUrl, alt: o.name, style: 'width:3.5rem;height:3.5rem;object-fit:contain;border-radius:var(--radius);' }));
    }
    const names = elem('div');
    names.appendChild(elem('h1', { text: o.name || o.id, style: 'font-size:1.6rem;color:var(--green);' }));
    if (o.type) names.appendChild(elem('span', { class: 'event-badge', text: o.type }));
    idBlock.appendChild(names);
    top.appendChild(idBlock);
    top.appendChild(buildJoin(o));
    header.appendChild(top);
    root.appendChild(header);

    // Mission / description (only when present).
    const mission = section('Mission', o.mission);
    if (mission) root.appendChild(mission);
    const about = section('About', o.description);
    if (about) root.appendChild(about);

    // Contact + website (only rows with data).
    const rows = [];
    if (o.address)      rows.push(['Address', o.address, null]);
    if (o.contactEmail) rows.push(['Email', o.contactEmail, `mailto:${o.contactEmail}`]);
    if (o.contactPhone) rows.push(['Phone', o.contactPhone, null]);
    if (o.website)      rows.push(['Website', o.website, o.website]);
    if (rows.length) {
      const grid = elem('div', { style: 'display:flex;gap:1.75rem;flex-wrap:wrap;font-size:.95rem;' });
      rows.forEach(([label, val, href]) => {
        const c = elem('div');
        c.appendChild(elem('div', { text: label, style: 'color:var(--gray-600);font-size:.75rem;' }));
        if (href) c.appendChild(elem('a', { href, target: href.startsWith('http') ? '_blank' : null, rel: 'noopener', text: val }));
        else c.appendChild(elem('div', { text: val }));
        grid.appendChild(c);
      });
      root.appendChild(section('Contact', grid));
    }

    // Upcoming events (only when there are any).
    if (o.upcomingEvents && o.upcomingEvents.length) {
      const list = elem('div', { style: 'display:flex;flex-direction:column;gap:.5rem;' });
      o.upcomingEvents.forEach(e => {
        const row = elem('a', {
          href: `/event.html?id=${encodeURIComponent(e.id)}&organizationId=${encodeURIComponent(e.organizationId)}`,
          style: 'display:flex;justify-content:space-between;gap:1rem;padding:.6rem .75rem;border:1px solid var(--gray-200);border-radius:var(--radius);text-decoration:none;color:inherit;flex-wrap:wrap;',
        });
        const left = elem('div');
        left.appendChild(elem('div', { text: e.title, style: 'font-weight:600;color:var(--green);' }));
        const meta = [];
        if (e.startDateTime) meta.push(new Date(e.startDateTime).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' }));
        if (e.location) meta.push(e.location);
        left.appendChild(elem('div', { text: meta.join(' · '), style: 'font-size:.8rem;color:var(--gray-600);' }));
        row.appendChild(left);
        if (e.hoursValue != null) row.appendChild(elem('span', { class: 'event-badge', text: `${e.hoursValue} hr${e.hoursValue === 1 ? '' : 's'}` }));
        list.appendChild(row);
      });
      root.appendChild(section('Upcoming events', list));
    }
  }

  function buildJoin(o) {
    const wrap = elem('div');
    if (o.alreadyMember) {
      const joined = elem('span', { class: 'status status-approved', text: 'Joined ✓', style: 'margin-right:.5rem;' });
      const leave = elem('button', { class: 'btn btn-secondary btn-sm', text: 'Leave' });
      leave.addEventListener('click', () => doLeave(o, wrap));
      wrap.appendChild(joined);
      wrap.appendChild(leave);
    } else if (o.allowSelfJoin === false) {
      // Assign-only org. Say who does add you, rather than showing a disabled button
      // with no explanation or a live one whose only outcome is a 403.
      wrap.appendChild(elem('span', {
        class: 'status',
        text: 'Members added by an admin',
        style: 'background:var(--gray-200);color:var(--gray-600);',
      }));
    } else {
      const join = elem('button', { class: 'btn btn-primary btn-sm', text: 'Join' });
      join.addEventListener('click', () => doJoin(o, wrap));
      wrap.appendChild(join);
    }
    return wrap;
  }

  async function doJoin(o, wrap) {
    const btn = wrap.querySelector('button');
    btn.disabled = true; btn.textContent = 'Joining…';
    try {
      await Api.Memberships.join(o.id);
      o.alreadyMember = true;
      wrap.replaceWith(buildJoin(o));
      showToast(`Joined ${o.name || o.id}.`, 'success');
    } catch (err) {
      btn.disabled = false; btn.textContent = 'Join';
      showToast(err.message || 'Could not join. Please try again.', 'error');
    }
  }
  async function doLeave(o, wrap) {
    const btn = wrap.querySelector('button');
    btn.disabled = true; btn.textContent = 'Leaving…';
    try {
      await Api.Memberships.leave(o.id);
      o.alreadyMember = false;
      wrap.replaceWith(buildJoin(o));
      showToast(`Left ${o.name || o.id}.`, 'success');
    } catch (err) {
      btn.disabled = false; btn.textContent = 'Leave';
      showToast(err.message || 'Could not leave this organization.', 'error');
    }
  }

  function showToast(message, type = 'success') {
    const toast = document.createElement('div');
    toast.className = `alert alert-${type}`;
    // bottom/right are safe-area aware: the page is edge-to-edge, so a flat 1.5rem would
    // put the toast under the home indicator (portrait) or the notch (landscape).
    toast.style.cssText = 'position:fixed;bottom:max(1.5rem,env(safe-area-inset-bottom));right:max(1.5rem,env(safe-area-inset-right));z-index:999;max-width:320px;box-shadow:0 4px 12px rgba(0,0,0,.15);';
    toast.textContent = message;
    document.body.appendChild(toast);
    setTimeout(() => toast.remove(), 4000);
  }
