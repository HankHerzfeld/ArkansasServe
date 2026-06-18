# Arkansas Serve — Codebase Setup Guide

## Folder structure on your machine

Save everything to:
  C:\Users\[USERNAME]\ArkansasServe\

The layout inside that folder:

```
ArkansasServe/
├── docs/
│   └── data-flow-plan.md
├── backend/
│   └── ArkansasServe.Functions/
│       ├── ArkansasServe.Functions.csproj
│       ├── Program.cs
│       ├── host.json
│       ├── local.settings.json        ← fill in your secrets here
│       ├── Models/Models.cs
│       ├── Middleware/AuthMiddleware.cs
│       ├── Services/CosmosService.cs
│       ├── Services/BlobService.cs
│       └── Functions/Functions.cs
└── frontend/
    ├── index.html
    ├── auth-callback.html
    ├── dashboard.html
    ├── events.html
    ├── org-portal.html
    ├── admin-portal.html
    ├── platform-admin.html
    ├── manifest.json
    ├── staticwebapp.config.json
    ├── styles/main.css
    └── js/
        ├── auth.js
        └── api.js
```

---

## Step 1 — Open the project in VS Code

1. Open VS Code
2. File → Open Folder → navigate to C:\Users\[USERNAME]\ArkansasServe
3. VS Code will show the full folder tree in the left sidebar

Install these VS Code extensions if you don't have them:
- C# Dev Kit (Microsoft)
- Azure Functions (Microsoft)
- Azure Static Web Apps (Microsoft)
- Azure Tools (Microsoft)

---

## Step 2 — Install prerequisites

Open a terminal in VS Code (Terminal → New Terminal) and check each one:

```bash
# .NET 8 SDK
dotnet --version          # should show 8.x.x
# Download from: https://dotnet.microsoft.com/download/dotnet/8.0

# Azure Functions Core Tools
func --version            # should show 4.x.x
# Install: npm install -g azure-functions-core-tools@4 --unsafe-perm true
# (requires Node.js — download from https://nodejs.org if needed)

# Azure CLI
az --version              # should show 2.x
# Download from: https://docs.microsoft.com/cli/azure/install-azure-cli
```

---

## Step 3 — Fill in your secrets in local.settings.json

Open backend/ArkansasServe.Functions/local.settings.json and replace
the placeholder values with your real Azure resource values.

Get your Cosmos DB connection string:
  portal.azure.com → your Cosmos DB account → Keys → PRIMARY CONNECTION STRING

Get your Blob Storage connection string:
  portal.azure.com → your storage account → Access keys → Connection string

Get your Entra values (after completing Entra External ID setup):
  portal.azure.com → Microsoft Entra External ID → App registrations
  → your app → Overview → copy Application (client) ID and Directory (tenant) ID

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "CosmosDb__ConnectionString": "AccountEndpoint=https://cosmos-arkansas-serve-arksrv...",
    "CosmosDb__DatabaseName": "arkansas-serve-db",
    "BlobStorage__ConnectionString": "DefaultEndpointsProtocol=https;AccountName=...",
    "Entra__TenantId": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
    "Entra__ClientId": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
    "Entra__Audience": "api://arkansas-serve"
  }
}
```

IMPORTANT: local.settings.json is already set to never be published to Azure
(CopyToPublishDirectory is Never in the .csproj). Never commit it to GitHub.
Add this line to your .gitignore:

```
local.settings.json
deployment-outputs.json
```

---

## Step 4 — Fill in your Entra values in auth.js

Open frontend/js/auth.js and update the two constants at the top:

```javascript
const TENANT_ID = 'xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx';  // your Entra External tenant ID
const CLIENT_ID = 'xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx';  // your app registration client ID
```

---

## Step 5 — Restore and build the backend

In the VS Code terminal:

```bash
cd backend/ArkansasServe.Functions
dotnet restore
dotnet build
```

You should see "Build succeeded" with 0 errors. If you see package errors,
make sure you have .NET 8 SDK installed (Step 2).

---

## Step 6 — Run the backend locally

```bash
cd backend/ArkansasServe.Functions
func start
```

You should see output like:
  Functions:
    GetOrCreateCurrentUser: [GET] http://localhost:7071/api/users/me
    GetEvents: [GET] http://localhost:7071/api/events
    CreateEvent: [POST] http://localhost:7071/api/events
    ...

Leave this terminal running. The backend is now live at http://localhost:7071

---

## Step 7 — Run the frontend locally

Open a SECOND terminal in VS Code (click the + button in the terminal panel):

```bash
cd frontend
# Install a simple local server if you don't have one:
npm install -g serve

# Start it:
serve . -p 3000
```

Open your browser to http://localhost:3000
You should see the Arkansas Serve landing page.

Note: Login will redirect to Entra, which will redirect back to
https://www.arkansasserve.com/auth-callback.html — not localhost.
For local login testing, temporarily add http://localhost:3000/auth-callback.html
as an allowed redirect URI in your Entra app registration.

---

## Step 8 — Add PWA icons

The manifest.json references two icon files that you need to create:
  frontend/icons/icon-192.png   (192×192 px)
  frontend/icons/icon-512.png   (512×512 px)

Create these using any image editor (even Paint) with the Arkansas Serve
logo or a simple placeholder. Without them, the PWA install prompt won't
appear, but the rest of the site still works fine.

---

## Step 9 — Push to GitHub and deploy

### Create your GitHub repo

1. Go to github.com → New repository
2. Name it: arkansas-serve
3. Keep it Private
4. Do NOT initialize with README (you already have files)

### Push your code

In the VS Code terminal (from the ArkansasServe root folder):

```bash
git init
git add .
git commit -m "Initial Arkansas Serve codebase"
git branch -M main
git remote add origin https://github.com/YOUR_USERNAME/arkansas-serve.git
git push -u origin main
```

### Link to Azure Static Web Apps

After pushing, run the Bicep deploy (from your infra folder) to create
the Static Web App linked to this GitHub repo. Azure will automatically:
1. Add a GitHub Actions workflow file to your repo
2. Build and deploy the frontend on every push to main
3. Deploy the backend Functions as the managed API

---

## Step 10 — Configure the backend Functions in Azure

Your local.settings.json values need to be added as Azure app settings
so the deployed Functions can use them. Run these CLI commands:

```bash
az functionapp config appsettings set \
  --resource-group rg-arkansas-serve \
  --name func-arkansas-serve-arksrv \
  --settings \
    "CosmosDb__ConnectionString=YOUR_VALUE" \
    "CosmosDb__DatabaseName=arkansas-serve-db" \
    "BlobStorage__ConnectionString=YOUR_VALUE" \
    "Entra__TenantId=YOUR_VALUE" \
    "Entra__ClientId=YOUR_VALUE" \
    "Entra__Audience=api://arkansas-serve"
```

Replace each YOUR_VALUE with the real values from local.settings.json.

---

## What to do next (in order)

1. Complete the Entra External ID setup (see resource-guide-and-domain-setup.md)
2. Add your Entra values to auth.js and local.settings.json
3. Run locally and test the login flow
4. Create your first Tenant (school) in the platform admin panel
5. Create a test student user and verify they can browse events
6. Create a test event as org staff and verify it appears in the event browser
7. Test the full hours flow: sign up → log hours → approve
8. Push to GitHub and verify the deployed site works end-to-end

---

## Files NOT to commit to GitHub

Add these to your .gitignore:

```
# Secrets
local.settings.json
deployment-outputs.json

# Build output
bin/
obj/

# VS Code (optional — keep if you want shared settings)
.vscode/

# Node
node_modules/
```
