// ui.js — shared header helpers: role-gated connector buttons and the active
// org/group scope bar. Keeps navigation and scope denotation consistent across
// every page.
//
// REQUIRES auth.js (and, for the scope bar, scope.js) loaded first.

'use strict';

const UI = (() => {
  // Elevated destinations, each gated by a minimum admin rank. "Find events"
  // is intentionally excluded — every page already links it in the navbar.
  const CONNECTORS = [
    { label: 'Manage events',       href: '/org-portal.html',     minRank: 1 }, // EventAdmin+
    { label: 'Approvals & reports', href: '/admin-portal.html',   minRank: 3 }, // OrganizationAdmin+
    { label: 'Admin backend',       href: '/admin-backend.html',  minRank: 3 }, // OrganizationAdmin+
    { label: 'Platform admin',      href: '/platform-admin.html', minRank: 4 }, // SuperAdmin
  ];

  // Renders the connector buttons the viewer is allowed to reach. `current` is
  // the current page's href (e.g. '/org-portal.html') so we don't link a page
  // to itself.
  function renderConnectors(container, adminLevel, current) {
    if (!container) return;
    const rank = Auth.adminRank(adminLevel || Auth.getAdminLevel());
    container.innerHTML = '';
    CONNECTORS.forEach(c => {
      if (rank < c.minRank || c.href === current) return;
      const a = document.createElement('a');
      a.className = 'btn btn-primary btn-sm';
      a.href = c.href;
      a.textContent = c.label;
      container.appendChild(a);
    });
  }

  function select(value, options, onChange) {
    const sel = document.createElement('select');
    sel.style.cssText = 'padding:.2rem .5rem;font-size:.85rem;max-width:16rem;';
    options.forEach(o => {
      const opt = document.createElement('option');
      opt.value = o.value;
      opt.textContent = o.label;
      if (o.value === value) opt.selected = true;
      sel.appendChild(opt);
    });
    sel.addEventListener('change', () => onChange(sel.value));
    return sel;
  }

  function renderScopeBar(container, snap) {
    if (!container) return;
    container.innerHTML = '';
    // Nothing to denote for students / users with no admin scope.
    if (!snap || !snap.org) return;

    const wrap = document.createElement('div');
    wrap.style.cssText = 'display:flex;align-items:center;gap:.5rem;flex-wrap:wrap;font-size:.85rem;color:var(--gray-700);';

    const label = document.createElement('span');
    label.textContent = 'Viewing:';
    label.style.color = 'var(--gray-600)';
    wrap.appendChild(label);

    // Org — a switcher when the user spans multiple orgs (SuperAdmin), else text.
    if (snap.orgs.length > 1) {
      wrap.appendChild(select(
        snap.activeOrgId,
        snap.orgs.map(o => ({ value: o.id, label: o.name })),
        (v) => Scope.setOrg(v)
      ));
    } else {
      const org = document.createElement('strong');
      org.textContent = snap.org.name;
      wrap.appendChild(org);
    }

    // Group — a switcher (with an "all groups" option) when the org has groups.
    const groups = snap.org.groups || [];
    if (groups.length) {
      const sep = document.createElement('span');
      sep.textContent = '›';
      sep.style.color = 'var(--gray-400)';
      wrap.appendChild(sep);
      wrap.appendChild(select(
        snap.activeGroupId || '',
        [{ value: '', label: 'All groups' }, ...groups.map(g => ({ value: g.id, label: g.name }))],
        (v) => Scope.setGroup(v)
      ));
    }

    container.appendChild(wrap);
  }

  // Renders the scope bar now and re-renders whenever the active scope changes.
  function mountScopeBar(container) {
    if (!container) return;
    Scope.onChange((snap) => renderScopeBar(container, snap));
    renderScopeBar(container, Scope.snapshot());
  }

  return { renderConnectors, renderScopeBar, mountScopeBar };
})();
