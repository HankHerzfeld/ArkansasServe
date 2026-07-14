// scope.js — the active organization/group context for admins.
//
// Resolves which orgs/groups the current user may act within, persists the
// active selection (sessionStorage, so it carries across pages), and notifies
// listeners when it changes. Scoped pages read Scope.activeOrgId / activeGroupId
// and pass them to the API; the shared scope bar (ui.js) renders the switcher.
//
// REQUIRES auth.js and api.js loaded first.

'use strict';

const Scope = (() => {
  const KEYS = { org: 'as_active_org', group: 'as_active_group' };

  const state = {
    ready: false,
    isSuperAdmin: false,
    orgs: [],            // [{ id, name, groups: [{ id, name }] }]
    activeOrgId: null,
    activeGroupId: null, // null = "all groups"
  };

  const listeners = [];

  function activeOrg()    { return state.orgs.find(o => o.id === state.activeOrgId) || null; }
  function activeGroups() { return activeOrg()?.groups || []; }
  function activeGroup()  { return activeGroups().find(g => g.id === state.activeGroupId) || null; }

  function snapshot() {
    return {
      ready: state.ready,
      isSuperAdmin: state.isSuperAdmin,
      orgs: state.orgs,
      org: activeOrg(),
      group: activeGroup(),
      activeOrgId: state.activeOrgId,
      activeGroupId: state.activeGroupId,
    };
  }

  function onChange(fn) { if (typeof fn === 'function') listeners.push(fn); }
  function emit() {
    const snap = snapshot();
    listeners.forEach(fn => { try { fn(snap); } catch (err) { console.error('[scope]', err); } });
  }

  function setOrg(orgId) {
    if (orgId === state.activeOrgId) return;
    state.activeOrgId = orgId || null;
    if (orgId) sessionStorage.setItem(KEYS.org, orgId);
    else sessionStorage.removeItem(KEYS.org);
    // Group belongs to an org, so switching orgs clears it.
    state.activeGroupId = null;
    sessionStorage.removeItem(KEYS.group);
    emit();
  }

  function setGroup(groupId) {
    state.activeGroupId = groupId || null;
    if (groupId) sessionStorage.setItem(KEYS.group, groupId);
    else sessionStorage.removeItem(KEYS.group);
    emit();
  }

  function mapGroups(groups) {
    return (groups || []).map(g => ({ id: g.id, name: g.name || g.id }));
  }

  // The org where the user holds the strongest admin role (null if none / supers,
  // whose orgs carry no per-org adminLevel). Used to pick a sensible default org.
  function bestAdminOrgId(orgs) {
    let best = null, bestRank = 0;
    (orgs || []).forEach(o => {
      const rank = Auth.adminRank(o.adminLevel);
      if (rank > bestRank) { bestRank = rank; best = o.id; }
    });
    return best;
  }

  // Resolve the orgs/groups this user can act within.
  //   SuperAdmin  → every tenant (with its groups)
  //   Org admin   → their own org (name + groups from the tenant record)
  //   Student     → their single implicit org, no switching
  async function init(currentUser) {
    const user = currentUser || await Api.Users.getMe();
    const level = user.adminLevel || 'Student';
    state.isSuperAdmin = level === 'SuperAdmin';

    if (state.isSuperAdmin) {
      // Supers can act in any tenant.
      const tenants = await Api.Admin.getTenants().catch(() => []);
      state.orgs = (tenants || []).map(t => ({
        id: t.id,
        name: t.name || t.id,
        groups: mapGroups(t.groups),
        raw: t,
      }));
    } else {
      // Everyone else: the orgs they actually hold a membership in (multi-org).
      const memberships = await Api.Memberships.list().catch(() => []);
      state.orgs = (memberships || []).map(m => {
        let groups = mapGroups(m.groups);
        // Group/Event admins (below OrganizationAdmin) only see the groups they
        // administer; an OrganizationAdmin manages all of the org's groups.
        const rank = Auth.adminRank(m.adminLevel);
        if (rank > 0 && rank < Auth.adminRank('OrganizationAdmin') && (m.groupIds || []).length) {
          const own = new Set(m.groupIds);
          groups = groups.filter(g => own.has(g.id));
        }
        return {
          id: m.organizationId,
          // Name only — never fall back to organizationId, which would put a raw tenant
          // GUID in the org switcher. The API omits memberships whose org no longer
          // exists, so anything reaching here has a real name.
          name: m.organizationName,
          groups,
          adminLevel: m.adminLevel,
          // Minimal tenant record for admin-backend's settings form.
          raw: { id: m.organizationId, name: m.organizationName, status: m.status, rbacEnabled: m.rbacEnabled, allowGroupAdminAddVolunteers: m.allowGroupAdminAddVolunteers },
        };
      });
    }

    const savedOrg = sessionStorage.getItem(KEYS.org);
    // Default to the org where the user holds the STRONGEST admin role, so admin
    // pages open on an org they can actually manage rather than, say, a volunteer-only
    // home org (which would render an unusable/empty admin view until they switch).
    state.activeOrgId = state.orgs.find(o => o.id === savedOrg)?.id
      || bestAdminOrgId(state.orgs)
      || state.orgs[0]?.id || null;

    const savedGroup = sessionStorage.getItem(KEYS.group);
    state.activeGroupId = activeGroups().find(g => g.id === savedGroup)?.id || null;

    state.ready = true;
    emit();
    return snapshot();
  }

  return {
    init,
    onChange,
    snapshot,
    setOrg,
    setGroup,
    activeOrg,
    activeGroup,
    activeGroups,
    get ready()        { return state.ready; },
    get isSuperAdmin() { return state.isSuperAdmin; },
    get activeOrgId()  { return state.activeOrgId; },
    get activeGroupId(){ return state.activeGroupId; },
  };
})();
