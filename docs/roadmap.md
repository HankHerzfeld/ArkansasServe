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

- **Rotate Cosmos/Blob keys.** ✅ Done manually 2026-07-14.
- **Scope Cosmos firewall.** ✅ Closed 2026-07-14 — **partial by necessity, not oversight.**
  The Function App runs on **Consumption (Y1/Dynamic)**, which supports no VNet integration
  and has no stable outbound IPs, so IP-allowlisting it against Cosmos is not viable — an
  allowlist would work until the shared pool shifted, then fail silently. Public access is
  restricted to selected networks with the **"Accept connections from within public Azure
  datacenters"** exception, which is *required* on Y1. That blocks the public internet but
  still admits any Azure-resident caller (key-gated). Real isolation needs **EP1 + VNet +
  a Cosmos Private Endpoint** (~$150+/mo) — an open cost decision, not a config toggle.

### Resolved 2026-07-14 (PR #65)
- **Raw org id rendered as an organization name.** ✅ Fixed. The dashboard showed a membership
  chip whose organization name was the raw tenant id. Cause: `GetMyMemberships` did
  `organizationName = tenant?.Name ?? orgId`, so a membership whose tenant no longer exists
  fell back to printing the raw partition key. Such a membership is **unusable** — you cannot
  scope to it, browse it, or leave it — so it is now omitted and logged as a warning rather
  than surfaced. (Omitting is safe: `GetTenantAsync` returns null only on a genuine 404.)
- **Latent: tenant teardown could not remove inactive members.** ✅ Fixed.
  `DeleteTenantCascadeAsync` deleted members via `GetUsersByTenantAsync`, which filters
  `status == "active"` — correct as a roster query, wrong for a teardown, since a
  suspended/inactive member would survive and be orphaned against a deleted tenant. It now
  uses an unfiltered partition read. *Not* the cause of the orphans above — there are
  currently zero non-active users, so those predate the cascade or were removed
  out-of-band — but it would have caused the same class of orphan later.

### Still open — next two, in this order

**① Org-less users land in the Entra directory as if it were an organization.**
`AuthMiddleware` resolves `TenantId = extension_OrganizationId ?? extension_SchoolId ??
extension_TenantId ?? Claim("tid")`. That last fallback is the **Entra directory id** — the
same GUID hardcoded as `TENANT_ID` in `frontend/js/auth.js`. A directory is not an
organization, so any user whose token carries no org claim is bootstrapped into a pseudo-org
that has no `Tenant` doc and never will. Live data matches exactly: every real user doc in
that partition is `status:"active"` with a real `externalId` — real sign-ins, not legacy
junk — and **`unknown-tenant` holds zero docs**, i.e. the intended fallback in
`ResolveTenantId` has never once been reached because `tid` always fills the slot first.
This reproduces on every such sign-in.

*Decided approach:* treat it as **"no assigned or joined organization"** rather than
inventing an org. Drop the `tid` fallback; keep a reserved, explicit partition as the
person's profile home (never presented as an org — PR #65 already omits it from the
memberships list, since it has no `Tenant` doc); and give the dashboard a real empty state
that hides itself once an org is present. Also remove the client-side
`organizationName || organizationId` fallback in `dashboard.js`, which would print a raw id
regardless of what the API returns.

**② Edge-to-edge (`viewport-fit=cover`) vs. the current letterboxed setup — decide.**

*Where we are.* The viewport meta is `width=device-width, initial-scale=1.0` — **no
`viewport-fit=cover`** — so iOS confines the page to the safe area and nothing can be hidden
by the Dynamic Island, the notch or the home indicator. There is **no current exposure**;
the risk only appears if we opt in. The navbar and drawer already pad via
`max(…, env(safe-area-inset-*))`, which resolves to plain padding today (insets are 0) and
would become correct automatically the moment we did opt in.

*The finding that decides it.* `theme-color`, `manifest.background_color` and the navbar's
`--green` are **all `#2d6a4f`**. In an installed PWA iOS fills the status-bar region with
`theme-color` — which is already exactly the navbar's green. **The top of the app already
looks seamless.** The main visual payoff of going edge-to-edge (a coloured bar bleeding into
the status-bar area instead of a mismatched strip) is therefore *already achieved* by the
colour match, at zero risk.

*What opting in would cost.* `viewport-fit=cover` is global. Every fixed/sticky element has
to be audited and padded or it slips under the island / home indicator: the navbar (the
brand + hamburger row — i.e. the exact "top items" concern), the nav drawer, `.modal-overlay`,
the notification pane, and the sticky header itself. Landscape adds
`safe-area-inset-left/right` on notched devices. Get one wrong and the failure is invisible
on a desktop browser and only shows on real hardware.

*What it would buy.* Genuine full-bleed under the island; slightly more vertical room; a more
native feel. **Only in the installed PWA** — in a Safari/Chrome tab the browser's own chrome
occupies that space and `viewport-fit` changes nothing at all.

*Recommendation: stay letterboxed for now.* This is a volunteer/hours tool whose users are
overwhelmingly in a browser tab, where edge-to-edge is a no-op; the one place it would show
(installed PWA) already looks right because of the colour match; and the cost is a
whole-app audit whose failure mode is precisely the occluded header we set out to avoid.
Revisit **if** installed-PWA usage becomes a real goal — the `env()` guards are already in
place, so switching later is cheap and mostly mechanical.

*Do not opt in halfway.* Adding `viewport-fit=cover` **without** auditing every fixed/sticky
element is strictly worse than today: it is what pushes the brand and hamburger under the
island.

### Still open
- **Orphaned membership data (needs an owner decision).** A small number of membership rows
  still point at a tenant that no longer exists (one admin-level, the rest test accounts), all
  `status:"active"`. The fix above hides them from the UI; the rows remain. Deleting
  production user records was deliberately **not** done unattended. Removing the admin row is
  safe for access — the `arkansas-serve-root` SuperAdmin membership is independent — but it is
  still a destructive write on real data. Identifiers are deliberately not recorded here: this
  repository is public. See the session notes / Cosmos for the specific rows.
- **Minor.** Per-org `displayName` differs across pages (per-org User doc).
- **Infra drift (P2) — now larger.** The Bicep `what-if`/apply workflow has never
  successfully run; its only runs were PR `what-if`s that failed at OIDC login. Live
  containers (incl. `ImpersonationSessions`/`AuditEvents`) were created out-of-band, and the
  Cosmos firewall above is a third out-of-band change: `main.bicep` still declares
  `publicNetworkAccess: 'Enabled'` with no `ipRules`/`networkAclBypass`, and sets
  `CosmosDb__ConnectionString` from `listConnectionStrings()` (which returns **primary**).
  So an apply today would **revert the firewall and overwrite the rotated key setting**.
  Bicep is not the source of truth for what exists — reconcile before any apply.

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
- **Responsive containment** — ✅ **Done 2026-07-14 (audited at 375×812, measured not eyeballed).**
  The audit overturned the assumption that the header and modals were each independently
  broken. Both were innocent:
  - **Header fits** at 375px — 0px overflow, no overflowing children, on every page checked.
  - **Modal CSS was already correct** (`width:min(92vw,520px)`, `max-height:calc(100vh - 3rem)`,
    `overflow-y:auto`, from the earlier "contain modals in frame" work).

  There was **one shared root cause**: `.table` had no scroll container, and long unbreakable
  strings (emails) plus cells holding selects/inputs pushed tables far past the viewport
  (`#users-table` 981px wide at 375px). That made the *document* wider than the viewport —
  and because `.modal-overlay` is `position:fixed;inset:0`, the overlay stretched to the
  **document** width (630px) and centred the dialog off-screen at `left:143`. The modal was a
  **symptom**, not a bug.

  Fix: wrap all 12 tables in a `.table-scroll` (`overflow-x:auto`) container. Measured
  alternatives were rejected — letting text wrap still left `#users-table` ~358px over (a
  `<select>` can't wrap) and blew row height 88px → 624px; `table-layout:fixed` still left
  ~49px. Only the scroll container both contains the page **and** keeps rows readable.
  Result: page overflow 0 on every page; modals centre correctly for free.

- **Phone header — pop-out drawer.** ✅ Done 2026-07-14. Separate from overflow: the header
  never overflowed, it **wrapped**. A SuperAdmin's 7 tabs stacked into 3 rows, making a
  **202px header — 25% of a 375×812 screen** — before any content. At ≤640px the tabs and the
  bell/name/sign-out cluster now slide out as a drawer behind a hamburger; the bar stays one
  60px row (**25% → 7%**). Backdrop, Esc, body scroll-lock, `aria-expanded`/`aria-controls`,
  44px touch target, and `prefers-reduced-motion` are handled.
  - *Note:* the drawer is `position:fixed;right:0`, so it **depends on the table fix above** —
    while the document was wider than the viewport, `right:0` resolved to the document's right
    edge, off-screen. They ship together.
  - *Known, pre-existing:* between ~641px and ~1010px a SuperAdmin's 7 tabs still wrap to two
    rows (74px bar). Left as-is: the drawer is deliberately phone-only, and tab count is
    role-dependent (a Student has 3), so a higher breakpoint would give most users a drawer
    they don't need. `height:60px` → `min-height:60px` means those wrapped tabs no longer
    overflow a fixed-height bar.

- **iOS safe area / Dynamic Island.** ✅ Assessed 2026-07-14 — **no current exposure, guarded
  for later.** The viewport meta is `width=device-width, initial-scale=1.0` with **no
  `viewport-fit=cover`**, so iOS letterboxes the page and nothing can slide under the island;
  `display:standalone` + the default status-bar style keeps the PWA below the status bar too.
  The header and drawer nevertheless pad with `env(safe-area-inset-*)` via `max()`, which
  resolves to the plain padding today (insets are 0) and becomes correct automatically if the
  app ever goes edge-to-edge. **Do not add `viewport-fit=cover` on its own** — that is what
  would push the brand/hamburger row under the island; it requires auditing every
  fixed/sticky element (navbar, drawer, modal overlay, notification pane) first.

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
