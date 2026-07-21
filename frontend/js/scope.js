// scope.js — the active organization/group context for admins.
//
// Resolves which orgs/groups the current user may act within, persists the
// active selection (sessionStorage, so it carries across pages), and notifies
// listeners when it changes. Scoped pages read Scope.activeOrgId / activeGroupId
// and pass them to the API; the shared scope bar (ui.js) renders the switcher.
//
// PER-PAGE SCOPE. `init` takes the calling page's scope declaration (ui.js's
// PAGE_SCOPE table) rather than resolving one global list for every page. The
// switcher should offer exactly the orgs the page can actually be USED in:
//
//   minRole   — drop orgs where the person lacks the page's minimum role. Without
//               this, someone who is an OrganizationAdmin at their school and a
//               plain volunteer elsewhere is offered the volunteer org on an admin
//               page, and picking it yields an empty view that looks broken. (The
//               existing `bestAdminOrgId` default already avoids *landing* there —
//               it just never stopped the list offering it.)
//   orgTypes  — drop orgs of the wrong KIND for the page. Approvals is School/JDC
//               work: a Community Organization has no approval queue, so offering
//               one is offering an empty page.
//   allTenants— whether a SuperAdmin's list is every tenant or just their own
//               memberships. Supers need reach into any org on the admin pages, so
//               this stays true there; it exists so a page can say otherwise.
//
// REQUIRES auth.js and api.js loaded first.

'use strict';

const Scope = (() => {
  const KEYS = { org: 'as_active_org', group: 'as_active_group' };

  // What a page gets when it declares nothing: today's behaviour, unfiltered.
  const DEFAULT_CONFIG = { minRole: null, orgTypes: null, allTenants: true };

  const state = {
    ready: false,
    isSuperAdmin: false,
    orgs: [],            // [{ id, name, groups: [{ id, name }] }]
    activeOrgId: null,
    activeGroupId: null, // null = "all groups"
    config: DEFAULT_CONFIG,
    // True when this page's declaration filtered every org away — distinct from
    // "you administer nothing". The page needs to say WHY it is empty.
    filteredEmpty: false,
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
      config: state.config,
      filteredEmpty: state.filteredEmpty,
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

  // Does this org's KIND match what the page works on?
  //
  // An org whose type is UNKNOWN always passes. A membership-based admin's record carries no
  // `type` at all (only the tenant doc has it, and non-supers read memberships), so filtering
  // unknowns out would hide a school's own admin from their school — the exact Finding 9 trap
  // of trusting a field that legitimately isn't there. Same rule `policyAppliesToActiveOrg`
  // already uses in admin-portal.js.
  function typeAllowed(org, orgTypes) {
    if (!orgTypes) return true;
    const type = org?.raw?.type;
    if (!type) return true;
    if (orgTypes === 'schoolLike') return !Taxonomy.isOrganization(type);
    if (orgTypes === 'organization') return Taxonomy.isOrganization(type);
    return true;
  }

  // Does the person hold at least the page's minimum role here?
  //
  // A SuperAdmin passes everywhere: their power is global and their tenant entries carry no
  // per-org adminLevel to compare against.
  function roleAllowed(org, minRole) {
    if (!minRole || state.isSuperAdmin) return true;
    return Auth.adminRank(org.adminLevel) >= Auth.adminRank(minRole);
  }

  // Resolve the orgs/groups this user can act within, narrowed to what this PAGE needs.
  //   SuperAdmin  → every tenant (with its groups), unless the page says allTenants:false
  //   Org admin   → the orgs they hold the page's minimum role in
  //   Student     → their single implicit org, no switching
  async function init(currentUser, config) {
    state.config = { ...DEFAULT_CONFIG, ...(config || {}) };
    const { minRole, orgTypes, allTenants } = state.config;

    const user = currentUser || await Api.Users.getMe();
    const level = user.adminLevel || 'Student';
    state.isSuperAdmin = level === 'SuperAdmin';

    if (state.isSuperAdmin && allTenants) {
      // Every tenant, Arkansas Serve included. It was filtered out while a SECOND browsable
      // "Arkansas Serve" org existed and the two appeared as identical entries; there is now
      // only one, so hiding it would hide a real org a super may need to scope to.
      const tenants = await Api.Admin.getTenants().catch(() => []);
      state.orgs = (tenants || [])
        .map(t => ({
          id: t.id,
          name: t.name || t.id,
          groups: mapGroups(t.groups),
          raw: t,
        }));
    } else {
      // Everyone else — and a super on a page that declared allTenants:false — the orgs they
      // actually hold a membership in (multi-org).
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
          // Minimal tenant record for admin-backend's settings form. `type` is carried so the
          // per-page `orgTypes` filter works for membership-based admins, not only supers.
          raw: { id: m.organizationId, name: m.organizationName, type: m.type, status: m.status, rbacEnabled: m.rbacEnabled, allowGroupAdminAddVolunteers: m.allowGroupAdminAddVolunteers },
        };
      });
    }

    // Narrow to what THIS page can be used in. Done after resolution rather than inside each
    // branch so the two paths (tenants vs memberships) can never drift apart on the rule.
    const resolved = state.orgs;
    state.orgs = resolved.filter(o => roleAllowed(o, minRole) && typeAllowed(o, orgTypes));
    // "The page filtered everything away" is a different message from "you belong to nothing",
    // so record which happened instead of leaving the page to guess from an empty list.
    state.filteredEmpty = resolved.length > 0 && state.orgs.length === 0;

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
    get filteredEmpty(){ return state.filteredEmpty; },
    get activeOrgId()  { return state.activeOrgId; },
    get activeGroupId(){ return state.activeGroupId; },
  };
})();
