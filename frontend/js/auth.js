// auth.js — Entra External ID authentication (MSAL.js redirect flow)
//
// REQUIRES /js/vendor/msal-browser.min.js to be loaded BEFORE this file
// (exposes the global `msal` namespace).
//
// Tenant: arkansasserve.onmicrosoft.com — Entra External ID (CIAM) directory.
// Sign-IN and sign-UP both go through the tenant's sign-up/sign-in user flow;
// signUp() deep-links straight into account creation with prompt=create.
//
// Page contract (all async where marked):
//   Auth.init()                    async — wire nav/buttons; call on every page
//   Auth.ready()                   async — resolves when MSAL is initialized
//   Auth.login(returnTo?)          async — redirect to sign-in
//   Auth.signUp(returnTo?)         async — redirect straight to account creation
//   Auth.logout()                  async — redirect logout
//   Auth.handleCallback()          async — ONLY on /auth-callback.html
//   Auth.requireAuth(minAdminLevel?) async — resolves profile or null (redirects)
//   Auth.getAccessToken()          async — resolves token string or null
//   Auth.isAuthenticated()         sync (valid after ready)
//   Auth.getProfile()              sync (valid after ready; { name, email, adminLevel })
//   Auth.setResolvedLevelFromUser() sync
//   Auth.clearSession()            sync
//   Auth.getLastLoginAttemptAt()   sync

'use strict';

const Auth = (() => {
  // ── Config ────────────────────────────────────────────────────────────────
  // Entra External ID tenant: arkansasserve.onmicrosoft.com
  const TENANT_ID        = '434cf17d-6ab5-48c3-be4a-5541ed0e74d0';
  const TENANT_SUBDOMAIN = 'arkansasserve';
  // "Arkansas Serve Web" SPA registration in arkansasserve.onmicrosoft.com.
  // Must match Entra__ClientId on the Function App.
  const CLIENT_ID        = '21ceb138-99f1-40b6-a73d-01965a1b6f06';

  const AUTHORITY    = `https://${TENANT_SUBDOMAIN}.ciamlogin.com/${TENANT_ID}/`;
  const API_SCOPES   = [`api://${CLIENT_ID}/User_Impersonation`];
  const LOGIN_SCOPES = ['openid', 'profile', 'email', ...API_SCOPES];
  const REDIRECT_URI = `${window.location.origin}/auth-callback.html`;

  // ── Storage keys (ours; MSAL manages its own msal.* keys) ─────────────────
  const KEYS = {
    lastLoginAt: 'as_last_login_at',
    adminLevel:  'as_admin_level',
    impersonation: 'as_impersonation',
  };

  // ── Admin level model (the single 5-level hierarchy) ──────────────────────
  const ADMIN_RANK = Object.freeze({
    Student: 0,
    EventAdmin: 1,
    GroupAdmin: 2,
    OrganizationAdmin: 3,
    SuperAdmin: 4,
  });

  // Boundary adapter for the Entra token's legacy `extension_Role`/`roles` claim,
  // which still speaks the old 4-role vocabulary. The only place legacy role
  // names survive on the client — retire once CIAM emits adminLevel directly.
  function claimRoleToAdminLevel(role) {
    if (role === 'PlatformAdmin') return 'SuperAdmin';
    if (role === 'SchoolAdmin')   return 'OrganizationAdmin';
    if (role === 'OrgStaff')      return 'EventAdmin';
    return 'Student';
  }

  function normalizeLevel(level) {
    return Object.hasOwn(ADMIN_RANK, level) ? level : 'Student';
  }

  function adminRank(level) {
    return Object.hasOwn(ADMIN_RANK, level) ? ADMIN_RANK[level] : 0;
  }

  function strongestLevel(a, b) {
    const la = normalizeLevel(a);
    const lb = normalizeLevel(b);
    return ADMIN_RANK[la] >= ADMIN_RANK[lb] ? la : lb;
  }

  // Resolved adminLevel: the precise value cached from /users/me if present,
  // otherwise derived from the token's (legacy) role claim.
  function getAdminLevel() {
    const cached = sessionStorage.getItem(KEYS.adminLevel);
    if (cached && Object.hasOwn(ADMIN_RANK, cached)) return cached;
    return getProfile()?.adminLevel || 'Student';
  }

  // ── MSAL instance ─────────────────────────────────────────────────────────
  if (CLIENT_ID === 'ENTER-NEW-CLIENT-ID') {
    console.error('[Arkansas Serve] auth.js: CLIENT_ID has not been configured.');
  }

  const msalInstance = new msal.PublicClientApplication({
    auth: {
      clientId: CLIENT_ID,
      authority: AUTHORITY,
      knownAuthorities: [`${TENANT_SUBDOMAIN}.ciamlogin.com`],
      redirectUri: REDIRECT_URI,
      postLogoutRedirectUri: '/index.html',
      // We always land on the dedicated callback page and navigate from state.
      navigateToLoginRequestUrl: false,
    },
    cache: {
      // sessionStorage only — repo privacy rule: no tokens in localStorage.
      cacheLocation: 'sessionStorage',
      storeAuthStateInCookie: false,
    },
    system: {
      loggerOptions: {
        logLevel: msal.LogLevel.Warning,
        loggerCallback: (_level, message, containsPii) => {
          if (!containsPii) console.warn('[Arkansas Serve auth]', message);
        },
      },
    },
  });

  // Kick off initialization once at script load; everything awaits this.
  const _ready = msalInstance.initialize();

  function ready() {
    return _ready;
  }

  function getAccount() {
    const active = msalInstance.getActiveAccount();
    if (active) return active;
    const all = msalInstance.getAllAccounts();
    if (all.length > 0) {
      msalInstance.setActiveAccount(all[0]);
      return all[0];
    }
    return null;
  }

  // Prevent open redirects: only same-origin relative paths are honored.
  function validateReturnPath(raw, fallback = '/dashboard.html') {
    try {
      const url = new URL(raw, window.location.origin);
      if (url.origin === window.location.origin) {
        return url.pathname + url.search + url.hash;
      }
    } catch { /* fall through */ }
    return fallback;
  }

  // ── Sign-in / Sign-up / Logout ────────────────────────────────────────────
  async function login(returnTo) {
    sessionStorage.setItem(KEYS.lastLoginAt, String(Date.now()));
    await _ready;
    await msalInstance.loginRedirect({
      scopes: LOGIN_SCOPES,
      state: returnTo || window.location.pathname,
    });
  }

  // Deep-links directly into the user flow's account-creation experience.
  async function signUp(returnTo) {
    sessionStorage.setItem(KEYS.lastLoginAt, String(Date.now()));
    await _ready;
    await msalInstance.loginRedirect({
      scopes: LOGIN_SCOPES,
      prompt: 'create',
      state: returnTo || '/dashboard.html',
    });
  }

  async function logout() {
    clearSession();
    await _ready;
    await msalInstance.logoutRedirect({
      account: getAccount(),
      postLogoutRedirectUri: '/index.html',
    });
  }

  // ── Callback (ONLY on /auth-callback.html) ────────────────────────────────
  async function handleCallback() {
    try {
      await _ready;
      const response = await msalInstance.handleRedirectPromise();
      if (response?.account) {
        msalInstance.setActiveAccount(response.account);
        window.location.href = validateReturnPath(response.state || '/dashboard.html');
        return;
      }
      // No auth response (page visited directly, or state already consumed).
      window.location.href = getAccount() ? '/dashboard.html' : '/index.html';
    } catch (err) {
      console.error('[Arkansas Serve] Auth callback error:', err);
      window.location.href = '/index.html?error=auth_failed';
    }
  }

  // ── Tokens ────────────────────────────────────────────────────────────────
  // Resolves the access token for the API, renewing silently when possible.
  // Returns null when interaction is required (caller decides whether to login()).
  async function getAccessToken() {
    await _ready;
    const account = getAccount();
    if (!account) return null;
    try {
      const result = await msalInstance.acquireTokenSilent({
        scopes: API_SCOPES,
        account,
      });
      return result.accessToken;
    } catch (err) {
      if (err instanceof msal.InteractionRequiredAuthError) return null;
      console.error('[Arkansas Serve] Token acquisition failed:', err);
      return null;
    }
  }

  function clearSession() {
    // sessionStorage holds only our as_* keys and MSAL's msal.* cache.
    sessionStorage.clear();
  }

  function getLastLoginAttemptAt() {
    return Number(sessionStorage.getItem(KEYS.lastLoginAt) || 0);
  }

  // ── Impersonation (#26) ─────────────────────────────────────────────────────
  // The session id travels only in sessionStorage + a request header (never a URL).
  // Cleared automatically on logout (sessionStorage.clear) or when expired.
  function setImpersonation(info) {
    sessionStorage.setItem(KEYS.impersonation, JSON.stringify(info));
  }
  function getImpersonation() {
    const raw = sessionStorage.getItem(KEYS.impersonation);
    if (!raw) return null;
    try {
      const info = JSON.parse(raw);
      if (info.expiresAt && Date.parse(info.expiresAt) <= Date.now()) {
        clearImpersonation();
        return null;
      }
      return info;
    } catch {
      clearImpersonation();
      return null;
    }
  }
  function getImpersonationSid() {
    return getImpersonation()?.sid || null;
  }
  function clearImpersonation() {
    sessionStorage.removeItem(KEYS.impersonation);
  }

  // ── Profile ───────────────────────────────────────────────────────────────
  function getProfile() {
    const account = getAccount();
    if (!account) return null;
    const claims = account.idTokenClaims || {};

    // Precise level from /users/me (cached) wins; else derive from the token claim.
    const tokenLevel  = claimRoleToAdminLevel(claims.extension_Role || claims.roles?.[0]);
    const cachedLevel = sessionStorage.getItem(KEYS.adminLevel);
    const adminLevel  = strongestLevel(tokenLevel, cachedLevel);

    return {
      name:  claims.name || account.name || claims.preferred_username || 'User',
      adminLevel,
      email: claims.email || claims.preferred_username || account.username || '',
    };
  }

  function setResolvedLevelFromUser(user) {
    if (!user || !user.adminLevel) return;
    // While impersonating, /users/me returns the TARGET — don't let that overwrite
    // the real account's cached level (which would strand the super below SuperAdmin
    // after exiting). The cached level always represents the real signed-in user.
    if (getImpersonation()) return;
    if (Object.hasOwn(ADMIN_RANK, user.adminLevel)) {
      sessionStorage.setItem(KEYS.adminLevel, user.adminLevel);
    }
  }

  function isAuthenticated() {
    return !!getAccount();
  }

  // ── Route guard ───────────────────────────────────────────────────────────
  // Resolves the profile, or null after starting a login redirect / role bounce.
  async function requireAuth(minAdminLevel = null) {
    await _ready;
    if (!getAccount()) {
      await login(window.location.pathname);
      return null;
    }
    const profile = getProfile();
    // Rank-based: a higher level satisfies a lower requirement (e.g. an
    // OrganizationAdmin clears an EventAdmin gate; SuperAdmin clears everything).
    if (minAdminLevel && adminRank(profile?.adminLevel) < adminRank(minAdminLevel)) {
      window.location.href = '/dashboard.html';
      return null;
    }
    return profile;
  }

  // ── Init (call on every page load) ────────────────────────────────────────
  async function init() {
    document.getElementById('btn-logout')?.addEventListener('click', () => logout());
    document.getElementById('btn-login')?.addEventListener('click', () => login());
    document.getElementById('btn-signup')?.addEventListener('click', () => signUp());

    await _ready;
    const profile = getProfile();
    const navName = document.getElementById('nav-user-name');
    const navRole = document.getElementById('nav-user-role');
    if (navName && profile) navName.textContent = profile.name;
    if (navRole && profile) navRole.textContent = profile.adminLevel;
    return profile;
  }

  return {
    ready,
    login,
    signUp,
    logout,
    handleCallback,
    requireAuth,
    isAuthenticated,
    getProfile,
    getAccessToken,
    setResolvedLevelFromUser,
    getAdminLevel,
    adminRank,
    clearSession,
    getLastLoginAttemptAt,
    setImpersonation,
    getImpersonation,
    getImpersonationSid,
    clearImpersonation,
    init,
  };
})();
