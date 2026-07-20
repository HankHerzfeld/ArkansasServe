# Arkansas Serve — Design Decisions (next cycle)

_Created 2026-07-19. Settles the design questions that gated #17, #18, #19, #20 and #21, plus two
smaller calls. Companion to `2026-07-16-build-plan.md`, which it SUPERSEDES on maps and branding._

Source of truth for shipped work stays `roadmap.md`. Nothing here is built yet — this is what to
build to.

---

## ⚠️ Two reversals of previously locked decisions

| Was locked (2026-07-16) | Now |
|---|---|
| Maps → free stack (Leaflet + OSM), **no Google billing** | **Google Maps stack.** The owner holds an API key with full stack access, so the constraint that forced the free stack is gone. |
| Branding → **fixed** palette tokens, hex-validated | **All tokens overridable**, with a generated palette + contrast warnings. Still no arbitrary CSS. |

Recorded explicitly because the old lines read as settled and would otherwise be followed.

---

## #19 Waivers + #20 Parental oversight

These were separated on the roadmap but share one data model, so they are settled together.

### The guardian is a real linked record, reachable by magic link
- **A `Guardian` record keyed by email, linked to MANY minors across MANY orgs.** A parent with
  children at two schools is one guardian, one inbox, one identity. Consent is a row per
  `(guardian, minor, org)`.
- **No Entra account.** Access is a **signed, single-use link, 7-day expiry**, re-requestable. A
  consumed or expired link lands on a "request a new link" page, never an error.
  - *Accepted weakness:* whoever holds the link can act as the guardian. Judged acceptable against
    the cost of a new Entra identity type, invite flow and permission model.
- Existing `User.guardianName/guardianEmail/guardianPhone/guardianConsent` (captured at intake)
  become the **seed** for the Guardian record, not a parallel store. Decide the migration at build
  time; do not leave two sources of truth.

### A waiver is a UserTag with a document attached
- Reuses `UserTag`'s shipped machinery: `status` None/Pending/Complete, `completedAt`, `expiresAt`,
  `note`, and `enforcement` advisory / `blockRegistration` / `blockCheckIn`. No second way to
  express "this person is cleared".
- **Signature: attestation is the default, document upload is opt-in PER TAG.** The guardian
  attests via the emailed link and we record timestamp, document version and IP. An org may flag a
  specific waiver as requiring an uploaded document, which needs a review surface — so build the
  attestation path first and the upload path behind that flag.

### Consent is per-organization, ongoing until revoked, with carve-outs
- One consent per org covers that org's events until withdrawn. Matches the per-org `User` doc
  model, so it needs no new scoping concept.
- **Carve-outs requiring fresh approval — exactly two:**
  1. **The org flags the event** ("requires fresh guardian approval") — the manual escape hatch,
     needs no threshold agreed up front.
  2. **Overnight / multi-day** — derived where the event crosses a calendar day boundary.
     ⚠️ Note the edge the rule accepts: a 10pm–2am shift triggers it. Use the LOCAL calendar day
     (`America/Chicago`), never UTC — the same trap `RecurrenceExpander` and PR #70 both hit.
- Deliberately NOT carve-outs: off-site/travel (needs structured org addresses most orgs lack) and
  duration thresholds (arbitrary, would need per-org tuning).

### Revocation cancels future registrations and tells both sides
- Withdrawing consent **cancels the minor's future registrations** and notifies the **org admin AND
  the guardian**, with the reason.
- The notification is what stops this being a silent mass-unregister — the same failure the
  delete-series rule already guards against. Past/attended registrations are untouched.

---

## #17 Maps + #18 Search map — now on Google

### Stack
| API | Used for |
|---|---|
| **Maps JavaScript API** | The map itself: markers, clustering, hover-highlight |
| **Geocoding API** | Street address → real lat/lng at event-create |
| **Places API** | Address autocomplete in the event/org create forms (the "Address auto-populate" roadmap item) |

### One referrer-restricted browser key — and no server key
- Single key, **HTTP-referrer restricted** to `arkansasserve.com/*`, **API-restricted** to those
  three. Served from a **Function App setting** so it rotates without a redeploy.
- **Geocoding runs CLIENT-SIDE at event-create** and the resulting coordinates are stored on the
  event. The backend never calls Google.
- ⚠️ **Why no server key:** the Function App is Consumption (Y1) with **no stable outbound IPs** —
  the identical constraint that made the Cosmos firewall un-scopeable. A server key could carry
  only API restrictions, no IP allowlist, making it strictly weaker than the referrer-restricted
  browser key. Do not "harden" this later by adding one.
- A Maps key is public by nature (it ships in client JS). **Restriction is the control, not
  secrecy** — do not treat it as a leaked credential if it appears in a bundle.
- **Cost:** geocoding bills per event SAVE (not per view); map loads bill per session. Inside the
  free tier at current volume, but this is no longer $0-by-construction the way Leaflet was —
  **set a budget alert.**

### Real coordinates supersede ZIP centroids
#16 populated `latitude`/`longitude` from the bundled ZIP dataset, so every existing coordinate is
a **ZIP centroid** and events in one ZIP stack on the same point. Geocoded addresses replace this
going forward. Pre-existing events keep centroid coordinates until re-saved — plan for a mixed
dataset rather than assuming precision.

### Layout: split view that collapses
- **Large screens:** side-by-side map + list, hovering one highlights the other.
- **Small screens:** collapses to a **list/map toggle** — this app is mostly used on phones.
- Either way it reuses PR #70's filter/sort stack untouched: the map draws whatever survived the
  filter, exactly as the card grid already does. One filter state, never two.

---

## #21 Per-school branding

### All tokens overridable, greens the expected surface
- `--green`, `--green-light`, `--green-pale` are the real brand surface (header, buttons, links,
  badges) and the expected thing to set. But **every token is settable**, including semantic
  red/amber and the grays.
- **Still no arbitrary CSS** — tokens and a logo only. That part of the original lock stands.

### A generated palette does the safety work
- A school picks **one primary color** and we generate a coherent palette from it, **auto-correcting
  contrast** — including flipping body/label text between black and white against harsh
  backgrounds.
- **Preset palettes** for common school schemes: red–white, maroon–gray, blue–white, green–white,
  black–gold, navy–silver. Most schools should never need the color picker.

### Contrast failures WARN, they do not refuse
- A manual override below WCAG AA saves anyway, showing the **measured ratio**. Owner decision: it
  is their brand.
- *Consequence, accepted:* a school can ship a hard-to-read palette to its own users. Surface the
  ratio prominently enough that it was clearly a choice.

### The PWA shell stays platform green
- `theme-color` and `manifest.background_color` remain `#2d6a4f`; branding applies **in-page only**.
- **Why:** those two plus `--green` being identical is exactly what makes the installed app look
  seamless. Per-school manifests would need generating and serving per user, and a PWA is installed
  once — a later brand change would never reach an already-installed app.

---

## Smaller decisions

### SuperAdmin sees "Sign Up" on events → keep the button
A super genuinely can register for any event, so the button is honest. **If the real defect is that
registration then FAILS for them, fix the failure rather than hiding the control.** Confirm the
actual mechanism before changing anything — the finding was logged as "token-vs-membership", which
suggests it may be the Finding 9 family rather than a display bug.

### orgTypes → re-type the tenants AND build a demo school
`orgTypes` filtering shipped in #103 but is switched off (#104) because **every prod tenant is typed
`Organization`** — no School or JDC exists, so the filter would have emptied Approvals entirely.

1. Set the correct `School`/`JDC` type on tenants that really are schools.
2. Fix the two tenants still holding lowercase `"organization"` from the casing split
   (`arkansas-serve-root`, `Test O3 Tenant`) — harmless today, but this is the moment type starts
   meaning something.
3. **Build a demo school user base**, mirroring the existing demo-events user base — a demo School
   tenant with students, a school admin, and approvable hours, so the school-side flows (approval
   policy, `orgTypes` filtering, per-org dashboards) can be exercised without waiting for a real
   district.
4. Then switch `orgTypes: 'schoolLike'` back on for `/admin-portal.html`.

---

## Suggested order

**#20/#19 first** (guardian record → magic link → waiver-as-tag → consent + carve-outs → revocation),
because the guardian model gates the most and nothing else depends on maps or branding.
Then **the demo school + re-typing** (small, unblocks `orgTypes` and makes school flows testable),
then **#17/#18 maps**, then **#21 branding**.

One PR at a time with a prod clickthrough before the next, per the standing rule.
