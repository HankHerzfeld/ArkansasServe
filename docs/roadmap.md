# Arkansas Serve — Priorities & Roadmap

_Last updated 2026-07-22._ Consolidated list of completed and upcoming priorities.
_2026-07-22 refresh: marked Maps #17/#18 and the recurring create-form (③) shipped, and the
scope-bar root-duplicate resolved, after a live prod audit — see `2026-07-22-feature-audit.md`._
Detailed context for shipped work lives in the referenced PRs and companion docs
(`production-cutover-plan.md`, `manual-verification-checklist.md`).

> **Design decisions for the NEXT cycle live in `2026-07-19-design-decisions.md`** — #17/#18 maps,
> #19 waivers, #20 parental oversight and #21 branding are no longer design-gated. It **supersedes
> `2026-07-16-build-plan.md` on two points**: maps moved from the free Leaflet/OSM stack to Google
> (the owner has a key, so the no-billing constraint is gone), and branding moved from a fixed
> token subset to all-tokens-overridable with a generated palette.

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
- **Daily Event Crawler.** ✅ **Schedule RE-ENABLED 2026-07-15.** The diagnosis stands: the
  endpoint validates an **Entra JWT**, and those expire in ~1h, so a static
  `CRAWLER_SERVICE_TOKEN` in a GitHub secret could never drive a daily job.
  **Owner decision: a shared-secret header** (over an app registration + `client_credentials`,
  or a timer-triggered Function). A static secret works precisely because it is not a token —
  nothing expires. `POST /manage/events/crawl` now accepts `X-Crawler-Secret` as an
  alternative to a JWT; the queue/publish/dismiss routes stay JWT + SuperAdmin.
  - **Deliberately the weaker path, and bounded.** It is scoped to the one route, compared in
    constant time, and that route can only create **Draft** events — a human still reviews and
    publishes them through the JWT-only routes before any student sees anything.
  - **Fail closed, verified:** with `Crawler__SharedSecret` unset the header path does not
    exist — the identical secret that returns 200 when configured returns 401 when it is not.
    Tested against a real local Functions host: no creds → 401; wrong secret → 401 (no
    fallthrough to the JWT path); correct secret + dryRun → 200.
  - **Needs both halves set or every run 401s:** GitHub secret `CRAWLER_SHARED_SECRET` and
    Function App setting `Crawler__SharedSecret`. Set the app setting **directly** — do not
    add it to Bicep (see infra drift below). Revoke by clearing the app setting; no redeploy.
    The old `CRAWLER_SERVICE_TOKEN` secret is now unused and can be deleted.

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

*Resolved 2026-07-14 (PR #65).* Treated as **"no assigned or joined organization"** rather
than inventing an org:
- `AuthMiddleware` no longer falls back to `tid`. A directory is not an organization.
- `ResolveTenantId` now resolves an org-less token to the reserved **`unassigned`**
  partition (`TenantIds.Unassigned`) — a holding place for the person's own profile/intake
  answers, never presented as an org. It has no `Tenant` doc, so it is omitted from
  `/manage/me/memberships` by the same guard that hides orphans. (`unknown-tenant` is gone;
  it held zero docs, so the rename was free.)
- **First join migrates and cleans up:** `JoinOrg` folds the holding profile
  (person-owned fields only — never `AdminLevel`/`GroupIds`/hours/background-check) into the
  new org document and deletes the holding record, best-effort so it can't fail the join.
- **`GetMe` prefers a real membership** over the holding partition. Self-joining does not
  change a person's Entra claims, so without this every later sign-in would re-create an
  `unassigned` profile beside the real one and silently undo the migration.
- **Both client-side GUID fallbacks removed** — `dashboard.js` *and* `scope.js` each did
  `organizationName || organizationId`, so a raw id could reach the UI (and the org
  switcher) regardless of what the API returned. The dashboard now shows a real
  "No assigned or joined organization" empty state, which hides itself once an org exists.

**② Edge-to-edge (`viewport-fit=cover`) — ✅ DONE 2026-07-15. Owner chose to opt in.**
The recommendation below was to stay letterboxed; the owner decided otherwise and the full
audit was carried out. `viewport-fit=cover` is now on all 13 pages and every fixed/sticky
element is inset-aware. The analysis below is retained because it is still the reasoning
that governs the area — but the decision it argues for was overridden deliberately.

*What the audit actually found (the reason it was worth doing).* The element list in the
note below was **incomplete**. Guarding only the five elements it names would have shipped
a half-opt-in — the exact failure the note warns is "strictly worse than today":
- **Toasts** (`position:fixed; bottom:1.5rem`, duplicated inline in four page scripts) were
  not on the list. They would have sat under the home indicator.
- **`.container`'s** `1.5rem` side padding is *smaller than a landscape notch inset* (~44px),
  so page text would slide under the notch on a rotated phone. 11 of 13 pages use it.
- **`index.html` has no `.container` at all** — it styles its sections with inline
  `padding: 4rem 1.5rem`, which a stylesheet rule cannot override. Measured **12 landscape
  violations** on the landing page (the `<h1>` at x=24, the footer at x=32, needing ≥44).
  Fixed by making the hero + three inline-styled sections + the footer inset-aware. 12 → 0.
- **`.modal`'s** `max-height: calc(100vh - 3rem)` silently became wrong: `100vh` is the full
  display height edge-to-edge, so the flat `3rem` under-subtracts. Now subtracts the
  overlay's real, inset-aware padding.

*Verified by measurement, not eyeballing.* Because `env(safe-area-inset-*)` is **always 0 in
a desktop browser** (the note's "invisible on a desktop browser" trap), verification worked by
re-injecting the **shipped** CSS rules with real iPhone 14 Pro insets substituted for `env()`,
then measuring geometry. Portrait (59/34) and landscape (44/21), plus an inset-0 pass to prove
no regression for today's users. Modal clamps to 759px = 852−59−34, top y=59, bottom y=818,
overflow 0. Navbar brand at y=66 clears the island; the green still bleeds under it.

⚠️ **Still requires a real-device pass before it is fully trusted.** Substituting literals for
`env()` proves the arithmetic and the layout, not iOS's actual reported insets. It is the
strongest check available on Windows; it is not an iPhone.

*Known trade-off, owner's call:* bottom guards use `max(base, inset)`, so at the page bottom
the footer copyright ends **flush** with the home indicator (gap 0). Nothing is occluded — that
is the requirement — but there is no breathing room. Switch those to
`calc(base + env(...))` if you want the design's spacing preserved on top of the inset.

*One regression was caught and fixed during the audit:* blockifying `.navbar-links a` (to give
the drawer's inline anchors a real 47px tap target instead of a ~19px line box) also hit
`terms.html`/`privacy.html`, which carry a cut-down navbar with **no drawer** — stacking their
links into a 102px two-row header. The rule is now scoped to `.nav-menu .navbar-links a`.
Both pages verified back at 60px.

<details><summary>Original 2026-07-14 analysis (recommended staying letterboxed — overridden)</summary>

*Status 2026-07-14: deliberately left open by the owner; not decided.* No action needed to
stay safe — the current setup has no exposure. Read the note below when picking it up.

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

</details>

### Still open
- **Root org page resolves to its public counterpart** — ✅ **Fixed 2026-07-19 (PR #110).**
  `organization.html?id=arkansas-serve-root` showed "Could not load this organization":
  `GetOrgProfile` 404s the internal platform partition **on purpose**, since publishing it
  would put the SuperAdmin roster on a public page.
  - **The broken link was introduced by #106.** The per-org dashboard card links to
    `/organization.html?id=<membership orgId>`, and a SuperAdmin's membership *is*
    `arkansas-serve-root`. The public directory already filtered root out, so that card was
    the only in-app route to the URL.
  - **Resolved, not unhidden.** `js/orgs.js` holds both ids + `canonicalOrgId()`; the org page
    resolves the requested id and canonicalises the address bar (`replaceState`) so sharing
    stops propagating the internal id; the dashboard links canonically. **The backend guard is
    untouched** — root remains unservable (re-verified in prod after the change) and no
    SuperAdmin names reach the page. This only decides which id to *ask for*.
  - *Related cleanup still open:* `RootTenantId` is duplicated across ~5 frontend files;
    `js/orgs.js` is the natural home to converge them on.
- **⚠️ `gh run list --commit` needs the FULL 40-char SHA (logged 2026-07-19).** With a short
  SHA it returns an empty list and exit 0 — indistinguishable from "no run exists". Measured:
  `--commit 5e39ecb` → 0 rows; `--commit $(git rev-parse 5e39ecb)` → 2 rows, same commit. This
  is why `scripts/wait-for-deploy.sh` rev-parses first and asserts 40 chars; removing that
  would turn it into a script that always claims nothing deployed. See Testing Notes in
  `.github/copilot-instructions.md`.
- **⚠️ `PUT /events/{id}` is a full replace that looks like a patch (logged 2026-07-19).**
  Found the hard way while verifying #101 in prod: sending `{ organizationId, shifts }` to update
  only the shifts **wiped `title`, `description`, `location`, `category`, `maxSlots` and
  `startDateTime`** (the last to `0001-01-01`, which silently dropped the event out of the
  upcoming-events list). The request record deserialises every omitted field to its default and
  `UpdateEvent` copies those defaults onto the stored doc. The roadmap's #9 note — "`UpdateEvent`
  copies field-by-field rather than replacing, so `seriesId`/`recurrence` survive" — is true only
  of fields absent from the request *type*; anything present in the type but omitted from the
  *body* is zeroed.
  - **Not currently biting:** `org-portal.js` always sends the complete payload, so no UI path
    hits it. It is a landmine for the next caller (and for anyone scripting against the API),
    not a live defect. Damage was to a throwaway test event only.
  - **Fix when convenient:** make the update fields nullable and copy only what was supplied, or
    document the endpoint as PUT-means-replace and reject partial bodies outright.
- **Scope bar showed the root partition as a duplicate "Arkansas Serve" (logged 2026-07-18).**
  ✅ **Resolved by the single-org collapse (PR #124, 2026-07-21); confirmed in prod 2026-07-22.**
  The duplicate existed because there were *two* "Arkansas Serve" tenants — `arkansas-serve-root`
  (the internal platform partition) and the separate public `arkansas-serve` org — and `scope.js`
  mapped both for supers. #124 deleted the separate org and made root itself the single browsable
  organization, so there is only one "Arkansas Serve" left to list. The authed Super Admin scope
  bar now reads **Arkansas Serve · Demo Community Organization (Alpha) · Demo Partner Organization
  (Beta)** — one clean entry, no duplicate. The originally-proposed fix (filtering root out of the
  super list) is moot; root is now a legitimate, scope-selectable org.
- **Orphaned membership data.** ✅ **Deleted 2026-07-15 on owner authorization.** 3 rows
  removed; `Users` went 18 → 15, and the stranded partition now holds 0.
  - **The old description here was wrong in a way worth recording.** These rows did *not*
    "point at a tenant that no longer exists" — that tenant **never existed**. The id they
    carried is the **Entra External-ID directory id** (the same GUID hardcoded as `TENANT_ID`
    in `frontend/js/auth.js`), i.e. they are exactly the pseudo-org artifacts of finding ①
    above, created while `AuthMiddleware` still fell back to `Claim("tid")`. Not a deleted
    org — an org that was never an org. Nor were they "one admin + test accounts": all three
    are the owner's own identities (one `@arkansasserve.com` SuperAdmin, two gmail).
  - **Pre-verified before the write, not assumed.** The claim that removing the admin row is
    "safe for access" was checked rather than trusted: each of the 3 identities has a live
    membership **matching on `externalId`**, not merely on email; 3 SuperAdmin docs survive in
    `arkansas-serve-root`; and `ServiceLogs` / `EventRegistrations` / `Notifications` /
    `PendingApprovals` held **0** rows referencing any of the 3 user ids. Post-delete checks
    confirmed the owner is still SuperAdmin in `arkansas-serve-root`.
  - **They could not have come back, and cannot recur.** PR #65 removed the `tid` fallback
    (`AuthMiddleware` now ends `?? string.Empty`) and that code is deployed, so a sign-in no
    longer re-creates them. They were also unreachable by #65's `unassigned` → `JoinOrg`
    migration, which only folds the `unassigned` partition — these sat in the directory-id
    partition, so nothing would ever have cleaned them up.
  - Identifiers remain deliberately unrecorded here: this repository is public.
- **Minor.** Per-org `displayName` differs across pages (per-org User doc).
- **Infra drift (P2) — reconciliation DEFERRED by owner 2026-07-15; Bicep marked
  NON-AUTHORITATIVE.** The drift itself is unchanged and still live. The `what-if`/apply
  workflow has never successfully run; its only runs were PR `what-if`s that failed at OIDC
  login. Live containers (incl. `ImpersonationSessions`/`AuditEvents`) were created
  out-of-band, and the Cosmos firewall is a third out-of-band change: `main.bicep` still
  declares `publicNetworkAccess: 'Enabled'` with no `ipRules`/`networkAclBypass`, and sets
  `CosmosDb__ConnectionString` from `listConnectionStrings()` (which returns **primary**).
  An apply today would **revert the firewall and overwrite the rotated key setting**.
  - **What changed 2026-07-15:** the danger is now documented where it would be encountered
    rather than only here. `infra/main.bicep` opens with a do-not-apply block naming all
    three divergences; `infra/README.md` no longer claims to be the "codified real state"
    (it said so at the top while being three changes stale — the more dangerous of the two
    documents, because it was reassuring). The `az deployment group create` command was
    **removed** from that README; `what-if` stays, being read-only and the way to measure the
    drift. A fourth live-affecting item is now recorded there too: `platformAdminEmailDomain`
    in `main.prod.bicepparam` is still `arkansasserve.com`, so an apply would re-open the
    PlatformAdmin bootstrap elevation closed on 2026-07-09.
  - **A clean `what-if` is NOT sufficient clearance.** ARM returns app-setting values masked,
    so the rotated-key clobber will never appear as a diff. Read the live setting directly.
  - Bicep is not the source of truth for what exists — reconcile before any apply.

---

## 🎯 Upcoming priorities

### Platform & UX foundations
- **AJAX everywhere for search/queries** — all search and query interactions should fetch
  asynchronously (no full-page reloads); results update in place.
- **DataTables for users & events — phase 1: client-side, first look.** *(Re-scoped by the
  owner 2026-07-14.)* Wire DataTables onto the users and events tables for searching, sorting
  and filtering **assuming under ~100 rows**, so the feature can be seen and shaped now. At
  that size the whole dataset ships in one response and DataTables does the work in the
  browser: no protocol contract, no paging maths, no Cosmos work. Live data today is ~18 user
  docs, so this is comfortably within range.
  - **This is explicitly a stepping stone, not the final answer.** The moment an org
    approaches four figures, shipping every row becomes the wrong shape and phase 2 below
    takes over. The upgrade is confined to how the table gets its rows (`data:` → `ajax:` +
    `serverSide: true`); the columns, filters and markup carry over.
  - Scale work is deliberately deferred — see **DataTables phase 2** at the end of this file.
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

- **iOS safe area / Dynamic Island.** ✅ **Opted in 2026-07-15** — superseded the 2026-07-14
  "no current exposure, guarded for later" assessment. The viewport meta on all 13 pages is
  now `width=device-width, initial-scale=1.0, viewport-fit=cover`, and the audit it demanded
  was carried out in full rather than partially. See **② Edge-to-edge** under "Open findings"
  for the element list, the four things the original audit list *missed*, and the measured
  verification. The `max(…, env(safe-area-inset-*))` guards the header and drawer already
  carried did exactly what they were put there for: they became correct the moment the app
  went edge-to-edge, with no change needed. Still owes a real-device pass — `env()` is always
  0 in a desktop browser, so the check substituted real insets into the shipped rules.

### Events & scheduling
- **"Spots left" per-shift** — ✅ **SHIPPED 2026-07-19 (PR #101).** Availability now reports the
  **tighter of the two capacity gates** the server already enforces, rather than the overall
  counter alone:

  ```
  remaining = min(overall remaining, sum of per-shift remaining)
  ```

  - **Both gates were always real.** `AdjustSlotsAsync` refuses a sign-up that would push
    `currentSlots` past `maxSlots` *or* a shift's `filled` past its `capacity`. Four surfaces
    read only the first: the card, the detail page's "Spots left" fact, the "only with spots
    left" filter and the "most spots left" sort. So the number could contradict the per-shift
    rows printed directly beneath it.
  - **Capacity 0 = uncapped is modelled as `Infinity`,** so it drops out of the `min()` and the
    no-shift case reduces to *exactly* the previous expression. A generalisation, not a branch
    on "does it have shifts" — which is why 15 of 16 live shifted events were unaffected.
  - **Verified in prod against real data, then against a constructed worst case.** Of 16
    shifted events, one changed: an event with `maxSlots: 0` but a real shift cap of 9 showed
    *no availability at all* and now reads "7 spots left across 1 shift". The case live data
    did not cover (overall open, every shift full) was built as a temp event and filled with
    three real walk-ins: overall said "7 spots left" while 2/2 and 1/1 shifts meant nobody
    could sign up. It now reads "0 spots left across 2 shifts" and is correctly excluded by
    "only with spots left". Temp event + 3 walk-in records deleted afterwards; verified 0
    leftovers.
  - Also converged the three duplicated per-shift `capacity > 0 ? …` expressions (shift list,
    sign-up selector, group-registration selector) onto the shared helper, and clamped the
    card's count, which was raw subtraction and could render negative.
- **Recurring / regularly-scheduled events** — let an event repeat on a schedule rather than
  re-creating it each time. *Scoped 2026-07-15; ① and ② landed. ③ (create-form UI) is now
  deployed — the New Event form on `org-portal.html` carries a "Repeat this event on a schedule"
  control (verified present in prod 2026-07-22). ⚠️ **Full end-to-end series creation was NOT
  exercised** in that pass — confirm the toggle writes a valid `RecurrenceRule` and materialises
  occurrences before calling this fully closed.*
  - **Occurrences are MATERIALISED as real Event docs, and the data model decided that.**
    `EventRegistrations` is partitioned by `/eventId`, so a computed occurrence has no id to
    partition by; and the capacity counters (`currentSlots`, `shifts[].filled`) live on the
    Event document, so it would have nowhere to keep them. Materialised occurrences are
    ordinary events, so registration, group registration, check-in, service logs, search and
    the event page all keep working untouched — computing them would have meant teaching
    every one of those about occurrences.
  - **Bounded series only (owner decision):** end date or count, capped at 100. Occurrences
    are created up front, so an endless series would need a rolling window plus a scheduled
    job — the timer deliberately avoided for the crawler.
  - ⚠️ **The DST trap, which is the whole difficulty.** Adding 7×24h to a UTC instant is not
    "the same time next week": a 1pm CST event stored as `19:00Z` reads as 2pm CDT once DST
    starts, so a naive series drifts an hour, twice a year, for every occurrence after the
    transition — and unlike PR #70's display bug this is *persisted* data that registrations
    and shifts inherit. `RecurrenceExpander` therefore steps the **local calendar** with the
    wall-clock held fixed and converts back to UTC; the UTC instants deliberately change so
    the local time does not. Verified both directions across 2027.
  - **Timezone is `America/Chicago`, one named constant** in `RecurrenceExpander` — a
    single-state programme in a single-zone state. Give Event/Tenant a timezone and only that
    line changes. The IANA id resolves on Linux *and* Windows (.NET 6+ uses ICU); the Windows
    id would not resolve on the Function App.
  - Invalid dates are **skipped, per RFC 5545, never clamped** ("the 31st" → Jan, Mar, May,
    Jul; a 5th Saturday only in months that have one). Clamping invents a date nobody chose.
  - **Editing one occurrence never re-expands the series** (owner decision: "this occurrence
    only"). `UpdateEvent` needed no guard — it already copies field-by-field onto the stored
    doc rather than replacing it, so `seriesId`/`recurrence` survive and cannot be rewritten.
  - **Delete-series refuses when anyone is signed up** and reports the count; `force=true`
    means it. Deleting one event is visible; doing it across twelve dates at once can quietly
    unregister dozens who are never told. OrganizationAdmin+, matching single-event delete.
  - *Known and accepted:* a 12-week series is 12 cards in the events list. That is correct —
    each is separately registerable — and PR #70's filters help. Nobody has decided whether
    the list should collapse a series; shipped without grouping to find out if it matters.
- **Live day-of check-in.** — ✅ **SHIPPED online-first 2026-07-16 (PR #89, fixes PR #90).** An
  admin **check-in page** (live roster by shift/user, manual toggle, **walk-in** add, and a minted
  **code/QR** panel) plus **student self-check-in**: the admin posts the code and a registered
  student scans/enters it to check *themselves* in (locked decision — not admin-scans-student).
  Enforces the `blockCheckIn` tag gate (same-org). Verified in prod (walk-in, self-check-in,
  blockCheckIn refusal). **Offline operation is deferred to #15** — this shipped online-first; the
  local-cache + queued-write/sync PWA model is that separate item, scoped to a single event's roster
  and distinct from the online AJAX search work.
- **Group registration** — register multiple individuals for an event in one action.
  *Scoped 2026-07-15 into three PRs; the first has landed.*
  - **Why it is not just a loop over the existing endpoint.** `POST /registrations` registers
    **only the caller** (`UserId = ctx.UserId`). The owner's chosen use case — an admin signs
    up people from their org roster — means registering people who have **no account**, and a
    managed volunteer's `User.ExternalId` is `string.Empty`. So the registrant identity has to
    change before any group UI is worth writing. That is an identity-model change, not an
    extension of the existing route.
  - **① Identity groundwork — ✅ done.** `EventRegistration.MemberId` (the per-org User doc
    id, which every roster member has, account or not) is now the canonical registrant
    identity. Reads (`BelongsTo`, `IsAlreadyRegisteredAsync`) accept **either** key, so rows
    written before the field existed keep working with no migration — the legacy arm can be
    dropped once every row carries a memberId. This also closes a **latent collision**: keying
    on `UserId` would give every accountless registrant the key `""`, so the first would read
    as "already registered" and block all the rest.
  - **② Group backend — ✅ done.** `POST /registrations/group`.
    - **Authorized on the REGISTRANTS' org, not the event's** — corrected during the build.
      The plan said "EventAdmin+ in the event's own org", copying Finding 9's cancel rule, but
      that rule is about clearing no-shows at *your own* event. The use case here is a
      school's admin signing students up for a **community org's** event, where they hold no
      role in the hosting org at all — the original rule would have refused the only case that
      matters. Now: EventAdmin+ in the org the registrants belong to, and every registrant
      must be in that same org. It grants nothing new, since each of those people could
      self-register individually; it only does it in bulk.
    - **All-or-nothing** on overflow (owner decision): 8 people into 5 spots is refused with
      "Only 5 spots left", writing nothing.
    - **Reserve first, write second** — `AdjustSlotsAsync` moves `CurrentSlots` and
      `shift.Filled` by ±N in one ETag'd update. This inverts the single-registration path,
      which still writes the doc first and, if the reserve fails, "compensates" by flipping it
      to `Cancelled` — which is why full events accumulate cancelled rows, and which at N
      people would have meant N wasted docs plus N compensating writes.
    - **Rollback removes the rows rather than cancelling them:** those people were never
      signed up, so a Cancelled tombstone would misreport what happened. If the release itself
      fails the event is over-counted — wrongly turning people away, never over-booking, which
      is the safe direction.
    - Required questions are validated **per person**, naming who is missing which answer.
  - **③ Group UI — ✅ done.** "Register a group" on the event page, for anyone holding
    EventAdmin+ somewhere. Two steps: roster multi-select (+ shift), then a **per-person ×
    per-question answer grid** (owner decision) so each volunteer's own answers are recorded
    rather than one shared set standing in for everybody. Step 2 is skipped entirely when the
    event asks nothing, which is the common case.
    - The button's visibility comes from **memberships, not the token's `adminLevel`** — a
      membership-based admin carries no admin claim on their token, which is exactly the trap
      Finding 9 documented (they read as Student and were refused on their own members).
    - **Which orgs are offered follows `scope.js`'s existing rule**, not a second one:
      SuperAdmin → every tenant; everyone else → the orgs they hold EventAdmin+ in. Deriving
      it from memberships alone (the first cut) made the feature **unusable for a SuperAdmin**,
      whose single membership is the `arkansas-serve-root` host org — whose roster is admins,
      not volunteers, so the dialog opened on an empty list with no way out. Found by running
      it against production, not by review. The host org is now filtered out of the picker
      entirely: its roster is always empty here, and it shares the public org's name, so
      offering both showed two identical "Arkansas Serve" entries.
    - Opens on the **event's own org** when the viewer can act there; a super's list is
      every tenant, so its first entry is arbitrary.
    - Server messages are surfaced verbatim ("Only 3 spots left", or who is already
      registered): the server knows what actually went wrong, and paraphrasing loses it.

### Discovery, search & maps
- **Event search & sort/filter** — ✅ **Done 2026-07-15 (PR #70).** Every axis the item asked
  for, on the cards (the grid is untouched — DataTables indexes a hidden mirror and the cards
  render from its results):
  - **Free text** — name, organization, tag, category and place, all terms in any order across
    any of them (DataTables' smart search). *Organization only became searchable once PR #69
    populated `organizationName`, which `CreateEvent` had never set.*
  - **Tag** — dropdown built from the tags actually present, not a hardcoded list; hides
    itself when no event carries one.
  - **Open spots** — "only with spots left". Matches how the card computes spots
    (`maxSlots === 0` = uncapped, so uncapped counts as roomy, and overbooked clamps to 0
    rather than going negative).
  - **Date range** — inclusive both ends.
  - **Sort** — soonest/latest, name A–Z/Z–A, most spots left. The card grid honours it because
    the row selector returns indexes in DataTables' applied order.
  - **Dates are compared on the LOCAL calendar day, never `toISOString()`.** Verified at
    UTC-5: a 7pm event on 1 Mar reads as `2027-03-02` under `toISOString()` — so the naive
    version would drift every evening event in Arkansas onto the next day and quietly drop it
    from a "1 March" filter.
  - *Not covered here:* ZIP/county and map-based selection are the two items below.
- **Search map** — ✅ **SHIPPED 2026-07-20 (PR #116, #18).** A split map view on `events.html`:
  side-by-side list + Google Map on large screens, a list/map toggle on phones, sharing PR #70's
  filter/sort stack untouched (the map draws whatever survived the filter). Coincident
  ZIP-centroid pins fan out. Degrades cleanly with no key. Verified rendering live in prod
  2026-07-22 with the current single event. ⚠️ **The "color-coded by tag" goal is unverified** —
  only one event exists in prod, so the per-tag colour coding could not be exercised; confirm once
  multiple tagged events are live.
- **ZIP / geo search** — ✅ **SHIPPED 2026-07-18 (PR #91).** Events gained `zip`/`city`/`county`
  (+`latitude`/`longitude`) alongside free-text `location`, resolved from a **bundled Arkansas ZIP
  dataset** (706 ZIPs, all 75 counties; GeoNames CC BY 4.0) — **no external geocoding API, no
  billing**. A recognised ZIP **auto-fills** city/county/coords on event create/edit
  (`GET /api/geo/zip/{zip}`), and the events list filters by **ZIP / town / county**. Forward-only
  (pre-#16 events keep their free-text location). Verified in prod. *Map-based ZIP selection lives
  with the maps items (#17/#18).*

### Addresses & mapping
- **Address auto-populate** — ✅ **SHIPPED 2026-07-20 (PR #115, #17).** Google Maps stack
  (Maps JavaScript + Geocoding + Places) behind a single **referrer-restricted browser key** in
  the `GoogleMaps__ApiKey` Function App setting (rotates without a redeploy; no server key — Y1 has
  no stable outbound IPs). Address autocomplete on the event-create Location field; **geocoding runs
  client-side at event save** and stores real `latitude`/`longitude` on the event. The free-text /
  custom-address path still previews a map. Degrades cleanly with no key. Verified live 2026-07-22.
  - ⚠️ **Set a budget alert** — geocoding now bills per event *save* (map loads bill per session).
    Inside the free tier at current volume, but no longer $0-by-construction the way the bundled ZIP
    dataset was.
  - Real geocoded coordinates supersede ZIP centroids going forward; **pre-#16 events keep centroid
    coordinates until re-saved**, so plan for a mixed-precision dataset.
  - *Not re-verified this pass:* Places autocomplete was not exercised by live typing, and the
    organization-create portal's autocomplete was not checked — only event-create.

### Dashboard
- **Per-organization, role-aware dashboard** — ✅ **SHIPPED 2026-07-19 (PR #106, fixes #107).**
  An overview, then **one card per organization whose content depends on the person's role in
  THAT org**. Someone is routinely an admin in one org and a plain volunteer in another, so a
  single global admin-vs-volunteer mode would have been wrong for one of them (owner decision).
  - **What it replaced was close to empty for an admin.** Any non-Student short-circuited to
    three em-dashes plus "Admin users can use the Admin Backend for scoped management tasks."
    So an admin who *also* volunteers was told their own hours didn't exist, and an admin who
    doesn't got a home screen with nothing actionable on it.
  - **Admin half** (per org, each figure linking where you'd act on it): awaiting approval
    (OrgAdmin+), volunteers assigned to me (EventAdmin+), upcoming events (EventAdmin+),
    volunteers (GroupAdmin+). Each call is gated by the role it *actually* requires —
    `/manage/volunteers` is GroupAdmin+, so asking as an EventAdmin would only ever 403 — and
    degrades to null rather than failing the card, so one 403 cannot blank the others.
  - **Volunteer half:** hours approved/pending in that org, and who oversees you there
    (name + email, owner decision).
  - **Hours key: `ServiceLog.schoolId`** — the org that APPROVED them — so hours served at an
    outside org still count under the membership that credited them, matching how approval
    works. The history table continues to name the HOST org per row, so both readings are on
    one screen (owner decision: "show both").
  - **New `GET /manage/me/overseers`.** #13 stored `assignedAdmins` on the volunteer's own
    per-org doc but only ever read it from the ADMIN side, so a volunteer had no way to see who
    was responsible for their hours. Deliberately its own endpoint rather than extra fields on
    `/manage/me/memberships`, which `scope.js` calls on every scoped page — one read per
    assigned admin is worth paying on the dashboard alone. A stale assignment naming a removed
    admin is skipped and logged, not surfaced as a blank row.
  - **Two things the prod clickthrough caught (PR #107), both invisible to a code read:**
    - The roster tile said **"Members" and read 1 for an org holding four people** —
      `GetVolunteersByTenantAsync` filters `AdminLevel == "Student"`, so it counts volunteers
      and excludes admins. Relabelled to match what it returns.
    - **Hours credited by an org you are no longer a member of belonged to no card**, so the
      cards summed to less than the "Total Approved Hours" tile directly above them — the exact
      "two numbers on one screen" failure the shared-figure design was meant to avoid. There is
      a live instance: an approved 2.0h log whose `schoolId` names a deleted tenant. A line
      under the cards now accounts for the difference.
  - Verified in prod as SuperAdmin and, via impersonation, as a volunteer with an assigned
    admin (test assignment created and removed afterwards; impersonation stopped).
  - *Note:* impersonation is **demo-users-only**, so a non-demo account cannot be used to
    exercise a volunteer view — set the case up on a demo user instead.

### Organization & user model
- **Richer org taxonomy** — ✅ **Vocabulary + fields done 2026-07-15.** Self-define-with-
  approval is the remaining piece (owner-requested; its own PR).
  - **Level 1 already existed.** "Org style" is `Tenant.Type`, a dropdown that has shipped for
    months (School / JDC / Community Organization). The real work was level 2.
  - **One shared vocabulary (owner decision):** `ServiceCategories` classifies BOTH an org and
    its events. Two lists is how "Senior Care" (event) and "elder care" (org) come to mean the
    same thing and filter differently — and PR #70's search already indexes category, so a
    split would surface immediately. There were in fact **three** hardcoded copies of the old
    list (org-portal, events filter, and the org type dropdown); all now fill from one place.
  - ⚠️ **Faith is an ATTRIBUTE, not a category** — the load-bearing decision here. A church
    running a food pantry is faith-based *and* doing food work; one dropdown cannot hold both,
    and with many Arkansas service orgs being churches that is a large share of the directory,
    not an edge case. `Tenant.FaithBased` is orthogonal, so "faith-based" and "food work"
    compose as separate filters. Two categories exist for faith **as a service** (Worship &
    Congregational Life, Religious Education & Ministry) — for orgs whose offering *is* the
    faith, not for any org that happens to be one. A denominational `faithAffiliation` is
    deferred: it needs an agreed list, and freeform would fragment exactly as an unmanaged
    category list would.
  - **Service category applies only to Community Organizations** (owner decision), enforced on
    both create and update. A school's "service category" is a question with no good answer,
    and asking anyway is how "Other" becomes the most popular value.
  - **`Tenant.Type` casing was split** — the dropdown wrote `"organization"` while seeded/demo
    orgs said `"Organization"`, so 3 of 5 live orgs disagreed with the other 2. Nothing
    branched on it (it renders as a badge), so it went unnoticed until it had to mean
    something. Canonical is now **Capitalized** (owner decision) and normalised on write — but
    `OrgTypes.IsOrganization` compares **case-insensitively** regardless: not depending on the
    stored casing is the fix, normalising it is only tidying. *Two live orgs still carry the
    lowercase value; harmless, and a one-off data fix when wanted.*
  - **Political Parties & Campaigns is deliberately separate from Civic Engagement &
    Elections.** Nonpartisan civic work (poll working, voter registration) and partisan
    campaign work have different implications for the school or court approving the hours;
    merging them would deny a school the ability to tell them apart. ⚠️ **The mechanism to act
    on that distinction is #12 (school approval tags), which does not exist yet** — until it
    does, a school cannot mark the partisan category approval-required.
  - **Self-define with approval — ✅ SHIPPED 2026-07-18 (PR #93).** An org (via its
    `serviceCategory`) or an event creator (via the event `category`) proposes a new label with
    **"Other → type a label"**; it stores as **pending** — shown to that org as "Other (pending
    review)" and **kept out of every dropdown/facet** — until a SuperAdmin resolves it from the
    admin-backend **queue**: **approve-as-new**, **approve-as-alias** onto an existing category
    (the anti-fragmentation path — prevents "Food Bank"/"food bank"/"Foodbank"), or reject.
    Vocabulary lives on the **root tenant** (no new container); stored values stay raw and resolve
    client-side, so alias approval takes effect on read with no batch rewrite. `GET /api/categories`
    serves the effective (canonical + approved-new) list. Verified in prod. A `POST
    /manage/backend/categories/scrub` **un-approve** path was added afterward (PR #95).
- **Per-org user tags / credentials** — ✅ **Model + definitions + admin API done 2026-07-15.**
  Gating and the admin UI remain.
  - **Per-org came free.** A `User` doc IS per-org (one per person per organization,
    partitioned by `tenantId`), so tags hang off a record that is already scoped correctly.
  - **One of the three examples was already built:** `BackgroundCheckStatus`
    (None/Pending/Cleared) + `BackgroundCheckCompletedAt`. So this item is really "stop adding
    a bespoke field per credential". It **stays as its own field** (owner decision) — folding
    it in would mean migrating live user records and rewiring every reader for no behaviour
    change. Cost: two ways to express a credential; worth converging once tags prove out.
  - **State + date, not a boolean** (owner decision), mirroring the background check —
    "pending" is genuinely different from "never started", and an admin chasing a waiver needs
    to see which.
  - **Per-tag enforcement** (owner decision), defaulting to **advisory**: a tag that started
    refusing volunteers the moment an admin created it would be a nasty surprise, so blocking
    is opt-in. `blockCheckIn` — "refused at the gate", which is the *better* rule for a waiver
    (sign up now, sign the form before you serve) — is **deliberately not implemented**: it
    lands with #14, because until a check-in exists the setting would silently do nothing, and
    a control that does nothing is worse than an absent one.
  - **Optional per-tag expiry** (owner decision). `ExpiresAt` is **stamped at completion from
    the policy then in force, and left alone** — so shortening a waiver from two years to one
    does not retroactively expire people who were compliant under the rule they were told
    about, and an admin can override one person without touching org policy.
  - ⚠️ **Gating has an unresolved cross-org problem — decide before building it.** Tags live
    on the per-org User doc, but registration is routinely cross-org (group registration
    exists precisely so a school can sign students up for a community org's event). If org A's
    event requires "waiver signed", a student from org B **has no User doc in org A** and can
    never satisfy it — blocked permanently, not for lacking a waiver but because the tag has
    nowhere to live. Options: put the tag state on the registration; give cross-org registrants
    a managed record in the event's org; or gate same-org only. Unanswered.
  - *Also note:* group registration is all-or-nothing, so once gating exists a group of 8 where
    2 lack a tag is refused entirely (naming those 2) rather than signing up 6.
  - *Remaining:* the gate itself, and the admin UI for defining tags and setting them.
- **User assignment under an org/EventAdmin** — ✅ **SHIPPED 2026-07-18 (PR #94).** Volunteers
  are assigned to overseeing admins — **many admins per volunteer**, each assignment carrying its
  **own** notification prefs (`{ adminId, notifyOnHours, notifyOnApproval }` on the per-org User
  doc; the list *is* the per-assignment pref store). `UpdateUserAccess` sets who oversees
  (OrgAdmin+, each admin validated EventAdmin+ in the org; prefs preserved across a role edit);
  `CreateServiceLog` **fans out** notifications to each assigned admin per their prefs (approval
  notice only when the log is Pending; additive — nothing notified admins before). Assigned
  admins get a **"My assigned volunteers"** view (org-portal) with per-volunteer notify toggles
  and a **direct-message** box (`GET /manage/me/assigned-volunteers`, `PATCH
  /manage/me/assignments/{volunteerId}`, `POST /manage/me/assigned-volunteers/notify`). Verified
  end-to-end in prod (assign → log → fan-out → pref-suppression → direct message) and demonstrated
  live in the browser.
  - *Follow-ups shipped alongside:* **notification delete/clear** (PR #96) — `DELETE
    /api/notifications/{id}` and `DELETE /api/notifications` (clear all), owner-scoped, filling
    the gap where notifications could only be marked read, never removed. **Category vocabulary
    scrub** (PR #95) — `POST /manage/backend/categories/scrub { label }` removes a label's
    approved-new / alias / proposal records: the un-approve path #10②'s self-define categories
    lacked.
- **Arkansas Serve as ONE organization** — ⚠️ **SUPERSEDED 2026-07-21 (owner decision).** The
  two-org split described below was **reversed**. There is now a single `arkansas-serve-root`
  tenant that is *both* the platform's host partition **and** its public, browsable
  organization. The separate `arkansas-serve` org was deleted; do not re-create it.
  - **What changed in code:** the guards that made root "not an org" are gone — it is listed in
    the public directory, its org page renders, and `JoinOrg` no longer hard-refuses it.
    Self-join is governed by the ordinary `AllowSelfJoin` flag (**set to false**: assign-only,
    matching what the deleted org used), not by a hardcoded rule an admin cannot change.
    `scope.js` and the group-registration picker no longer filter it out, and `js/orgs.js` —
    the root→public alias added in PR #110 — was **deleted** as it now aliases a thing to
    itself.
  - **The one special case that REMAINS, for a different reason.** Root still cannot be
    deleted. Not because it "isn't an organization" — it is — but because the **category
    vocabulary (#10②) is stored on this tenant document** (no new Cosmos container was
    available). Deleting it would destroy every approved category and alias platform-wide.
  - **The stated reason for the original split did not hold up.** The note below argues that
    unhiding root "would have published the SuperAdmin partition — Demo SuperAdmin 1/2 would
    appear on the public roster". `GetOrgProfile` returns **no member roster at all** — only
    name, type, description, mission, website, logo, contact details, address, `allowSelfJoin`
    and upcoming events. There was no roster to publish. The owner also judged the visibility
    concern a non-issue independently.
  - **SuperAdmins need no data change.** `ResolveActorInOrgAsync` already grants a global super
    full admin rights in every org including this one, so they act as org admins here already.
    Rewriting their `adminLevel` to `OrganizationAdmin` would have *removed* their
    platform-wide powers (Platform Admin, tenant creation, DB console, impersonation), which
    all check the real level.

<details><summary>Original 2026-07-15 two-org design (superseded — retained for the reasoning)</summary>

- **Arkansas Serve as a distinct organization** — ✅ **Done 2026-07-15.** Seeded as a real,
  browsable org (`arkansas-serve`) with its own org page, kept **separate** from
  `arkansas-serve-root`.
  - **Two orgs on purpose — do not "tidy" this into one.** `arkansas-serve-root` stays the
    *internal platform partition*: `UserFunctions.ResolveTenantId` auto-lands every
    `@arkansasserve.com` account there, both demo SuperAdmins live there, it is filtered out
    of the org directory, and its org page 404s. Unhiding it instead would have published the
    SuperAdmin partition — "Demo SuperAdmin 1/2" would appear on the public roster — and made
    the reserved-partition concept incoherent. The new doc is an ordinary org; root is
    untouched.
  - **Nothing was invented.** Root already carried the real Arkansas Serve description,
    mission, website, contact details and uploaded logo; the seed copies them verbatim. The
    logo reuses root's blob — `ResolveDisplayUrl` only signs the name, and does not validate
    the path against the org id, so the `…/arkansas-serve-root/…` prefix is cosmetic.
    Everything is editable afterwards in Admin Backend → tenant.
  - **Assign-only (owner decision):** visible and browsable, but an admin adds members.
    Expressed as a new `Tenant.AllowSelfJoin` flag rather than another hardcoded id check —
    it joins the existing `RbacEnabled` / `AllowGroupAdminAddVolunteers` /
    `AllowProfileSelfEdit` family, and schools plausibly want the same policy (see school
    approval tags). Editable in the tenant modal like its siblings: a flag settable only by
    hand-editing Cosmos is the same trap as the Bicep drift.
  - **The gate sits at the create-from-nothing step, not beside the root check**, so an
    existing member still gets idempotent success, a global super still gets their effective
    actor, and someone an admin already added still **adopts** their managed record at first
    sign-in — that last one *is* the assign-only path working, not a bypass of it.
  - **Defaults to true, verified against the real model:** a Tenant doc written before the
    field existed deserialises to `true`, so no backfill is needed and no existing org
    silently became assign-only. Checked all three cases (absent → true, `false` → false,
    `true` → true).
  - *Not done here:* it hosts events like any other org (nothing extra was needed — events
    are org-scoped already), but it has none yet.

</details>

### Approvals & compliance
- **School approval tags for events** — ✅ **SHIPPED 2026-07-18 (PR #92).** A School/JDC tenant
  carries an approval policy — a **default** plus **per-org** and **per-category** rules,
  **most-specific-wins** (org > category > default) — so a school can preapprove a trusted org
  *and* blanket "Political Parties & Campaigns" as approval-required without listing every org.
  The gate is one branch in `CreateServiceLog`: **preapproved** → the log is `Approved` and skips
  the queue (hours auto-count); everything else keeps the existing Pending + approval-queue path.
  Default is `approvalRequired`, so **behaviour is unchanged until a school configures it**
  (forward-only). Editor lives in the School Admin Portal. Verified in prod.
- **Real-time waiver prompting** — email + phone prompts through to a student's parent/guardian,
  with **documents uploaded by the organization**.
- **Parental account oversight** — guardian oversight features that don't require up-front
  action every single time.
- **Terms & Conditions and Privacy Policy pages** — ✅ **Built 2026-07-14 (PR #66), pending
  counsel review.** `/terms.html` + `/privacy.html`, linked from the landing footer and the
  acceptance prompt. Anonymous-accessible via the existing `/*` route; no inline scripts, so
  the CSP serves them unchanged.
  - **The text is a draft and says so.** The privacy page's data inventory is written from the
    fields the code actually persists, so it is accurate rather than boilerplate — but every
    item that is a *legal decision* rather than an observable behaviour is left explicitly
    blank under "Still to be completed": retention (**note: no TTL is configured anywhere, so
    records persist until an admin deletes them**), data-subject rights, FERPA/COPPA, juvenile
    -record confidentiality, breach notification, liability, termination, governing law.
    Those need counsel; guessing would be worse than blank.
  - **Acceptance is recorded at first login, not sign-up** — sign-up is Entra-hosted
    (`prompt:'create'`), so there is no sign-up form of ours to put a checkbox on. It lives in
    the existing first-login intake modal, which already gates required consent that way.
  - **Recorded as a version, not a boolean** (`PolicyVersions.Current` +
    `acceptedPolicyVersion`/`acceptedPolicyAt`). Re-issuing the documents therefore re-prompts
    everyone — including the moment counsel signs off and the drafts stop being drafts. The
    server validates the version it is sent (a stale tab gets a 400, not a false record) and
    stamps the timestamp itself, since that timestamp is the evidence.
  - ⚠️ **Still skippable** via "Skip for now". Blocking access until acceptance is a
    product/legal call, and forcing agreement to an *unreviewed draft* would be wrong. Once
    counsel approves and the version bumps, making it blocking is a one-line change.
  - **When the text is approved:** update the wording, remove both draft banners, and bump the
    version in all four places — `PolicyVersions.cs`, `dashboard.js`, `terms.html`,
    `privacy.html`.

### UI & branding
- **Org switcher tabs** — ✅ **Done 2026-07-14 (PR #66).** The organization dropdown is now a
  tab strip, applied in `renderScopeBar` so every page using the shared shell gets it, not
  just the dashboard.
  - **Searchable, because of scale.** A SuperAdmin's list is *every tenant on the platform*
    (`scope.js` init), not just their memberships — four orgs today, dozens once the schools
    are on. So past 6 orgs the strip gains a filter box: tabs to browse when short,
    type-to-find when long. Verified with 30 synthetic orgs: the bar stays a single 44px row,
    the strip scrolls (5443px of tabs inside 718px) rather than wrapping, and page overflow
    stays 0.
  - The active org stays visible even when it doesn't match the filter, but deliberately does
    **not** count as a match — otherwise a query matching nothing would leave one tab on
    screen with no explanation and look broken rather than empty.
  - The group selector remains a dropdown; only the org switcher changed.
- **Scope-bar option #2 — per-page scope config** — ✅ **SHIPPED 2026-07-19 (PR #103, fix #104).**
  Each scoped page declares what its switcher should offer (`PAGE_SCOPE` in `ui.js`, keyed by the
  same href as `TABS`) instead of every page receiving one globally-resolved list:
  `{ minRole, orgTypes, allTenants, showGroups }`. A page absent from the table keeps today's
  behaviour, so adding one is opt-in.
  - **`minRole` is the substance.** `scope.js` mapped EVERY membership regardless of role, so
    someone who is an OrganizationAdmin at their school and a plain volunteer elsewhere was
    *offered* the volunteer org on an admin page; picking it gives an empty view that reads as
    broken. `bestAdminOrgId` already stopped them *landing* there — nothing stopped the list
    offering it. **Latent, not live:** only one multi-org person exists today and both their
    memberships are Student, so they see no scope bar at all. It arrives with the schools.
  - **The dashboard's scope bar is gone.** `dashboard.js` reads no `Scope.*` state, so the
    switcher filtered nothing on that page while silently re-scoping every OTHER page through
    `sessionStorage`. Per-org dashboard content is its own item (below).
  - **Backend:** `GetMyMemberships` now projects `tenant.Type` — free (the tenant doc is already
    loaded) and necessary, because without it `orgTypes` could only ever narrow a SuperAdmin's
    list; everyone else's orgs come from memberships, which carried no type. An org whose type is
    **unknown always passes**, so pre-deploy memberships are still offered.
  - ⚠️ **`orgTypes` is BUILT AND TESTED BUT DELIBERATELY NOT APPLIED (PR #104).** Approvals was
    declared `orgTypes: 'schoolLike'` on sound reasoning — hours approve against the student's
    school (`ServiceLog.schoolId`), so a Community Organization has no queue. It was wrong against
    live data: **every tenant in production is typed `Organization`; there is not one School or
    JDC doc.** Schools are currently modelled as Organizations and approvals flow through them, so
    the filter dropped all five tenants and left a SuperAdmin unable to open Approvals at all.
    Caught in the prod clickthrough *after* the SWA deploy had landed; reverted to `orgTypes: null`
    within minutes. **Turn it on when real School/JDC tenants exist.**
    - *The lesson, which is the reusable part:* the 15-case local harness invented
      `School`/`JDC`/`Organization` tenants and proved the filter handles them correctly — which it
      does. It never asked whether those types exist in this deployment. A fixture that builds its
      own world can only prove internal consistency. Same lesson as the group-registration note
      above: found by running it against production, not by review.
- **Per-school branding / customizable CSS** — schools choose a **logo and color palette** that
  applies to their assigned users; assignable to school-scoped accounts.

---

## 🔚 Deferred to last — scale work

_Moved to the back of the list by the owner (2026-07-14): get the features shaped first, then
make them scale. Nothing else depends on these, so they can be picked up whenever real data
volume starts to arrive — but they must land **before** any single org gets near four figures._

### DataTables phase 2 — server-side processing at 1,200+ rows
Phase 1 (above) ships every row and lets the browser search it, which is fine at tens of rows
and wrong at thousands. Phase 2 moves paging, sorting and filtering to the server.

- **Design the query contract.** DataTables' server-side protocol expects `draw`,
  `recordsTotal`, `recordsFiltered` and one page of rows, and sends `start`/`length`, a global
  `search[value]`, per-column filters and `order[]`. The endpoint shape should be designed to
  that protocol from the start rather than retrofitted.
- **Solve Cosmos paging + counting — the actual hard part.** The protocol assumes offset
  paging and an exact filtered count; Cosmos gives neither cheaply. It pages with
  **continuation tokens**, not `OFFSET`, and an exact `COUNT` over a filtered cross-partition
  query costs a full scan — so `recordsFiltered` is the expensive field, on every keystroke.
  Options to weigh: map page numbers to cached continuation tokens; bound the count (`TOP N` +
  "1000+"); or drive search from a service better suited to it. **Decide this before writing
  the endpoints** — it dictates their shape.
- Once solved, phase 1 upgrades by swapping `data:` for `ajax:` + `serverSide: true`; columns,
  filters and markup are unaffected.

### AJAX for the remaining query surfaces
Extend the async in-place pattern to the other search/query surfaces once the users/events
pair has proven it.

---

_When picking up any upcoming item, confirm scope/specifics with the owner before building —
several (taxonomy values, branding model, waiver flow, maps integration) have open design
questions._
