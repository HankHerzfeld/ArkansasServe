# Arkansas Serve — Action Plan (companion to the Feature Audit)

_Created 2026-07-22. Turns [`2026-07-22-feature-audit.md`](2026-07-22-feature-audit.md) into a
severity-ranked to-do list, grounded in a code read **and** an authenticated Super Admin pass on
the live production app. Its organizing principle is the owner's standing goal: **land any
Event / User / Guardian / Tenant schema additions for the next week's work EARLY**, as
nullable/defaulted fields, so future behaviour work never forces migrating legacy documents._

---

## 0 · Corrections to the audit (verified this pass)

Three items the audit lists as **open** are already **fixed in code** — confirm against these
before acting on that doc:

- **#139 `PUT /events/{id}` full-replace** — already merge-semantics, not a blind replace.
  `UpdateEvent` reads the raw JSON and touches only fields the caller sent
  (`EventFunctions.cs`, commit `bbb7d0c`). The landmine is closed.
- **BlobService ctor throw (triage O2)** — already guarded. A malformed connection string is
  caught and degrades to a null client instead of 500-ing every route (`BlobService.cs:38–46`,
  commit `443f47b`).
- **Category-proposal queue** — **empty live** ("No pending category proposals" in Admin
  Backend). The audit's "two test proposals unresolved" is already cleared.

Also found live (SuperAdmin `@arkansasserve.com`):

- The **only live event is an external one-sided listing**, so **#138** (does a SuperAdmin see a
  Sign-Up button on a *hosted* event?) is **untestable in prod** until a hosted event exists.
- The **split search map (#18)** renders live; the single clean **"Arkansas Serve" + two demo
  orgs** scope bar confirms the #124 collapse.
- **Data cleanliness:** the owner's own SuperAdmin record carries junk `personType=Student /
  Grade 17`. (Commit `825482b` only *hid* the grade for org-level admins; the value persists.)

---

## 1 · Schema-first — land these before next week's event/user work

The rule that keeps legacy data safe: **additive nullable/defaulted fields are safe to land
anytime — they never touch existing rows.** The dangerous changes are the ones that (a) change a
partition key, (b) require existing rows to carry new required data, or (c) fork behaviour on a
legacy-vs-new field.

### 1a · Additive fields — LANDED 2026-07-22 (inert until wired, build clean)

Each defaults so every existing document deserialises to today's behaviour with **zero
migration**. Serialization is reflection-based (no source-gen context to update).

| Field | File | Default → legacy reads as | For |
|---|---|---|---|
| `Event.RequiresFreshGuardianApproval` | `Models/Event.cs` | `false` → covered by standing consent | #20 per-event guardian carve-out |
| `Guardian.EventApprovals[]` + `GuardianEventApproval` | `Models/Guardian.cs` | `[]` → none on file | #20 |
| `TenantUserTag.Evidence` + `TagEvidence`; `UserTagState.DocumentBlobName` | `Models/UserTag.cs` | `attestation` / `null` → attestation-only | #19 waiver doc-upload opt-in |
| `Tenant.Branding` + `TenantBranding{PrimaryColor, TokenOverrides}` | `Models/Tenant.cs` | `null` → platform green | #21 palette-token branding |

Follow-up (behaviour, separate PRs): the gate that reads `RequiresFreshGuardianApproval`, the
`UpdateEvent` merge line that lets an admin set it, the doc-upload endpoint + SAS signing for
`DocumentBlobName`, and the client palette generator for `Branding`.

### 1b · Cross-org tag gating (#11) — DECIDED: same-org-only (owner, 2026-07-22)

**Resolved as same-org-only** — the no-schema-change option, and already the implemented
behaviour: `RegistrationFunctions` and `CheckInFunctions` apply the tag gate (and guardian
consent) only when the registrant's tenant equals the event's org; a cross-org sign-up has no
User doc in the event's org to carry a tag state, so it **proceeds ungated** rather than being
blocked. No code change, no schema change, no backfill. `User.tags` comment updated to record it;
every other reference already said "the locked cross-org decision".

Rejected alternatives (both rewrite existing data): **tag-on-registration** (adds tag state to
every `EventRegistration` row) and **managed-record** (materialises a User doc in the event's org).

This unblocks further registration work — no more rows accrue that a later decision would migrate.

### 1c · Converge legacy forks before they compound

- **Group-reg `UserId` → `MemberId`** — `RegistrationFunctions.cs` still has a legacy `UserId`
  arm "that can drop once every row carries `memberId`." Write `memberId` on every new group
  registration now (stop widening the fork), then backfill, then delete the arm.
- **Users partition-key fallback** — `CosmosService.UsersCompatibility.cs` silently retries writes
  against `/id` when the container was built with the wrong key. Confirm which key prod's users
  container actually uses before the user base grows.

### 1d · Keep deferred (these WOULD force migration)

Background-check → tag convergence (rewrites live User records); denominational `faithAffiliation`
(needs an agreed vocabulary first).

---

## 2 · Severity-ranked to-do list

### P0 — live bugs / landmines
1. ~~BlobService ctor throw~~ — **already fixed** (§0).
2. **Bicep-apply landmine** — do **not** run `az deployment … apply`; it reverts the firewall,
   clobbers the rotated key, and re-opens the admin backdoor (`main.prod.bicepparam` still names
   the domain). Reconcile Bicep to live state before any apply. *(Owner-deferred; action now is a
   guardrail + reconciliation task.)*

### P1 — data-integrity fixes
3. Phantom **2.0h service log** crediting a deleted tenant (surfaced live on the dashboard).
4. Event `bada594a` has **empty `organizationId`**.
5. Two tenants hold lowercase `"organization"` type (root, Test O3).
6. Owner's SuperAdmin record `personType=Student / Grade 17` (§0).

### P2 — correctness & blocked-on-external
7. **ACS email inert** — `EmailService.SendAsync` is a silent no-op and isn't wired into guardian
   link-issue at all; magic links are returned in the API response, not emailed. ⛔ owner
   provisions ACS + 2 app settings, then wire it (gate the first live send on owner OK).
8. **Terms/Privacy** still skippable, draft text, no enforced versioning. ⛔ counsel review.
9. **Crawler imports 0** every run (keyed sources skip silently). Owner sets per-source keys;
   delete unused `CRAWLER_SERVICE_TOKEN`.

### P3 — verify / close-the-loop
10. **Recurring series create end-to-end (③)** — create a real series, confirm a valid
    `RecurrenceRule` is written.
11. **#138 SuperAdmin Sign-Up button** — create a *hosted* test event (only external exists live)
    and verify display vs. registration.
12. **Color-coded-by-tag map pins (#18)** — needs >1 live event to confirm.

### P4 — residue cleanup
13. Converge `RootTenantId` dup (×5), delete dead `TenantIds.IsReserved`, extract one shared toast
    helper. Update the audit doc to mark #139 / triage O2 / category queue resolved.

---

## 3 · Coverable now (no owner input, no external provisioning)

1. ✅ **The four additive schema fields** (§1a) — done 2026-07-22.
2. **Residue cleanup + audit-doc corrections** (P4 #13).
3. **Group-reg: write `memberId` on every new registration** (§1c) — stop widening the fork even
   before backfilling.

~~Gate before writing more registration code: the cross-org tag-gating decision (§1b).~~
**Resolved 2026-07-22 — same-org-only (§1b); registration work is unblocked.**
