# Phase F — Priorities

**Drafted:** 2026-07-09
**Predecessor:** [phase-e-gap-remediation-plan.md](phase-e-gap-remediation-plan.md) (P0–P4 / #3–#18, all merged).
**Source:** product owner requests, grounded against `main` by Claude.

> All data sensitive to minors and court-involved youth. No PII in URLs or logs.
> Issue numbers continue the Phase E sequence (#19+).

---

## Priority summary

| Priority | Items | Theme |
|---|---|---|
| **F0 — bugs (fix first)** | #19, #21 | Dashboard hours wrong; pop-ups overflow the frame |
| **F1 — identity & intake data model** | #22, #23, #24, #20 | Student vs adult volunteer; structured intake; name-only identity; approval clarity |
| **F2 — admin UX** | #25, #26 | Searchable/filterable user tables; SuperAdmin remote access |

---

## F0 — Bugs

### #19 — Service hours don't display correctly on the dashboard — **root cause fixed 2026-07-09**
**Symptom:** a user's approved-hours total on the dashboard is wrong / missing.

**Root cause found:** the org-portal logging path (`CreateServiceLog`) never populated the log's `eventTitle`/`organizationName` — only the manual-hours path did (`ManualHoursFunctions.cs:113,115`). So the dashboard's detail table rendered **blank Event and Org columns** for every org-logged row (`dashboard.js:179,183`), reading as "hours not displaying." (The IDs are actually consistent: registration `userId` = Entra `oid` = the dashboard's `ctx.UserId`, so the totals aggregate correctly.)

**Fixed:** `ServiceLogFunctions.CreateLog` now denormalizes `EventTitle`/`OrganizationName` from server-side lookups (`GetEventAsync`/`GetTenantAsync`); `dashboard.js` guards blanks with `—` and the total with `?? 0` so pre-existing blank rows render gracefully. Backend builds clean.

**Remaining (optional, deferred):** decide the fate of the unused stored `User.TotalApprovedHours` field (write-through vs delete), and a one-time backfill of `eventTitle`/`organizationName` on historical logs. Original analysis below.

---


**Where it lives:** the dashboard reads a **live** total (`frontend/js/pages/dashboard.js:154-158` → `Api.ServiceLogs.myLogs()` → `ServiceLogFunctions.GetMyLogs`, `ServiceLogFunctions.cs:79-81`), which sums `Approved` logs from `GetServiceLogsByStudentAsync(ctx.UserId)`. `ServiceLogs` is partitioned by `studentId` (`CosmosService.cs:349,387-390`).

**Two probable root causes (investigate both):**
1. **Identity mismatch across memberships/adoption.** `ctx.UserId` is the *per-org* actor id. Hours logged under a different `studentId` (a managed-volunteer id before adoption, or a different org-context membership) live in a different partition, so the live sum reads **0 or partial**. Adoption is *supposed* to migrate them (`MigrateServiceLogsStudentIdAsync`, `CosmosService.cs:405-428`; callers in `UserFunctions.cs:145`, `MembershipFunctions.cs:228`) — verify it actually runs and completes, and that a person viewing the dashboard in one org sees hours logged in another.
2. **Stale stored field.** `User.TotalApprovedHours` (`User.cs:52-53`) exists but is not written anywhere the dashboard path touches. Any surface that reads the stored field instead of the live sum shows `0`/stale, contradicting the dashboard.

**Plan:**
- Make the **live aggregate the single source of truth**; aggregate across *all* of a person's identities (current membership UserIds + any pre-adoption `studentId`s), not just `ctx.UserId`.
- Either keep `User.TotalApprovedHours` updated on every approve/reject (write-through) **or** delete the field to remove the second source of truth. Pick one.
- Add a reconciliation/backfill for logs stranded in an old partition after adoption.

**Acceptance:** a student who logged hours as a managed volunteer, then self-registered, sees the correct approved total on first login; the number matches the detail table row-sum.

### #21 — Pop-ups / edit modals overflow the site frame — **fixed 2026-07-09**
**Symptom (already hit):** tall edit screens escape the viewport; no way to scroll the modal body.

**Fixed:** `.modal` now caps at `max-height: calc(100vh - 3rem)` with `overflow-y: auto`, `box-sizing: border-box`, `width: min(92vw, 520px)`, and the overlay carries `padding: 1.5rem` so the dialog never touches the frame edges. Verified in-browser at desktop (898px cap on a 946px viewport) and mobile (375px, 24px gutters both sides, internal scroll) — modal stays fully within the frame with a 60-row body. Original plan below.

---


**Root cause:** `.modal` (`frontend/styles/main.css:192-199`) sets `width: 90%; max-width: 520px` but **no `max-height` and no internal `overflow`**. A form taller than the viewport pushes the footer (and its buttons) off-screen with nowhere to scroll.

**Plan:**
- `.modal { max-height: 90vh; overflow-y: auto; box-sizing: border-box; width: min(92vw, 520px); }`
- Keep the footer reachable — either let the whole modal scroll, or make `.modal` a flex column with a scrollable body and a pinned `.modal-footer`.
- Audit the notification pane too (`.notif-pane` already caps `max-height: 24rem` — good; mirror that discipline everywhere).
- Verify at desktop, tablet, and mobile widths, and confirm no element renders outside `100vw`/`100vh` (the "standard windowed" expectation). Test the tallest forms first: platform-admin tenant edit, admin-backend user access.

**Acceptance:** every modal stays within the viewport at all breakpoints; footer buttons always reachable via internal scroll.

---

## F1 — Identity & intake data model

> **Status — core landed 2026-07-09 (#22, #23, #24).** Data model + validation + API + first-login intake + admin add-user form are in. What remains is downstream *rendering/consumption*, tracked with #25: name-with-context + email-on-hover across all user **tables** (only the profile card and forms use the new fields so far), a `personType`/name column in the volunteers/users tables, admin UI for the **admin-managed** background-check fields, and (optional) a one-time backfill splitting legacy `displayName` into `firstName`/`lastName`. Enforcement is **soft** (first-login prompt with "Skip for now"), not a hard lockout — revisit if intake completion needs to be mandatory. Details per-item below.

### #22 — Separate students from adult volunteers (person type) — **landed**
**Done:** new `PersonTypes` constants (`Student | AdultVolunteer | Staff`); `User.personType` (orthogonal to `AdminLevel`); set via first-login wizard and admin add-user; `PersonTypes.IsMinorType` gates student guardian-consent intake. Default is an **explicit choice**, not inference (person picks at first login; admin picks when adding).
**Today:** there is no student/adult distinction on a person. `AdminLevel` (`User.cs:13-14`, values `Student | EventAdmin | GroupAdmin | OrganizationAdmin | SuperAdmin`) is a **permission tier**, not a description of who the human is. "Student" being a role conflates the two.

**Plan — add an orthogonal `personType` dimension:**
- New `User.personType`: `Student | AdultVolunteer` (extensible to `Staff`). Independent of `AdminLevel`, so e.g. an adult volunteer can also be an `EventAdmin`.
- Drives everything downstream: which intake fields are required (#23), minor-consent handling, event eligibility, and reporting/filter facets (#25).
- Set at creation/first-login; default inference: managed volunteers created by a school admin → `Student`; org-directory self-join by an adult → `AdultVolunteer` (make it an explicit choice, not a guess).
- **Minor-safety:** for court-involved youth, `personType = Student` should gate guardian-consent capture and tighten what's shown publicly. Treat this as a requirement, not a nicety.

**Acceptance:** every user has a `personType`; admin lists and reports can filter by it; intake branches on it.

### #23 — Structured user data collection at create/add — **landed**
**Done:** intake schema on `User` (student: `grade`, `dateOfBirth`, `guardian{Name,Email,Phone}`, `guardianConsent`; adult: `affiliation`, `emergencyContact{Name,Phone}`; admin-only: `backgroundCheck{Status,CompletedAt}`). Single required-field policy in `IntakeValidation` (keyed by `personType`; background-check excluded — it's admin-managed, not self-reported). Two entry points share it: the first-login **wizard** (`dashboard.html` intake modal + `dashboard.js`) and the admin **add-user form** (`admin-backend`). `PUT /users/me` persists intake and recomputes `profileComplete`; the self-edit lock is exempted while a profile is still incomplete so first-login can't dead-end. Validation runs both client- and server-side. **Remaining:** admin UI to set the background-check fields; DOB is captured but not yet used for age-banding.

_Original analysis:_ profile creation is thin get-or-create (`UserFunctions` `/users/me`) plus admin-created managed volunteers; fields are sparse (`displayName`, `email`, `phone`, `grade`, `schoolId`).

**Plan:**
- Define an **intake schema** keyed by `personType`:
  - *Student:* legal first/last name, grade, school, guardian contact + consent, DOB/age band.
  - *Adult volunteer:* legal first/last name, affiliation/employer, emergency contact, background-check status/date.
- Two entry points, one schema: (a) **first-login wizard** ("complete your profile") replacing the silent get-or-create; (b) **admin "Add user" form** collecting the same structured fields.
- Server-side validation of required-vs-optional per type; store on the `User` doc; never put PII in URLs/logs.

**Acceptance:** a new user cannot reach the app past onboarding without the required fields for their `personType`; admin add-user captures the same.

### #24 — Name-only identity (no separate usernames) — **core landed**
**Done:** `User.firstName`/`lastName` added; `User.ComposeName` is the single rule that keeps `displayName` in sync (falls back to legacy `displayName`, then email). Names seed from token `given_name`/`family_name` (or a split of the `name` claim) on bootstrap; the intake wizard and admin form both collect structured first/last. No username field exists or was introduced. **Remaining (folds into #25):** apply the decided disambiguation — context line **+** email-on-hover — across all user tables (currently only the profile card renders the name); sort/search by last name.

_Original analysis:_ there is **no username field today** — identity keys are the Entra `externalId` and `email` (`User.cs:10-11,31-32`); humans are shown via `displayName`. So "no usernames" is mostly about discipline, plus one data-model improvement.

**Plan:**
- Split `displayName` into `firstName` + `lastName` (keep a computed `displayName` for rendering). Enables reliable sort, "Last, First" display, and name search (#25).
- **Never surface `email`/`externalId` as a handle** in any UI — they stay backend keys only.
- **Disambiguation without usernames (decided 2026-07-09):** show a secondary context line (school/org, grade) in tables and dropdowns, **and** email-on-hover as the tiebreaker when even the context matches. Never invent a username or append numbers.
- Collect legal name at intake (#23); reference users by name everywhere (tables, approvals, notifications, reports).

**Acceptance:** no username field or generated handle anywhere; all user references render as a person's name with context for disambiguation.

### #20 — Approval process clarity
**Symptom:** the approve/reject flow is confusing.

**Where:** `admin-portal` queue → `GET /api/approvals` → `PATCH /servicelogs/{id}` (`ServiceLogFunctions.cs`). Pairs with #19 since both concern the hours lifecycle.

**Plan:**
- Make each queue row self-explanatory: **who** (student name, #24), **what** (event/date/hours), **when submitted**, **by whom** (org logging on their behalf), current **status**.
- Explicit, labeled Approve / Reject affordances with a required reason on reject; confirmation + immediate status feedback; show the resulting notification.
- Clarify the state machine in-UI: `Pending → Approved/Rejected`, and where reconciliation-on-read applies.

**Acceptance:** an admin can tell at a glance what they're approving and see the outcome without guessing.

---

## F2 — Admin UX

### #25 — Filterable / searchable user tables
**Today:** user tables in `platform-admin.js` and `admin-backend.js` render lists without robust search/filter.

**Plan:**
- Server-side query params: search by **name** (uses #24 first/last), filter by `personType`, `adminLevel`, org/school, `status`, `isDemoUser`.
- Table UX: a search box + facet filters + sortable columns + pagination for large tenants.
- Reuse the same list endpoint shape across platform-admin and admin-backend.

**Acceptance:** an admin can find any user by typing part of their name and narrow by type/role/org quickly.

### #26 — SuperAdmin remote access (impersonation), incl. demo users
**Today:** SuperAdmin can *manage* demo users (`AdminFunctions` `GetDemoUsers`/`ResetDemoUsers`, `:255-286`) but cannot **act as** an arbitrary user. No impersonation path exists.

**Plan:**
- New SuperAdmin-only endpoint to mint a **scoped, time-limited, audited** session that acts as any target user (real **or** demo).
- Persistent, unmistakable "You are viewing as {name}" banner with a one-click exit; full audit log (who impersonated whom, when, what they did).
- Enforce SuperAdmin via the existing `IsGlobalSuperAsync` check (`AdminFunctions.cs:90`); reuse `ResolveActorInOrgAsync` for the target's per-org context.
- **Security:** this is the most sensitive item in the app — minors' records. Impersonation must be audited, non-repudiable, SuperAdmin-only, and ideally consent-flagged. Design-doc it before building.

**Acceptance:** a SuperAdmin can enter any user's session (including demo users), see exactly what they see, exit cleanly, and every action is logged.

---

## Suggested execution order
1. **#19 + #21** — the two bugs already hit in use.
2. **#22 → #23 → #24** — data-model trio; do together (schema + migration land once). #20 folds in with #19's hours work.
3. **#25** then **#26** — admin UX; #25's name search depends on #24's name split; #26 is a design-doc-first security feature.
