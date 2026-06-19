// api.js — all calls to the /api/* backend
// Automatically attaches the auth token to every request.

const API_BASE = '/api';

const Api = (() => {
  async function request(method, path, body = null) {
    const token = Auth.getAccessToken();
    const headers = { 'Content-Type': 'application/json' };
    if (token) headers['Authorization'] = `Bearer ${token}`;

    const options = { method, headers };
    if (body) options.body = JSON.stringify(body);

    const res = await fetch(`${API_BASE}${path}`, options);

    if (res.status === 401) { Auth.login(); throw new Error('Authentication required'); }
    if (!res.ok) {
      const err = await res.json().catch(() => ({ error: `HTTP ${res.status}` }));
      throw new Error(err.error || `Request failed: ${res.status}`);
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
    list:          (schoolId)   => request('GET',  `/events${schoolId ? `?schoolId=${schoolId}` : ''}`),
    listOrgEvents: ()           => request('GET',  '/org/events'),
    get:           (id, orgId)  => request('GET',  `/events/${id}?organizationId=${orgId}`),
    create:        (data)       => request('POST', '/events', data),
    update:        (id, data)   => request('PUT',  `/events/${id}`, data),
    registrations: (id)         => request('GET',  `/events/${id}/registrations`),
    uploadToken:   (fileName)   => request('POST', '/events/upload-token', { fileName }),
  };

  // ── Registrations ─────────────────────────────────────────────────────────
  const Registrations = {
    create: (eventId, organizationId) => request('POST', '/registrations', { eventId, organizationId }),
    cancel: (id)                      => request('DELETE', `/registrations/${id}`),
  };

  // ── Service Logs ──────────────────────────────────────────────────────────
  const ServiceLogs = {
    create:   (data)           => request('POST',  '/servicelogs', data),
    review:   (id, studentId, status, note) =>
                                  request('PATCH', `/servicelogs/${id}`, { studentId, status, reviewNote: note }),
    myLogs:   ()               => request('GET',   '/students/me/servicelogs'),
  };

  // ── Approvals ─────────────────────────────────────────────────────────────
  const Approvals = {
    list: (schoolId) => request('GET', `/approvals${schoolId ? `?schoolId=${schoolId}` : ''}`),
  };

  // ── Admin ─────────────────────────────────────────────────────────────────
  const Admin = {
    getTenants:    ()     => request('GET',  '/admin/tenants'),
    createTenant:  (data) => request('POST', '/admin/tenants', data),
  };

  return { Users, Events, Registrations, ServiceLogs, Approvals, Admin };
})();
