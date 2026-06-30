// auth.js — Entra External ID authentication
// All pages import this. Call Auth.init() on page load.

'use strict';

const Auth = (() => {
  // ── Config ────────────────────────────────────────────────────────────────
  // Replace these with your real Entra External ID values after app registration
  const TENANT_ID    = '2d72a425-cd59-4b55-a8d6-a67c1ed565c6';
  const CLIENT_ID    = '16150d6e-7d28-4c6b-91b3-4ec839fff75f';
  const REDIRECT_URI = `${window.location.origin}/auth-callback.html`;
  const SCOPES       = 'openid profile email api://16150d6e-7d28-4c6b-91b3-4ec839fff75f/User_Impersonation';

  const AUTH_ENDPOINT =
    `https://${TENANT_ID}.ciamlogin.com/${TENANT_ID}/oauth2/v2.0/authorize`;
  const TOKEN_ENDPOINT =
    `https://${TENANT_ID}.ciamlogin.com/${TENANT_ID}/oauth2/v2.0/token`;

  // ── Storage keys ──────────────────────────────────────────────────────────
  const KEYS = {
    accessToken:  'as_access_token',
    expiresAt:    'as_expires_at',
    codeVerifier: 'as_code_verifier',
    appRole:      'as_app_role',
  };

  const ROLE_RANK = Object.freeze({
    Student: 0,
    OrgStaff: 1,
    SchoolAdmin: 2,
    PlatformAdmin: 3,
  });

  function mapAdminLevelToRole(adminLevel) {
    if (adminLevel === 'SuperAdmin') return 'PlatformAdmin';
    if (adminLevel === 'OrganizationAdmin') return 'SchoolAdmin';
    if (adminLevel === 'GroupAdmin' || adminLevel === 'EventAdmin') return 'OrgStaff';
    return 'Student';
  }

  function normalizeRole(role) {
    if (!role) return 'Student';
    return Object.hasOwn(ROLE_RANK, role) ? role : 'Student';
  }

  function strongestRole(a, b) {
    const roleA = normalizeRole(a);
    const roleB = normalizeRole(b);
    return ROLE_RANK[roleA] >= ROLE_RANK[roleB] ? roleA : roleB;
  }

  function decodeJwtPayload(token) {
    if (!token) return null;
    const parts = token.split('.');
    if (parts.length < 2) return null;

    try {
      const base64 = parts[1].replace(/-/g, '+').replace(/_/g, '/');
      const padded = base64.padEnd(base64.length + (4 - base64.length % 4) % 4, '=');
      const json = decodeURIComponent(atob(padded).split('').map(c =>
        '%' + ('00' + c.charCodeAt(0).toString(16)).slice(-2)).join(''));
      return JSON.parse(json);
    } catch {
      return null;
    }
  }

  // ── PKCE helpers ─────────────────────────────────────────────────────────
  function generateRandomString(length = 64) {
    const chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~';
    const arr   = new Uint8Array(length);
    crypto.getRandomValues(arr);
    return Array.from(arr, b => chars[b % chars.length]).join('');
  }

  async function sha256(plain) {
    const encoder = new TextEncoder();
    const data    = encoder.encode(plain);
    return crypto.subtle.digest('SHA-256', data);
  }

  function base64UrlEncode(buffer) {
    return btoa(String.fromCharCode(...new Uint8Array(buffer)))
      .replace(/\+/g, '-').replace(/\//g, '_').replace(/=/g, '');
  }

  // ── Login ─────────────────────────────────────────────────────────────────
  async function login() {
    sessionStorage.setItem(KEYS.lastLoginAt, String(Date.now()));

    const verifier  = generateRandomString(64);
    const challenge = base64UrlEncode(await sha256(verifier));
    sessionStorage.setItem(KEYS.codeVerifier, verifier);

    const params = new URLSearchParams({
      client_id:             CLIENT_ID,
      response_type:         'code',
      redirect_uri:          REDIRECT_URI,
      scope:                 SCOPES,
      code_challenge:        challenge,
      code_challenge_method: 'S256',
      response_mode:         'query',
      state:                 window.location.pathname, // return to current page after login
    });

    window.location.href = `${AUTH_ENDPOINT}?${params}`;
  }

  // ── Handle auth callback (call from auth-callback.html) ──────────────────
  async function handleCallback() {
    const params   = new URLSearchParams(window.location.search);
    const code     = params.get('code');
    const rawState = params.get('state') || '/dashboard.html';
    // Prevent open redirects: only allow same-origin relative paths
    let validatedState = '/dashboard.html';
    try {
      const stateUrl = new URL(rawState, window.location.origin);
      if (stateUrl.origin === window.location.origin) {
        validatedState = stateUrl.pathname + stateUrl.search + stateUrl.hash;
      }
    } catch { /* fall through to default */ }
    const verifier = sessionStorage.getItem(KEYS.codeVerifier);

    if (!code || !verifier) {
      console.error('Auth callback missing code or verifier');
      window.location.href = '/index.html';
      return;
    }

    try {
      const body = new URLSearchParams({
        client_id:     CLIENT_ID,
        grant_type:    'authorization_code',
        code,
        redirect_uri:  REDIRECT_URI,
        code_verifier: verifier,
      });

      const res  = await fetch(TOKEN_ENDPOINT, {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
        body: body.toString(),
      });

      if (!res.ok) throw new Error(`Token exchange failed: ${res.status}`);
      const tokens = await res.json();

      // Store tokens
      sessionStorage.setItem(KEYS.accessToken, tokens.access_token);
      sessionStorage.setItem(KEYS.expiresAt,   String(Date.now() + tokens.expires_in * 1000));
      sessionStorage.removeItem(KEYS.codeVerifier);

      window.location.href = validatedState;
    } catch (err) {
      console.error('Auth callback error:', err);
      window.location.href = '/index.html?error=auth_failed';
    }
  }

  // ── Logout ────────────────────────────────────────────────────────────────
  function logout() {
    clearSession();
    const params = new URLSearchParams({
      client_id:              CLIENT_ID,
      post_logout_redirect_uri: `${window.location.origin}/index.html`,
    });
    window.location.href =
      `https://${TENANT_ID}.ciamlogin.com/${TENANT_ID}/oauth2/v2.0/logout?${params}`;
  }

  // ── Token access ──────────────────────────────────────────────────────────
  function getAccessToken() {
    const token     = sessionStorage.getItem(KEYS.accessToken);
    const expiresAt = Number(sessionStorage.getItem(KEYS.expiresAt) || 0);
    if (!token || Date.now() >= expiresAt) return null;
    return token;
  }

  function clearSession() {
    Object.values(KEYS).forEach(k => sessionStorage.removeItem(k));
  }

  function getLastLoginAttemptAt() {
    return Number(sessionStorage.getItem(KEYS.lastLoginAt) || 0);
  }

  function getProfile() {
    const token = getAccessToken();
    const payload = decodeJwtPayload(token);
    if (!payload) return null;

    const tokenRole = payload.extension_Role || payload.roles?.[0] || 'Student';
    const cachedRole = sessionStorage.getItem(KEYS.appRole);
    const role = strongestRole(tokenRole, cachedRole);

    return {
      name: payload.name || payload.preferred_username || 'User',
      role,
      email: payload.email || payload.preferred_username || '',
    };
  }

  function setResolvedRoleFromUser(user) {
    if (!user) return;
    const roleFromUser = user.role || mapAdminLevelToRole(user.adminLevel);
    const role = normalizeRole(roleFromUser);
    sessionStorage.setItem(KEYS.appRole, role);
  }

  function isAuthenticated() {
    return !!getAccessToken();
  }

  // ── Route guard ───────────────────────────────────────────────────────────
  // Call on every protected page. Redirects to login if not authenticated.
  // Optionally checks for a required role.
  function requireAuth(requiredRole = null) {
    if (!isAuthenticated()) {
      login();
      return null;
    }
    const profile = getProfile();
    if (requiredRole && profile?.role !== requiredRole && profile?.role !== 'PlatformAdmin') {
      window.location.href = '/dashboard.html';
      return null;
    }
    return profile;
  }

  // ── Init (call on every page load) ───────────────────────────────────────
  function init() {
    const profile = getProfile();
    const navName = document.getElementById('nav-user-name');
    const navRole = document.getElementById('nav-user-role');
    if (navName && profile) navName.textContent = profile.name;
    if (navRole && profile) navRole.textContent = profile.role;

    document.getElementById('btn-logout')?.addEventListener('click', logout);
    document.getElementById('btn-login')?.addEventListener('click', login);
  }

  return { login, logout, handleCallback, requireAuth, isAuthenticated, getProfile, getAccessToken, setResolvedRoleFromUser, init };
})();
