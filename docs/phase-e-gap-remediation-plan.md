# Phase E — Gap Remediation Plan

**Branch:** `feat/phase-e-gaps` (based on `main` @ `79deb00`, post-crawler PR #42)
**Drafted:** 2026-07-08
**Source:** GitHub Copilot gap analysis (`copilot/identify-*-gaps`), re-verified by Claude against real `main`.

> ⚠️ **Provenance note.** The original Copilot analysis was written against a snapshot and a few items were stale or inconsistent with the live Azure setup. This plan keeps only **actionable** work in the body. Items that need no code change (#15, #17) are in the [No-Action Appendix](#no-action-appendix) with the reason. Corrections to false/inconsistent claims are called out inline as **_Correction:_**.

---

## Priority summary

| Priority | Items | Theme |
|---|---|---|
| **P0 — correctness bugs** | #3, #4 | Blob uploads 404 / photo reads fail on private containers |
| **P1 — backend gaps + durability** | #9, #10, #11 | Missing get-by-id endpoints; side-effect hardening |
| **P2 — frontend architecture** | #6, #7, #8, #16 | Extract inline JS, tighten CSP, component stubs, logo upload |
| **P3 — roadmap features** | #12, #13, #14 | Self-registration, email, report export UI |
| **P4 — polish** | #5 (note), #18 | Crawler partition doc; workflow `jq` swap |

---

## P0 — Infrastructure correctness bugs

### #3 — Provision `verification-docs/` and `org-logos/` blob containers
**Status:** TRUE. `infra/main.bicep` only declares the `event-photos` container (`main.bicep:132`). `BlobService` happily generates SAS for *any* container name, so an upload to `verification-docs/` or `org-logos/` returns **404 ContainerNotFound**.

**_Correction:_** these must be **private** containers. The storage account sets `allowBlobPublicAccess: false` (`main.bicep:122`), so "public-read" is not an option account-wide — reads must go through `GenerateReadSasToken`.

**Work:**
- Add two container resources in `main.bicep` alongside `eventPhotosContainer`, both `publicAccess: 'None'`.
- Redeploy infra. Note the **full-replacement hazard** documented at `main.bicep:305-312` — deploy infra *before* the Functions workflow (or re-run Functions deploy after). The stale `copilot/rework-workflow-yaml-documents` branch has a `deploy-infra.yml` path-filtered workflow concept worth cribbing here, but do not merge that branch (75 commits stale).

**Acceptance:** SAS upload to each container succeeds; blob is retrievable via a read-SAS URL.

### #4 — Event photos: stop using `GetPublicBlobUrl` for reads
**Status:** TRUE bug. `EventFunctions.GetUploadToken` returns `finalUrl = blob.GetPublicBlobUrl("event-photos", …)` (`EventFunctions.cs:152`) as the permanent photo URL. On the private `event-photos` container (`main.bicep:132-138`, `allowBlobPublicAccess:false`) a bare blob URL **401/404s** on read.

**_Correction:_** the Copilot suggestion "set container access to Blob (public read) **or** SAS" is half-invalid — the account flag rules out public-read. SAS is the only path, and it is already the intended design (`main.bicep:130`, `production-cutover-plan.md §4`).

**Work:**
- Persist only the stable `blobName` on the event (not a URL).
- At **display time**, mint a short-lived read URL via `GenerateReadSasToken("event-photos", blobName)`. Decide the surface: a `GET /events/{id}/photo-url` endpoint, or include a freshly-signed `photoUrl` in event read responses.
- Remove `GetPublicBlobUrl` from the event-photo flow. Keep the method only if a genuinely-public container is later introduced.

**Acceptance:** a displayed event photo loads for an anonymous browser using the returned URL; the raw blob URL without SAS returns 404.

---

## P1 — Backend gaps + durability

### #9 — `GET /servicelogs/{id}`
**Status:** TRUE. `ServiceLogFunctions` has `POST /servicelogs`, `PATCH /servicelogs/{id}`, `GET /students/me/servicelogs` — no single-log fetch (`ServiceLogFunctions.cs:14-82`). The service layer already has `GetServiceLogAsync(id, studentId)` (used at `:53`).

**Work:** add `GET /servicelogs/{id}` guarded by `AuthMiddleware`; authorize via `ResolveActorInOrgAsync` against the log's `SchoolId` (mirror the review-flow check at `:57-59`) so students see their own and admins see their org's. Partition key (`studentId`) must come from the caller context or a query param since it's the Cosmos PK.

### #10 — `GET /manage/tenants/{id}`
**Status:** TRUE. `AdminFunctions` exposes `GET /manage/tenants` (list) and `POST /manage/tenants` (create) but no get-by-id (`AdminFunctions.cs:17-42`). `cosmos.GetTenantAsync(id)` already exists (used at `:68`, `:147`).

**Work:** add `GET /manage/tenants/{id}` → `GetTenantAsync`. Reuse the same auth*level* as the list endpoint.

### #11 — Harden inline side-effects + fix the design doc
**Status:** partially TRUE, with a **wrong rationale to correct**.

**_Correction:_** the claim "Change Feed removed because SWA **Free tier** doesn't support Cosmos triggers" is inconsistent on two counts: (a) the Static Web App is **`Standard`** SKU (`main.bicep:52`), and (b) Cosmos triggers are a property of the **Functions host**, not the SWA tier. A `leases` change-feed container is in fact still provisioned (`main.bicep:231`). The same false "SWA Free tier" line is repeated in the crawler workflow header (`event-crawler.yml:7`). **Fix this rationale everywhere it appears.**

**_Correction:_** "both try/catch **swallow** failures" — inaccurate. `TryCreatePendingApprovalAsync` and `TryReviewSideEffectsAsync` both `logger.LogError` (`ServiceLogFunctions.cs:100-107`, `:128-135`). They **log but do not retry** — that is the real gap.

**Work:**
- Add durability: on failure, write a lightweight retry/outbox record (or a `Notifications`-style dead-letter doc) so a lost `PendingApproval`/`Notification` is recoverable, not just logged.
- Update the design doc that still describes a `ChangeFeedFunction` to reflect the **inline synchronous** implementation and the corrected rationale (Consumption plan / cost / simplicity — not "SWA Free tier").

---

## P2 — Frontend architecture

### #6 / #8 — Extract remaining inline `<script>` and tighten CSP
**Status:** TRUE but **overstated**. Shared logic is *already* externalized into `/js/auth.js`, `/js/api.js`, `/js/scope.js`, `/js/ui.js` (loaded via `<script src>` on every page). What remains is **one page-specific inline `<script>` block per page** (e.g. `dashboard.html:100`, `platform-admin.html:181`). That single block is why CSP still needs `'unsafe-inline'` in `script-src` (`staticwebapp.config.json:61`).

**Work:**
- Move each page's trailing inline block to `/js/pages/<page>.js` and load with `<script src>`.
- Once no inline scripts remain, drop `'unsafe-inline'` from `script-src` (keep it on `style-src` unless inline styles are also removed, or migrate to hashes).

**Acceptance:** no inline `<script>` in any page; app functions with the hardened CSP; browser console shows no CSP violations.

### #7 — Empty component stubs
**Status:** TRUE — `frontend/components/event-card.html` and `navbar.html` are **0 bytes** (confirmed). Pages inline their navbar/cards.

**Decision needed:** either (a) build a minimal client-side include/render for these two components and de-duplicate the inlined markup, or (b) **delete the stubs** if no component system is planned, so they don't imply one exists. Recommend (b) unless #6's extraction naturally introduces a render helper, in which case fold navbar/card into it.

### #16 — Org-logo SAS upload on Platform Admin
**Status:** TRUE (depends on **#3** `org-logos` container). **_Correction:_** platform-admin.html currently has **no** logo field at all (grep found none) — the "plain text URL field" described by Copilot isn't present, so this is net-new, not a replacement.

**Work:** add a SAS-gated upload widget (reuse the `events/upload-token` pattern → new `POST /manage/orgs/upload-token` for `org-logos`), store the returned `blobName`, render via read-SAS (same pattern as #4).

---

## P3 — Roadmap features (larger; acknowledged in `project-state-and-roadmap.md:480-482`)

### #12 — Student self-registration
Zero implementation. New students are admin-created managed volunteers, adopted on first login. Design a self-serve Entra External ID sign-up + first-login profile completion + tenant association. **Scope this as its own design doc** before building — it touches auth, tenant assignment, and the managed-volunteer adoption path (see `[[multiorg_membership_model]]`).

### #13 — Email notifications
Only in-app `Notification` docs today. **Recommendation:** use **Azure Communication Services Email** (native to the existing Azure stack) rather than SendGrid. Hook into the same points as `TryReviewSideEffectsAsync` (approve/reject) — and pairs naturally with the #11 durability work.

### #14 — Report export UI — **done**
_Correction:_ the "no UI" premise was **false**. `admin-portal` already had the full Service-Hour Report — date-range filters, roster table, summary line, and CSV **summary + detail** export (`Api.Reports.serviceHours` → `ReportFunctions.cs`). The only missing piece was PDF. Added a **Print / Save-as-PDF** view: a print-only header (org name, date range, generated timestamp) plus `@media print` rules that hide the app chrome and render a clean roster. No dependencies, CSP-safe. Report remains in `admin-portal` for OrganizationAdmin+.

---

## P4 — Polish

### #5 — Crawler partition (note / optional)
**Status:** TRUE and **already an accepted tradeoff in code.** Crawled events live in `Events` under `organizationId = "ark-serve-crawler"` (`CosmosService.Crawler.cs:15`). **_Correction:_** the "Containers mapping doesn't reference this partition" framing is a category error — `CosmosDb__Containers__*` maps *logical name → container name* (`main.bicep:336-342`), not partitions; `Events` is mapped. The cross-partition cost is acknowledged inline as acceptable (`CosmosService.Crawler.cs:20`).
**Real wrinkle worth documenting:** published crawled events **stay** in the crawler partition (`CosmosService.Crawler.cs:11-12`), so they surface in normal listings only via cross-partition queries. **Action:** document this in the data-flow doc; optionally re-home an event to its owning org's partition on publish if/when volume warrants. No urgent code change.

### #18 — `event-crawler.yml`: `python3` → `jq`
**Status:** TRUE. Inline `python3` at `event-crawler.yml:62-67` (build sources array) and `:106-108` (parse response). `jq` is preinstalled on `ubuntu-latest` and more idiomatic. Straightforward swap. (While in this file, also fix the false "SWA Free tier" comment at `:7` per #11.)

---

## No-Action Appendix

### #15 — .NET 8 vs .NET 10 — **already decided: stay on .NET 8 LTS**
No mismatch to resolve. `csproj` targets `net8.0`, both workflows install 8.0, `staticwebapp.config.json` uses `dotnet-isolated:8.0`, and the roadmap + cutover plan **explicitly recommend staying on 8 LTS** (`project-state-and-roadmap.md:366-368`, `production-cutover-plan.md:118`). The ".NET 10 convention" is a stale README line, already flagged as such. Optional: delete the stale README mention.

### #17 — Volunteer profile self-edit — **already enforced**
Copilot claimed "no frontend enforcement; form always enabled." **False:** `dashboard.html:179-181` hides `btn-edit-profile` when `allowProfileSelfEdit` is false. *Optional nicety only:* the rule is a permissive `memberships.some(m => m.allowProfileSelfEdit !== false)` and a UI hint (server is authoritative). Tighten to per-org context if desired, but no functional gap exists.

---

## Suggested execution order
1. **#3 + #4** together (both blob/infra; one infra redeploy). Unblocks #16.
2. **#9, #10** (small, self-contained endpoints).
3. **#11** durability + doc/rationale cleanup (also fixes #18's comment; pairs with #13).
4. **#6/#8** inline-script extraction + CSP tighten; **#7** decide stubs.
5. **#18** `jq` swap (quick).
6. **#16** logo upload (after #3).
7. **#12, #13, #14** — separate design/build efforts, sequence per product priority.
