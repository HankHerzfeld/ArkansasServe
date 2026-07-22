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

  // ── Per-page scope declaration ──────────────────────────────────────────────
  // Each scoped page states what its switcher should offer, instead of every page
  // receiving one global list. Keyed by the same href as TABS above (TABS gates who
  // may SEE the page; this gates which orgs the page is usable in — related, but not
  // the same question, so they stay separate tables).
  //
  //   minRole    minimum role in an org for it to be offered here (supers exempt)
  //   orgTypes   'schoolLike' | 'organization' | null — the kind of org this page works on
  //   allTenants whether a SuperAdmin sees every tenant or just their memberships
  //   showGroups whether the group selector appears
  //
  // A page absent from this table gets DEFAULTS: unfiltered, all tenants, groups shown —
  // i.e. exactly today's behaviour, so adding a page here is opt-in.
  const PAGE_SCOPE = {
    // Manage Events — a super legitimately creates/manages events in any org, so the
    // full tenant list is reach rather than noise.
    '/org-portal.html':    { minRole: 'EventAdmin',        orgTypes: null,         allTenants: true, showGroups: true },
    // Approvals — approving hours. In PRINCIPLE this is School/JDC work (hours are approved
    // against the student's school, `ServiceLog.schoolId`), so `orgTypes: 'schoolLike'` is the
    // eventual right answer and the mechanism is built and tested for it.
    //
    // ⚠️ It is deliberately NOT applied yet: EVERY tenant in production is currently typed
    // `Organization` — there is not one School or JDC doc — so the filter would drop all of
    // them and leave a SuperAdmin unable to open Approvals at all. Schools are today modelled
    // as Organizations, and approvals genuinely flow through them. Turn this on once real
    // School/JDC tenants exist; the page's own `policyAppliesToActiveOrg` keeps hiding the
    // policy card for Community Organizations in the meantime.
    '/admin-portal.html':  { minRole: 'OrganizationAdmin', orgTypes: null,         allTenants: true, showGroups: true },
    // Admin Backend — tenant settings/roster/tags for any org. Groups are managed as
    // data here rather than used as a filter, hence no group selector (this replaces
    // the ad-hoc { showGroups: false } the page used to pass at its call site).
    '/admin-backend.html': { minRole: 'OrganizationAdmin', orgTypes: null,         allTenants: true, showGroups: false },
  };

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
  // The pane shows the viewer's own notifications plus role-scoped admin items
  // (pending approvals, recent self-joins) each with a place to jump and act.
  const bell = { open: false, personal: [], admin: [], unread: 0, actionCount: 0 };

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

  async function loadPane() {
    try {
      const data = await Api.Notifications.pane();
      bell.personal = data.personal || [];
      bell.admin = data.admin || [];
      bell.unread = data.unread || 0;
      bell.actionCount = data.actionCount || 0;
    } catch {
      bell.personal = []; bell.admin = []; bell.unread = 0; bell.actionCount = 0;
    }
    const total = bell.unread + bell.actionCount;
    const badge = document.getElementById('notif-badge');
    if (badge) {
      badge.textContent = total > 9 ? '9+' : String(total);
      badge.style.display = total ? 'inline-flex' : 'none';
    }
    if (bell.open) renderPane();
  }

  function renderPane() {
    const pane = document.getElementById('notif-pane');
    if (!pane) return;
    pane.innerHTML = '';
    pane.appendChild(el('div', { class: 'notif-head', text: 'Notifications' }));

    if (!bell.admin.length && !bell.personal.length) {
      pane.appendChild(el('div', { class: 'notif-empty', text: 'You’re all caught up.' }));
      return;
    }

    // Role-scoped admin items first — each links to where you act on it.
    if (bell.admin.length) {
      pane.appendChild(el('div', { class: 'notif-section', text: 'Needs attention' }));
      bell.admin.forEach(a => {
        const item = el('div', { class: 'notif-item action' });
        item.appendChild(el('div', { class: 'notif-msg', text: a.message }));
        if (a.href) item.appendChild(el('a', { class: 'notif-dismiss', href: a.href, text: a.kind === 'approvals' ? 'Review' : 'View' }));
        pane.appendChild(item);
      });
    }

    // The viewer's own notifications.
    if (bell.personal.length) {
      if (bell.admin.length) pane.appendChild(el('div', { class: 'notif-section', text: 'For you' }));
      bell.personal.forEach(n => {
        const item = el('div', { class: 'notif-item' + (n.isRead ? '' : ' unread') });
        item.appendChild(el('div', { class: 'notif-msg', text: n.message }));
        if (!n.isRead) {
          item.appendChild(el('button', {
            class: 'notif-dismiss', text: 'Mark read',
            onClick: async (e) => {
              const b = e.currentTarget; b.disabled = true;
              try { await Api.Notifications.markRead(n.id); n.isRead = true; await loadPane(); renderPane(); }
              catch { b.disabled = false; }
            },
          }));
        }
        pane.appendChild(item);
      });
    }
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

    const right = el('div', { class: 'nav-right' });
    right.appendChild(buildBell());
    right.appendChild(el('a', { class: 'navbar-user', href: '/dashboard.html', title: 'Go to your dashboard', text: profile?.name || '' }));
    right.appendChild(el('button', { class: 'btn btn-secondary btn-sm', onClick: () => Auth.logout(), text: 'Sign Out' }));

    // Tabs + right cluster share one container so the phone breakpoint can slide the
    // whole lot out as a drawer. On desktop this is just a flex row and nothing moves:
    // a SuperAdmin sees 7 tabs, which wrapped to 3 rows and ate 202px — a quarter of a
    // 375x812 screen — before content began.
    const menu = el('div', { class: 'nav-menu', id: 'nav-menu' }, tabs, right);

    const toggle = el('button', {
      class: 'nav-toggle',
      type: 'button',
      'aria-label': 'Open menu',
      'aria-expanded': 'false',
      'aria-controls': 'nav-menu',
      onClick: () => setMenuOpen(nav, !nav.classList.contains('nav-open')),
    });
    // Three bars, drawn not typed, so it never depends on an emoji font.
    for (let i = 0; i < 3; i++) toggle.appendChild(el('span', { class: 'nav-toggle-bar' }));

    const backdrop = el('div', { class: 'nav-backdrop', onClick: () => setMenuOpen(nav, false) });

    nav.appendChild(toggle);
    nav.appendChild(menu);
    nav.appendChild(backdrop);

    // Any navigation or action inside the drawer should close it — otherwise the
    // drawer stays open over the page it just navigated to.
    menu.addEventListener('click', (e) => {
      if (e.target.closest('a, button') && !e.target.closest('.notif')) setMenuOpen(nav, false);
    });

    return nav;
  }

  // Single place that owns drawer open/close, so the button's aria state, the
  // backdrop and the body scroll lock can never drift apart.
  function setMenuOpen(nav, open) {
    nav.classList.toggle('nav-open', open);
    const toggle = nav.querySelector('.nav-toggle');
    if (toggle) {
      toggle.setAttribute('aria-expanded', String(open));
      toggle.setAttribute('aria-label', open ? 'Close menu' : 'Open menu');
    }
    // Stop the page scrolling under an open drawer.
    document.body.style.overflow = open ? 'hidden' : '';
    if (open) {
      const first = nav.querySelector('.nav-menu a, .nav-menu button');
      if (first) first.focus();
    }
  }

  // Escape closes the drawer from anywhere, matching the backdrop click.
  document.addEventListener('keydown', (e) => {
    if (e.key !== 'Escape') return;
    const nav = document.querySelector('.navbar.nav-open');
    if (nav) { setMenuOpen(nav, false); const t = nav.querySelector('.nav-toggle'); if (t) t.focus(); }
  });

  // Renders the whole shell (header + scope bar). Kept named `setupHeader` so the
  // existing page calls keep working. `current` is this page's href.
  async function setupHeader(current, currentUser, opts = {}) {
    // Cache the authoritative name/level from /users/me before rendering, so the
    // header shows the person's real display name (not the token's `name` claim)
    // right away on pages that resolve the user first.
    if (currentUser) Auth.setResolvedLevelFromUser(currentUser);
    // While impersonating, the whole shell must reflect the TARGET, not the real
    // super — so the nav shows the target's name and only their tab set (a faithful
    // "view as"), and the scope bar uses their level. Default to Student (minimal)
    // if the stored level is somehow missing, never the super's.
    const imp = Auth.getImpersonation && Auth.getImpersonation();
    const profile = imp
      ? { name: imp.name, adminLevel: imp.adminLevel || 'Student', email: imp.email }
      : Auth.getProfile();

    const host = document.getElementById('app-header');
    if (host) {
      host.innerHTML = '';
      renderImpersonationBanner(host);
      renderExpiredNotice(host);
      host.appendChild(buildNav(current, profile));
      loadPane();
    }

    const scopeBar = document.getElementById('scope-bar');
    if (scopeBar && Auth.adminRank(profile?.adminLevel || Auth.getAdminLevel()) > 0) {
      // This page's declaration, overridable per call site for one-offs.
      const cfg = { ...(PAGE_SCOPE[current] || {}), ...opts };
      try {
        await Scope.init(currentUser, cfg);
        mountScopeBar(scopeBar, cfg.showGroups !== false);
      } catch (err) {
        console.error('[ui] scope init failed', err);
      }
    }
  }

  // One-time notice after an impersonation session ended (expiry/revoke), so the
  // operator understands why they're back on their own account.
  function renderExpiredNotice(host) {
    const params = new URLSearchParams(window.location.search);
    if (params.get('impersonation') !== 'expired') return;
    const note = document.createElement('div');
    note.style.cssText = 'background:var(--amber-pale);color:var(--amber);padding:.5rem 1rem;text-align:center;font-size:.85rem;font-weight:600;';
    note.textContent = 'The impersonation session ended — you are back on your own account.';
    host.insertBefore(note, host.firstChild);
    setTimeout(() => note.remove(), 6000);
  }

  // ── Impersonation banner (#26) ──────────────────────────────────────────────
  // Unmistakable, always-present while a SuperAdmin is "acting as" another user.
  function renderImpersonationBanner(host) {
    const imp = Auth.getImpersonation && Auth.getImpersonation();
    if (!imp) return;

    async function exitImpersonation(stopServer, expired) {
      if (stopServer) { try { await Api.Impersonation.stop(imp.sid); } catch { /* clear locally regardless */ } }
      Auth.clearImpersonation();
      window.location.href = expired ? '/admin-backend.html?impersonation=expired' : '/admin-backend.html';
    }

    // A write session is the more dangerous state, so it gets the louder colour; the
    // label must always state the ACTUAL mode — never assume read-only.
    const writable = String(imp.mode || 'read-only').toLowerCase() === 'read-write';

    const bar = document.createElement('div');
    bar.id = 'impersonation-banner';
    bar.style.cssText = `background:${writable ? '#b91c1c' : '#b45309'};color:#fff;padding:.5rem 1rem;display:flex;align-items:center;justify-content:center;gap:1rem;font-size:.9rem;font-weight:600;flex-wrap:wrap;`;

    const label = document.createElement('span');
    const lvl = imp.adminLevel ? ` (${imp.adminLevel})` : '';
    const expiry = imp.expiresAt ? new Date(imp.expiresAt) : null;
    const expiryTxt = expiry ? ` — expires ${expiry.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' })}` : '';
    const modeTxt = writable ? 'READ-WRITE session — changes are real' : 'read-only session';
    label.textContent = `⚠ Viewing as ${imp.name}${lvl} — ${modeTxt}${expiryTxt}`;
    bar.appendChild(label);

    const exit = document.createElement('button');
    exit.textContent = 'Exit';
    exit.className = 'btn btn-sm';
    exit.style.cssText = 'background:#fff;color:#b91c1c;font-weight:700;';
    exit.addEventListener('click', async () => {
      exit.disabled = true;
      exit.textContent = 'Exiting…';
      await exitImpersonation(true, false);
    });
    bar.appendChild(exit);

    // Auto-exit exactly at expiry so the banner never outlives the session and the
    // operator is never left silently acting as themselves behind a stale banner.
    if (expiry) {
      const ms = expiry.getTime() - Date.now();
      if (ms <= 0) { exitImpersonation(false, true); return; }
      setTimeout(() => exitImpersonation(false, true), Math.min(ms, 2147483647));
    }

    host.insertBefore(bar, host.firstChild);
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

  // Org switcher: TABS for a handful, a compact DROPDOWN once the list is long.
  //
  // Tabs make the orgs you can act in visible at a glance — great for a few. But a SuperAdmin's
  // list is EVERY tenant on the platform (see scope.js init), not just their memberships, which
  // turns a bare tab strip unusable once every Arkansas school is on it. Past a threshold a
  // single native <select> replaces the strip: it stays on one line and has built-in
  // type-to-find, so the bar never grows into the multi-row block the phone header used to have.
  // (The internal root partition is already filtered out of this list in scope.js.)
  const ORG_DROPDOWN_THRESHOLD = 5;

  function buildOrgSwitcher(snap) {
    if (snap.orgs.length > ORG_DROPDOWN_THRESHOLD) {
      // Compact, one-line, type-to-find. Reuses the shared styled select helper.
      return select(
        snap.activeOrgId || '',
        snap.orgs.map(o => ({ value: o.id, label: o.name })),
        (v) => Scope.setOrg(v)
      );
    }

    // Few orgs: tabs (chips), so the ones you can act in are visible without a click.
    const strip = el('div', { class: 'scope-tabs', role: 'group', 'aria-label': 'Choose organization' });
    snap.orgs.forEach(o => {
      const isActive = o.id === snap.activeOrgId;
      const b = el('button', {
        class: 'scope-tab' + (isActive ? ' active' : ''),
        type: 'button',
        text: o.name,
        title: o.name,
        onClick: () => Scope.setOrg(o.id),
      });
      // aria-current, not a tablist: these switch the data the page is scoped to, they
      // don't reveal sibling panels.
      if (isActive) b.setAttribute('aria-current', 'true');
      strip.appendChild(b);
    });
    return strip;
  }

  function renderScopeBar(container, snap, showGroups = true) {
    if (!container) return;
    container.innerHTML = '';

    // The page's own declaration filtered every org away — say so. Rendering nothing
    // would leave an admin on a blank page with no clue why, which is how the old
    // silent-empty-queue behaviour read.
    if (snap && snap.filteredEmpty) {
      const note = document.createElement('div');
      note.style.cssText = 'font-size:.85rem;color:var(--gray-600);';
      note.textContent = snap.config?.orgTypes === 'schoolLike'
        ? 'This page applies to schools and juvenile-service organizations. You don\'t administer one.'
        : 'You don\'t administer an organization that can use this page.';
      container.appendChild(note);
      return;
    }

    if (!snap || !snap.org) return;

    const wrap = document.createElement('div');
    wrap.style.cssText = 'display:flex;align-items:center;gap:.5rem;flex-wrap:wrap;font-size:.85rem;color:var(--gray-700);';

    const label = document.createElement('span');
    label.textContent = 'Viewing:';
    label.style.color = 'var(--gray-600)';
    wrap.appendChild(label);

    if (snap.orgs.length > 1) {
      wrap.appendChild(buildOrgSwitcher(snap));
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

  // ── Event listing attribution (MVP external listings; also crawled events) ──
  // A "display-only" listing is not registerable on Arkansas Serve — it points at an outside
  // host. Two sources share the behaviour: a hand-entered external listing (listingType
  // 'external', hostOrganization*) and a crawler import (isCrawled, crawlerSource*). Both
  // suppress the sign-up surface and show an attribution line + a link out, so the shared logic
  // lives here and both event pages (card + detail) call it — the single renderer the plan asks
  // for, so a crawled event and a hand-entered listing can never disagree on how they attribute.
  function isDisplayOnlyListing(evt) {
    return !!evt && (evt.listingType === 'external' || evt.isCrawled === true);
  }

  // Returns { text, url } for the attribution line, or null for an ordinary hosted event
  // (whose host is the owning org, shown as organizationName by the caller).
  function listingAttribution(evt) {
    if (!evt) return null;
    if (evt.listingType === 'external') {
      return {
        text: 'Hosted by ' + (evt.hostOrganizationName || 'another organization'),
        url: evt.hostOrganizationUrl || evt.externalUrl || null,
      };
    }
    if (evt.isCrawled === true) {
      return {
        text: evt.crawlerAttribution || (evt.crawlerSourceName ? 'Via ' + evt.crawlerSourceName : 'External listing'),
        url: evt.crawlerSourceUrl || evt.externalUrl || null,
      };
    }
    return null;
  }

  return { setupHeader, mountScopeBar, renderScopeBar, isDisplayOnlyListing, listingAttribution };
})();
