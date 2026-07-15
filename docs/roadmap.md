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

### Approvals & compliance
- **School approval tags for events** — a school can mark some organizations **pre-approved**
  (events auto-count) and others **approval-required**.
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
