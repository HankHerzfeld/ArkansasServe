# Arkansas Serve — Production Cutover Plan

> **Architecture decision (locked): Standalone Function App + SWA Linked Backend.**
> The Static Web App serves the frontend and proxies `/api/*` to the existing
> standalone Function App `func-arkansas-serve-arksrv` via a SWA *linked backend*.
> This is a planning document — **no code or infra is changed by reading it.**
>
> Companion to [`project-state-and-roadmap.md`](project-state-and-roadmap.md). Where the two
> disagree, this document is newer and authoritative for the cutover.
>
> **Updated 2026-07-02 with verified live Azure state — see §0.** The pre-verification
> theory (broken link + divergent secrets) was **disproven**; the live infra works. The task
> is codifying it into IaC safely, not repairing a broken runtime.

---

## 0 · Verified live state (read-only audit, 2026-07-02)

I ran a read-only `az` sweep of `rg-arkansas-serve`. The earlier hypothesis in this plan —
"the linked backend doesn't exist and the SWA/Function App secrets have diverged, causing the
401 saga" — **does not match reality.** Corrected facts:

| Item | Verified state |
|------|----------------|
| **Linked backend** | **Already exists.** `swa-arkansas-serve-arksrv → func-arkansas-serve-arksrv`, created 2026-06-29, `provisioningState: Succeeded`. `/api/*` *does* route to the Function App. |
| **Secret parity** | **In sync, not divergent.** Function App and SWA app settings hold *identical* Cosmos, Blob, Entra, and App Insights values. |
| **Entra values** | `Entra__TenantId = 2d72a425…` (SalineServe CIAM), `Entra__ClientId = 16150d6e…`, `Entra__Audience = api://16150d6e…` — **all match `frontend/js/auth.js`.** No frontend↔backend drift. |
| **Function App** | `func-arkansas-serve-arksrv`, Central US, **Windows Consumption (`Y1` Dynamic)** plan `asp-arkansas-serve-arksrv`, .NET 8 (`v8.0`), `FUNCTIONS_WORKER_RUNTIME=dotnet-isolated`, `FUNCTIONS_EXTENSION_VERSION=~4`, `WEBSITE_RUN_FROM_PACKAGE` set, running, HTTPS-only. **No managed identity attached.** |
| **SWA** | `swa-arkansas-serve-arksrv`, **Standard** SKU (linked backend supported), Central US, host `brave-bush-0d4b9f510.7.azurestaticapps.net`. |
| **`oidc-msi-9fae`** | **NOT orphaned.** User-assigned identity with a GitHub federated credential (`repo:HankHerzfeld/ArkansasServe:ref:refs/heads/main`) and `Website Contributor` on the Function App → it's the **CI/CD deploy identity** for the Functions workflow. clientId `276e9803-a4b3-4090-852e-ee5336f75a00`. **Keep it.** |
| **Cosmos data-plane RBAC** | **Empty** — the audit's `readMetadata` assignment was never applied. Irrelevant to the current connection-string runtime; only needed for the MI-hardening path (§3c). |
| **Cosmos security** | `disableLocalAuth: false`, `publicNetworkAccess: Enabled` (key auth reachable from internet — still an open hardening item). |
| **Storage** | `allowBlobPublicAccess: false` — confirms the P1 correction: anonymous blob reads are **already blocked at the account level**, overriding the container's `'Blob'` setting. |

### What this means for the diagnosis

- **The 401 saga is not caused by infra divergence.** The link exists and every secret matches.
  The historical 401s were most plausibly (a) the pre-2026-06-29 window before the linked backend
  was created, and (b) the frontend redirect-loop fixed in the "2 prop push" commit. **To confirm
  current behavior, do a live authenticated smoke test** (§6 step 1) rather than trusting either theory.
- **The real, still-true problem is IaC drift (P2), not a runtime bug.** `infra/main.bicep` does not
  describe the Function App, its plan, the linked backend, or `oidc-msi-9fae`, and it uses *wrong
  names* for App Insights / Log Analytics (see §3a). A blind `az deployment group create` today would
  diverge from or disturb the working environment. The job is to **codify the working reality into
  Bicep**, verified with `what-if`.

### ⚠ Secret exposure note

Running the audit surfaced the live **Cosmos and Blob account keys** in plaintext (they're stored as
connection strings on both the Function App and SWA). They now exist in this session's transcript.
Because this data store holds records for minors/court-involved youth, **rotate both keys** if this
transcript isn't fully trusted, and prefer the managed-identity model (§3c) which removes keys
entirely. Regenerate: `az cosmosdb keys regenerate` and `az storage account keys renew` (then update
the Function App settings).

---

## 1 · Target architecture — the connection map

```
                         Browser (arkansasserve.com)
                                │
              ┌─────────────────┼──────────────────────────┐
              │ static assets   │ /api/*        │ PKCE login │
              ▼                 ▼               ▼            
   ┌──────────────────┐   (linked backend    ┌────────────────────────┐
   │  Static Web App  │────proxy, same───────►│ Entra External ID      │
   │  (Standard SKU)  │    origin)            │ (CIAM, *.ciamlogin.com)│
   │  serves frontend/│        │              └────────────────────────┘
   └──────────────────┘        ▼                        ▲
                       ┌──────────────────┐   OIDC keys  │
                       │  Function App    │──────────────┘
                       │  func-...-arksrv │
                       │  .NET 8 isolated │
                       └───────┬──────────┘
                    ┌──────────┼─────────────┬───────────────┐
                    ▼          ▼             ▼               ▼
             ┌──────────┐ ┌──────────┐ ┌──────────┐  ┌──────────────┐
             │ Cosmos DB│ │  Blob    │ │App Insights│ │Runtime storage│
             │ (7 cont.)│ │event-photos│(→Log Anal.)│ │(AzureWebJobs) │
             └──────────┘ └──────────┘ └──────────┘  └──────────────┘
```

Every connection, source → target → mechanism:

| # | From | To | Mechanism | Owned/verified where |
|---|------|-----|-----------|----------------------|
| 1 | Browser | SWA | HTTPS, custom domain `arkansasserve.com` | SWA hostname binding |
| 2 | Browser | Entra External ID | PKCE redirect to `*.ciamlogin.com` | `frontend/js/auth.js` |
| 3 | SWA | Function App | **Linked backend** proxy of `/api/*` (same-origin) | Bicep `linkedBackends` (NEW) |
| 4 | Function App | Entra | OIDC discovery + signing keys fetch | `AuthMiddleware.cs` ← `Entra__TenantId` |
| 5 | Function App | Cosmos DB | `CosmosClient` | `Program.cs` ← `CosmosDb__ConnectionString` |
| 6 | Function App | Blob Storage | `BlobServiceClient` (SAS issue + reads) | `BlobService.cs` ← `BlobStorage__ConnectionString` |
| 7 | Function App | App Insights | telemetry SDK | `Program.cs` ← `APPLICATIONINSIGHTS_CONNECTION_STRING` |
| 8 | Function App | Runtime storage | `AzureWebJobsStorage` (host state) | Function App setting |
| 9 | Browser | Blob Storage | **SAS** upload (write) + **SAS** read (see §4 P1) | `BlobService` SAS tokens |
| 10 | App Insights | Log Analytics | workspace-based sink | Bicep (already wired) |

Note connection #9: with the P1 fix (§4), photo **reads** move from public URL to short-lived
SAS read URLs. Uploads already use SAS.

---

## 2 · File-by-file — what each file connects to, and what changes

| File | Connects to | Change needed |
|------|-------------|---------------|
| `infra/main.bicep` | *everything* | **Major rewrite** — add Function App + plan + its app settings + linked backend; move secrets off the SWA; fix blob container. See §3. |
| `frontend/js/api.js` | SWA `/api/*` | None. Relative `/api` is correct once the linked backend exists. |
| `frontend/js/auth.js` | Entra External ID | Verify `TENANT_ID`/`CLIENT_ID` are the **CIAM** tenant values and match the Function App's `Entra__*` (see §5). No structural change. |
| `frontend/staticwebapp.config.json` | SWA routing | **Remove** `platform.apiRuntime` — that key configures *managed* functions; with a linked backend it's misleading/ignored. Keep routes, CSP, headers. |
| `backend/.../Program.cs` | Cosmos, App Insights, `AuthConfig` | None structurally — but its settings must live on the **Function App** (§3). |
| `backend/.../Middleware/AuthMiddleware.cs` | Entra (`config.TenantId`) | None structurally. Depends entirely on correct `Entra__TenantId` on the Function App. |
| `backend/.../Services/BlobService.cs` | Blob Storage | Add a read path via `GenerateReadSasToken` (already exists) and stop relying on `GetPublicBlobUrl` for event photos (§4). |
| `backend/.../Functions/EventFunctions.cs` | Blob | On event GET, return **SAS read URLs** for `photoUrl` instead of raw public URLs (§4 P1). |
| `.github/workflows/main_func-arkansas-serve-arksrv.yml` | Function App | **Fix** `AZURE_FUNCTIONAPP_PACKAGE_PATH` — it's `.` (repo root); must publish `backend/ArkansasServe.Functions`. See §6. |
| `.github/workflows/azure-static-web-apps.yml` | SWA | Keep frontend-only (do **not** add `api_location`). Backend deploys via its own workflow. Correct as-is. |
| `*.csproj`, `staticwebapp.config.json`, workflows | build target | Confirm `.NET 8` everywhere (already consistent; README's ".NET 10" claim is stale doc only). |

---

## 3 · The Bicep rewrite — how it functions from here

The current template provisions Log Analytics, App Insights, Storage, Cosmos, and the SWA, and
then dead-ends the secrets on the SWA. The rewrite makes Bicep describe the **whole** runtime and
put secrets where the code reads them.

> **Reframed after the audit:** the live infra works. Bicep is not "fixing a broken app" — it is
> **capturing the working environment as code** so future deploys are safe and repeatable. Use the
> *exact* deployed resource names below; the current Bicep param defaults use different names
> (`ai-arkserve-prod`, `log-arkserve-prod`) and would create duplicates.

### 3a. What Bicep will own after the rewrite

**Exact deployed names to match (verified 2026-07-02):**

| Resource | Deployed name | Current Bicep default | Action |
|----------|---------------|----------------------|--------|
| Log Analytics | `log-arkansas-serve-arksrv` | `log-arkserve-prod` | rename param to match |
| App Insights | `appi-arkansas-serve-arksrv` | `ai-arkserve-prod` | rename param to match |
| Storage | `starkansasservearksrv` | *(param)* | pass as param |
| Cosmos | `cosmos-arkansas-serve-arksrv` | *(param)* | pass as param; db `arkansas-serve-db` |
| SWA | `swa-arkansas-serve-arksrv` (Standard) | *(param)* | pass as param |
| App Service Plan | `asp-arkansas-serve-arksrv` (**Y1 Windows**) | *(absent)* | **add** |
| Function App | `func-arkansas-serve-arksrv` (.NET 8 isolated) | *(absent)* | **add** |
| Deploy identity | `oidc-msi-9fae` (user-assigned) | *(absent)* | **add** (see §3b) |
| Linked backend | `func-arkansas-serve-arksrv` (exists, Succeeded) | *(absent)* | **add** |

Change:
- **`eventPhotosContainer`** → `publicAccess: 'None'` (P1). Confirmed: `storage.allowBlobPublicAccess`
  is `false` live, so anonymous reads are *already* blocked — this just removes the contradictory
  declaration. The functional fix is the code change in §4.
- **Keep** `staticWebAppAppSettings`? Optional. With a linked backend the SWA's app settings are
  **not** injected into the Function App, so they're currently harmless dead weight (they happen to
  match). Cleanest: drop the secret settings from the SWA so there's exactly one home for secrets
  (the Function App) and no future reader is misled into thinking the SWA copy is live.

Add (new resources) — **all already exist in Azure; Bicep is catching up**:
- **App Service Plan** `asp-arkansas-serve-arksrv` — **Windows Consumption `Y1` / Dynamic** (this is
  what's deployed; do *not* silently switch to Flex `FC1` in the same template — a plan SKU change is
  a migration, track it separately if desired).
- **`Microsoft.Web/sites` (kind `functionapp`)** = `func-arkansas-serve-arksrv`, .NET 8 isolated,
  `~4`, `WEBSITE_RUN_FROM_PACKAGE`, HTTPS-only, with these app settings (values already live):
  - `FUNCTIONS_EXTENSION_VERSION = ~4`, `FUNCTIONS_WORKER_RUNTIME = dotnet-isolated`
  - `AzureWebJobsStorage` (runtime storage — reuses `starkansasservearksrv`)
  - `APPLICATIONINSIGHTS_CONNECTION_STRING`
  - `CosmosDb__ConnectionString`, `CosmosDb__DatabaseName`
  - `BlobStorage__ConnectionString`
  - `Entra__TenantId`, `Entra__ClientId`, `Entra__Audience`
  - Attach `oidc-msi-9fae` as a **user-assigned identity**? Not required for runtime (it's the deploy
    identity, and runtime uses connection strings). Only add an identity block when you adopt the MI
    hardening path (§3c) — then prefer a **system-assigned** identity for data access.
- **`oidc-msi-9fae`** (`Microsoft.ManagedIdentity/userAssignedIdentities`) + its federated credential
  (`repo:HankHerzfeld/ArkansasServe:ref:refs/heads/main`) + its `Website Contributor` role assignment
  on the Function App. Declaring these in Bicep keeps `what-if` from proposing to drop them.
- **`Microsoft.Web/staticSites/linkedBackends@2024-04-01`** — **already exists** (Succeeded); declare
  it so Bicep owns it rather than leaving it as untracked drift:
  ```bicep
  resource linkedBackend 'Microsoft.Web/staticSites/linkedBackends@2024-04-01' = {
    parent: staticWebApp
    name: 'func-arkansas-serve-arksrv'   // match the deployed link name
    properties: {
      backendResourceId: functionApp.id
      region: location                    // Central US — confirmed working
    }
  }
  ```
  SWA is already **Standard** and Central US already hosts a working link, so region support is
  confirmed by existence.

### 3b. Adopting the *existing* resources (avoid the drift trap)

Everything above **already exists in Azure** — the Function App, its plan, `oidc-msi-9fae`, and even
the linked backend. A naive `az deployment group create` could conflict with or reset them. Two safe
paths:

1. **Preferred — reconcile via what-if.** Author the resources in Bicep to match reality, then run
   `az deployment group what-if` and read the diff *before* applying. Adjust Bicep until what-if
   shows no destructive change to the live Function App, then deploy. Bicep is declarative, so once
   names/props match, a deploy is a no-op on those resources and simply *adds* the linked backend +
   corrected settings.
2. **Alternative — `az bicep`/`az resource` import** of the existing definitions to seed the
   template, then trim. More faithful but noisier.

Either way: **never run the deploy without a clean what-if first.** That is the single guardrail
that prevents repeating the drift that caused P2.

### 3c. Secret model — connection strings now, managed identity as the end-state

- **Cutover (minimal):** keep the code's current connection-string model. Bicep computes
  `cosmos.listConnectionStrings()` and `storage.listKeys()` and writes them to the **Function App**
  settings — matching what's already live. (Nothing to "fix" at runtime; this just makes the working
  settings reproducible from IaC.)
- **Hardening (recommended follow-up, now more urgent — keys were exposed in the audit):** switch
  Cosmos + Blob to a **system-assigned managed identity** on the Function App with data-plane RBAC
  (Cosmos SQL data-contributor role `00000000-0000-0000-0000-000000000002`; `Storage Blob Data
  Contributor` on `starkansasservearksrv`), then set `Cosmos disableLocalAuth: true` and rotate the
  now-exposed keys. Requires code changes (`DefaultAzureCredential` in `Program.cs`/`BlobService.cs`).
  This eliminates connection-string secrets entirely and closes the "key auth reachable from the
  internet" finding. Track separately; do not block cutover on it.

### 3d. New/changed parameters

Add: `functionAppName`, `functionAppPlanName`, `functionAppPlanSku` (default `Y1`),
`deployIdentityName` (`oidc-msi-9fae`). **Fix** the `appInsightsName` / `logAnalyticsWorkspaceName`
defaults to the deployed names (§3a table) or they'll create duplicates. Keep a `main.parameters.json`
(or `.bicepparam`) pinning the exact prod values so nothing is typed on the CLI.

---

## 4 · P1 — event-photo access (reconcile code with infra)

Current contradiction: container declares `publicAccess: 'Blob'`, account blocks it
(`allowBlobPublicAccess: false`), and `BlobService.GetPublicBlobUrl` hands back a URL that
therefore **won't load anonymously**. Net: photo reads are effectively broken *and* the intent is
ambiguous.

Resolution (aligns with the README's "short-lived SAS" policy):
1. Bicep: container `publicAccess: 'None'` (explicit).
2. Code: event read paths call `GenerateReadSasToken(...)` (already implemented in `BlobService.cs`)
   and return a time-limited SAS read URL as `photoUrl`; retire `GetPublicBlobUrl` for event photos.
3. CSP already allows `img-src https://*.blob.core.windows.net` — SAS URLs satisfy it. No CSP change.

---

## 5 · Config single-source-of-truth (the thing that must never drift again)

Three Entra values must stay identical in **three** places. **Verified 2026-07-02: all three are
currently in sync** — no action needed now, but the rewrite must not break the alignment.

| Value | Frontend (`auth.js`) | Backend (Function App settings) | Verified live |
|-------|----------------------|--------------------------------|---------------|
| Tenant | `TENANT_ID = 2d72a425…` | `Entra__TenantId = 2d72a425…` | ✅ match |
| Client | `CLIENT_ID = 16150d6e…` | `Entra__ClientId = 16150d6e…` | ✅ match |
| Audience | `api://16150d6e…/User_Impersonation` | `Entra__Audience = api://16150d6e…` | ✅ match |

Tenant `2d72a425-cd59-4b55-a8d6-a67c1ed565c6` is confirmed to be the **SalineServe.onmicrosoft.com**
CIAM external tenant (Saline Serve is the original/legacy name; Arkansas Serve, LLC is the current
entity and Saline Serve is its Saline-County d/b/a). `AuthMiddleware` derives the `*.ciamlogin.com`
issuer/key endpoint from this GUID, so this is correct — the tenant is **not** a 401 cause. The one
governance rule going forward: whoever edits `auth.js` and whoever edits the Function App settings must
change these together, since they are two independent writers of the same three values.

---

## 6 · Sequenced action plan (now → production)

Each step has a **gate** — do not proceed until it passes.

1. **Live authenticated smoke test** (the one thing the read-only audit couldn't settle). Log into
   the deployed SWA and watch a real `/api/users/me` call in devtools. *Gate:* it returns 200 for a
   valid session and 401 *only* when unauthenticated. This confirms whether the 401 saga is actually
   resolved (audit says the infra is correct; only a live token proves the token path). If it still
   401s with a valid token, capture the response and the token's `aud`/`iss`/`exp` before touching Bicep.
2. **Author the Bicep rewrite** per §3 on a branch, using the exact deployed names (§3a table).
   *Gate:* `az bicep build` clean.
3. **`what-if`** against `rg-arkansas-serve`. *Gate:* diff shows only additive/no-op changes and
   **no** destructive change to the live Function App, plan, identity, linked backend, or data stores.
4. **Deploy Bicep.** *Gate:* what-if-predicted result matches; all existing resources unchanged.
5. **Fix + run the Function App workflow** (package path → `backend/ArkansasServe.Functions`).
   *Gate:* deploy green; `func-...-arksrv` responds.
6. **Fix `staticwebapp.config.json`** (drop `apiRuntime`) and deploy frontend. *Gate:* site loads;
   auth smoke test from step 1 still green.
7. **Land the P1 code change** (SAS read URLs) + Bicep container `None`. *Gate:* an event photo
   loads via a SAS URL; a bare blob URL returns 404/403.
8. **(Recommended) Rotate the exposed Cosmos + Blob keys** and update Function App settings. *Gate:*
   app still works on the new keys. (Best combined with the MI hardening in §3c.)
9. **Validate all four role flows** (Student / OrgStaff / SchoolAdmin / PlatformAdmin) per the
   roadmap checklist §5b⑪.
10. **Custom domain + monitoring smoke test**, then go live.

---

## 7 · Open items — RESOLVED by the 2026-07-02 audit

- [x] **CIAM tenant GUID** — `2d72a425-…` **is** the SalineServe.onmicrosoft.com CIAM tenant, and it
      matches `auth.js` and the Function App. Correct; no fix needed. (§5)
- [x] **Function App hosting plan** — **Windows Consumption `Y1` Dynamic** (`asp-arkansas-serve-arksrv`).
      Bicep must match `Y1`; do not switch to Flex in the same deploy. (§3a)
- [x] **Linked-backend region support** — Central US already hosts a working, `Succeeded` linked
      backend (created 2026-06-29). Confirmed. (§3a)
- [x] **Existing app settings** — captured. Function App has all runtime settings; they match the SWA
      copy and `auth.js`. No divergence. (§0)
- [x] **Cosmos data-plane RBAC** — **not applied** (assignment list is empty). Irrelevant to the
      current connection-string runtime; required only for the MI hardening path. (§3c)
- [x] **`oidc-msi-9fae`** — **NOT orphaned.** It's the GitHub Actions deploy identity (federated cred
      on `refs/heads/main` + `Website Contributor` on the Function App). **Keep and declare in Bicep.**

### Still open (new, from the audit)

- [ ] **Rotate the exposed Cosmos + Blob account keys** (surfaced in plaintext during the audit). (§0)
- [ ] **Confirm** the workflow's `AZUREAPPSERVICE_CLIENTID_72ACD1D6…` secret value equals
      `oidc-msi-9fae`'s clientId `276e9803-a4b3-4090-852e-ee5336f75a00` (GitHub secret values aren't
      readable via `az`; check in GitHub or by inference from a successful deploy). If yes, the deploy
      identity story is fully consistent.
- [ ] **P3 auth backdoor** (`@arkansasserve.com` → auto-`PlatformAdmin` in `AuthMiddleware.cs:72`) is
      unchanged and still a production risk — out of scope for this cutover doc but track it.

---

---

## 8 · Reconciled Bicep + what-if results (2026-07-02)

`infra/main.bicep` was rewritten to match verified live state, with `infra/main.prod.bicepparam`
holding the Entra values. `az deployment group what-if` against `rg-arkansas-serve` is **clean and
non-destructive**:

- **0 resource creates, 0 resource deletes**, 5 NoChange (identity, federated credential, Log
  Analytics, Storage, App Service Plan — exact matches), 17 Modify (all benign), 3 Ignore (Entra
  directories).
- **No partition-key, `enableFreeTier`, `capabilities`, or tag changes** — the destructive-risk
  properties are all matched. The 17 Modifies are read-only/computed props (e.g. Cosmos `sqlEndpoint`,
  SWA `stableInboundIP`/`repositoryUrl`), default re-assertions (container indexing policies identical
  to current), and the **intended** app-settings change.

### Corrections baked into the reconciled template

- Cosmos is **provisioned with DB-level shared throughput 1000 RU/s**, *not* serverless (removed the
  `EnableServerless` capability, added `options.throughput`), `enableAutomaticFailure: true`,
  `enableFreeTier: true` (immutable — must not be stripped).
- Real containers are **PascalCase** (`Tenants`, `Users`, `Events`, `EventRegistrations`,
  `ServiceLogs`, `PendingApprovals`, `Notifications`) plus a change-feed **`leases`** container.
- Function App (`Y1` Windows Consumption), its plan, `oidc-msi-9fae` + federated credential, and the
  linked backend are now all declared. The `Website Contributor` role assignment is **left commented
  out** — it already exists, so a deploy would 409; delete the manual one first to adopt it.
- Original resource tags (`environment`/`managedBy`/`project`, plus the App Insights hidden-link on
  the Function App) preserved.

### 🔴 New finding — likely cause of the "500" half of the saga (separate from auth)

`CosmosService.cs:43-49` defaults to **lowercase** container names (`tenants`, `users`, `events`,
`registrations`, …) unless overridden by `CosmosDb__Containers__*` app settings — and the Function App
has **no such overrides**. But the live containers are **PascalCase** and Cosmos names are
**case-sensitive**, so every data operation targets a non-existent container → 404s surfacing as 500s.
Note `registrations` (code) vs `EventRegistrations` (live) is a name mismatch, not just case. The
reconciled Bicep adds the 7 `CosmosDb__Containers__*` mappings to fix this. **This is a runtime
behavior change — validate with the smoke test (§6 step 1) right after deploy.**

### Code-review fixes applied to the reconciled template (2026-07-02)

- **`defaultTtl` preserved** on `ServiceLogs`/`leases` (`-1`) and **`Notifications` (`2592000` =
  30-day expiry)** — omitting these would have silently disabled TTL (notifications never expiring;
  a retention change on minors' data). Verified against live; final what-if shows no `defaultTtl` diff.
- **Throughput moved to a `throughputSettings/default` child resource** so `cosmosSharedThroughput`
  actually takes effect on redeploy (`options.throughput` on the database is honored only at creation).
- **CORS origin** now uses `staticWebApp.properties.defaultHostname` instead of a hardcoded,
  non-existent hostname.
- **SWA GitHub linkage** (`provider`/`repositoryUrl`/`branch`) declared to match live so an
  incremental deploy can't clear it (these dropped out of the what-if diff entirely).
- **Linked-backend `region`** pinned to `'Central US'` to kill a permanent spurious what-if diff.
- **`CosmosService.cs` defaults corrected** to the real PascalCase names (`Tenants`, `Users`,
  `Events`, `EventRegistrations`, `ServiceLogs`, `PendingApprovals`, `Notifications`) so the fix
  survives in local dev / new environments that lack the `CosmosDb__Containers__*` overrides. The
  app-setting overrides stay as an immediate fix for the currently-deployed build.

### Two operational items the review surfaced (not code — do at cutover)

- **Deploy ordering (app-settings full-replacement):** the `appsettings` child resource replaces the
  whole collection. Deploy **infra before** the Functions workflow, or **re-run the Functions deploy
  after** any infra deploy, so a pipeline-set value (e.g. `WEBSITE_RUN_FROM_PACKAGE`) isn't reverted.
- **Delete the dead SWA secret copies** (ARM incremental never removes omitted child settings, so
  they persist and would survive key rotation as a stale second secret store):
  ```
  az staticwebapp appsettings delete --name swa-arkansas-serve-arksrv \
    --setting-names CosmosDb__ConnectionString BlobStorage__ConnectionString \
    Entra__TenantId Entra__ClientId Entra__Audience CosmosDb__DatabaseName \
    APPINSIGHTS_INSTRUMENTATIONKEY APPLICATIONINSIGHTS_CONNECTION_STRING
  ```

### Not yet done in this pass (deliberately)

- The P1 **code** change (SAS read URLs) is not an infra change — still tracked in §4.
- Key rotation (§0) and MI hardening (§3c) remain follow-ups.

---

*Created 2026-07-02. Updated same day with verified live Azure state and a clean what-if against the
reconciled `infra/main.bicep`. Supersedes the API-hosting sections of
`project-state-and-roadmap.md` §5b②.*
