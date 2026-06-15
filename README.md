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

```
# Arkansas Serve — Resource Group Guide & Custom Domain Setup

## What each resource does, in plain English

---

### 1. Static Web App
**What it is:** The front door of your platform. It hosts your Blazor PWA (the website
and installable app) and serves it to anyone who visits your URL.

**What it does:**
- Delivers every page, screen, and UI component to the user's browser
- Handles your custom domain (e.g. arkansasserve.org) with free automatic SSL/HTTPS
- Automatically rebuilds and redeploys your site every time you push code to GitHub
- Routes any request to /api/* over to your Azure Functions (so the frontend and
  backend share the same domain — no CORS issues)

**Think of it as:** Your web host, build pipeline, and CDN all in one.

---

### 2. Azure Functions
**What it is:** Your C# backend API. It runs only when called (serverless) —
you are not paying for a server to sit idle.

**What it does:**
- Receives API calls from your Blazor frontend (e.g. "get this student's service hours")
- Reads from and writes to Cosmos DB (user data, events, logs)
- Generates short-lived SAS tokens so the frontend can securely upload/download
  files from Blob Storage
- Listens to the Cosmos DB change feed to maintain the PendingApprovals index
- Will enforce Entra authentication tokens — if a request doesn't carry a valid
  login token, the Function rejects it

**Think of it as:** The brains. Every action that touches data goes through here.

---

### 3. Cosmos DB (NoSQL, free tier)
**What it is:** Your main database. Stores structured records as JSON documents
rather than SQL rows and tables.

**What it holds:**
- Tenants — your schools, JDCs, and community organizations
- Users — students, org staff, school admins, platform admins
- Events — volunteer opportunities posted by organizations
- EventRegistrations — which students signed up for which events
- ServiceLogs — the actual hours records (the core of the platform)
- PendingApprovals — a fast index for school admins awaiting verification
- Notifications — alerts for users (auto-deleted after 30 days)

**Think of it as:** Your filing cabinet. Everything important lives here permanently.

---

### 4. Blob Storage
**What it is:** File storage for anything that isn't structured data.

**What it holds:**
- event-photos/ — photos uploaded by organizations for their event listings
- verification-docs/ — signed forms, court documents, verification paperwork
- org-logos/ — profile images for community organizations

All files are private. Students and admins never access Blob Storage directly —
your Azure Functions generate a short-lived secure link (SAS token) for each file
on demand.

**Think of it as:** Your secure filing cabinet for documents and photos.

---

### 5. Microsoft Entra External ID
**What it is:** The login system. Handles all authentication so you don't have to
build it yourself.

**What it does:**
- Lets students and staff log in with their existing school Microsoft 365 account
  or Google Workspace for Education account — no new password to remember
- Issues a secure token after login that your Azure Functions verify on every API call
- Keeps you out of the business of storing passwords (a huge liability reduction
  when working with juveniles)
- Free for up to 50,000 monthly active users

**Think of it as:** The secure front gate. Nobody gets inside without going through here.

---

### 6. Application Insights + Log Analytics
**What it is:** Your monitoring dashboard. Watches everything that happens and
tells you when something goes wrong.

**What it does:**
- Records every API call, error, and exception from your Azure Functions
- Tracks page load times and PWA performance
- Sends alerts if your error rate spikes or a function starts failing
- Gives you a cost-tracking view so you can see when usage is growing

**Think of it as:** The security camera and smoke detector for your platform.

---

### How they work together (the full request flow)

A student visits arkansasserve.org on their phone:

1. Their browser hits your **Static Web App** → the Blazor PWA loads
2. They click "Log In" → **Entra External ID** handles the school SSO flow
3. After login, the PWA sends API calls to /api/* → **Azure Functions** receives them
4. Functions verify the login token (from Entra), then query **Cosmos DB** for the
   student's data and service log history
5. If the student uploads a verification document, Functions write it to **Blob Storage**
   and save a reference URL in Cosmos DB
6. **App Insights** silently records every step for monitoring

---

---
