# Arkansas Serve — MVP: Arkansas-Serve-hosted informational listings

_Created 2026-07-22. First production step before contracting begins. Companion to
`2026-07-22-feature-audit.md` (the full status chart) — this doc narrows to the one slice we
build first and lists every task in priority order._

## Goal

Publish **one-sided informational event posts, hosted by Arkansas Serve on behalf of outside
organizations that are not yet tenants on the site.** Each post carries only:

- **Location** (address + map)
- **Contact information** (name / email / phone / website)
- **Attribution** to the outside organization actually running the activity

These are **display-only**: no on-site sign-up, no group registration, no service-credit/hours
framing, no shifts or sign-up questions. They exist to seed the site with live activity ahead of
contracting real organizations onto the platform.

## Key finding (why this is a build, not a config change)

The MVP listing is **not a smaller version of the current event** — it is a genuinely different,
simpler object. The schema half-anticipates it (there is already a contact block and a full set of
crawler-attribution fields) but **the UI never exposes the attribution on the form and never
renders it on the public pages**, and every event created today is shaped as a registerable
volunteer opportunity (Hours Credit, Max Sign-ups, Shifts, Sign-up questions).

Live-verified 2026-07-22 in an authed SuperAdmin session (`arkansasserve.com`):

- The one prod event renders with "⏱ 2 hours credit", **Sign Up**, **Register a group**, and
  **Day-of check-in** — the whole registration surface.
- **Bug #138 reproduced:** the **Sign Up** button shows for a SuperAdmin. `events.js:330` /
  `event.js:224` gate on `profile.adminLevel === 'Student'` read from the **token**, and a
  SuperAdmin's role comes from a *membership*, so the token reads "Student" (the Finding-9 trap).
- **No org/host attribution renders on the event detail** — the header shows title + status only.
  The model's `crawlerAttribution` / `crawlerSourceName` / `crawlerSourceUrl` fields are never
  displayed anywhere, despite the model comment promising they "must display."
- The New Event form has **no host-organization field** and **no contact-URL field**
  (`contactUrl` exists on the model but the form omits it).

## Design decision (chosen)

Use a **dedicated listing type**, not the dormant crawler pipeline. The crawler path already models
"outside-org event" but carries dedup/provenance semantics (`crawlerSourceId`, `crawledAt`) that
don't apply to a hand-entered post, and reusing `isCrawled` would misreport provenance. Either way
the attribution **display** has to be built — it does not exist yet — so the crawler path buys us
nothing here. Build the attribution renderer once and share it with crawled events later.

New Event fields:

- `listingType`: `"hosted"` (default, today's registerable event) | `"external"` (informational,
  externally hosted, display-only). This one flag drives the UI: display-only, attribution shown,
  registration surface + hours framing suppressed.
- `hostOrganizationName`: the outside org's name — rendered as "Hosted by {name}".
- `hostOrganizationUrl`: optional link to that org.
- `contactUrl`: already on the model; wire it through the form and display.
- `externalUrl` stays as-is ("More info ↗" for this specific event).

---

## P0 — build now (in order)

1. **Event model + backend persistence.** Add `listingType`, `hostOrganizationName`,
   `hostOrganizationUrl` to `Event.cs`; wire them plus the existing `contactUrl` through
   create/update in `EventFunctions.cs`.
2. **Create form: "Informational listing" toggle.** When on, hide Hours Credit, Max Sign-ups,
   Shifts, Sign-up questions, Recurrence; show Host organization name + Host URL + full contact
   block (incl. contact URL). `org-portal.html`, `org-portal.js`.
3. **Public display: attribution + CTA swap.** On external listings show "Hosted by {hostOrg}" and
   a "More info / Visit host ↗" button; suppress Sign Up + Register-a-group; drop the "hours credit"
   line from the card. Build the shared attribution renderer here. `event.js`, `events.js`.
4. **Fix #138 (Sign-Up leak).** Gate registration affordances on the membership / `/users/me`
   strongest level, not the token, so real non-Student roles never see a stray Sign Up.
   `events.js`, `event.js`.
5. **Guard the `PUT` full-replace landmine (#139).** Ensure new fields round-trip; confirm the
   external-listing edit path sends a full payload and/or make update fields nullable.
   `EventFunctions.cs`.
6. **Pre-go-live data hygiene.** Delete "Test AS Event"; resolve/reject the 2 test category
   proposals in the queue; clean the 2.0h orphan log.

## Actionable backlog by severity (deferred behind the MVP)

**P1 — latent, high blast radius**
- **O2 — `BlobService` ctor throw:** a malformed blob connection string 500s every `EventFunctions`
  route before auth. Not biting now, but every event route (incl. new listings) depends on it. Wrap
  the ctor parse → log-and-degrade. `BlobService.cs`.
- **Bicep drift / admin-bootstrap re-open:** an apply reverts firewall, clobbers the rotated key,
  re-opens the closed admin backdoor. Owner-deferred; reconcile before any infra apply.

**P2 — correctness landmines / data**
- `#139` `PUT /events/{id}` full-replace (covered by P0 #5).
- 2 tenants hold lowercase `"organization"`; event `bada594a` has empty `organizationId`.

**P3 — cheap cleanup**
- Converge duplicated `RootTenantId` (×5); delete dead `TenantIds.IsReserved`; extract one shared
  toast helper. "Grade 17" showing for a SuperAdmin — minor display quirk.

**Owner-action (not code)**
- Crawler imports 0 (per-source API keys unset); delete unused `CRAWLER_SERVICE_TOKEN`. Set a
  Google Maps budget alert (bills per event save).

## Out of MVP scope — do not touch now

Guardians & carve-outs (#20), ACS email delivery, real-time waivers (#19), per-school branding
(#21), DataTables phase 2, offline check-in (#15), Terms/Privacy blocking, School/JDC approval
policy (dormant — no school tenants). None are needed to publish one-sided informational listings.
