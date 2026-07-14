# Design — SuperAdmin remote access (impersonation) · Phase F #26

**Status:** Draft for review (design-only; no code yet)
**Author:** Claude, 2026-07-09
**Scope:** Let a platform SuperAdmin securely "act as" any user — real or demo — to see exactly what they see, with a hard audit trail and unmistakable UI, in an app holding **court-involved-youth records**. Security is the primary constraint, not a footnote.

> ⚠️ This is the single most sensitive capability in the product. The bar is: **non-repudiable, SuperAdmin-only, time-boxed, reason-required, and impossible to do silently.** If any of those can't hold, don't ship that part.

---

## 1 · Motivation & use cases

- **Support / debugging** — "the parent says hours aren't showing" → view the student's dashboard as they see it, without guessing.
- **Onboarding & demos** — walk a new org through the product as each role using the seeded demo users (`AdminFunctions.GetDemoUsers`, `admin-backend`).
- **Verification** — confirm a role/permission change landed for a specific person.

## 2 · Non-goals

- Not a password/credential recovery tool — we never learn or reset a user's Entra credentials.
- Not a way to gain powers the target doesn't have (no privilege *escalation* via impersonation — see §7 guardrails).
- Not automation/service-account access — this is an interactive, human-driven, short session.

---

## 3 · The core challenge

Authorization today is **entirely token-driven**. `AuthMiddleware` validates the Entra JWT and builds a `UserContext` from its claims — `UserId = oid/sub`, `TenantId`, `AdminLevel`, `Email` (`AuthMiddleware.cs:96-105`). Every function trusts that context; per-org checks flow through `ResolveActorInOrgAsync(ctx.UserId, ctx.AdminLevel, orgId)`.

Two facts make classic "get a token for the other user" impossible:
1. We **cannot mint an Entra token** for another person (that's the IdP's job, gated by their credentials).
2. **Demo users have no Entra identity at all** — they're Cosmos `User` docs with `isDemoUser:true`, emails like `demo.student.1@arkansasserve.local`, and **no `externalId`**. There is nothing to "log in as."

So impersonation must live at the **application layer**: the real SuperAdmin's Entra token remains the transport identity, but the app resolves an **effective actor** (the target) for authorization and data scoping, while retaining the **real** admin identity for every audit record and guardrail.

### Identity resolution wrinkle (must get right)
The app keys users by `externalId` (`GetUserByExternalIdAsync`). Demo and not-yet-adopted managed users have none. Service logs are partitioned by an "acting id" that is already defined as `ExternalId ?? Id` (`ManualHoursFunctions.cs:109`, `VolunteerFunctions`). The impersonation layer must reuse that **same** convention so, e.g., a demo student's dashboard reads their logs from the right partition.

> **`actingId(user) = user.externalId (if present) else user.id`** — single helper, reused everywhere the effective context is built.

---

## 4 · Design options

| Option | How the "act as" context travels | Pros | Cons | Verdict |
|---|---|---|---|---|
| **A — Signed impersonation JWT** | Backend mints its own short-lived HMAC/RSA-signed token with `act`=adminOid, `sub`=targetActingId; client sends it in a header. | No per-request DB read. | New token-signing infra + secret in Key Vault; revocation only via expiry; a second token format to secure. | Over-built for our scale. |
| **B — Opaque server-side session + per-request lookup** ✅ | `POST /manage/impersonation` creates an `ImpersonationSession` doc and returns its id; client sends `X-Impersonation-Session: <sid>`; middleware point-reads the session each request. | No new signing/secret; **instant revocation**; the session doc *is* the audit anchor; one point read (~1 RU) only while a session is active (rare). | A point read per request during a session (mitigable with a short in-memory cache). | **Recommended.** |
| **C — Naive `X-Impersonate-UserId` header** | Client just names the target; middleware checks caller is super. | Trivial. | No session object, no reason capture, weak audit, easy to leave "on"; trusts a bare header. | Rejected. |

**Recommendation: Option B.** It adds no new cryptographic surface, gives instant revocation, and the session document doubles as the tamper-evident record of who-impersonated-whom-and-when. A 60-second in-memory cache of `sid → session` per function instance keeps the per-request cost negligible while preserving near-instant revocation.

---

## 5 · Detailed design (Option B)

### 5.1 Data model

**New container `impersonationSessions`** (partition key `/adminUserId` — you always query "sessions started by this admin"):

```jsonc
{
  "id": "imp-<guid>",
  "adminUserId": "<real super oid>",       // who is impersonating
  "adminName": "…", "adminEmail": "…",     // denormalized for audit readability
  "targetUserId": "<target user doc id>",  // the User doc impersonated
  "targetActingId": "<externalId ?? id>",  // effective UserId while impersonating
  "targetTenantId": "…", "targetName": "…", "targetIsDemo": true|false,
  "reason": "support ticket #123 — hours not showing",
  "mode": "read-only" | "read-write",      // see §7
  "startedAt": "…", "expiresAt": "…",       // hard cap, e.g. +30 min
  "endedAt": null,                          // set on explicit stop
  "revoked": false                          // kill-switch
}
```

**New container `auditEvents`** (partition key `/adminUserId`) — append-only:

```jsonc
{
  "id": "aud-<guid>", "sessionId": "imp-…",
  "adminUserId": "…", "targetUserId": "…",
  "action": "impersonation.start | impersonation.stop | impersonation.action",
  "method": "POST", "route": "/api/servicelogs", "statusCode": 201,
  "at": "…"
}
```

> Every **mutating** request made during an impersonation session writes one `auditEvents` row (non-repudiation). Reads are covered by the session's `startedAt/endedAt` window; we don't need a row per GET.

**Infra note (do not skip):** adding containers means updating `infra/main.bicep` **and** the `CosmosDb__Containers__*` mapping app settings (the app resolves logical→physical container names via those — see [[project_swa_admin_route_reservation]] neighbor note and the existing 7-container mapping). This intersects the open **P2 Bicep-drift** work; coordinate so the infra deploy stays additive/no-op.

### 5.2 API (all under `/manage/*` — never `/admin/*`, which SWA silently 404s on linked backends)

| Method & route | Who | Does |
|---|---|---|
| `POST /manage/impersonation` `{ targetUserId, targetTenantId, reason, mode }` | **real** global super | Verify super; verify target exists; create session (`expiresAt = now+30m`); write `impersonation.start` audit; return `{ sessionId, target, expiresAt, mode }`. |
| `DELETE /manage/impersonation/{sid}` | session's admin | Set `endedAt`; write `impersonation.stop`; idempotent. |
| `GET /manage/impersonation` | real global super | List that admin's recent sessions (self-audit view). |

`reason` is **required** and non-empty. `mode` defaults to `read-only`.

### 5.3 AuthMiddleware changes — effective vs real context

`ValidateRequest` gains a step **after** Entra validation:

```
1. Validate Entra JWT  → realCtx   (unchanged)
2. If header X-Impersonation-Session present:
     a. load session (cache→Cosmos point read)
     b. REJECT unless: session exists && !revoked && !endedAt && now<expiresAt
                       && session.adminUserId == realCtx.UserId
                       && IsGlobalSuper(realCtx)        // re-checked every request
     c. load target User doc; build effectiveCtx:
          UserId      = actingId(target)
          TenantId    = target.tenantId
          AdminLevel  = target.adminLevel   // the TARGET's level, never the super's
          Email/Name  = target's
          RealUserId       = realCtx.UserId    // NEW field, retained for audit/guardrails
          ImpersonationSid = sid               // NEW field
     d. return effectiveCtx
3. Else return realCtx (RealUserId == UserId, ImpersonationSid == null)
```

Downstream code is **unchanged** — it reads `ctx.UserId/TenantId/AdminLevel` and naturally operates as the target. Crucially, `ctx.IsSuperAdmin` becomes **false** while impersonating a non-super, so the super's powers do **not** leak into the target session. `UserContext` gains `RealUserId`, `ImpersonatedBy`/`ImpersonationSid`, and `IsImpersonating`.

A tiny middleware post-step writes the `auditEvents` row for mutating methods when `IsImpersonating`.

### 5.4 Sequence

```
SuperAdmin UI                     Functions                         Cosmos
  │  POST /manage/impersonation ───►  verify super + target ─────►  create ImpersonationSession
  │       {target, reason, mode}      write audit(start)             + auditEvents(start)
  │  ◄── { sessionId, expiresAt } ────┘
  │
  │  (store sid in sessionStorage; show banner)
  │
  │  GET /users/me                    validate Entra (realCtx=super)
  │  X-Impersonation-Session: sid ─►  load+check session → effectiveCtx = TARGET
  │  ◄── target's profile ────────────┘  (mutations also write audit(action))
  │
  │  DELETE /manage/impersonation/sid ► set endedAt + audit(stop)
  │  (drop sid; hide banner)
```

---

## 6 · Frontend design

- **Start:** in `admin-backend` (and/or the #25 user tables), a super gets an **"Act as"** action per user row → a modal requiring a **reason** and a **mode** choice → calls `Api.Admin.impersonate(...)`.
- **Token plumbing:** `api.js request()` already attaches `Authorization` from `Auth.getAccessToken()` (`api.js:12-19`). Add: if `Auth.getImpersonationSid()` is set (a new `sessionStorage` key, managed in `auth.js` alongside the existing key map), attach `X-Impersonation-Session`. One-line change, applies to every call.
- **Unmistakable banner:** a persistent, high-contrast bar (amber/red, distinct from normal chrome) rendered by `UI.setupHeader` on **every** page: **"⚠ Viewing as {name} ({org}) — expires 3:42pm — Exit"**. Exit calls `DELETE`, clears the sid, reloads.
- **Guarding the guard:** the banner and sid live in `sessionStorage`, so closing the tab ends the visual session; the server session still hard-expires and can be revoked regardless.
- **Never in URLs/logs:** sid travels only in a header and `sessionStorage`, consistent with the access-token handling and the existing tight CSP.

---

## 7 · Guardrails (defense in depth)

1. **SuperAdmin re-checked every request** (§5.3b), not just at start — a demotion mid-session kills it.
2. **No privilege escalation:** effective `AdminLevel` is the target's; `IsSuperAdmin` is false while impersonating a non-super.
3. **No nested impersonation:** `POST /manage/impersonation` requires `IsGlobalSuper(realCtx)` **and** `!ctx.IsImpersonating`.
4. **Elevation-proofing:** while impersonating, block endpoints that could grant the *target* (or the admin via the target) more power — e.g. reject `PATCH /manage/backend/users/{id}/access`, tenant create/update, demo-user reset, and the DB console (`manage/db/*`) when `ctx.IsImpersonating`. (Admins do those as themselves, not through a user.)
5. **`read-only` mode (default):** middleware rejects all non-GET requests during the session — safest for "just show me what they see." `read-write` is opt-in per session, captured in the reason/audit, for when the admin must reproduce a mutating bug. **`read-write` is accepted only when the target is a demo user** (`isDemoUser:true`) — demo records hold no real PII and are rebuilt by Reset Demo Users, so a write session cannot reach a real person's data. Real-user targets are forced to `read-only` regardless of the requested mode; lifting that is Phase 3, not a config toggle.
6. **Hard expiry** (≤30 min) + **explicit stop** + **kill-switch** (`revoked`), plus a global "end all sessions" for incident response.
7. **Audit is mandatory:** if the `auditEvents` write for `impersonation.start` fails, the **start fails** (fail-closed) — no unlogged impersonation.

---

## 8 · Audit, compliance & ethics

- **Non-repudiation:** `ImpersonationSession` + per-mutation `auditEvents`, both partitioned by `adminUserId`, append-only, denormalized names for human-readable review.
- **Retention:** keep audit rows well beyond session life (proposal: 2 years; TTL off). Court-involved data ⇒ err long.
- **Transparency (decided 2026-07-09): notify the user.** When a **real** (non-demo) account is impersonated, the impersonated person is notified (in-app `Notification`, and email if configured). Demo users get no notice. This lands with **Phase 2** (real-user impersonation); Phase 1 is demo-only so no notification is generated. Copy should be reassuring and plain ("A platform administrator viewed your account to help with support on {date}").
- **Policy doc:** ship a short written policy on acceptable use (support/debugging only), reviewable by schools/orgs, referencing the audit capability. This is a trust product for youth data; the policy is part of the feature.

---

## 9 · Rollout plan (phased, lowest-risk first)

- **Phase 1 — demo users only (MVP).** ✅ **Chosen as the launch scope (2026-07-09).** Impersonation restricted to `isDemoUser:true` targets. Delivers the onboarding/demo value with **zero real-PII exposure**, while proving the whole mechanism (session, banner, audit, guardrails). Low blast radius. No user notification (demo accounts). **`read-write` is selectable per session for these demo targets** (§7.5) — the read-only default protects *real* users, and demo records are disposable, so forcing read-only here bought no safety and blocked the main testing use case.
  **Demo fixtures (2026-07-14):** demo personas are parented to dedicated demo organizations —
  `demo-org-alpha` (home) and `demo-org-beta` (secondary) — with the SuperAdmin personas left on
  the `arkansas-serve-root` host org. Beta exists so a **secondary-org admin** is reproducible: the
  cross-org persona is two `User` docs sharing one `externalId` (per the one-doc-per-org model), which
  is the shape behind Findings 2/7/9 and is inexpressible with a single-org fixture set. Seeding is
  now **global**, not per-org: `POST /manage/backend/demo-users/reset` asserts the demo orgs and
  rebuilds every persona across them.
- **Phase 2 — real users, `read-only`, behind a config flag.** Adds the support/debugging value. **Notifies the impersonated user** on start (decision §8).
- **Phase 3 — `read-write` for real users**, opt-in per session, only if a concrete need emerges. May stay disabled indefinitely.

Each phase is independently shippable and reversible via config. The middleware/session/audit/banner machinery is identical across phases — only the **target eligibility filter** (`isDemoUser` gate) and the notify-on-start hook differ, so Phase 1 is a true subset, not throwaway.

---

## 10 · Decisions

**Settled (2026-07-09):**
1. **Launch scope** → **Phase 1, demo users only.** ✅
2. **Notification** → **notify the impersonated user** (applies when Phase 2 real-user impersonation ships; no-op for Phase 1 demo accounts). ✅

**Still open (not blocking Phase 1):**
3. **Capability** — is `read-only` sufficient long-term, or will `read-write` for real users be needed (Phase 3)? Phase 1 can ship `read-only`.
4. **Session length** — 30 min default OK? Absolute cap?
5. **Audit container** — new `auditEvents` container (recommended) vs. reusing an existing one; coordinate with the **P2 Bicep-drift** work before any infra change. *This is the one true prerequisite for building Phase 1.*

## 11 · Acceptance criteria

- A super can start an impersonation session (reason required), see the target's app exactly as they do, and exit cleanly; a demo user works identically to a real one.
- Every session and every mutating action is recorded, attributable to the real admin, and start fails closed if it can't be logged.
- While impersonating: no super powers leak, no nested impersonation, no privilege-granting endpoints, `read-only` blocks writes, session hard-expires and is revocable.
- The "viewing as" banner is present on every page for the entire session and cannot be dismissed without exiting.

---

## 12 · Rough effort (post-decisions)

Backend: `UserContext` fields + middleware effective-context + audit hook (~0.5d); 3 endpoints + 2 containers + Cosmos helpers (~1d); guardrail checks (~0.5d). Frontend: start modal + `api.js` header + banner in `UI.setupHeader` + exit (~1d). Infra: Bicep containers + mappings, folded into P2 (~0.5d). Tests/audit review (~1d). **≈ 4–5 days for Phases 1–2**, excluding policy/legal sign-off.
