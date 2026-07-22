# Arkansas Serve — Feature Audit & Status Chart

_Created 2026-07-22. A single chart of **every** feature from the planning docs against where it
actually stands. Source of truth for shipped work remains `roadmap.md` — this is a cross-cutting
snapshot, and `roadmap.md` was refreshed the same day to match the two staleness catches below._

**Status legend:** ✅ shipped & live · 🟡 partial (staged, more remains) · 🔵 designed, not built ·
⛔ blocked (external input). Counts: **33 shipped · 6 partial · 6 planned · 4 blocked** (item-level;
sub-stages roll up).

## Method — live-verified, not doc-trusted

Status was checked against the **live production app in an authenticated Super Admin session**
(dashboard, `events.html` + maps, `admin-backend.html`, and the New Event form), not read from the
roadmap alone. That pass produced three corrections the docs would otherwise have gotten wrong:

- **Maps #17/#18 are live** (split map view renders on `events.html`; geocoding/autocomplete on
  event create) — `roadmap.md` had them as *upcoming*. Now marked shipped.
- **Recurring create-form UI (③) is deployed** — the "Repeat this event on a schedule" control is on
  the New Event form — `roadmap.md` had ③ as *remaining*. Now marked deployed (end-to-end series
  creation still unverified).
- **Scope-bar root-duplicate is resolved** by the single-org collapse (#124): the switcher shows one
  clean "Arkansas Serve" + two demo orgs.

Also observed live: crawler draft queue empty (matches "imports 0"); two test category proposals
sitting unresolved in the queue; a 2.0h service log crediting a deleted tenant; guardian magic link
is returned to the admin in the API response, **not emailed** (`EmailService.SendAsync` is a no-op
and isn't wired into the link path yet).

**Not independently re-verified live this pass:** Approvals page internals, Platform Admin,
Organizations directory, `guardian.html` redeem flow, terms/privacy, offline check-in (unbuilt).

---

## 1 · Auth & multi-org correctness

| Item | Dates | Touches | Pages | Variables | Future work | Bugs / incomplete |
|---|---|---|---|---|---|---|
| ✅ Strongest adminLevel across memberships (Finding 2) | PR #57, 2026-07-13 | Scope bar, route guards, dashboard | `auth.js`, `scope.js` | `ADMIN_RANK`, `strongestLevel()`, `adminLevel` | None — foundation | none open |
| ✅ Real display name in shell (Finding 3) | PR #57 | Header shell, greeting | `auth.js`, `ui.js` | `displayName` vs token `name` | None | Minor: per-org `displayName` can differ across pages |
| ✅ Event detail registered-state (Finding 5) | PR #57 | Registration, cancel | `event.html`, `event.js` | `myRegistration` | None | none open |
| ✅ Admin Backend for secondary-org admins (Finding 7) | PR #58 | Scope default, context endpoint | `admin-backend.html`, `scope.js` | `bestAdminOrgId`, `ResolveActorInOrgAsync` | None | none open |
| ✅ Cancel authorizes per-membership (Finding 9) | PR #62, 2026-07-14 | Group reg, dashboard, check-in | `RegistrationFunctions.cs` | `ResolveActorInOrgAsync`, `AdminLevels.AtLeast` | Reuse pattern everywhere — never trust token level | resolved; still the #1 recurring gotcha class |
| ✅ Cross-org cancel frees the counter (Finding 8) | PR #59 | Slots, shifts, reconcile | `RegistrationFunctions.cs` | `EventRegistration.OrganizationId` | None | resolved |
| ✅ Org-less users → `unassigned` partition (Finding ①) | PR #65, 2026-07-14 | JoinOrg migration, empty-state | `dashboard.js`, `scope.js` | `TenantIds.Unassigned`; dropped `Claim("tid")` | None | resolved; 3 orphan rows deleted 2026-07-15 |
| ✅ PlatformAdmin bootstrap backdoor closed | 2026-07-09 | SuperAdmin host org, Bicep param | `UserFunctions.cs`, AuthMiddleware | `platformAdminEmailDomain` | None in code | ⚠️ Bicep drift: `main.prod.bicepparam` still names the domain — an apply re-opens it |

## 2 · Infrastructure & Ops

| Item | Dates | Touches | Pages | Variables | Future work | Bugs / incomplete |
|---|---|---|---|---|---|---|
| 🟡 Daily event crawler (shared-secret auth) | 2026-07-15 re-enabled | Draft events, review queue, publish | `CrawlerFunctions.cs`, `admin-backend.html` | `Crawler__SharedSecret`, GH `CRAWLER_SHARED_SECRET`, `isCrawled` | Set per-source API keys | **Imports 0 every run** — keyed sources skip silently; queue empty in prod (owner action). Delete unused `CRAWLER_SERVICE_TOKEN` |
| ✅ Cosmos/Blob key rotation + firewall | 2026-07-14 | All data access, Bicep | infra / app settings | `CosmosDb__ConnectionString`, `BlobStorage__ConnectionString` | Real isolation needs EP1 + VNet + Private Endpoint (~$150+/mo) — open cost decision | Partial by necessity (Y1 no stable IPs); admits any Azure-resident caller (key-gated) |
| 🔵 BlobService ctor throw (triage O2) | open | Every EventFunctions route, local-dev | `BlobService.cs` | `BlobStorage__ConnectionString` | Wrap ctor parse in try/catch → log-and-degrade | **Live bug:** a malformed conn-string 500s every EventFunctions route before auth |
| ✅ Deploy tooling (wait-for-deploy) | PR #109/#111/#125 | Every deploy verification | `scripts/wait-for-deploy.sh` | `AZURE_STATIC_WEB_APPS_API_TOKEN` | None; never hand-roll `until` loops | Gotcha: `gh run list --commit` needs the full 40-char SHA or returns empty + exit 0 |
| ⛔ Infra drift / Bicep non-authoritative | Deferred 2026-07-15 | Firewall, rotated keys, containers, admin domain | `infra/main.bicep`, `main.prod.bicepparam` | `publicNetworkAccess`, `listConnectionStrings()` | Reconcile Bicep to live state before any apply (owner-deferred) | **An apply today reverts firewall, clobbers rotated key, re-opens admin bootstrap**; what-if never ran clean (OIDC) |

## 3 · Platform & UX foundations

| Item | Dates | Touches | Pages | Variables | Future work | Bugs / incomplete |
|---|---|---|---|---|---|---|
| ✅ DataTables phase 1 (client-side) | 2026-07-14 | Users & events tables | `datatables.js`, `events.html`, `admin-backend.html` | `data:` (client), assumes <~100 rows | Phase 2 server-side near 1,200 rows | none — stepping stone by design |
| ✅ Responsive containment (table scroll) | 2026-07-14 | Modals (root cause), all 12 tables | `main.css` | `.table-scroll` (overflow-x:auto) | None | none — measured at 375×812 |
| ✅ Phone header pop-out drawer | 2026-07-14 | Depends on table-scroll fix | `ui.js`, `main.css` | `aria-expanded`, `aria-controls` | 641–1010px still wraps 7 tabs (phone-only, left as-is) | none |
| 🟡 iOS safe area / edge-to-edge | 2026-07-15 opted in | All 13 pages, toasts, modal, navbar | all pages, `main.css` | `viewport-fit=cover`, `env(safe-area-inset-*)` | **Needs a real-device iOS pass** — env() is 0 on desktop | Footer flush (gap 0) with home indicator by choice |
| 🟡 AJAX everywhere for search/query | ongoing | Events search, all query surfaces | `events.js`, `api.js` | — | Extend in-place async pattern to remaining surfaces | none — events search async; others pending |

## 4 · Events & scheduling

| Item | Dates | Touches | Pages | Variables | Future work | Bugs / incomplete |
|---|---|---|---|---|---|---|
| ✅ Spots-left per shift | PR #101/#102 | Card, detail, filter, sort, slot-drift | `availability.js`, `event.html`, `events.html` | `maxSlots`, `currentSlots`, `shifts[].capacity/filled` | None | resolved; surfaced a live slot-leak on the way |
| ✅ Slot-drift reconcile | PR #112 | Cancel path, per-shift availability | `RegistrationFunctions.cs`, `CosmosService.Reconciliation.cs` | `POST /manage/backend/events/{id}/reconcile-slots` | None; repaired live event `9f618807` | resolved — cancel-path leak now logs at Error |
| ✅ Recurring events — occurrences materialised (① + ②) | 2026-07-15 | Registration, check-in, search, DST | `RecurrenceExpander.cs`, `RecurrenceRule.cs`, `EventFunctions.cs` | `seriesId`, `recurrence`, `America/Chicago` constant | Decide whether the list collapses a series into one card | none — DST verified both directions across 2027 |
| ✅ Recurring events — create-form UI (③) | present in prod 2026-07-22 | Event create form | `org-portal.html`, `org-portal.js` | "Repeat this event on a schedule" toggle | **Confirm the UI writes a valid `RecurrenceRule` end-to-end** (create a real series) | roadmap.md had marked ③ "remains" — reconcile + verify full create |
| ✅ Day-of check-in (online) + self-check-in | PR #89 (fix #90) | QR, walk-ins, blockCheckIn tag gate | `event-checkin.html`, `checkin.html`, `CheckInFunctions.cs` | `checkInCode`, `checkInCodeExpiresAt` | Offline is a separate item | none — walk-in, self-check-in, blockCheckIn refusal verified |
| 🔵 Offline day-of check-in (#15) | not started | PWA cache, queued writes, sync | `event-checkin.html` (extends) | local cache / queue | Local-cache + queued-write/sync PWA model, scoped to one event's roster | n/a — unbuilt |
| ✅ Group registration (①ident ②backend ③UI) | 2026-07-15→18 | Slots reserve-first, per-person questions, scope.js | `event.js`, `RegistrationFunctions.cs` | `EventRegistration.MemberId`, `POST /registrations/group` | Legacy `UserId` arm can drop once every row carries `memberId` | none; all-or-nothing on overflow (by design); interacts w/ tag gating |

## 5 · Discovery, search & maps

| Item | Dates | Touches | Pages | Variables | Future work | Bugs / incomplete |
|---|---|---|---|---|---|---|
| ✅ Event search / sort / filter | PR #70, 2026-07-15 | Cards grid, ZIP filter, map | `events.html`, `events.js`, `datatables.js` | `organizationName` (needed populating), local-day date compare | None | none — verified live |
| ✅ ZIP / geo search | PR #91, 2026-07-18 | Event create, list filters, maps | `events.html`, `org-portal.html`, `ZipLookup.cs`, `GeoFunctions.cs` | `zip`, `city`, `county`, `latitude`, `longitude`, `GET /api/geo/zip/{zip}` | None — bundled 706-ZIP AR dataset, no billing | none — ZIP/Town/County filters verified live |
| ✅ Maps #17 — geocoding + address autocomplete | PR #115, 2026-07-20 | Event create, ZIP coords, split map | `maps.js`, `org-portal.js` | `GoogleMaps__ApiKey` (referrer-restricted), client-side geocode | **Set a budget alert** — bills per event save. Exercise Places autocomplete typing + org-create portal | roadmap.md was stale (listed upcoming). Pre-#16 events keep ZIP-centroid coords (mixed dataset) |
| ✅ Maps #18 — split search map | PR #116, 2026-07-20 | Shares #70 filter/sort stack | `events.html`, `eventmap.js` | list/map toggle, fan-out for coincident pins | Verify **color-coded-by-tag** pins — only 1 live event, can't confirm | roadmap.md was stale; degrades cleanly with no key |

## 6 · Dashboard

| Item | Dates | Touches | Pages | Variables | Future work | Bugs / incomplete |
|---|---|---|---|---|---|---|
| ✅ Per-org, role-aware dashboard | PR #106 (fix #107) | Assignment (#13), hours, approvals | `dashboard.html`, `dashboard.js` | `ServiceLog.schoolId` (credit key), `GET /manage/me/overseers` | None | **Live data:** a 2.0h log whose `schoolId` names a deleted tenant — surfaced by the "credited by an org you're no longer in" line (verified live) |

## 7 · Organization & user model

| Item | Dates | Touches | Pages | Variables | Future work | Bugs / incomplete |
|---|---|---|---|---|---|---|
| ✅ Org taxonomy L1 (org style) | pre-existing | orgTypes scope filter, approvals | `admin-backend.html`, `OrgTypes.cs` | `Tenant.Type` (School / JDC / Organization) | Re-type real schools; build a demo School tenant | 2 tenants still hold lowercase `"organization"` (root, Test O3) — harmless data fix |
| ✅ Org taxonomy L2 — service categories | 2026-07-15 | Org + event classification, #70 search | `admin-backend.html`, `org-portal.html`, `taxonomy.js`, `ServiceCategories.cs` | `Tenant.ServiceCategory`, `Event.Category`, one shared vocab | None (self-define is its own row) | none — full canonical list verified live |
| ✅ Faith as an attribute | 2026-07-15 | Directory filters, categories | `Tenant.cs`, directory | `Tenant.FaithBased` (orthogonal) | Denominational `faithAffiliation` deferred — needs an agreed list | none; open Q: keep faith-as-attribute? (non-blocking) |
| ✅ Self-define category w/ approval | PR #93 (+scrub #95) | Category queue, aliasing, facets | `admin-backend.html`, `categories.js`, `CategoryService.cs`, `CategoryFunctions.cs` | `Tenant.CategoryVocabulary` (on root), `GET /api/categories`, `POST …/categories/scrub` | None | Housekeeping: 2 test proposals sit in the live queue — resolve or reject |
| 🟡 Per-org user tags / credentials | Model+API 2026-07-15; admin UI + reg gate PR #98 | Registration gate, check-in gate, group reg | `admin-backend.html`, `UserTag.cs`, `TagGate.cs` | `Tenant.UserTags`, `User.Tags`, `Enforcement` (Advisory/BlockReg/BlockCheckIn), `ExpiresAt` | **Cross-org gating decision unresolved** — pick: tag-on-registration / managed record / same-org-only | Same-org gate + admin UI live; cross-org path **blocks permanently** until the model is chosen |
| ✅ User assignment under org/EventAdmin | PR #94, 2026-07-18 | Notifications fan-out, dashboard overseers | `admin-backend.html`, `org-portal.html`, `AssignmentFunctions.cs` | `User.AssignedAdmins[]`, `notifyOnHours`, `notifyOnApproval` | None | none — assign → log → fan-out → direct-message verified end-to-end |
| ✅ Notification delete / clear | PR #96, 2026-07-18 | Notification pane | `ui.js`, `NotificationFunctions.cs` | `DELETE /api/notifications/{id}`, `DELETE /api/notifications` | None | none |
| ✅ One Arkansas Serve (single org) | PR #124, 2026-07-21 | Scope bar, directory, JoinOrg, org page | `scope.js`, deleted `js/orgs.js` | `arkansas-serve-root` (host + public), `AllowSelfJoin=false` | None | Special case remains: root can't be deleted (holds category vocabulary). Scope-bar duplicate now resolved (verified live) |

## 8 · Approvals & compliance (incl. guardians)

| Item | Dates | Touches | Pages | Variables | Future work | Bugs / incomplete |
|---|---|---|---|---|---|---|
| ✅ School approval policy for events | PR #92, 2026-07-18 | Hour-counting gate, categories (#10) | `admin-portal.html`, `ApprovalPolicy.cs` | `Tenant.ApprovalPolicy` (default+per-org+per-category, most-specific-wins) | Only visible on School/JDC tenants — none exist yet, so effectively dormant | **Untestable in prod** until a real School/JDC tenant exists |
| ✅ Guardian model + magic link + consent + gate (#20 core) | PR #120–123, 2026-07-21 | Registration gate, withdrawal cascade, notifications | `guardian.html`, `guardian.js`, `Guardian.cs`, `GuardianGate.cs`, `GuardianLinkService.cs` | `Guardian.Consents[]`, `Links[]`, `MagicLink`, `Session`, `Tenant.RequireGuardianConsent` | Carve-outs (below); real delivery depends on ACS email (below) | **"Verified" caveat:** link is returned to the admin in the API response, not emailed — no real delivery path yet. Consent toggle verified live |
| 🔵 Guardian carve-outs + per-event approval (#20 remainder) | in design (next build) | Guardian gate, event flag, overnight calc, notifications | `GuardianGate.cs`, `Event.cs`, `guardian.html`, `org-portal.html` | proposed `Event.RequiresFreshGuardianApproval`, `Guardian.EventApprovals[]` | Build per-event approval mechanism; two carve-outs (org-flag + overnight on `America/Chicago` local day). Two policy calls open | unbuilt — deferred so a carve-out block always has a way to clear |
| 🔵 Real-time waiver prompting (#19) | design settled | Guardian model, UserTag, ACS email | `Guardian.cs`, `UserTag.cs` | waiver = `UserTag` (attestation default, doc-upload opt-in per tag) | Build attestation path first, then doc-upload behind a per-tag flag + review surface | unbuilt — reuses guardian + tag machinery |
| ⛔ ACS email delivery | inert | Guardian links, consent/revoke notices, assignments | `EmailService.cs` | `Communication__ConnectionString`, `Communication__SenderAddress` | Owner provisions ACS + 2 app settings; wire `EmailService` into guardian link-issue + notifications (not called anywhere yet); gate first live send on owner OK | **`SendAsync` is a silent no-op** until configured; link-issue endpoint doesn't call it at all yet |
| ⛔ Terms & Privacy pages | PR #66, 2026-07-14 | First-login intake, policy versioning | `terms.html`, `privacy.html`, `dashboard.js`, `PolicyVersions.cs` | `PolicyVersions.Current`, `acceptedPolicyVersion`, `acceptedPolicyAt` | Counsel review → replace draft text, remove banners, bump version in 4 places, make blocking | **Still skippable** via "Skip for now"; text is a draft; no TTL configured (records persist) |

## 9 · UI & branding

| Item | Dates | Touches | Pages | Variables | Future work | Bugs / incomplete |
|---|---|---|---|---|---|---|
| ✅ Org switcher tabs | PR #66, 2026-07-14 | Every scoped page (shared shell) | `scope.js`, `ui.js` | `renderScopeBar`, searchable past 6 orgs | None | none |
| 🟡 Scope-bar option #2 — per-page config | PR #103 (fix #104) | Approvals, per-page role gating | `ui.js`, `scope.js` | `PAGE_SCOPE` {minRole, orgTypes, allTenants, showGroups}; `GetMyMemberships` projects `tenant.Type` | **`orgTypes:'schoolLike'` built & tested but switched OFF** — turn on once real School/JDC tenants exist | `minRole` gap latent (only one multi-org person, both Student) |
| 🔵 Per-school branding (#21) | design settled | Palette tokens, logo, PWA shell | `main.css` tokens, tenant editor | `--green`/`--green-light`/`--green-pale` + all tokens overridable, logo | Build: all-tokens-overridable, generated palette from one primary, contrast-warn (not refuse), preset school schemes; PWA `theme-color` stays platform green | unbuilt; no arbitrary CSS (tokens+logo only) |

## 10 · Scale work (deferred to last)

| Item | Dates | Touches | Pages | Variables | Future work | Bugs / incomplete |
|---|---|---|---|---|---|---|
| 🔵 DataTables phase 2 — server-side (#22/#23) | deferred | Users/events tables, Cosmos paging | `datatables.js` + new endpoints | `draw`, `recordsTotal`, `recordsFiltered`, `start`/`length`/`order[]` | Design query contract to DT protocol; solve Cosmos paging + counting **before** any org nears 1,200 rows | unbuilt — hard part is `recordsFiltered` on every keystroke |
| 🔵 AJAX for remaining query surfaces (#24) | deferred | All non-events search surfaces | multiple | — | Extend the async pattern after users/events prove it | unbuilt |

## 11 · Cross-cutting open bugs & small findings

| Item | Dates | Touches | Pages | Variables | Future work | Bugs / incomplete |
|---|---|---|---|---|---|---|
| 🔵 SuperAdmin sees "Sign Up" on events | logged | Registration, token-vs-membership | `event.js` | token adminLevel vs membership | Design call: button is honest; if registration FAILS, fix the failure (likely Finding-9 family) — confirm mechanism first | Open — verify display vs registration-failure |
| 🔵 `PUT /events/{id}` full-replace landmine | logged 2026-07-19 | Any partial event update / API scripting | `EventFunctions.cs` | omitted fields zeroed (incl. `startDateTime`→0001-01-01) | Make update fields nullable + copy only supplied, or reject partial bodies | Not biting (org-portal sends full payload); landmine for the next caller |
| 🔵 Small residue (cheap cleanup) | logged | Various | `TenantIds.cs`, ~5 frontend files, toast helpers | `TenantIds.IsReserved` (dead), `RootTenantId` (dup ×5), toast helper (×5) | Converge `RootTenantId`, delete dead `IsReserved`, extract one toast helper | Event `bada594a` has empty `organizationId` (data fix) |

---

_A rendered, colour-coded version of this chart was produced as a Claude artifact on 2026-07-22.
When picking up any upcoming item, confirm scope/specifics with the owner before building._
