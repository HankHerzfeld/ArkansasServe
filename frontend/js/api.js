// api.js — all calls to the /api/* backend
// Automatically attaches the auth token to every request.

'use strict';

const API_BASE = '/api';

const Api = (() => {
  async function request(method, path, body = null) {
    const token = Auth.getAccessToken();
    const headers = {};
    if (token) headers['Authorization'] = `Bearer ${token}`;
    if (body !== null) headers['Content-Type'] = 'application/json';

    const options = { method, headers, cache: 'no-store' };
    if (body !== null) options.body = JSON.stringify(body);

    let res;
    try {
      res = await fetch(`${API_BASE}${path}`, options);
    } catch {
      throw new Error('Network error. Please check your connection and try again.');
    }

    if (res.status === 401) { Auth.login(); throw new Error('Authentication required'); }
    if (!res.ok) {
      const text = await res.text();
      let errorMessage = `Request failed: ${res.status}`;
      if (text) {
        try {
          const parsed = JSON.parse(text);
          errorMessage = parsed.error || parsed.message || errorMessage;
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
    listOrgEvents: ()           => request('GET',  '/org/events'),
    get:           (id, orgId)  => request('GET',  `/events/${encodeURIComponent(id)}${orgId ? `?organizationId=${encodeURIComponent(orgId)}` : ''}`),
    create:        (data)       => request('POST', '/events', data),
    update:        (id, data)   => request('PUT',  `/events/${encodeURIComponent(id)}`, data),
    registrations: (id)         => request('GET',  `/events/${encodeURIComponent(id)}/registrations`),
    uploadToken:   (fileName)   => request('POST', '/events/upload-token', { fileName }),
  };

  // ── Registrations ─────────────────────────────────────────────────────────
  const Registrations = {
    create: (eventId, organizationId) => request('POST', '/registrations', { eventId, organizationId }),
    cancel: (id, eventId)             => {
      if (!eventId) throw new Error('eventId is required to cancel registration');
      return request('DELETE', `/registrations/${encodeURIComponent(id)}?eventId=${encodeURIComponent(eventId)}`);
    },
  };

  // ── Service Logs ──────────────────────────────────────────────────────────
  const ServiceLogs = {
    create:   (data)           => request('POST',  '/servicelogs', data),
    review:   (id, studentId, status, note) =>
                                  request('PATCH', `/servicelogs/${encodeURIComponent(id)}`, { studentId, status, reviewNote: note }),
    myLogs:   ()               => request('GET',   '/students/me/servicelogs'),
  };

  // ── Approvals ─────────────────────────────────────────────────────────────
  const Approvals = {
    list: () => request('GET', '/approvals'),
  };

  // ── Admin ─────────────────────────────────────────────────────────────────
  const Admin = {
    getTenants:    ()     => request('GET',  '/admin/tenants'),
    createTenant:  (data) => request('POST', '/admin/tenants', data),
  };

  // ── Admin Backend ─────────────────────────────────────────────────────────
  const AdminBackend = {
    context:          ()                        => request('GET',  '/admin/backend/context'),
    users:            ()                        => request('GET',  '/admin/backend/users'),
    updateUserAccess: (id, data)                => request('PATCH', `/admin/backend/users/${encodeURIComponent(id)}/access`, data),
    tenantGroups:     (tenantId)                => request('GET',  `/admin/backend/tenants/${encodeURIComponent(tenantId)}/groups`),
    createTenantGroup:(tenantId, data)          => request('POST', `/admin/backend/tenants/${encodeURIComponent(tenantId)}/groups`, data),
    updateTenant:     (tenantId, data)          => request('PATCH', `/admin/backend/tenants/${encodeURIComponent(tenantId)}`, data),
    demoUsers:        ()                        => request('GET',  '/admin/backend/demo-users'),
    resetDemoUsers:   ()                        => request('POST', '/admin/backend/demo-users/reset'),
  };

  return { Users, Events, Registrations, ServiceLogs, Approvals, Admin, AdminBackend };
})();
