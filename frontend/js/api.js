// api.js — all calls to the /api/* backend
// Automatically attaches the auth token to every request.

'use strict';

const API_BASE = '/api';

const Api = (() => {
  async function request(method, path, body = null) {
    // MSAL token acquisition is async (silent renew under the hood).
    // Null means no session or interaction required — send the user to login.
    const token = await Auth.getAccessToken();
    if (!token) {
      Auth.login();
      throw new Error('Authentication required');
    }

    const headers = {};
    headers['Authorization'] = `Bearer ${token}`;
    if (body !== null) headers['Content-Type'] = 'application/json';
    // #26: when a SuperAdmin is "acting as" another user, every call carries the
    // opaque session id; the backend resolves the effective (target) context.
    const impSid = Auth.getImpersonationSid && Auth.getImpersonationSid();
    if (impSid) headers['X-Impersonation-Session'] = impSid;

    const options = { method, headers, cache: 'no-store' };
    if (body !== null) options.body = JSON.stringify(body);

    let res;
    try {
      res = await fetch(`${API_BASE}${path}`, options);
    } catch {
      throw new Error('Network error. Please check your connection and try again.');
    }

    if (res.status === 401) {
      const lastLoginAt = Auth.getLastLoginAttemptAt();
      const recentlyRetried = (Date.now() - lastLoginAt) < 45_000;

      Auth.clearSession();
      if (!recentlyRetried) {
        window.location.href = '/index.html?error=session_expired';
      }

      throw new Error('Authentication required');
    }
    if (!res.ok) {
      const text = await res.text();
      let errorMessage = `Request failed: ${res.status}`;
      if (text) {
        try {
          const parsed = JSON.parse(text);
          errorMessage = parsed.error || parsed.message || errorMessage;
          // #26: the impersonation session ended server-side (expired/revoked) while
          // the client still thought it was active. Exit cleanly rather than letting
          // the operator keep acting as themselves behind a stale "viewing as" banner.
          if (res.status === 409 && parsed.code === 'impersonation_expired') {
            Auth.clearImpersonation();
            window.location.href = '/admin-backend.html?impersonation=expired';
          }
        } catch {
          errorMessage = text;
        }
      }
      throw new Error(errorMessage);
    }

    const text = await res.text();
    return text ? JSON.parse(text) : null;
  }

  // ── Users ────────────────────────────────────────────────────────────────
  const Users = {
    getMe:    ()     => request('GET',  '/users/me'),
    updateMe: (data) => request('PUT',  '/users/me', data),
  };

  // ── Events ───────────────────────────────────────────────────────────────
  const Events = {
    list:          ()           => request('GET',  '/events'),
    listOrgEvents: (orgId, groupId) => {
      const qs = new URLSearchParams();
      if (orgId)   qs.set('organizationId', orgId);
      if (groupId) qs.set('groupId', groupId);
      const q = qs.toString();
      return request('GET', `/org/events${q ? `?${q}` : ''}`);
    },
    get:           (id, orgId)  => request('GET',  `/events/${encodeURIComponent(id)}${orgId ? `?organizationId=${encodeURIComponent(orgId)}` : ''}`),
    create:        (data)       => request('POST', '/events', data),
    update:        (id, data)   => request('PUT',  `/events/${encodeURIComponent(id)}`, data),
    delete:        (id, orgId)  => request('DELETE', `/events/${encodeURIComponent(id)}${orgId ? `?organizationId=${encodeURIComponent(orgId)}` : ''}`),
    // Deletes EVERY occurrence of a recurring series. Refuses with a 409 naming how many
    // people are signed up unless force is true — so the confirmation can quote the real
    // number instead of guessing.
    deleteSeries:  (seriesId, orgId, force = false) =>
      request('DELETE', `/events/series/${encodeURIComponent(seriesId)}?organizationId=${encodeURIComponent(orgId)}${force ? '&force=true' : ''}`),
    // Dates a recurrence rule WOULD generate, without creating anything. Server-side on
    // purpose: it is the same expander that will run, so the preview cannot drift from the
    // result — which matters because the hard part (DST) is invisible until it is wrong.
    previewRecurrence: (data) => request('POST', '/events/preview-recurrence', data),
    registrations: (id)         => request('GET',  `/events/${encodeURIComponent(id)}/registrations`),
    uploadToken:   (fileName)   => request('POST', '/events/upload-token', { fileName }),
    // SuperAdmin: run the past-event archival sweep now instead of waiting for the nightly
    // timer. Returns { archived, at }.
    archivePastNow: ()          => request('POST', '/manage/events/archive-past'),
    match:         (orgId, q)   => {
      const qs = new URLSearchParams();
      if (orgId) qs.set('organizationId', orgId);
      if (q)     qs.set('q', q);
      return request('GET', `/manage/events/match?${qs.toString()}`);
    },
  };

  // ── Registrations ─────────────────────────────────────────────────────────
  const Registrations = {
    // SuperAdmin repair: recompute an event's currentSlots + per-shift filled from the
    // registrations that actually exist. Returns { changed, activeRegistrations, before, after }.
    reconcileSlots: (eventId, orgId) =>
      request('POST', `/manage/backend/events/${encodeURIComponent(eventId)}/reconcile-slots?organizationId=${encodeURIComponent(orgId)}`),
    // data: { eventId, organizationId, shiftId?, answers? }
    create: (data) => request('POST', '/registrations', data),
    // Sign several roster members up at once. All-or-nothing: if the group doesn't fit,
    // nobody is registered and the server says how many spots remain.
    // data: { eventId, organizationId, shiftId?, registrantOrganizationId,
    //         registrants: [{ memberId, answers: [{ questionId, answer }] }] }
    createGroup: (data) => request('POST', '/registrations/group', data),
    cancel: (id, eventId)             => {
      if (!eventId) throw new Error('eventId is required to cancel registration');
      return request('DELETE', `/registrations/${encodeURIComponent(id)}?eventId=${encodeURIComponent(eventId)}`);
    },
  };

  // ── Day-of check-in (#14) ─────────────────────────────────────────────────
  const CheckIn = {
    // Admin live overview: non-cancelled registrations + check-in state + shifts.
    roster:   (eventId, orgId) => request('GET', `/events/${encodeURIComponent(eventId)}/roster?organizationId=${encodeURIComponent(orgId)}`),
    // Admin marks one registrant present/absent. data: { organizationId, registrationId, checkedIn }
    set:      (eventId, data)  => request('POST', `/events/${encodeURIComponent(eventId)}/checkin`, data),
    // Admin mints/rotates the posted code; returns { code, url, expiresAt }.
    mintCode: (eventId, orgId) => request('POST', `/events/${encodeURIComponent(eventId)}/checkin/qr`, { organizationId: orgId }),
    // Student self-check-in after scanning the posted code. data: { organizationId, code }
    self:     (eventId, data)  => request('POST', `/events/${encodeURIComponent(eventId)}/checkin/self`, data),
    // Admin adds someone unregistered + checks them in.
    // data: { organizationId, firstName, lastName, email?, shiftId? }
    walkIn:   (eventId, data)  => request('POST', `/events/${encodeURIComponent(eventId)}/checkin/walkin`, data),
  };

  // ── Service categories (#10②) ─────────────────────────────────────────────
  const Categories = {
    // Effective list + aliases for dropdowns/facets: { canonical, effective, aliases }.
    list: () => request('GET', '/categories'),
  };
  const CategoryProposals = {
    // SuperAdmin queue: { pending:[...], aliasTargets:[...] }.
    list:    ()     => request('GET',  '/manage/backend/category-proposals'),
    // data: { label, action: 'approveNew'|'approveAlias'|'reject', canonical? }
    resolve: (data) => request('POST', '/manage/backend/category-proposals/resolve', data),
    // Remove a label from the vocabulary entirely (approved-new, alias, and proposal records).
    scrub:   (label) => request('POST', '/manage/backend/categories/scrub', { label }),
  };

  // ── Tags / credentials (#11②) ─────────────────────────────────────────────
  const Tags = {
    // Tenant tag DEFINITIONS (OrganizationAdmin+).
    list:      (tenantId)          => request('GET',  `/manage/backend/tenants/${encodeURIComponent(tenantId)}/user-tags`),
    create:    (tenantId, data)    => request('POST', `/manage/backend/tenants/${encodeURIComponent(tenantId)}/user-tags`, data),
    update:    (tenantId, tagId, data) => request('PUT', `/manage/backend/tenants/${encodeURIComponent(tenantId)}/user-tags/${encodeURIComponent(tagId)}`, data),
    // One member's STATE against a tag (GroupAdmin+). data: { status, note?, completedAt?, expiresAt? }
    setMember: (memberId, orgId, tagId, data) =>
      request('PUT', `/manage/volunteers/${encodeURIComponent(memberId)}/tags/${encodeURIComponent(tagId)}?organizationId=${encodeURIComponent(orgId)}`, data),
    // The CALLER's own requirements + state in one org, and signing one themselves (#19).
    // Only a tag the org marked self-attestable can be signed this way — see AttestMyTag.
    mine:   (orgId)        => request('GET',  `/manage/me/tags?organizationId=${encodeURIComponent(orgId)}`),
    attest: (tagId, orgId) => request('POST', `/manage/me/tags/${encodeURIComponent(tagId)}/attest?organizationId=${encodeURIComponent(orgId)}`),
  };

  // ── Admin oversight / assignments (#13) ───────────────────────────────────
  const Assignments = {
    // Volunteers assigned to me in this org, with my per-volunteer notify prefs.
    mine:     (orgId) => request('GET', `/manage/me/assigned-volunteers?organizationId=${encodeURIComponent(orgId)}`),
    // Edit MY own notify prefs on one assigned volunteer. data: { notifyOnHours?, notifyOnApproval? }
    setPrefs: (volunteerId, orgId, data) => request('PATCH', `/manage/me/assignments/${encodeURIComponent(volunteerId)}?organizationId=${encodeURIComponent(orgId)}`, data),
    // Send a direct in-app message to all my assigned volunteers. data: { organizationId, message }
    notify:   (data) => request('POST', '/manage/me/assigned-volunteers/notify', data),
    // The mirror of `mine`: who oversees ME, per org — [{ organizationId, admins:[{id,name,email}] }].
    overseers: () => request('GET', '/manage/me/overseers'),
  };

  // ── Geo (#16) ─────────────────────────────────────────────────────────────
  const Config = {
    // Google Maps browser key from the Function App setting. { enabled, apiKey }.
    // enabled:false when unset — callers degrade to the bundled ZIP dataset (#16).
    maps: () => request('GET', '/config/maps'),
  };

  const Geo = {
    // Resolve an AR ZIP to { zip, city, county, latitude, longitude }. Rejects on an
    // unknown/out-of-state ZIP (backend 404) so the caller can fall back to manual entry.
    lookupZip: (zip) => request('GET', `/geo/zip/${encodeURIComponent(String(zip).trim())}`),
  };

  // ── Service Logs ──────────────────────────────────────────────────────────
  const ServiceLogs = {
    create:   (data)           => request('POST',  '/servicelogs', data),
    review:   (id, studentId, status, note) =>
                                  request('PATCH', `/servicelogs/${encodeURIComponent(id)}`, { studentId, status, reviewNote: note }),
    myLogs:   ()               => request('GET',   '/students/me/servicelogs'),
    bulkCreate: (data)         => request('POST',  '/manage/servicelogs/bulk', data),
    import:     (data)         => request('POST',  '/manage/servicelogs/import', data),
    voidLog:  (id, studentId)  => request('DELETE', `/servicelogs/${encodeURIComponent(id)}?studentId=${encodeURIComponent(studentId)}`),
  };

  // ── Approvals ─────────────────────────────────────────────────────────────
  const Approvals = {
    // schoolId is honored for SuperAdmins; ignored (pinned to own school) otherwise.
    list: (schoolId) => request('GET', `/approvals${schoolId ? `?schoolId=${encodeURIComponent(schoolId)}` : ''}`),
  };

  // ── Memberships (the orgs the current person belongs to) ──────────────────
  const Memberships = {
    list:  ()      => request('GET',    '/manage/me/memberships'),
    join:  (orgId) => request('POST',   '/manage/me/memberships', { organizationId: orgId }),
    leave: (orgId) => request('DELETE', `/manage/me/memberships/${encodeURIComponent(orgId)}`),
  };

  // ── Organizations (public directory for volunteer self-service) ────────────
  const Orgs = {
    browse: ()   => request('GET', '/manage/orgs'),
    get:    (id) => request('GET', `/manage/orgs/${encodeURIComponent(id)}`),
  };

  // ── Volunteers ────────────────────────────────────────────────────────────
  const Volunteers = {
    list:   (params = {}) => {
      const qs = new URLSearchParams();
      if (params.organizationId) qs.set('organizationId', params.organizationId);
      if (params.groupId)        qs.set('groupId', params.groupId);
      const q = qs.toString();
      return request('GET', `/manage/volunteers${q ? `?${q}` : ''}`);
    },
    create: (data) => request('POST', '/manage/volunteers', data),
  };

  // ── Reports ───────────────────────────────────────────────────────────────
  const Reports = {
    // OrganizationAdmin: schoolId is derived server-side from the token (ignored here).
    // SuperAdmin: pass { schoolId } to target a specific school.
    serviceHours: (params = {}) => {
      const qs = new URLSearchParams();
      if (params.schoolId) qs.set('schoolId', params.schoolId);
      if (params.from)     qs.set('from', params.from);
      if (params.to)       qs.set('to', params.to);
      const q = qs.toString();
      return request('GET', `/manage/reports/service-hours${q ? `?${q}` : ''}`);
    },
  };

  // ── Notifications ─────────────────────────────────────────────────────────
  const Notifications = {
    list:     ()   => request('GET',   '/notifications'),
    pane:     ()   => request('GET',   '/notifications/pane'),
    markRead: (id) => request('PATCH', `/notifications/${encodeURIComponent(id)}`, { isRead: true }),
    delete:   (id) => request('DELETE', `/notifications/${encodeURIComponent(id)}`),
    clear:    ()   => request('DELETE', '/notifications'),
  };

  // ── Role matrix (SuperAdmin) ──────────────────────────────────────────────
  const Matrix = {
    // Server-side filtered + paginated: { items, continuationToken }.
    list: (params = {}) => {
      const qs = new URLSearchParams();
      if (params.organizationId)    qs.set('organizationId', params.organizationId);
      if (params.search)            qs.set('search', params.search);
      if (params.continuationToken) qs.set('continuationToken', params.continuationToken);
      if (params.pageSize)          qs.set('pageSize', params.pageSize);
      const q = qs.toString();
      return request('GET', `/manage/matrix${q ? `?${q}` : ''}`);
    },
    assign:   (data)              => request('POST',   '/manage/backend/memberships', data),
    unassign: (userId, tenantId)  => request('DELETE', `/manage/backend/memberships/${encodeURIComponent(userId)}?tenantId=${encodeURIComponent(tenantId)}`),
  };

  // ── Admin ─────────────────────────────────────────────────────────────────
  const Admin = {
    getTenants:    ()     => request('GET',  '/manage/tenants'),
    createTenant:  (data) => request('POST', '/manage/tenants', data),
    updateTenant:  (id, data)       => request('PATCH',  `/manage/backend/tenants/${encodeURIComponent(id)}`, data),
    deleteTenant:  (id, confirmName) => request('DELETE', `/manage/tenants/${encodeURIComponent(id)}?confirmName=${encodeURIComponent(confirmName)}`),
  };

  // ── Admin Backend ─────────────────────────────────────────────────────────
  const AdminBackend = {
    context:          ()                        => request('GET',  '/manage/backend/context'),
    users:            (tenantId)                => request('GET',  `/manage/backend/users${tenantId ? `?tenantId=${encodeURIComponent(tenantId)}` : ''}`),
    updateUserAccess: (id, data)                => request('PATCH', `/manage/backend/users/${encodeURIComponent(id)}/access`, data),
    tenantGroups:     (tenantId)                => request('GET',  `/manage/backend/tenants/${encodeURIComponent(tenantId)}/groups`),
    createTenantGroup:(tenantId, data)          => request('POST', `/manage/backend/tenants/${encodeURIComponent(tenantId)}/groups`, data),
    updateTenant:     (tenantId, data)          => request('PATCH', `/manage/backend/tenants/${encodeURIComponent(tenantId)}`, data),
    logoUploadToken:  (tenantId, fileName)      => request('POST', `/manage/backend/tenants/${encodeURIComponent(tenantId)}/logo-upload-token`, { fileName }),
    demoUsers:        ()                        => request('GET',  '/manage/backend/demo-users'),
    resetDemoUsers:   ()                        => request('POST', '/manage/backend/demo-users/reset'),
    // #12 school/JDC event-approval policy: { default, byOrg:{orgId:policy}, byCategory:{cat:policy} }.
    approvalPolicy:    (tenantId)       => request('GET', `/manage/backend/tenants/${encodeURIComponent(tenantId)}/approval-policy`),
    setApprovalPolicy: (tenantId, data) => request('PUT', `/manage/backend/tenants/${encodeURIComponent(tenantId)}/approval-policy`, data),
  };

  // ── Impersonation / remote access (SuperAdmin, #26) ───────────────────────
  const Impersonation = {
    start: (data) => request('POST',   '/manage/impersonation', data),
    stop:  (sid)  => request('DELETE', `/manage/impersonation/${encodeURIComponent(sid)}`),
    list:  ()     => request('GET',    '/manage/impersonation'),
  };

  // ── DB Console (SuperAdmin, read-only) ────────────────────────────────────
  const Db = {
    containers: ()                       => request('GET',  '/manage/db/containers'),
    query:      (container, query, maxItems) =>
                                            request('POST', '/manage/db/query', { container, query, maxItems }),
  };

  // ── Event Crawler (SuperAdmin) ────────────────────────────────────────────
  const Crawler = {
    /**
     * Trigger a crawl run against one or more external sources.
     * @param {string[]|null} sources - Source names to include, or null for all.
     * @param {boolean} [dryRun=false] - Fetch only; do not persist new events.
     * @returns {Promise<{imported: number, skipped: number, dryRun: boolean, errors: string[]}>}
     */
    run: (sources, dryRun = false) => request('POST', '/manage/events/crawl', { sources, dryRun }),

    /**
     * Fetch the list of crawled Draft events awaiting review.
     * @returns {Promise<Object[]>} Array of Draft Event documents.
     */
    queue: () => request('GET', '/manage/events/crawl/queue'),

    /**
     * Publish a crawled Draft event, making it visible to students.
     * @param {string} id - Cosmos document id of the Draft event.
     * @param {string|null} [organizationName] - Override the organization display name.
     * @returns {Promise<Object>} The updated Event document.
     */
    publish: (id, organizationName = null) =>
      request('POST', `/manage/events/crawl/${encodeURIComponent(id)}/publish`, { organizationName }),

    /**
     * Permanently dismiss a crawled Draft event from the review queue.
     * @param {string} id - Cosmos document id of the Draft event.
     * @returns {Promise<{dismissed: boolean}>}
     */
    dismiss: (id) => request('DELETE', `/manage/events/crawl/${encodeURIComponent(id)}`),
  };

  return { Users, Events, Registrations, CheckIn, Categories, CategoryProposals, Config, Geo, Tags, Assignments, ServiceLogs, Approvals, Reports, Notifications, Memberships, Orgs, Volunteers, Matrix, Admin, AdminBackend, Impersonation, Db, Crawler };
})();
