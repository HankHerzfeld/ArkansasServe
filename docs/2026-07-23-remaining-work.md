# Arkansas Serve — Remaining Work (2026-07-23)

_Companion to `2026-07-23-feature-audit.md`. Every item from the planning docs still to do or
address, grouped by what unblocks it. Core auth / registrations / search+maps / dashboard / most of
the org model are shipped and, where checked, live-verified — this is the tail._

## Buildable now — no external dependency

Three have their **additive schema already merged (PR #129)**, so only behavior remains:

- **Guardian carve-outs + per-event approval (#20 remainder)** — gate logic, event-flag UI,
  overnight/multi-day carve-out calc (`America/Chicago` local day), `UpdateEvent` merge line.
  Fields `Event.RequiresFreshGuardianApproval` + `Guardian.EventApprovals[]` are in. _2 policy
  calls still open._ **Most self-contained of the three.**
- **Real-time waiver prompting (#19)** — attestation path first, then doc-upload behind a per-tag
  flag + a review surface. `TagEvidence` + `UserTagState.DocumentBlobName` are in.
- **Per-school branding (#21)** — client palette generator from one primary, tenant editor UI,
  contrast-warn (not refuse), preset school schemes; PWA `theme-color` stays platform green.
  `Tenant.Branding` is in.
- **Offline day-of check-in (#15)** — PWA cache + queued-write/sync, scoped to one event's roster.
- **DataTables phase 2 — server-side (#22/#23)** — DT query contract + Cosmos paging/counting.
  Do **before** any org nears ~1,200 rows.
- **AJAX for remaining query surfaces (#24)** — extend the events async pattern.
- **iOS safe-area real-device pass** — verify `env(safe-area-inset-*)` on hardware (0 on desktop).

## Blocked on owner action / decision

- **⛔ Infra / Bicep reconciliation** — highest stakes. An `az deployment … apply` today reverts
  the firewall, clobbers the rotated Cosmos/Blob key, and **re-opens the admin backdoor**
  (`main.prod.bicepparam` still names the domain). Guardrail: **do not apply** until reconciled.
- **⛔ ACS email provisioning** — provision ACS + set `Communication__ConnectionString` /
  `Communication__SenderAddress`, then wire `EmailService` into guardian link-issue + notifications
  (currently `SendAsync` is a silent no-op). Unblocks guardian real-delivery.
- **Crawler per-source API keys** — set them so the daily crawl imports > 0.
- **Google Maps budget alert** — billing fires per event save; set an alert.
- **Build a real School/JDC tenant + re-type schools** — none exist; keeps three things dormant:
  approval-policy testing, the scope-bar `orgTypes:'schoolLike'` gate (built + tested, OFF), and
  denominational work.
- **Cosmos/Blob real network isolation** — EP1 + VNet + Private Endpoint (~$150+/mo); cost decision.
- **Two design calls** — recurring-series list collapse (one card vs many); keep faith-as-attribute
  vs add a denominational `faithAffiliation` (needs an agreed vocabulary).

## Blocked on external

- **⛔ Terms & Privacy** — counsel review → replace draft text, remove the "Skip for now" escape +
  banners, bump the version in 4 places, make it blocking. Currently skippable with draft text.

## Verify-live (cheap; need a live artifact)

- **Recurring series end-to-end** — create a real series; confirm a valid `RecurrenceRule` writes.
- **SuperAdmin "Sign Up" button (#138)** — needs a *hosted* event live (only prod event is
  external); confirm display vs registration. Mechanism already addressed by the #136 rename.
- **Map pins color-coded-by-tag (#18)** and **Places autocomplete (#17)** — need >1 live event.
- Not re-verified live recently: Approvals internals, Platform Admin, Organizations directory,
  `guardian.html` redeem flow.

## Done this cycle (for reference)

Forward-compat schema (#129) · memberId convergence (#130) · P4 residue (#131) · tenant-type
normalize endpoint + applied (#132) · intake K–12 grade + staff exemption (#133) ·
intake-exempt-user normalize + applied (#134) · crawler comment (#135) · **`Student`→`Member`
rename + migration (#136)** · cross-org tag gating decided (same-org-only) · all P1 data-integrity
items resolved in prod. Three maintenance endpoints now exist under `manage/maintenance/*`.
