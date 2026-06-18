// auth.js — Entra External ID authentication
// All pages import this. Call Auth.init() on page load.

const Auth = (() => {
  // ── Config ────────────────────────────────────────────────────────────────
  // Replace these with your real Entra External ID values after app registration
  const TENANT_ID    = '434cf17d-6ab5-48c3-be4a-5541ed0e74d0';
  const CLIENT_ID    = '16150d6e-7d28-4c6b-91b3-4ec839fff75f';
  const REDIRECT_URI = 'https://www.arkansasserve.com/auth-callback.html';
  const SCOPES       = 'openid profile email api://arkansas-serve/user_impersonation';

  const AUTH_ENDPOINT =
    `https://${TENANT_ID}.ciamlogin.com/${TENANT_ID}/oauth2/v2.0/authorize`;
  const TOKEN_ENDPOINT =
    `https://${TENANT_ID}.ciamlogin.com/${TENANT_ID}/oauth2/v2.0/token`;

  // ── Storage keys ──────────────────────────────────────────────────────────
  const KEYS = {
    accessToken:  'as_access_token',
    idToken:      'as_id_token',
    expiresAt:    'as_expires_at',
    userProfile:  'as_user_profile',
    codeVerifier: 'as_code_verifier',
  };

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
    const state    = params.get('state') || '/dashboard.html';
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
      sessionStorage.setItem(KEYS.idToken,     tokens.id_token ?? '');
      sessionStorage.setItem(KEYS.expiresAt,   String(Date.now() + tokens.expires_in * 1000));
      sessionStorage.removeItem(KEYS.codeVerifier);

      // Decode id_token for profile (no signature check needed client-side;
      // server validates the access token on every API call)
      const payload = JSON.parse(atob(tokens.id_token.split('.')[1]));
      sessionStorage.setItem(KEYS.userProfile, JSON.stringify({
        userId:      payload.oid || payload.sub,
        name:        payload.name || payload.preferred_username,
        email:       payload.email || payload.preferred_username,
        role:        payload.extension_Role || payload.roles?.[0] || 'Student',
        tenantId:    payload.tid,
      }));

      window.location.href = state;
    } catch (err) {
      console.error('Auth callback error:', err);
      window.location.href = '/index.html?error=auth_failed';
    }
  }

  // ── Logout ────────────────────────────────────────────────────────────────
  function logout() {
    Object.values(KEYS).forEach(k => sessionStorage.removeItem(k));
    const params = new URLSearchParams({
      client_id:              CLIENT_ID,
      post_logout_redirect_uri: 'https://www.arkansasserve.com/index.html',
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

  function getProfile() {
    const raw = sessionStorage.getItem(KEYS.userProfile);
    return raw ? JSON.parse(raw) : null;
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

  return { login, logout, handleCallback, requireAuth, isAuthenticated, getProfile, getAccessToken, init };
})();
