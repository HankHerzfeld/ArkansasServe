# Arkansas Serve — Feature Audit & Status Chart (2026-07-23 refresh)

_Supersedes `2026-07-22-feature-audit.md`. Same purpose — one chart of **every** feature from the
planning docs against where it actually stands — refreshed after the 2026-07-22→23 work landed
(PRs #129–#136) and re-verified against the **live production app in an authenticated Super Admin
session**. Source of truth for shipped work remains `roadmap.md`._

**Status legend:** ✅ shipped & live · 🟡 partial (staged, more remains) · 🔵 designed, not built ·
⛔ blocked (external input). Counts: **40 shipped · 4 partial · 6 planned · 4 blocked**.

## Live-verified prod state (2026-07-23, DB console + maintenance dry-runs)

- **Users (13):** owner `SuperAdmin` / personType `Staff`; demo personas at their proper admin
  levels; **base level is `Member`** (demo students read `adminLevel: "Member"`). The
  `normalize-admin-levels` dry-run returns **0 candidates** — no legacy `"Student"` admin level
  remains.
- **Tenants (3):** all `type: "Organization"` (root's lowercase casing fixed & applied).
- **Registrations (13):** every row carries `memberId` — 0 without one (legacy `UserId` matching
  arm dropped).
- **ServiceLogs:** empty (0 rows). **Category-proposal queue:** empty. **Crawler draft queue:**
  empty (matches "imports 0"). **Events:** one live listing, an *external* one-sided post.

## Resolved / changed since 2026-07-22

| Item | Where | Status |
|---|---|---|
| Forward-compat additive schema (`Event.RequiresFreshGuardianApproval`, `Guardian.EventApprovals[]`, `TagEvidence`+`UserTagState.DocumentBlobName`, `Tenant.Branding`) | PR #129 | ✅ landed (inert until behavior built) |
| Registration identity → `memberId` only (legacy externalId arm dropped) | PR #130 | ✅ |
| P4 residue: dead `IsReserved` removed, `RootTenantId`→`TenantIds.Root`, toast→`UI.toast` | PR #131 | ✅ |
| Maintenance: `normalize-tenant-types` endpoint + **applied in prod** | PR #132 | ✅ root now `Organization` |
| Intake: grade constrained to K–12; platform staff/supers exempt from volunteer intake | PR #133 | ✅ |
| Maintenance: `normalize-intake-exempt-users` + **applied** (owner → `Staff`, grade cleared) | PR #134 | ✅ |
| Crawler comment tidy (`CRAWLER_SERVICE_TOKEN` confirmed removed) | PR #135 | ✅ |
| **Rename base adminLevel `Student`→`Member`** (decouple from `PersonTypes.Student`) + migration endpoint | PR #136 | ✅ live, data clean |
| Cross-org tag gating decision | owner, 2026-07-23 | ✅ **same-org-only** (no schema change) |
| BlobService ctor throw (O2), `PUT /events/{id}` full-replace, category-queue residue | earlier commits | ✅ were already fixed (stale in prior audit) |

---

## 1 · Auth & multi-org correctness — ✅ (all shipped)

Strongest-adminLevel resolution, real display name, per-membership authorization (Finding 9),
cross-org cancel, org-less `unassigned` partition, PlatformAdmin backdoor closed. **New:** base
admin level renamed `Student`→`Member` (PR #136) to break the collision with `PersonTypes.Student`
— backward-compatible (`RankOf` folds any unknown to rank 0), migrated in prod (0 legacy rows).
⚠️ Standing risk: `main.prod.bicepparam` still names the admin domain — a Bicep apply re-opens the
backdoor (see §2).

## 2 · Infrastructure & Ops

| Item | Status | Remaining |
|---|---|---|
| Daily event crawler (shared-secret auth) | 🟡 | **Imports 0** — set per-source API keys (owner). `CRAWLER_SERVICE_TOKEN` removed ✅ |
| Cosmos/Blob key rotation + firewall | ✅ | Real isolation needs EP1+VNet+Private Endpoint (~$150+/mo) — open cost decision |
| BlobService ctor degrade (O2) | ✅ | none (commit `443f47b`) |
| Deploy tooling (wait-for-deploy) | ✅ | none |
| Infra drift / Bicep non-authoritative | ⛔ | **Do not apply** — reverts firewall, clobbers rotated key, re-opens admin bootstrap. Reconcile before any apply (owner-deferred) |

## 3 · Platform & UX foundations

| Item | Status | Remaining |
|---|---|---|
| DataTables phase 1, table-scroll containment, phone drawer | ✅ | none |
| iOS safe area / edge-to-edge | 🟡 | needs a **real-device iOS pass** |
| AJAX everywhere for search/query | 🟡 | extend async pattern to remaining surfaces |

## 4 · Events & scheduling

| Item | Status | Remaining |
|---|---|---|
| Spots-left per shift; slot-drift reconcile | ✅ | none |
| Recurring — occurrences materialised (①②) | ✅ | decide whether the list collapses a series into one card |
| Recurring — create-form UI (③) | ✅ present | **verify end-to-end** (create a real series → valid `RecurrenceRule`) |
| Day-of check-in (online) + self-check-in | ✅ | none |
| Group registration | ✅ | legacy `UserId` arm **dropped** (PR #130) — done |
| Offline day-of check-in (#15) | 🔵 | PWA cache + queued writes/sync, one event's roster |

## 5 · Discovery, search & maps — ✅

Event search/sort/filter, ZIP/geo search, Maps #17 (geocode + autocomplete), Maps #18 (split
map) all shipped. Remaining: **set a Google Maps budget alert** (bills per save); verify
**color-coded-by-tag pins** and **Places autocomplete** (need >1 live event / a hosted event).

## 6 · Dashboard — ✅

Per-org role-aware dashboard shipped. The "2.0h log crediting a deleted tenant" it once surfaced is
gone (ServiceLogs empty, verified live).

## 7 · Organization & user model

| Item | Status | Remaining |
|---|---|---|
| Org taxonomy L1 (style) | ✅ | **build a real School/JDC tenant** + re-type schools (none exist). Lowercase-casing data fix **applied** ✅ |
| Org taxonomy L2 (service categories); faith attribute; self-define category | ✅ | denominational `faithAffiliation` deferred (needs agreed list); open Q keep faith-as-attribute |
| Per-org user tags / credentials | ✅ | cross-org gating **decided: same-org-only** — feature complete as scoped |
| User assignment; notification delete/clear; One Arkansas Serve | ✅ | none |

## 8 · Approvals & compliance (incl. guardians)

| Item | Status | Remaining |
|---|---|---|
| School approval policy | ✅ (dormant) | untestable until a real School/JDC tenant exists (§7) |
| Guardian model + magic link + consent + gate (#20 core) | ✅ | real delivery depends on ACS email (below) |
| Guardian carve-outs + per-event approval (#20 remainder) | 🔵 | **schema landed (#129)**; build gate logic, event-flag UI, overnight carve-out calc, `UpdateEvent` merge line. 2 policy calls open |
| Real-time waiver prompting (#19) | 🔵 | **schema landed (#129)**; build attestation path, then doc-upload behind per-tag flag + review surface |
| ACS email delivery | ⛔ | owner provisions ACS + `Communication__ConnectionString`/`__SenderAddress`; wire `EmailService` in; gate first live send on owner OK |
| Terms & Privacy pages | ⛔ | counsel review → replace draft text, remove "Skip for now", bump version ×4, make blocking |

## 9 · UI & branding

| Item | Status | Remaining |
|---|---|---|
| Org switcher tabs | ✅ | none |
| Scope-bar per-page config | 🟡 | `orgTypes:'schoolLike'` built & tested but **OFF** until real School/JDC tenants exist |
| Per-school branding (#21) | 🔵 | **schema landed (#129, `Tenant.Branding`)**; build client palette generator, tenant editor UI, contrast-warn, preset schemes |

## 10 · Scale work (deferred to last)

| Item | Status | Remaining |
|---|---|---|
| DataTables phase 2 — server-side (#22/#23) | 🔵 | design DT query contract + Cosmos paging/counting **before** any org nears ~1,200 rows |
| AJAX for remaining query surfaces (#24) | 🔵 | extend after users/events prove it |

## 11 · Cross-cutting & tooling

| Item | Status | Notes |
|---|---|---|
| SuperAdmin sees "Sign Up" on events (#138) | 🔵 | mechanism addressed by the rename (`event.js` gates on resolved level, not the token's base "Member"); **verify with a hosted event** |
| `PUT /events/{id}` full-replace landmine | ✅ | merges on present-keys (commit `bbb7d0c`) |
| Small residue cleanup | ✅ | code done (#131); data fixes applied |
| **Maintenance tooling** (new) | ✅ | SuperAdmin dry-run endpoints under `manage/maintenance/*`: `normalize-tenant-types`, `normalize-intake-exempt-users`, `normalize-admin-levels`. Invoke via authenticated POST |

---

_Verification method: authenticated Super Admin session — DB console reads over Users / Tenants /
Events / Registrations / ServiceLogs, plus dry-run calls of the maintenance endpoints. Not
re-verified live this pass: `guardian.html` redeem flow, Platform Admin internals, Approvals
internals. Remaining work is enumerated in `2026-07-23-remaining-work.md`. When picking up any
upcoming item, confirm scope with the owner before building._
