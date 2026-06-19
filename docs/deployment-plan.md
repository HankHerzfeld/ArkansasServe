## Arkansas Serve Deployment Plan (arkansasserve.com)

This guide is a practical runbook for deploying this repo to Azure Static Web Apps (frontend) with a managed Azure Functions API (backend under `/api/*`).

Current repo status (as of 2026-06-18):
- `backend/ArkansasServe.Functions` exists and targets `.NET 8`.
- `frontend` is static HTML/CSS/JS and calls `/api/*`.
- There is no `infra/` folder yet.
- There is no `.github/workflows` deployment workflow yet.

---

## 1. Deployment architecture you are implementing

1. Frontend (`frontend/`) is hosted on Azure Static Web Apps (SWA).
2. Backend (`backend/ArkansasServe.Functions/`) is deployed as the SWA managed API.
3. Frontend API calls in `frontend/js/api.js` use `/api/*`, which SWA routes to the Functions backend.
4. Auth uses Microsoft Entra External ID with redirect handling in `frontend/auth-callback.html` and config in `frontend/js/auth.js`.
5. Data and files are external dependencies:
	- Azure Cosmos DB
	- Azure Blob Storage

---

## 2. Prerequisites

Run these checks locally first:

```bash
dotnet --version
az --version
func --version
node --version
npm --version
```

Install SWA CLI if needed:

```bash
npm install -g @azure/static-web-apps-cli
```

Sign in and set subscription:

```bash
az login
az account set --subscription "<YOUR_SUBSCRIPTION_ID_OR_NAME>"
az account show --output table
```

---

## 3. Confirm the app builds before cloud work

From repo root:

```bash
cd backend/ArkansasServe.Functions
dotnet restore
dotnet build
```

Expected outcome: build succeeds with target framework `net8.0`.

---

## 4. Create Azure resources

Use one resource group for the initial deployment.

If your Azure resources already exist, do not recreate them. Use the fast path in section 4.0, then continue at section 5.

Suggested names:
- Resource group: `rg-arkansas-serve`
- Region: `eastus2` (or your preferred region)
- Cosmos account: globally unique, for example `cosmos-arkserve-prod`
- Storage account: globally unique, for example `starkserveprod`
- SWA resource: `swa-arkansas-serve-prod`

### 4.0 Fast path when resources already exist

1. Confirm you are in the correct subscription:

```bash
az account show --output table
```

2. Identify existing resource names in your resource group:

```bash
az resource list \
	--resource-group rg-arkansas-serve \
	--query "[].{name:name,type:type,location:location}" \
	--output table
```

3. Capture the actual names you will use for wiring:
	 - Static Web App name
	 - Cosmos DB account name
	 - Cosmos DB database name
	 - Storage account name
	 - Entra tenant ID and app (client) ID

4. Validate required containers exist in Cosmos DB:

```bash
az cosmosdb sql container list \
	--account-name <COSMOS_ACCOUNT_NAME> \
	--resource-group rg-arkansas-serve \
	--database-name <COSMOS_DATABASE_NAME> \
	--query "[].name" \
	--output table
```

5. Validate required blob container exists:

```bash
ACCOUNT_KEY=$(az storage account keys list --resource-group rg-arkansas-serve --account-name <STORAGE_ACCOUNT_NAME> --query "[0].value" -o tsv)

az storage container list \
	--account-name <STORAGE_ACCOUNT_NAME> \
	--account-key "$ACCOUNT_KEY" \
	--query "[].name" \
	--output table
```

6. If a required container is missing, create only the missing one(s):

```bash
az cosmosdb sql container create --account-name <COSMOS_ACCOUNT_NAME> --resource-group rg-arkansas-serve --database-name <COSMOS_DATABASE_NAME> --name <MISSING_CONTAINER_NAME> --partition-key-path "/id"

az storage container create --name uploads --account-name <STORAGE_ACCOUNT_NAME> --account-key "$ACCOUNT_KEY" --public-access off
```

7. Continue to section 5 (SWA GitHub linkage) and section 6 (app settings), using your existing resource names.

### 4.1 Create resource group

```bash
az group create \
  --name rg-arkansas-serve \
  --location eastus2
```

### 4.2 Create Cosmos DB (NoSQL)

```bash
az cosmosdb create \
  --name cosmos-arkserve-prod \
  --resource-group rg-arkansas-serve \
  --locations regionName=eastus2 failoverPriority=0 isZoneRedundant=False

az cosmosdb sql database create \
  --account-name cosmos-arkserve-prod \
  --resource-group rg-arkansas-serve \
  --name arkansas-serve-db
```

Create the containers your backend expects (partition key paths may be adjusted to match your data model):

```bash
az cosmosdb sql container create --account-name cosmos-arkserve-prod --resource-group rg-arkansas-serve --database-name arkansas-serve-db --name users --partition-key-path "/id"
az cosmosdb sql container create --account-name cosmos-arkserve-prod --resource-group rg-arkansas-serve --database-name arkansas-serve-db --name events --partition-key-path "/id"
az cosmosdb sql container create --account-name cosmos-arkserve-prod --resource-group rg-arkansas-serve --database-name arkansas-serve-db --name registrations --partition-key-path "/id"
az cosmosdb sql container create --account-name cosmos-arkserve-prod --resource-group rg-arkansas-serve --database-name arkansas-serve-db --name serviceLogs --partition-key-path "/id"
az cosmosdb sql container create --account-name cosmos-arkserve-prod --resource-group rg-arkansas-serve --database-name arkansas-serve-db --name pendingApprovals --partition-key-path "/id"
az cosmosdb sql container create --account-name cosmos-arkserve-prod --resource-group rg-arkansas-serve --database-name arkansas-serve-db --name tenants --partition-key-path "/id"
```

### 4.3 Create Blob Storage

```bash
az storage account create \
  --name starkserveprod \
  --resource-group rg-arkansas-serve \
  --location eastus2 \
  --sku Standard_LRS \
  --kind StorageV2
```

Create the private blob container used for event/service-log uploads:

```bash
ACCOUNT_KEY=$(az storage account keys list --resource-group rg-arkansas-serve --account-name starkserveprod --query "[0].value" -o tsv)

az storage container create \
  --name uploads \
  --account-name starkserveprod \
  --account-key "$ACCOUNT_KEY" \
  --public-access off
```

### 4.4 Create Application Insights (recommended)

```bash
az monitor app-insights component create \
  --app ai-arkansas-serve-prod \
  --location eastus2 \
  --resource-group rg-arkansas-serve \
  --application-type web
```

---

## 5. Create Static Web App and connect GitHub

You can create SWA in the Azure Portal or with CLI. Portal is easiest for first setup because it can create the GitHub Actions workflow automatically.

### 5.1 Portal path (recommended)

1. Azure Portal -> Create resource -> Static Web App.
2. Select:
	- Subscription and resource group: `rg-arkansas-serve`
	- Name: `swa-arkansas-serve-prod`
	- Plan: Standard
	- Region: same as above
3. Deployment source: GitHub.
4. Select repo: `HankHerzfeld/ArkansasServe`, branch `main`.
5. Build details:
	- App location: `frontend`
	- Api location: `backend/ArkansasServe.Functions`
	- Output location: empty string
6. Create resource.

Expected outcome:
- Azure creates a deployment token.
- Azure adds a GitHub Actions workflow in `.github/workflows/`.
- Pushes to `main` deploy frontend and API together.

### 5.2 If workflow is not auto-generated

1. Re-open SWA in portal and verify GitHub linkage.
2. Regenerate deployment token.
3. Add a workflow manually using the official SWA deploy action.

---

## 6. Configure backend app settings (required)

Values from local-only settings must be added to SWA Application Settings (they become environment variables for managed Functions).

Get connection strings:

```bash
COSMOS_CONN=$(az cosmosdb keys list --name cosmos-arkserve-prod --resource-group rg-arkansas-serve --type connection-strings --query "connectionStrings[0].connectionString" -o tsv)
STORAGE_CONN=$(az storage account show-connection-string --name starkserveprod --resource-group rg-arkansas-serve --query connectionString -o tsv)
```

Set SWA app settings:

```bash
az staticwebapp appsettings set \
  --name swa-arkansas-serve-prod \
  --resource-group rg-arkansas-serve \
  --setting-names \
	 "CosmosDb__ConnectionString=$COSMOS_CONN" \
	 "CosmosDb__DatabaseName=arkansas-serve-db" \
	 "BlobStorage__ConnectionString=$STORAGE_CONN" \
	 "Entra__TenantId=<YOUR_TENANT_ID>" \
	 "Entra__ClientId=<YOUR_CLIENT_ID>" \
	 "Entra__Audience=api://arkansas-serve"
```

Important:
- Do not commit secrets to git.
- `backend/ArkansasServe.Functions/local.settings.json` is local-only and not published.

---

## 7. Configure Entra External ID for production domain

In your Entra app registration:

1. Add redirect URI:
	- `https://arkansasserve.com/auth-callback.html`
2. If you use `www`, also add:
	- `https://www.arkansasserve.com/auth-callback.html`
3. Confirm scopes and audience used by frontend and backend match.

Update frontend auth config in `frontend/js/auth.js`:

1. Set `TENANT_ID` to production tenant.
2. Set `CLIENT_ID` to production app registration.
3. Set `REDIRECT_URI` to production callback URL you registered.

Commit and push changes after updating these values.

---

## 8. Configure custom domain: arkansasserve.com

In the SWA resource:

1. Open Custom domains.
2. Add `arkansasserve.com`.
3. Add `www.arkansasserve.com` (optional, recommended).
4. Add DNS records exactly as Azure requests.
5. Wait for validation and managed TLS certificate issuance.

DNS guidance:
- Apex (`arkansasserve.com`) usually needs ALIAS/ANAME (or provider-specific apex flattening) plus TXT validation.
- `www` usually uses CNAME plus TXT validation.

---

## 9. Validate deployment end-to-end

After the workflow completes:

1. Open `https://arkansasserve.com`.
2. Verify static pages load and navigation works.
3. Verify browser network calls to `/api/*` return success.
4. Test login flow:
	- `index.html` -> Entra login -> `auth-callback.html` -> authenticated page.
5. Verify role-based views and service-hours workflow:
	- Student
	- Organization staff
	- School admin
	- Platform admin
6. Upload flow smoke test:
	- Generate upload token
	- Upload file to Blob
	- Verify URL/storage access pattern works.

---

## 10. Hardening and follow-up tasks

1. Add infrastructure-as-code in a new `infra/` folder (Bicep preferred) so environment creation is repeatable.
2. Move secrets to Azure Key Vault and reference them from app settings.
3. Add branch protection and require successful SWA deployment checks before merge.
4. Add a staging SWA environment for pull requests.
5. Add monitoring alerts (failed requests, Function exceptions, latency).

---

## 11. Quick rollback strategy

1. If a bad release goes out, redeploy the previous known-good commit by re-running that workflow commit.
2. Keep a tagged release history in git for fast rollback targets.
3. If app settings changed incorrectly, restore last known-good values with `az staticwebapp appsettings set`.

---

## 12. One-time repo updates to do now

1. Add missing icon files referenced by `frontend/manifest.json`:
	- `frontend/icons/icon-192.png`
	- `frontend/icons/icon-512.png`
2. Ensure `.gitignore` excludes secrets and build artifacts.
3. Commit generated `.github/workflows/*` once SWA linkage creates it.

This completes the deployment path for both the public site and its supporting backend API.
