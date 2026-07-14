# Arkansas Serve — Priorities & Roadmap

_Last updated 2026-07-13._ Consolidated list of completed and upcoming priorities.
Detailed context for shipped work lives in the referenced PRs and companion docs
(`production-cutover-plan.md`, `manual-verification-checklist.md`).

---

## ✅ Completed (recent)

### Auth / multi-org correctness (branch `feat/student-self-registration` → `main`)
- **Finding 2 — secondary-org admins unlocked.** `/users/me` now reports the strongest
  `adminLevel` across all memberships, so tab visibility, route guards, and the scope
  bar work for admins whose role is in a non-home org. (PR #57, deployed)
- **Finding 3 — real display name in the shell.** Header/greeting prefer the `/users/me`
  `displayName` over the token `name` claim (which could read "unknown"). (PR #57, deployed)
- **Finding 5 — event detail registered-state.** `event.html` shows "You're signed up" +
  Cancel instead of always offering Sign Up; event GET returns `myRegistration`. (PR #57, deployed)
- **Finding 7 — Admin Backend reachable for secondary-org admins.** The context endpoint
  falls back to the caller's strongest admin membership; scope defaults to the
  strongest-admin org. (PR #58, deployed)

### Admin tooling
- **SuperAdmin: delete event & void approved service log.** Delete cascades registrations
  (keeps earned logs); Void hard-deletes an approved log (the only way to reverse one).
  (PR #59, deployed & verified in prod)
- **Finding 8 — cross-org cancel frees the counter.** Registrations record the event's
  `OrganizationId` at sign-up; cancel uses it (fallback to `SchoolId`) to locate the event,
  so the slot/shift `filled` counter decrements even for cross-org sign-ups. (PR #59, deployed)

### Earlier program work (see companion docs)
- Volunteer self-registration, managed-volunteer adoption + service-log migration, role
  matrix, PWA/a11y, shared app shell, dashboard profile, notification pane, event
  detail/shifts, org profile pages (PRs #31–#44) — verified against production.
- Production cutover (standalone Function App + SWA linked backend), auth migration to the
  arkansasserve.onmicrosoft.com External-ID tenant, blob SAS read fix, Bicep IaC
  reconciliation. (see `production-cutover-plan.md`)

---

## 🐛 Open findings / follow-ups

### Resolved 2026-07-14 (PR #62)
- **Finding 9 — registration cancel checked token level, not membership.** ✅ Fixed.
  `CancelRegistration` now authorizes per-org via `ResolveActorInOrgAsync`: you may always
  cancel your own; cancelling another's needs **EventAdmin+ in the event's own org**
  (decided — whoever runs an event clears no-shows on the day; deliberately lower than the
  destructive delete/void, which stay at OrganizationAdmin+). The old token check was wrong
  both ways: it refused membership-based admins *and* let a token-level admin from an
  unrelated org cancel anything anywhere.
- **Finding 1 — role granted via Roles doesn't apply.** ✅ Hint added. Verified mechanism:
  `GetMe` **does** adopt at sign-in, but only within the person's **token/home org**; a role
  granted in any *other* org needs a self-join. The Roles UI now says so.
- **Finding 6 — adopted membership can't Leave.** ✅ **By design, decided 2026-07-14.** An
  adopted membership was built by an org (e.g. a school's roster), so the person may not
  remove themselves — Leave hard-deletes the doc. They must ask an admin, who removes it via
  the role matrix. Only a membership someone opted into themselves is theirs to drop. The
  UX half *was* real and is fixed: the 403 now explains why and what to do next.
- **Stale ".NET 10" doc.** ✅ Fixed (README + copilot-instructions, which was actively
  telling agents to target `net10.0`). Staying on **.NET 8 LTS** is deliberate; the "decide
  8 vs 10" action is closed.
- **Daily Event Crawler.** ✅ Schedule **disabled** (manual `workflow_dispatch` only).
  Not merely a missing secret: the endpoint validates an **Entra JWT**, and those expire in
  ~1h, so a static `CRAWLER_SERVICE_TOKEN` in a GitHub secret can never drive a daily job.
  Re-enable once M2M auth is settled — app registration + `client_credentials` (mint a fresh
  token per run), a shared-secret header for that route, or a timer-triggered Function.

### Still open
- **Minor.** Per-org `displayName` differs across pages (per-org User doc); rotate
  Cosmos/Blob keys; scope Cosmos firewall.
- **Infra drift (P2).** The Bicep `what-if`/apply workflow has never successfully run — its
  only runs were PR `what-if`s that failed at OIDC login. Live containers (incl.
  `ImpersonationSessions`/`AuditEvents`) were created out-of-band, so Bicep is not the source
  of truth for what exists.

---

## 🎯 Upcoming priorities

### Platform & UX foundations
- **AJAX everywhere for search/queries** — all search and query interactions should fetch
  asynchronously (no full-page reloads); results update in place.
- **DataTables for users & events** — integrate DataTables to power searching, sorting, and
  filtering of the users and events lists. **Server-side processing** (paging/sorting/filtering
  handled by the AJAX endpoints), driven by scale: schools reach **1,200+ users per org** and a
  comparable event count after a few months, so shipping full datasets client-side is not viable.
  Effective server-side search is essential.
- **Responsive containment** — all content must stay well contained within the browser/app
  viewport across display sizes. Several elements currently break out of the screen,
  especially in the **header** and **modals**, which don't fit properly on smaller widths.

### Events & scheduling
- **Recurring / regularly-scheduled events** — let an event repeat on a schedule rather than
  re-creating it each time.
- **Live day-of check-in.** A generated **QR code** that redirects to a check-in flow, plus a
  built-in **org/EventAdmin check-in page** for the day of the event: check people in **by
  shift** and **by user**, and **add walk-in volunteers on the spot**. **Must work offline** —
  this is the primary offline use case (updating events / checking in / authorizing volunteer
  service at work sites without wifi), so it needs local caching + a queued-write/sync model
  (PWA) scoped to a single event's roster. This is distinct from the online AJAX search work,
  which requires connectivity by design and does not conflict with it.
- **Group registration** — register multiple individuals for an event in one action.

### Discovery, search & maps
- **Event search & sort/filter** — by tag, event name, organization, open spots, date, and
  date range; results sortable and filterable.
- **Search map** — plots service locations with date/availability, **color-coded by tag**, with
  the same filter controls as the list view.
- **ZIP / geo search** — capture **ZIP + city** on event addresses for searchability and
  location; search by **ZIP code, town, and county**; map-based ZIP selection. Prefer/prompt
  for ZIP at event creation.

### Addresses & mapping
- **Address auto-populate** in the event and organization creation portals (Google Maps API),
  with an **optional custom address** path (no auto-populate) that still shows a **previewed
  Google Map**.

### Organization & user model
- **Richer org taxonomy** — two-level org classification: an org *style* (e.g. Community
  Organization) plus a **service category** sub-definition (clothing distribution, housing,
  elder care, Parks & Rec, Outdoor, etc.).
- **Per-org user tags / credentials** — admin-defined markers on a person's per-org record
  (e.g. "waiver signed", "masonry training complete", background-check state) that can gate or
  inform scheduling.
- **User assignment under an org/EventAdmin** — assign volunteers to a specific admin who then
  has **direct oversight of those users' hours and approvals**, **per-action notification
  settings**, and **direct communication with assigned users via notifications**.
- **Arkansas Serve as a distinct organization** — with its own organization page.

### Approvals & compliance
- **School approval tags for events** — a school can mark some organizations **pre-approved**
  (events auto-count) and others **approval-required**.
- **Real-time waiver prompting** — email + phone prompts through to a student's parent/guardian,
  with **documents uploaded by the organization**.
- **Parental account oversight** — guardian oversight features that don't require up-front
  action every single time.
- **Terms & Conditions and Privacy Policy pages** — plus a sign-up confirmation that records
  the user's acceptance of the TOC and Privacy Policy.

### UI & branding
- **Dashboard tabs** — replace the organization-viewing dropdown with tabs.
- **Per-school branding / customizable CSS** — schools choose a **logo and color palette** that
  applies to their assigned users; assignable to school-scoped accounts.

---

_When picking up any upcoming item, confirm scope/specifics with the owner before building —
several (taxonomy values, branding model, waiver flow, maps integration) have open design
questions._
