# Manual verification checklist — 2026-07-07 changes

Covers everything shipped on `auth/external-id-rewrite` today (PRs #31–#36). None
of this could be exercised from the build environment (no interactive Entra
sign-in), so it needs a human pass against deployed `main`.

## Prerequisites

- All PRs merged to `main` **and** both deploys green:
  - "Azure Static Web Apps CI/CD" (frontend)
  - "Build and deploy dotnet core app to Azure Function App - func-arkansas-serve-arksrv" (backend)
    - ⚠️ This backend workflow runs **only on push to `main`** (not on PRs) and
      depends on nuget.org; transient `NU1301`/"Central Directory corrupt"
      restore failures recur occasionally. Fix = re-run the failed job.
- Test accounts (create/reuse; **must not** be `@arkansasserve.com` — that domain
  auto-elevates to SuperAdmin):
  - **Volunteer** account (Student level).
  - **Org-level admin** account — granted `OrganizationAdmin` (and separately
    testable at `EventAdmin`) in a real org via Platform Admin → Roles.
  - Your own **SuperAdmin** identity.
- Use a separate browser / incognito window per identity so sessions don't collide.
- After any JS/CSS change looks stale, hard-refresh (Cloudflare/browser cache).

## Quick unauthenticated smoke test (no login)

Backend deployed + gated returns 401; a fresh deploy may 500/hang on cold start —
re-probe after ~30s.

```
curl -s -o /dev/null -w "%{http_code}\n" https://www.arkansasserve.com/api/manage/orgs     # 401
curl -s -o /dev/null -w "%{http_code}\n" https://www.arkansasserve.com/api/manage/matrix    # 401
curl -s -o /dev/null -w "%{http_code}\n" https://www.arkansasserve.com/api/users/me         # 401
```

---

## PR #35 — legacy role retirement (HIGHEST RISK, test first)

Authorization was re-based from the legacy 4-role model onto the 5-level
`adminLevel`. A few gates **changed behavior on purpose**. If any of these fail,
this is the PR to roll back.

- [ ] **Landing redirects** (`index.html` sends you to the right portal by level):
  - [ ] Student → `/dashboard.html`
  - [ ] EventAdmin / GroupAdmin → `/org-portal.html`
  - [ ] OrganizationAdmin → `/admin-portal.html`
  - [ ] SuperAdmin → `/platform-admin.html`
- [ ] **Route guards** (`requireAuth`): each account can open its own portal and
      is **redirected to `/dashboard.html`** when opening a higher one.
  - [ ] Student cannot open `/org-portal.html`, `/admin-portal.html`, `/platform-admin.html`.
  - [ ] Org admin cannot open `/platform-admin.html`.
- [ ] **Broadened gate (intended):** an **OrganizationAdmin can now create/edit an
      event** (previously the event gate was `OrgStaff,PlatformAdmin`, which
      skipped school admins). Confirm create + edit both work as OrganizationAdmin.
- [ ] **Broadened gate (intended):** registration create/cancel and "my service
      logs" now allow **any authenticated user**; per-record ownership still holds:
  - [ ] A student can register for and cancel **their own** registration.
  - [ ] A student **cannot** cancel someone else's registration (403).
- [ ] **Super determination:** your SuperAdmin identity still resolves as super
      (matrix + tenants load, platform-admin reachable). Note super now comes from
      token `adminLevel==SuperAdmin` (role claim or `@arkansasserve.com` bootstrap)
      or a SuperAdmin membership — **not** a bare `"PlatformAdmin"` token string.
- [ ] **Regression sweep:** approvals, reports, admin-backend user management, and
      the role matrix all still authorize correctly per org.

---

## PR #31 — volunteer self-service (browse + join/leave)

- [ ] As a volunteer, open **Organizations** (nav link on dashboard/events).
- [ ] The directory lists active organizations; ones you belong to show **Joined ✓**.
- [ ] **Join** an org → button flips to Joined ✓ + Leave; it now appears in your
      memberships (`GET /api/manage/me/memberships`).
- [ ] **Leave** a self-joined org → returns to Join.
- [ ] "My organizations only" filter and search both work.
- [ ] **Email-unique-per-org:** if an admin pre-created you as a managed volunteer
      in that org, joining **adopts** that record (no duplicate membership).
- [ ] Leave is **refused** on a membership where you hold an admin role (only
      self-joined Student memberships can be dropped here).
- [ ] Unauth probe: `GET /api/manage/orgs` → 401.

---

## PR #32 — managed-volunteer service-log migration on adoption

- [ ] As an admin, bulk-log or CSV-import approved hours for a **managed volunteer**
      (identified by email, no login yet).
- [ ] That person signs in for the first time with the **matching email**.
- [ ] Their **dashboard shows those historical hours** and the approved-hours total
      includes them (the logs migrate from the old doc-id partition to their
      externalId on adoption).
- [ ] Signing in again (or joining another org) does not duplicate logs.

---

## PR #33 — role matrix server-side filtering & pagination

Platform Admin → **Roles** tab.

- [ ] The matrix loads (first page, default 50 rows).
- [ ] **Organization filter** dropdown scopes rows to one org.
- [ ] **Search** (name/email) filters server-side; clearing it restores results.
- [ ] **Load more** appears when there are more rows and fetches the next page.
- [ ] Changing org/search resets to page 1.
- [ ] Level-change and Remove still work on a row (see #34 for their error UX).

---

## PR #34 — matrix inline error surfacing

Platform Admin → Roles.

- [ ] Trigger a rejected level change (e.g. try to assign a level **at/above your
      own** as a non-super, or otherwise cause a 403).
- [ ] A red inline banner appears with the server's reason, and the dropdown
      **reverts** to its previous value.
- [ ] Trigger a failing Remove → inline banner appears, button re-enables.
- [ ] The banner auto-hides after a few seconds and clears on the next reload.

---

## PR #36 — PWA icons + accessibility polish

- [ ] Icons resolve as real PNGs (previously 404-masked as HTML):
  - `https://www.arkansasserve.com/icons/icon-192.png` → a 192×192 image
  - `https://www.arkansasserve.com/icons/icon-512.png` → a 512×512 image
- [ ] **Install the PWA** (Chrome desktop/Android "Install app") → the app icon
      shows the Arkansas Serve logo, standalone window, green theme.
- [ ] Every page has a browser-tab theme color and a `<meta name="description">`.
- [ ] Filter/search inputs expose accessible names (screen reader / DevTools
      Accessibility pane): events search + category, organizations search, matrix
      org + search.

### Still to run (need tools not available in the build env)

- [ ] **Lighthouse** (Chrome DevTools → Lighthouse) on `www.arkansasserve.com` —
      review Performance / Accessibility / Best Practices / SEO / PWA. Static gaps
      (icons, manifest, meta, input labels, viewport) are fixed; Lighthouse will
      surface runtime-only items: color-contrast ratios, tap-target sizing,
      focus-visible states.
- [ ] **Google Workspace SSO end-to-end:** verify the Entra External ID ↔ Google
      Workspace federation and sign in with a real Google account through to a
      created/adopted membership. This is an Entra-side config check plus a live
      federated login.
