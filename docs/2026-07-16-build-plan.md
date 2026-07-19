# Arkansas Serve — Build Plan (next cycle)

_Created 2026-07-16. Companion to `2026-07-16-remaining-work-triage.md`. Sequenced per the
owner-approved order: **O1 → O2 → 14 → 16 → 12 → 10② → 13**. Decisions resolved this session
are inlined. Confirm scope on each item before building, per the roadmap's standing note._

Decisions locked 2026-07-16:
- **Tag gating** → state on the registration record (`EventRegistration`); ship same-org-only first, extend cross-org with #14/#19.
- **Maps** → free stack (Leaflet + OSM tiles, Nominatim optional); ZIP search needs no API. No Google billing.
- **Waiver channel** → Azure Communication Services, **email-only** for v1; SMS decided later.
- **Branding** → fixed palette tokens + logo, hex-validated; no arbitrary CSS.

---

## O1 — Crawler secrets (owner action, not code)
Confirmed failing: `CRAWLER_SHARED_SECRET` absent from the repo; every scheduled run 401s at
the validate step (last: 2026-07-16 11:34 UTC, 6s). Fix = set both halves to one value, then
`gh workflow run event-crawler.yml -f dry_run=true` to verify a 200. Commands in the chat
answer. **Blocks nothing in code; unblocks the daily import pipeline.**

---

## O2 — `BlobService` constructor throw  ·  ~0.5 day  ·  BUILD NOW
**Problem.** The ctor parses `BlobStorage__ConnectionString` with no guard, so a present-but-
malformed value throws at construction and 500s **every** `EventFunctions` route before auth.
Absent → logs-and-degrades; malformed → throws. Inconsistent, and it's why the backend won't
run locally.

**Do.**
1. Wrap the connection-string parse in `Services/BlobService.cs` in try/catch; on failure log a
   warning and enter the same degraded (no-blob) mode the absent case already uses.
2. Confirm every blob read path already tolerates degraded mode (SAS/display-url helpers return
   null/placeholder rather than throwing) — the org-logo path is the one to check.
3. Local-dev note in README: `DOTNET_ROLL_FORWARD=LatestMajor` for the ASP.NET Core 8 gap.

**Verify.** Start the Functions host locally with a deliberately bad connection string; hit an
events route; expect 200 (degraded), not 500. Then a valid string; expect blob URLs resolve.

---

## O3 — Verification-debt clickthrough  ·  ~0.5 day  ·  do alongside O2
Prod clickthrough of merged-but-never-exercised surfaces: #9 delete-series + recurring
create-form; #10 category create/update forms; #11 tag endpoints (no UI yet — exercise via
REST). Record results in `manual-verification-checklist.md`.

---

## #14 — Day-of check-in (QR + admin page)  ·  ~1 week  ·  BUILD NOW
Unblocks `blockCheckIn` enforcement reserved in `Models/UserTag.cs`. The data model is largely
present: `EventRegistration` already has `CheckedInAt` and `ShiftId`.

**Backend** (`Functions/RegistrationFunctions.cs`, or a new `CheckInFunctions.cs`):
- `POST /events/{eventId}/checkin` — body `{ registrationId | memberId, shiftId? }`; stamps
  `CheckedInAt`; idempotent (re-check-in is a no-op, returns current state). Authz: EventAdmin+
  in the **event's org** (matches Finding 9's "runs the event on the day" rule).
- `POST /events/{eventId}/checkin/undo` — clears `CheckedInAt`.
- `GET /events/{eventId}/roster` — check-in view: registrants grouped by shift, each with
  check-in state. Reuse the existing registration read; add `checkedInAt` to the projection.
- **Walk-in** = reuse the managed-volunteer + group-registration machinery: create/adopt a
  per-org `User` doc, register (reserve-first via `AdjustSlotsAsync`), then check in — one
  endpoint `POST /events/{eventId}/checkin/walkin`.
- **QR**: generate server-side a signed, short-lived deep link to the check-in flow scoped to
  one eventId (not a raw id in the URL — sign it). Endpoint `GET /events/{eventId}/checkin/qr`.
- **`blockCheckIn` gate**: when the event's org has tags with `enforcement == "blockCheckIn"`,
  refuse check-in naming the missing tag. Add `BlockCheckIn` to `TagEnforcement` (currently
  reserved in a comment) and to `IsValid`.

**Frontend**: new admin check-in page (or a tab on `event.html` for EventAdmin+): shift/user
roster, check-in toggles, walk-in add, QR display. Mobile-first — this is used on-site.

**Open sub-decision (small):** does the QR resolve to a public self-check-in (student scans,
confirms) or admin-only (admin scans the student)? Recommend **admin-scans** for v1 — simpler
authz, matches the "admin runs the door" model. Offline (#15) is a separate, later item.

**Verify.** In prod: check in by user, by shift, undo, add a walk-in, and confirm a
`blockCheckIn` tag refuses at the door.

---

## #16 — ZIP / geo search  ·  ~3-4 days  ·  BUILD NOW  ·  no external API
Unblocks #18. Independent of #17.

**Model** (`Models/Event.cs`): ensure the address block carries `zip`, `city`, `county`
(add whichever are missing). Backfill `county` from `zip` via a bundled dataset (below).

**Data**: bundle a static US ZIP→(city, county, lat/lng) table scoped to Arkansas (Census ZCTA
or a free ZIP CSV) as a resource file; a small `ZipLookup` service resolves county/city/coords
from a ZIP. No network dependency.

**Create form** (`org-portal.html` / event create): prompt for ZIP; auto-fill city/county from
`ZipLookup`; let the user correct.

**Search** (`events.html`): extend the existing DataTables/PR-70 filter set with ZIP, town, and
county inputs. County/town match server-side or client-side depending on where the row set
lives today (PR #70 indexed a hidden mirror client-side — follow that pattern).

**Verify.** Create an event by ZIP → city/county auto-fill; filter the events list by ZIP, by
town, by county; confirm date-filter regression from PR #70 stays local-calendar-correct.

---

## #12 — School approval tags for events  ·  ~4-5 days  ·  BUILD NOW
The missing guardrail for the "Political Parties & Campaigns" category shipped in #10.

**Model** (`Models/Tenant.cs`, on the **school** tenant): an approval-policy map, e.g.
`Dictionary<orgId, "preapproved" | "approvalRequired">` plus a default for unlisted orgs
(recommend default = `approvalRequired` for safety, or `preapproved` if the owner wants
opt-in friction only — **confirm default**). Optionally key by service-category as well as by
org, so a school can blanket "Political Parties & Campaigns" as approval-required without
listing every org.

**Gate** (`Functions/ApprovalFunctions.cs` / `ServiceLogFunctions.cs`): when hours are logged
against an event, look up the logging student's school policy for that event's org/category —
`preapproved` → auto-count (existing path); `approvalRequired` → route to the pending-approval
queue that already exists. This rides the current approval machinery rather than adding a new
one.

**Frontend**: school-admin UI (in `admin-portal.html` or `org-portal.html`) to set per-org /
per-category approval policy.

**Verify.** Mark an org approval-required for a school; a student at that school logs hours →
lands in the queue, not auto-counted. Mark preapproved → auto-counts.

**Decisions locked 2026-07-18:**
- **Default for unlisted orgs → `approvalRequired`.** Preserves today's behavior (every log is
  Pending + queued until reviewed); nothing auto-counts until a school deliberately preapproves.
  So #12 ships with zero behavior change until a school configures a policy.
- **Key by BOTH org and category, most-specific-wins** (org rule > category rule > default).
  Category-keying is what lets a school blanket "Political Parties & Campaigns" as
  approval-required without listing every org — the guardrail this feature exists for — while
  org-keying still preapproves a specific trusted partner.

Notes for the build: the policy lives on the student's **School/JDC** tenant (`ServiceLog.schoolId`),
not only "school" — JDCs approve their people's hours too. The gate goes in `CreateServiceLog`
(the event, hence its category, is already loadable there). Forward-only: existing Pending logs
are untouched. Edge: a student with no `schoolId` has no policy to apply → keep the current
Pending path.

---

## #10② — Self-define service category with approval  ·  ~1 week  ·  BUILD NOW
Design settled. Supersedes the "fixed list in code" decision. `ServiceCategories` is static
today (`Models/ServiceCategories.cs`) — this makes the vocabulary partly stored.

**Model**: a stored `ProposedCategory` (label, proposingOrgId, status
`pending|approvedNew|approvedAlias`, `aliasOfCanonical?`). Canonical list stays code-defined;
approved-new values extend a stored supplement; aliases map a proposed label onto an existing
canonical value.

**Flow**:
- Org create/update: an org may propose a category → stored as `pending`, shown to that org as
  **"Other (pending review)"**. **A pending value must never enter search/filter facets** —
  guard the facet builder from PR #70 and the org directory.
- SuperAdmin queue (`admin-backend.html`): approve-as-new (adds to the stored supplement) **or**
  approve-as-alias onto an existing canonical value. The alias path is the point — prevents
  "Food Bank"/"food bank"/"Foodbank" fragmentation.
- On approval, the proposing org's category resolves to the canonical/new value.

**Verify.** Propose a category → it shows as pending to the org and is absent from filters →
SuperAdmin approves-as-alias → the org's org now filters under the canonical value.

**Decisions locked 2026-07-18:**
- **Org admins self-serve the proposal.** Today only SuperAdmins set an org's `serviceCategory`
  (platform-admin); this adds a `serviceCategory` field with an "Other → propose" path to the
  **org-admin** settings form (`admin-backend.html`), so an org proposes and a SuperAdmin approves.
- **Events can also propose inline.** Both the org `serviceCategory` AND the event `category`
  are proposal entry points, so BOTH must quarantine a pending value from search/filter facets.

**Build notes / findings:**
- The vocabulary is read from the hardcoded list in **five** places: `ServiceCategories.All`
  (backend `IsValid`, 4 call sites — EventFunctions, AdminFunctions x2, and the #12 policy) and
  `Taxonomy.SERVICE_CATEGORIES` (frontend: event-create, events filter, org serviceCategory,
  #12 grid). #10② adds a `GET /api/categories` returning **canonical + approved-new** (never
  pending); the frontend dropdowns/facets switch to it. `IsValid` is relaxed to accept a
  currently-pending value for storage while it stays out of every facet.
- The org directory (`organizations.js`) has **no** category facet today, so "guard the facet
  builder" is mainly: don't introduce one that lists pending, and render a proposing org's own
  category as "Other (pending review)".
- Recommended storage: a singleton vocabulary doc (approved-new[], aliases{label→canonical},
  proposals[]) — one cheap read for the effective list; per-proposal org state lives on the
  proposal entry. Confirm container/shape at build time.

## #13 — User assignment under an org/EventAdmin  ·  ~1 week  ·  BUILD NOW
**Model** (`Models/User.cs`, per-org doc): `assignedAdminId?` (the admin who oversees this
volunteer). Optional per-admin notification-preference block.

**Backend** (`Functions/MembershipFunctions.cs` / `MatrixFunctions.cs`):
- Assign/unassign a volunteer to an admin (OrganizationAdmin+ in the org).
- The assigned admin gets scoped oversight: their hours/approvals views filter to assigned
  users; per-action notifications (approval needed, hours logged) route to the assigned admin;
  direct-comms = a notification the admin can send to assigned users (reuse
  `NotificationFunctions.cs`).

**Frontend**: assignment control in the role matrix (`admin-portal.html`); an "my assigned
volunteers" view for the admin; notification-preference toggles.

**Verify.** Assign a volunteer; log hours as them → the assigned admin is notified and sees the
approval; send a direct notification → it lands.

**Decisions locked 2026-07-18:**
- **Many admins per volunteer** (not one). The volunteer's per-org User doc carries a LIST of
  assignments, not a single `assignedAdminId`.
- **Per-assignment notification prefs.** Each assignment is an `{ adminId, notifyOnHours,
  notifyOnApproval }` record (prefs default true) — the assignment list IS the per-assignment
  pref store. Every routing/filter/comms path fans out over the list.

**Revised design (supersedes the single-field model above):**
- **Model** (`User.cs`): `assignedAdmins: [{ adminId, notifyOnHours, notifyOnApproval }]` on the
  per-org doc. Empty/absent = unassigned.
- **Placement correction:** the assign control belongs in **admin-backend "User Access
  Management"** (OrganizationAdmin+ reach it via `UpdateUserAccess`), NOT platform-admin's
  super-only role matrix. `UpdateUserAccess` already replaces `groupIds`/`eventAdminEventIds` as
  full lists — extend it to replace `assignedAdmins` (OrgAdmin sets WHO oversees; each new entry
  defaults prefs true; validate every adminId is an EventAdmin+ member of the same org).
- **Pref editing** is the assigned admin's own call (it is their inbox): a self-service endpoint
  lets an EventAdmin+ toggle `notifyOnHours`/`notifyOnApproval` on THEIR OWN assignment for a
  given volunteer, without OrgAdmin rights.
- **Routing:** `CreateServiceLog` (and the approval path) fans out — notify each assigned admin
  whose matching pref is true. Today `CreateServiceLog` notifies no admin, so this is additive.
- **Direct comms:** new `POST /notifications` — an admin messages the volunteers who list them in
  `assignedAdmins` (reuses `Notification { userId, type, message, relatedId }`; no send endpoint
  exists yet).
- **Scoped views:** "my assigned volunteers" = org members whose `assignedAdmins` contains me;
  the admin's hours/approvals filter to that set.

---

## #11② — Tag admin UI (+ finish the gate)  ·  ~3-4 days  ·  TEED UP 2026-07-18
Tag BACKEND already shipped (2026-07-15): definitions API `GET/POST/PUT
/manage/backend/tenants/{id}/user-tags` (OrgAdmin+) + per-person state `PUT
/manage/volunteers/{memberId}/tags/{tagId}` (GroupAdmin+). `blockCheckIn` enforcement shipped
with #14 (same-org). Missing: any frontend, `Api.Tags` helpers, and the `blockRegistration` gate.

**Decisions locked 2026-07-18:**
- **Build the `blockRegistration` gate too**, not UI-only. Add it in `RegisterEvent`, mirroring
  #14's same-org `BlockedByTagsAsync` (refuse sign-up naming the missing tag; cross-org registrants
  skip, per the locked cross-org decision). Completes the enforcement story so the UI can offer all
  three levels honestly.
- **Per-member tag state lives in the admin-backend Volunteers card** (GroupAdmin+, matching
  `SetVolunteerTag`) — a per-volunteer "Credentials" action, not User Access Management.

**Build:**
1. `Api.Tags` — wrap the existing endpoints (list/create/update tenant tags; set member state).
2. **Tag definitions manager** — new admin-backend card (OrgAdmin+, like Nested Groups): list +
   create/edit/archive with label, description, enforcement (advisory / blockRegistration /
   blockCheckIn, each clearly explained), and optional `expiresAfterDays`.
3. **Per-member state** — Credentials action in the Volunteers card → set None/Pending/Complete
   (+ note/date) via `SetVolunteerTag`; render the live current/expired read from `IsCurrentAt`.
4. **`blockRegistration` gate** in `RegisterEvent` (+ group registration), same-org only.
5. Verify in prod: define a `blockRegistration` tag → a same-org member without it is refused at
   sign-up naming the tag → record the tag Complete → sign-up succeeds; advisory only flags.

Build note: confirm `Api.Volunteers.list` returns each member's `.tags` for the current-state
read, or fetch per-member on opening the Credentials modal.

## Not in this cycle (unchanged from triage)
- **#17 / #19 / #21** now have their tech decisions (free maps / ACS email / palette tokens) but
  still want a short design pass before building.
- **#18** waits on #16 + #17. **#1/#2** wait on counsel. **#22-24** stay deferred to scale.

---

## Suggested cadence
O1 (you, today) ∥ O2+O3 (0.5-1 day) → #14 (~1wk) → #16 (~4d) → #12 (~5d) → #10② (~1wk) →
#13 (~1wk). Each lands as its own PR with a prod clickthrough before the next starts — the
stacked-PR breakage and green-build-hides-bugs lessons from last session both argue for
one-at-a-time, verified-in-prod.
