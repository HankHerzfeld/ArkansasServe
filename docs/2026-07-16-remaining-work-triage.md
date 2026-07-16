# Arkansas Serve — Remaining-Work Triage

_Created 2026-07-16. Purpose: a single actionable list of what's left, sorted by readiness,
so the next step can be planning. Source of truth for shipped work stays `roadmap.md`._

Legend: **BUILD NOW** = unblocked, no decision needed · **DECIDE FIRST** = one open question
gates the build · **BLOCKED** = waiting on you / counsel / an external input.

---

## 0. Operational — do before feature work

| # | Task | Notes |
|---|------|-------|
| O1 | **Confirm crawler secrets are set** | ⏰ The `0 11 * * *` cron was armed 07-15 and fires 6 AM CT daily. It 401s every run unless BOTH exist with the same value: GitHub secret `CRAWLER_SHARED_SECRET` + Function App setting `Crawler__SharedSecret` (`openssl rand -base64 48`; set the app setting **directly, not via Bicep**). **Feedback needed: are both set? If not, has the run been failing?** Delete the unused `CRAWLER_SERVICE_TOKEN`. |
| O2 | **Fix `BlobService` ctor throw** | Real bug, not just a local-dev annoyance: constructor parses `BlobStorage__ConnectionString` with no try/catch, so a present-but-invalid string 500s **every** `EventFunctions` route before auth. Logs-and-degrades when absent but throws when malformed — inconsistent. Small, self-contained. Also unblocks running the backend locally (combine with `DOTNET_ROLL_FORWARD=LatestMajor` for the ASP.NET Core 8 runtime gap). |
| O3 | **Clear verification debt** | Merged-but-never-clicked in prod: #9 delete-series + recurring create-form; #10 category forms; #11 tag endpoints (no UI at all). Group-registration proved this session that green builds hide real bugs. One focused prod-clickthrough pass. |

---

## 1. BUILD NOW — unblocked features

| # | Task | Execution notes |
|---|------|-----------------|
| 10② | **Self-define service category w/ approval** | Design already settled: org proposes → shows as "Other" (pending value must NOT leak into filters) → SuperAdmin approves-as-new **or** approves-as-alias onto an existing category. Alias path is the whole point (prevents "Food Bank"/"food bank"/"Foodbank"). Needs a stored vocabulary + approval queue. ~Size of #8. Supersedes the old "fixed list in code" decision. |
| 12 | **School approval tags for events** | Per-school: mark orgs **pre-approved** (hours auto-count) vs **approval-required**. This is the missing guardrail for the "Political Parties & Campaigns" category already shipped in #10 — until this lands, a school can't mark that category approval-required. Self-contained; mostly a per-school policy map + a gate at hour-counting. |
| 13 | **User assignment under org/EventAdmin** | Assign volunteers to a specific admin with direct oversight of their hours/approvals, per-action notification settings, and direct comms via notifications. Builds on existing membership + notification model. |
| 14 | **Day-of check-in — QR + admin page** | Generated QR → check-in flow, plus an org/EventAdmin check-in page: check in **by shift** and **by user**, add **walk-ins** on the spot. **Unblocks `blockCheckIn` tag enforcement from #11** (reserved but inert until this exists). Build online-first here; offline is #15. |
| 16 | **ZIP / geo search** | Capture **ZIP + city** on event addresses; search by ZIP, town, county; prompt for ZIP at event creation. Pure data + search — no external API. **Unblocks #18.** Independent of #17. |

---

## 2. DECIDE FIRST — one open question gates the build

| # | Task | Decision I need from you |
|---|------|--------------------------|
| 11② | **Tag gating + tag admin UI** | Two pieces: (a) the **gate at registration**, (b) **admin UI** to define tags + set them per-person. The admin UI is buildable now; the gate is **blocked on a cross-org decision**: tags live on the per-org User doc, but registration is cross-org — a student from org B has no User doc in org A, so an org-A required tag can never be satisfied. **Pick one:** ① tag state on the registration · ② give cross-org registrants a managed record in the event's org · ③ gate same-org only. (Also: `blockCheckIn` enforcement still needs #14.) |
| 17 | **Address auto-populate** | Google Maps API + optional custom-address path (still previews a map). **Design sign-off + a billing decision** — Maps JS/Places is a paid, keyed Google service. Confirm: use Google, or a cheaper geocoder? Who owns the key/billing? |
| 19 | **Real-time waiver prompting** | Email + phone prompts to guardian; org-uploaded docs. **Design sign-off.** Will reuse #11 tag states (e.g. "sent, awaiting guardian"). Needs flow design + a send channel for SMS (Twilio/ACS?) and email. |
| 20 | **Parental account oversight** | Guardian relationship model + ongoing (not per-action) consent. **Needs a data-model decision** — how a guardian links to a minor's account and what consent persists. |
| 21 | **Per-school branding / custom CSS** | Logo + color palette per school, applied to that school's users. **Design sign-off** — how far does theming go (palette tokens only, or arbitrary CSS)? Arbitrary CSS is a security surface; recommend constrained palette tokens. |

---

## 3. DEPENDENT

| # | Task | Blocked on |
|---|------|-----------|
| 18 | **Search map** | Locations color-coded by tag, sharing the list's filters. Needs **#16 + #17**. |

---

## 4. BLOCKED — external input

| # | Task | Blocked on |
|---|------|-----------|
| 1 | **Terms/Privacy — counsel review** | Counsel-approved text. When ready: replace draft text, remove both banners, bump version in 4 places (`PolicyVersions.cs`, `dashboard.js`, `terms.html`, `privacy.html`). Version bump auto-re-prompts everyone. |
| 2 | **Terms acceptance — make blocking** | Blocked on #1. One-line change (currently skippable via "Skip for now") once approved text ships. |

---

## 5. DEFERRED TO LAST (your prior call — scale work)

Gated on data volume, must land **before any org nears ~1,200 rows**. Nothing depends on them.

- **22 — DataTables phase 2 query contract** (`draw`/`recordsTotal`/`recordsFiltered` + `start`/`length`/`order[]`).
- **23 — Cosmos paging** (the hard part: no cheap OFFSET/COUNT; continuation tokens only). Decide the counting strategy before writing endpoints.
- **24 — AJAX for remaining query surfaces** (after users/events prove the pattern).

---

## 6. Non-blocking decisions to close when convenient

- **Faith-as-attribute** — re-examinable now, expensive later. Keep, or revisit?
- **`faithAffiliation` granularity** — deferred; needs an agreed denominational list.
- **Recurring series in the events list** — collapse a series into one card, or leave 12 cards?
- **Footer flush with home indicator** — `max()` (flush, current) vs `calc()` (adds breathing room).
- **Real-device iOS pass** — edge-to-edge verified by substituting insets, never on a real iPhone.

## 7. Small logged findings (cheap cleanup, unfixed)

SuperAdmin sees "Sign Up" on events (token-vs-membership bug) · 2 orgs still hold lowercase
`"organization"` (harmless) · event `bada594a` has empty `organizationId` · event `9f618807`
over-counts slots by 1 · `TenantIds.IsReserved` dead code · `RootTenantId` duplicated across
files · toast helper copied ~5×.

---

## Feedback I need before planning

1. **O1 — crawler secrets:** both set? Any failing runs since 07-15?
2. **11② cross-org tag gating:** which of the three models?
3. **17 — maps:** Google (paid, keyed) or a cheaper geocoder, and who owns billing?
4. **19 — waiver SMS/email channel:** which provider (Twilio / Azure Communication Services)?
5. **21 — branding scope:** palette tokens only, or arbitrary CSS?
6. **Priority order for the BUILD-NOW set** — my recommended sequence: **O1 → O2 → 14 → 16 →
   12 → 10② → 13**, because 14 unblocks #11 enforcement and 16 unblocks #18, so both open
   downstream work early.
