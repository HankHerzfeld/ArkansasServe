# Manual verification checklist — 2026-07-07 changes

Covers everything shipped on `auth/external-id-rewrite` (PRs #31–#44): the
backlog items (#31–#36) and the display + events/orgs rework (Phases A–D,
#37–#44). None of this could be exercised from the build environment (no
interactive Entra sign-in), so it needs a human pass against deployed `main`.

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

---
---

# Display + events/orgs rework — Phases A–D (PRs #37–#44)

Same prerequisites and test accounts as above. New pages introduced:
`event.html?id=&organizationId=` and `organization.html?id=`.

## Phase A — shared app shell (#37)

Applies to every authenticated page (dashboard, events, organizations,
org-portal, admin-portal, admin-backend, platform-admin).

- [ ] Every page shows the **same header**: brand, a role-aware tab bar, a
      notification bell, your name, and Sign Out — identical across pages.
- [ ] The **current page's tab is highlighted** (active state) and doesn't link
      to itself.
- [ ] Tabs shown match your level: everyone sees Dashboard / Find Events /
      Organizations; EventAdmin+ also Manage Events; OrganizationAdmin+ also
      Approvals + Admin Backend; SuperAdmin also Platform Admin.
- [ ] **Your name in the header links to `/dashboard.html`.**
- [ ] Sign Out works from the shell on any page.
- [ ] In Chrome, navigating between pages **animates** (cross-document view
      transition) rather than a hard flash. (No-op in browsers without support —
      not a failure.)
- [ ] `index.html` (landing) and `auth-callback.html` keep their own minimal
      chrome (no app shell) — expected.

## Phase B — dashboard profile + editing (#38)

- [ ] Dashboard shows a **profile card**: avatar initial, name, email, role
      badge, and org-membership chips. Phone/grade appear **only when set**.
- [ ] **Edit profile** opens a modal; saving name/phone/grade updates the card
      and the greeting.
- [ ] Renders for **both** students and admins.
- [ ] **Per-org lock:** in Admin Backend → Organization Settings, turn **off**
      "Allow members to edit their own profile" for an org. Then, as a volunteer
      in that org, the Edit button is hidden and a direct `PUT /users/me` is
      refused (403). An **org admin can still edit** even when it's off.

## Phase C — role-scoped notification pane (#39)

- [ ] The bell shows an **unread badge** (unread personal notifications + admin
      action items).
- [ ] As a **student**: the pane shows only a **"For you"** list; "Mark read"
      clears items and drops the badge.
- [ ] As an **org admin** with a pending approval and a recent self-join: the
      pane shows a **"Needs attention"** section with a **Review** link (→ Approvals)
      and a **View** link (→ Admin Backend), above "For you".
- [ ] The pane closes on an outside click and is reachable from every page.
- [ ] The dashboard no longer shows a separate in-body notifications section
      (it moved into the bell) — expected.

## Phase D1 — event detail page + richer fields (#40)

- [ ] From Find Events, clicking an event title opens **`event.html`**.
- [ ] The page renders hero photo, title/org, status + category + tag badges, a
      facts row (**When**, **Where** with a maps link, credit, spots), **About**,
      **What to know**, **Contact** (with mailto), and a **More info** link —
      each shown **only when it has data**.
- [ ] A volunteer can **Sign Up** inline; the slot count updates.
- [ ] In org-portal, an admin can set the new fields (tags, requirements,
      external link, contact) and they appear on the detail page.

## Phase D2 — event shifts/slots + custom questions (#43)

- [ ] In org-portal, build an event with **2 shifts** (give one a small capacity)
      and **1 required question** (text or choice); save and reopen — the shifts +
      question **persist** in the form.
- [ ] On the event's list card, its **Sign Up** now routes to the detail page
      (not the quick modal) because it's structured.
- [ ] Signing up requires **choosing a shift** and **answering the required
      question**; the sign-up modal shows both.
- [ ] Fill a shift to capacity → it shows **FULL / 0 left** and is not selectable;
      the overall event may still be open via other shifts.
- [ ] Cancelling a registration **frees** the shift's spot.
- [ ] **Editing the event does not reset shift fill counts** (filled is preserved
      by shift id on the server).

## Phase D3 — public organization profile page (#41)

- [ ] From Organizations, the card title / **View** opens **`organization.html`**.
- [ ] Renders logo + name/type header with **Join/Leave**, **Mission** / **About**,
      a **Contact** block, and an **Upcoming events** list linking to each event —
      each shown only when it has data.
- [ ] **Join/Leave** on the profile works and reflects membership.
- [ ] An org with no description/mission yet shows those sections **omitted**
      (until an admin fills them in — see D4).

## Phase D4 — org admin editing of the public profile (#44)

- [ ] In Admin Backend → Organization Settings, a **"Public profile"** group
      exists: description, mission, website, logo URL, contact email/phone, address.
- [ ] As an **org admin** (not just SuperAdmin), the form **loads the org's
      current values** (from context) and saving updates them.
- [ ] Saved values then appear on that org's **`organization.html`** (D3); cleared
      values disappear (empty = hidden).
- [ ] Editing is gated to `OrganizationAdmin+` for that org.

## Cross-cutting regression pass

- [ ] All prior surfaces still work behind the new shell: approvals, reports,
      role matrix (filter/search/load-more + inline errors), admin backend user
      management, bulk/CSV hours, volunteer self-service join/leave.
- [ ] Unauthenticated probes still 401: `GET /api/manage/orgs`,
      `GET /api/manage/orgs/<id>`, `GET /api/manage/matrix`, `GET /api/users/me`.
      (`GET /api/registrations` returns 404 — it's POST-only — which is expected.)
