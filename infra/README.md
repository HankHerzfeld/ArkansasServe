# Infrastructure (Bicep)

> ## ⚠️ `main.bicep` is NON-AUTHORITATIVE — do not apply it
>
> **Status 2026-07-15 (owner decision): reconciliation deferred.** This template does **not**
> describe the live environment, and it has **never successfully applied** — its only runs were
> PR `what-if`s that failed at OIDC login. Nothing in it has ever been enforced against live.
>
> **Applying it today would break production in two specific ways:**
>
> | # | Divergence | Consequence of an apply |
> |---|---|---|
> | 1 | Template declares `publicNetworkAccess: 'Enabled'`, no `ipRules`/`networkAclBypass`. Live firewall was scoped 2026-07-14. | **Reverts the firewall, re-opens Cosmos.** |
> | 2 | Template sets `CosmosDb__ConnectionString` from `listConnectionStrings()` (returns **primary**). Keys were rotated manually 2026-07-14. | **Overwrites the rotated key setting.** |
> | 3 | Containers (incl. `ImpersonationSessions`/`AuditEvents`) were created out-of-band. | Additive only — creates what exists, or 409. Not dangerous. |
>
> Plus: `platformAdminEmailDomain` in `main.prod.bicepparam` is still `arkansasserve.com`, so an
> apply would **re-open the domain-based PlatformAdmin bootstrap elevation** closed on 2026-07-09.
>
> **A clean `what-if` is not sufficient clearance.** ARM returns app-setting values masked, so
> divergence #2 will not appear as a diff. Read the live setting directly before trusting it.
>
> Reconcile all four points against live before any apply. Until then, **Bicep is a record of
> intent, not of reality** — read live via `az`/portal to know what exists.

_The description below is what the template **claims**, and was true as of 2026-07-09. It is
retained for reconciliation work. It is not a statement about live today._

`main.bicep` was reconciled to the verified live environment on 2026-07-02 and re-verified via
`what-if` on 2026-07-09. As of that date every default in the template was the actual deployed
name/config; the divergences listed above have accumulated since.

- `main.bicep` — all resources: Log Analytics, App Insights, Storage (+3 private containers),
  Cosmos (provisioned/free-tier, 8 containers), the Function App (Y1 Consumption, .NET 8
  isolated) + app settings, the GitHub OIDC deploy identity, and the Static Web App + linked
  backend.
- `main.prod.bicepparam` — the production parameter values (Entra IDs are **public**
  identifiers, not secrets).

## Golden rule: never deploy without `what-if` — and today, don't deploy at all

A full-replacement app-settings write and shared Cosmos infra make a blind deploy risky.
**Always** run `what-if` and confirm the change is additive/no-op first.

`what-if` is read-only and safe to run at any time — it is the right way to *measure* the
drift described at the top of this file:

```bash
az deployment group what-if \
  --resource-group rg-arkansas-serve \
  --template-file infra/main.bicep \
  --parameters infra/main.prod.bicepparam
```

The `az deployment group create` command is **deliberately not written out here.** While the
divergences at the top of this file stand, an apply reverts the Cosmos firewall and clobbers
the rotated key — and a clean `what-if` will not warn you about the key, because ARM masks
app-setting values. Reconcile first, then restore this section along with the command.

> Deploy **infra before** the Functions workflow (or re-run Functions after). The app-settings
> collection is a full replacement; the run-from-package settings live backends rely on are
> declared in the template so an infra deploy no longer knocks the backend offline — but keep
> this list in sync with the deploy pipeline.

## What a "clean" what-if looks like (expected noise baseline)

Verified 2026-07-09: **0 resource Create, 0 resource Delete.** The gate is **no Deletes**;
Creates are additive and reviewed. The remaining `Modify` entries are **ARM false positives** —
`what-if` reports the service's computed/default properties as deltas against the template's
(conventional) silence, but an incremental deploy does not change them:

| Resource | Reported delta | Why it's benign |
|---|---|---|
| `cosmos-…` (account) | `sqlEndpoint`, `defaultIdentity`, `analyticalStorageConfiguration`, `configurationOverrides` | Read-only / computed properties; not settable, not stripped. |
| `appi-…` (App Insights) | `Flow_Type`, `Request_Source` | Service-populated defaults. |
| storage `blobServices/default` + 3 containers | `defaultEncryptionScope`, `denyEncryptionScopeOverride`, `properties` | Account-default encryption scope; omission ≠ removal. |
| `func-…` (site) | `siteConfig.*` (cors, ftpsState, minTlsVersion, netFrameworkVersion, use32BitWorkerProcess, localMySqlEnabled) | Classic `Microsoft.Web/sites` siteConfig what-if noise; values already match live. |
| `func-…/config/appsettings` | (masked) | ARM returns secret app-setting values masked, so it always shows a diff. Verify the setting **keys** match, not values. |
| `swa-…` | `deploymentAuthPolicy`, `stableInboundIP`, `trafficSplitting` | SWA service defaults. |
| `…/linkedBackends/…` | `managedServiceIdentityType` | Service default. |

The 8 Cosmos **containers** used to appear here too; they now show `NoChange` because the
template declares the default `indexingPolicy`/`conflictResolutionPolicy` explicitly
(all live containers use the defaults — verified).

**If `what-if` ever shows a resource `Delete`, or a `Modify` outside this table, stop and
reconcile the template with live before deploying.**

## CI gate (`.github/workflows/deploy-infra.yml`)

- **On PRs touching `infra/**`**: compiles the Bicep and runs `what-if`; the job **fails on any
  resource Delete** and posts a change-type summary. Additive Creates are surfaced for review.
- **Apply is manual only** (`workflow_dispatch` with `apply=apply`) and runs against the
  `production-infra` environment, whose required reviewers make the apply a deliberate action.

### One-time setup to enable the workflow

1. Create an OIDC-federated identity (or reuse a dedicated deploy SP) with **Contributor on
   `rg-arkansas-serve`** and a federated credential for this repo. (The existing
   `oidc-msi-9fae` identity only has Website Contributor on the Function App — insufficient for
   infra; do **not** widen it without review.)
2. Add repo secrets: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`,
   `ENTRA_TENANT_ID`, `ENTRA_CLIENT_ID`, `ENTRA_AUDIENCE`.
3. Create a `production-infra` environment with required reviewers.

Until this is done the workflow fails at login and nothing deploys implicitly.

## Notes / open items

- **Out-of-band prod fixes applied 2026-07-12 that the live env needs and a full Bicep deploy
  would also set** (recorded here because the app-settings/CORS have only ever been applied
  manually, not via a full `deployment group create`):
  - `BlobStorage__ConnectionString` app setting was **missing** on the live Function App
    (uploads 500'd with "Blob storage is not configured"). Set it out-of-band to the storage
    account's connection string. The Bicep already declares it (`functionAppSettings`), so a
    full deploy re-asserts it. Also note: services must read config via **both** the `__` and
    `:` key forms — Azure's env-var provider exposes `Foo__Bar` app settings under `Foo:Bar`,
    so a `__`-only read returns null in the deployed app (this bit `BlobService`/`EmailService`;
    fixed in code).
  - **Blob CORS**: the account had no CORS rules, so browser direct-to-blob SAS uploads were
    blocked by preflight. A rule allowing `https://arkansasserve.com` (+ `www`) for
    `PUT/GET/OPTIONS/HEAD` was applied live and is now codified in `main.bicep`
    (`blobService.properties.cors`, param `blobCorsAllowedOrigins`) — `what-if` shows no CORS
    diff, confirming template == live.
- **`platformAdminEmailDomain`** in `main.prod.bicepparam` is currently `arkansasserve.com`,
  i.e. deploying would re-enable domain-based PlatformAdmin bootstrap elevation. If the first
  admin is already seeded, set this to `''` and redeploy to close that path (the middleware
  only honors it when the setting is non-empty).
- Adding containers for **#26 impersonation** (`impersonationSessions`, `auditEvents`) is a
  natural additive change to make here — they'll show as resource `Create`, which the gate
  allows.
- The Website-Contributor role assignment for the deploy identity is intentionally left
  commented out in `main.bicep` (it already exists manually; re-declaring would 409). See the
  note there before bringing it under IaC.
