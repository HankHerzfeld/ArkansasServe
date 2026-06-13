# Arkansas Serve — Azure Infrastructure

Bicep + CLI deployment for the Arkansas Serve platform.
Target cost: **under $50/month** on Azure Pay-As-You-Go.

---

## What gets deployed

| Resource | Tier | Est. cost |
|---|---|---|
| Resource group | — | $0 |
| Azure Static Web Apps | Free | $0 |
| Azure Functions | Consumption (serverless) | $0–3/mo |
| Cosmos DB for NoSQL | **Free tier** (1,000 RU/s + 25 GB, permanent) | $0 |
| Blob Storage | Standard LRS | $1–4/mo |
| Application Insights + Log Analytics | Free under 5 GB/mo | $0 |
| Microsoft Entra External ID | Free under 50,000 MAU | $0 |
| **Total** | | **~$5–10/mo** |

---

## Prerequisites

- [Azure CLI](https://learn.microsoft.com/en-us/cli/azure/install-azure-cli) installed and logged in
- Bicep CLI: `az bicep install`
- A GitHub repo for your code (needed for Static Web Apps CI/CD)
- An Azure Pay-As-You-Go subscription

---

## Deployment steps

### 1. Edit params.json

Open `params.json` and update:

```json
"githubRepoUrl": { "value": "https://github.com/YOUR_USERNAME/YOUR_REPO" }
```

The `uniqueSuffix` (`arksrv`) is appended to resource names to ensure global
uniqueness. Change it only if you get naming conflicts on deploy.

### 2. Run the deploy script

```bash
chmod +x deploy.sh
./deploy.sh
```

The script will:
- Log you in to Azure
- Create the resource group `rg-arkansas-serve` in Central US
- Validate the Bicep template (dry run)
- Show a what-if preview of every resource that will be created
- Deploy on your confirmation
- Save outputs (URLs, connection strings, deploy token) to `deployment-outputs.json`

> **Keep `deployment-outputs.json` out of git.** It contains secrets.
> It is listed in `.gitignore` automatically if you use the project template.

### 3. Add the GitHub Actions secret

After deploy, copy `staticWebAppDeployToken` from `deployment-outputs.json` and add it to your GitHub repo:

`Settings → Secrets and variables → Actions → New repository secret`

| Name | Value |
|---|---|
| `AZURE_STATIC_WEB_APPS_API_TOKEN` | (paste the token) |

Azure will have already added a workflow file (`.github/workflows/azure-static-web-apps-*.yml`) to your repo. Every push to `main` will now build and deploy automatically.

### 4. Set up Entra External ID (portal only)

This step cannot be scripted — do it once in the Azure portal:

1. `portal.azure.com` → **Microsoft Entra ID** → **Manage tenants** → **Create**
2. Choose **External** tenant type
3. Name: `Arkansas Serve External` | Region: **United States**
4. Once created, navigate into the new tenant
5. **External Identities** → **All identity providers**
   - Add **Microsoft** (covers school Microsoft 365 / Entra accounts)
   - Add **Google** (covers Google Workspace for Education)
6. **App registrations** → **New registration** — register your web app
7. Copy the **Tenant ID**, **Client ID**, and create a **Client Secret**
8. Add them to your Function App config:

```bash
az functionapp config appsettings set \
  --resource-group rg-arkansas-serve \
  --name func-arkansas-serve-arksrv \
  --settings \
    "EntraExternalId__TenantId=YOUR_TENANT_ID" \
    "EntraExternalId__ClientId=YOUR_CLIENT_ID" \
    "EntraExternalId__ClientSecret=YOUR_CLIENT_SECRET"
```

---

## Re-deploying after changes

Bicep is idempotent — you can safely re-run the deploy command at any time.
Resources that haven't changed are left untouched.

```bash
az deployment group create \
  --resource-group rg-arkansas-serve \
  --template-file main.bicep \
  --parameters @params.json \
  --name "arkansas-serve-update-$(date +%Y%m%d)" \
  --output table
```

---

## Upgrading for production (when you secure a contract)

| Upgrade | When | Cost added |
|---|---|---|
| Static Web Apps → Standard | Need SLA or staging slots | +$9/mo |
| Storage → GRS redundancy | Need geo-redundant backups | +~$2/mo |
| Cosmos DB beyond free tier | Exceeding 1,000 RU/s or 25 GB | Pay-per-use |
| Entra External ID beyond 50k MAU | Very unlikely early on | $0.03/MAU |

---

## Useful CLI commands

```bash
# Check current resource group costs (last 30 days)
az consumption usage list \
  --scope /subscriptions/$(az account show --query id -o tsv) \
  --query "[?contains(instanceId, 'rg-arkansas-serve')]" \
  --output table

# List all resources in the group
az resource list --resource-group rg-arkansas-serve --output table

# Tail Function App logs live
az webapp log tail --resource-group rg-arkansas-serve --name func-arkansas-serve-arksrv

# Force a re-deploy without changing Bicep
az deployment group create \
  --resource-group rg-arkansas-serve \
  --template-file main.bicep \
  --parameters @params.json \
  --mode Incremental
```
