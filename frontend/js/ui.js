// ui.js — the shared application shell.
//
// One consistent header rendered identically on every page — brand, role-aware
// nav tabs (active tab highlighted), a notification bell/pane, and the user's
// name (which links to their dashboard) + sign out. Rendering the same chrome
// everywhere, plus cross-document view transitions (main.css), makes moving
// between pages read as flipping between tabs. Also mounts the active org/group
// scope bar for admins.
//
// Pages include an empty `<div id="app-header"></div>` at the top of <body> and
// call `UI.setupHeader('/thispage.html'[, currentUser])`.
//
// REQUIRES auth.js, api.js (and scope.js for the scope bar) loaded first.

'use strict';

const UI = (() => {
  // Primary tabs everyone sees, then role-gated tabs by minimum admin rank.
  const TABS = [
    { label: 'Dashboard',      href: '/dashboard.html',      minRank: 0 },
    { label: 'Find Events',    href: '/events.html',         minRank: 0 },
    { label: 'Organizations',  href: '/organizations.html',  minRank: 0 },
    { label: 'Manage Events',  href: '/org-portal.html',     minRank: 1 }, // EventAdmin+
    { label: 'Approvals',      href: '/admin-portal.html',   minRank: 3 }, // OrganizationAdmin+
    { label: 'Admin Backend',  href: '/admin-backend.html',  minRank: 3 }, // OrganizationAdmin+
    { label: 'Platform Admin', href: '/platform-admin.html', minRank: 4 }, // SuperAdmin
  ];

  // Tiny DOM helper (textContent only — never innerHTML with data).
  function el(tag, props = {}, ...children) {
    const node = document.createElement(tag);
    Object.entries(props).forEach(([k, v]) => {
      if (v == null) return;
      if (k === 'class') node.className = v;
      else if (k === 'text') node.textContent = v;
      else if (k.startsWith('on') && typeof v === 'function') node.addEventListener(k.slice(2).toLowerCase(), v);
      else node.setAttribute(k, v);
    });
    children.forEach(c => { if (c != null) node.appendChild(typeof c === 'string' ? document.createTextNode(c) : c); });
    return node;
  }

  // ── Notification bell + pane ────────────────────────────────────────────────
  // Phase A shows the viewer's own notifications; role-scoped feeds and actions
  // are layered on later.
  const bell = { open: false, items: [] };

  function buildBell() {
    const wrap = el('div', { class: 'notif' });
    const btn = el('button', { class: 'notif-bell', 'aria-label': 'Notifications', 'aria-haspopup': 'true', onClick: (e) => { e.stopPropagation(); togglePane(); } }, '🔔');
    const badge = el('span', { class: 'notif-badge', id: 'notif-badge' });
    badge.style.display = 'none';
    btn.appendChild(badge);
    const pane = el('div', { class: 'notif-pane', id: 'notif-pane', role: 'dialog', 'aria-label': 'Notifications' });
    pane.style.display = 'none';
    wrap.appendChild(btn);
    wrap.appendChild(pane);
    return wrap;
  }

  function togglePane() {
    const pane = document.getElementById('notif-pane');
    if (!pane) return;
    bell.open = !bell.open;
    pane.style.display = bell.open ? 'block' : 'none';
    if (bell.open) renderPane();
  }

  async function loadNotifications() {
    try { bell.items = (await Api.Notifications.list()) || []; }
    catch { bell.items = []; }
    const unread = bell.items.filter(n => !n.isRead).length;
    const badge = document.getElementById('notif-badge');
    if (badge) {
      badge.textContent = unread > 9 ? '9+' : String(unread);
      badge.style.display = unread ? 'inline-flex' : 'none';
    }
    if (bell.open) renderPane();
  }

  function renderPane() {
    const pane = document.getElementById('notif-pane');
    if (!pane) return;
    pane.innerHTML = '';
    pane.appendChild(el('div', { class: 'notif-head', text: 'Notifications' }));
    if (!bell.items.length) {
      pane.appendChild(el('div', { class: 'notif-empty', text: 'You have no notifications.' }));
      return;
    }
    bell.items.forEach(n => {
      const item = el('div', { class: 'notif-item' + (n.isRead ? '' : ' unread') });
      item.appendChild(el('div', { class: 'notif-msg', text: n.message }));
      if (!n.isRead) {
        item.appendChild(el('button', {
          class: 'notif-dismiss', text: 'Mark read',
          onClick: async (e) => {
            const b = e.currentTarget; b.disabled = true;
            try { await Api.Notifications.markRead(n.id); n.isRead = true; await loadNotifications(); renderPane(); }
            catch { b.disabled = false; }
          },
        }));
      }
      pane.appendChild(item);
    });
  }

  // Close the pane on an outside click.
  document.addEventListener('click', (e) => {
    if (!bell.open) return;
    if (!e.target.closest('.notif')) {
      bell.open = false;
      const pane = document.getElementById('notif-pane');
      if (pane) pane.style.display = 'none';
    }
  });

  // ── Header / nav ─────────────────────────────────────────────────────────────
  function buildNav(current, profile) {
    const rank = Auth.adminRank(profile?.adminLevel || Auth.getAdminLevel());
    const nav = el('nav', { class: 'navbar' });

    nav.appendChild(el('a', { class: 'navbar-brand', href: '/', text: 'Arkansas Serve' }));

    const tabs = el('div', { class: 'nav-tabs' });
    TABS.forEach(t => {
      if (rank < t.minRank) return;
      const a = el('a', { class: 'nav-tab', href: t.href, text: t.label });
      if (t.href === current) { a.classList.add('active'); a.setAttribute('aria-current', 'page'); }
      tabs.appendChild(a);
    });
    nav.appendChild(tabs);

    const right = el('div', { class: 'nav-right' });
    right.appendChild(buildBell());
    right.appendChild(el('a', { class: 'navbar-user', href: '/dashboard.html', title: 'Go to your dashboard', text: profile?.name || '' }));
    right.appendChild(el('button', { class: 'btn btn-secondary btn-sm', onClick: () => Auth.logout(), text: 'Sign Out' }));
    nav.appendChild(right);

    return nav;
  }

  // Renders the whole shell (header + scope bar). Kept named `setupHeader` so the
  // existing page calls keep working. `current` is this page's href.
  async function setupHeader(current, currentUser, opts = {}) {
    const profile = Auth.getProfile();

    const host = document.getElementById('app-header');
    if (host) {
      host.innerHTML = '';
      host.appendChild(buildNav(current, profile));
      loadNotifications();
    }

    const scopeBar = document.getElementById('scope-bar');
    if (scopeBar && Auth.adminRank(profile?.adminLevel || Auth.getAdminLevel()) > 0) {
      try {
        await Scope.init(currentUser);
        mountScopeBar(scopeBar, opts.showGroups !== false);
      } catch (err) {
        console.error('[ui] scope init failed', err);
      }
    }
  }

  // ── Scope bar (active org/group for admins) ─────────────────────────────────
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

  function renderScopeBar(container, snap, showGroups = true) {
    if (!container) return;
    container.innerHTML = '';
    if (!snap || !snap.org) return;

    const wrap = document.createElement('div');
    wrap.style.cssText = 'display:flex;align-items:center;gap:.5rem;flex-wrap:wrap;font-size:.85rem;color:var(--gray-700);';

    const label = document.createElement('span');
    label.textContent = 'Viewing:';
    label.style.color = 'var(--gray-600)';
    wrap.appendChild(label);

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

    const groups = snap.org.groups || [];
    if (showGroups && groups.length) {
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

  function mountScopeBar(container, showGroups = true) {
    if (!container) return;
    Scope.onChange((snap) => renderScopeBar(container, snap, showGroups));
    renderScopeBar(container, Scope.snapshot(), showGroups);
  }

  return { setupHeader, mountScopeBar, renderScopeBar };
})();
