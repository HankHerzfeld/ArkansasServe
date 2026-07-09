# Arkansas Serve — Project State & Production Roadmap

> Concise reference: what was built, what needs to happen, and how the system works end-to-end.
> All data sensitive to minors and court-involved youth. No PII in URLs or logs.

---

## 1 · CHANGE LOG

### Commit 1 — "Prop push for 401" (`b586d23`)

Initial bulk commit. Created all project scaffolding from scratch.

#### Backend (`backend/ArkansasServe.Functions/`)

| File | What it does |
|---|---|
| `ArkansasServe.Functions.csproj` | .NET 8 isolated-worker Azure Functions project; dependencies: Cosmos SDK, Blob SDK, Identity.Web, JWT |
| `Program.cs` | DI bootstrap — registers `CosmosClient`, `CosmosService`, `BlobService`, `AuthConfig` as singletons |
| `host.json` | Functions host config — App Insights sampling, Cosmos change feed connection key |
| `Middleware/AuthMiddleware.cs` | Validates every ****** against Entra External ID OIDC keys; extracts `UserContext` (userId, tenantId, role); caches signing keys 1 h; returns 401/403 on failure |
| `Models/CosmosDocument.cs` | Base class with `id`, `_etag`, `createdAt`, `updatedAt` |
| `Models/User.cs` | User record; fields: tenantId, externalId, role, adminLevel, organizationId, groupIds, permissions, grade, schoolId, totalApprovedHours, isDemoUser |
| `Models/Tenant.cs` | School/org record; fields: type, name, ssoDomain, contactEmail, rbacEnabled, groups (nested), eventScopeRules |
| `Models/Event.cs` | Volunteer event; fields: organizationId, title, location, startDateTime, maxSlots, currentSlots, hoursValue, status, eligibleSchoolIds, photoUrl |
| `Models/ServiceLog.cs` | Hour-submission record; fields: studentId, schoolId, eventId, hoursLogged, status (Pending/Approved/Rejected), reviewedByUserId, reviewNote |
| `Models/EventRegistration.cs` | Student-to-event link; fields: eventId, userId, studentName, schoolId, status |
| `Models/PendingApproval.cs` | School admin queue item created when a service log is submitted |
| `Models/Notification.cs` | In-app notification record for HoursApproved / HoursRejected |
| `Services/CosmosService.cs` | All Cosmos DB reads and writes; 7 container references; point reads preferred; LINQ queries where needed |
| `Services/CosmosService.EventsCompatibility.cs` | Cross-partition event query helpers with backward-compat wrappers |
| `Services/CosmosService.UsersCompatibility.cs` | Cross-partition user lookup helpers; upsert-with-partition-fallback |
| `Services/BlobService.cs` | SAS-token generation (15 min expiry) for direct-to-blob uploads; public URL construction for event photos |
| `Functions/UserFunctions.cs` | `GET /api/users/me` (get-or-create profile); `PUT /api/users/me` (update display name, phone, grade) |
| `Functions/EventFunctions.cs` | `GET /api/events`; `GET /api/events/{id}`; `POST /api/events`; `PUT /api/events/{id}`; `GET /api/events/{id}/registrations`; `POST /api/events/upload-token`; `GET /api/org/events` |
| `Functions/RegistrationFunctions.cs` | `POST /api/registrations` (register with optimistic concurrency on slot count); `DELETE /api/registrations/{id}` (cancel, decrements slot count) |
| `Functions/ServiceLogFunctions.cs` | `POST /api/servicelogs` (creates log + pending approval); `PATCH /api/servicelogs/{id}` (approve/reject; clears approval, creates notification); `GET /api/students/me/servicelogs` |
| `Functions/ApprovalFunctions.cs` | `GET /api/approvals` (school-scoped queue for SchoolAdmin; all for PlatformAdmin) |
| `Functions/AdminFunctions.cs` | Tenant CRUD; admin backend context; user access management; tenant group management; demo user management (SuperAdmin only) |
| `Functions/HttpHelper.cs` | Typed helpers: `OkJson`, `CreatedJson`, `Error`; `ReadBody<T>` |

#### Frontend (`frontend/`)

| File | What it does |
|---|---|
| `index.html` | Public landing page with login CTA |
| `auth-callback.html` | OAuth PKCE callback handler; exchanges code for token then redirects |
| `dashboard.html` | Post-login home; shows role-appropriate links |
| `events.html` | Public event browser; students can register |
| `org-portal.html` | OrgStaff portal: create/manage events, log hours, view registrations |
| `admin-portal.html` | SchoolAdmin portal: pending approvals queue, approve/reject |
| `platform-admin.html` | PlatformAdmin portal: tenant list, cross-school oversight |
| `admin-backend.html` | SuperAdmin backend: user access management, demo users, group management |
| `js/auth.js` | PKCE login/logout/callback; token stored in `sessionStorage`; `requireAuth()` route guard; `getProfile()` decodes JWT |
| `js/api.js` | Typed API client; attaches ******; handles 401/network errors |
| `js/ui.js` | Empty stub (pending implementation) |
| `styles/main.css` | Global theme: CSS custom properties, dark mode, BEM |
| `staticwebapp.config.json` | SWA routing rules, CSP headers, security headers |
| `manifest.json` | PWA manifest |

#### Infrastructure / CI

| File | What it does |
|---|---|
| `infra/main.bicep` | Provisions: Log Analytics, App Insights, Blob Storage, Cosmos DB (7 containers, serverless), Static Web App; outputs connection strings |
| `.github/workflows/azure-static-web-apps.yml` | On push to `main`: deploys frontend to SWA. On PR: validates Functions build |
| `.github/workflows/main_func-arkansas-serve-arksrv.yml` | On push to `main`: builds and deploys Functions to `func-arkansas-serve-arksrv` (standalone, not SWA managed API) |

#### Docs

| File | Content |
|---|---|
| `docs/data-flow-plan.md` | Role permissions matrix; all data flows by feature |
| `docs/deployment-plan.md` | Step-by-step Azure deployment runbook |
| `docs/setup-guide.md` | Local dev setup |
| `docs/admin-backend-rbac-plan.md` | RBAC expansion design doc |
| `docs/changebase-azure-compatibility-report.md` | Azure gap analysis report |

---

### Commit 2 — "2 prop push" (`f14a104`)

Two targeted fixes to prevent silent 401 auth loops.

#### `frontend/js/auth.js`
- Added `lastLoginAt: 'as_last_login_at'` to the storage key map
- Exports two new functions: `clearSession()`, `getLastLoginAttemptAt()`
- **Why:** API layer needed to know when the last login attempt occurred to avoid redirect-looping on a bad token

#### `frontend/js/api.js`
- Token now checked **before** the request (not after); triggers `Auth.login()` immediately if null
- On a 401 response: checks if login was retried within the last 45 s; if yes, does NOT redirect (avoids infinite loop); if no, clears session and redirects to `/index.html?error=session_expired`
- **Why:** Prior code could loop: expired token → 401 → login redirect → token still bad → repeat

---

## 2 · AZURE ARCHITECTURE

### Production Components

```
┌──────────────────────────────────────────────────────────┐
│                    AZURE SUBSCRIPTION                    │
│                                                          │
│  ┌─────────────────────────────────────────────────┐    │
│  │         Azure Static Web Apps (SWA)             │    │
│  │                                                  │    │
│  │  ┌──────────────┐   /api/*   ┌───────────────┐  │    │
│  │  │   frontend/  │ ─────────► │ Azure Functions│  │    │
│  │  │  (HTML/CSS/  │            │  (isolated     │  │    │
│  │  │   JS, icons) │            │   worker .NET) │  │    │
│  │  └──────────────┘            └───────┬───────┘  │    │
│  └──────────────────────────────────────┼───────────┘    │
│                                         │                │
│        ┌────────────────────────────────┼──────────┐    │
│        │                                │          │    │
│        ▼                                ▼          │    │
│  ┌──────────────┐           ┌──────────────────┐   │    │
│  │  Cosmos DB   │           │  Blob Storage    │   │    │
│  │  (NoSQL,     │           │  (event-photos   │   │    │
│  │  serverless) │           │   container)     │   │    │
│  └──────────────┘           └──────────────────┘   │    │
│                                                     │    │
│  ┌──────────────────────────────────────────────┐   │    │
│  │  App Insights  ←──  Log Analytics workspace  │   │    │
│  └──────────────────────────────────────────────┘   │    │
└──────────────────────────────────────────────────────────┘

┌─────────────────────────────┐
│  Microsoft Entra External ID│  (external tenant, separate from Azure sub)
│  - App registration         │
│  - PKCE OAuth 2.0 flows     │
│  - JWT signing keys (OIDC)  │
└─────────────────────────────┘
```

### How Each Component Connects

| Component | Role | Connected to |
|---|---|---|
| **SWA** | Serves HTML/CSS/JS; proxies `/api/*` to Functions | Functions, Entra (CSP), Blob Storage (img src) |
| **Azure Functions** | All business logic and data access | Cosmos DB, Blob Storage, Entra (key fetch) |
| **Cosmos DB** | Persistent data store (7 containers) | Functions only — never accessed by frontend directly |
| **Blob Storage** | Event photo files | Frontend uploads directly via SAS; Functions generate SAS tokens |
| **Entra External ID** | Identity provider; issues JWTs | Browser (PKCE redirect); Functions (OIDC key validation) |
| **App Insights** | Telemetry, errors, traces | Functions (SDK); Log Analytics (log sink) |

### App Settings Flow (Secrets at Runtime)

```
infra/main.bicep
  └─► SWA App Settings (set via Bicep output / az CLI)
        ├── CosmosDb__ConnectionString
        ├── CosmosDb__DatabaseName
        ├── BlobStorage__ConnectionString
        ├── Entra__TenantId
        ├── Entra__ClientId
        ├── Entra__Audience
        └── APPLICATIONINSIGHTS_CONNECTION_STRING
              └─► Injected into Functions via IConfiguration
```

### Authentication Data Flow

```
1. Browser → Entra /authorize (PKCE code_challenge)
2. User signs in (SSO, school email, etc.)
3. Entra → browser with ?code=...
4. auth-callback.html → Entra /token (code + verifier)
5. Entra → access_token (JWT with role, tenantId claims)
6. Token stored in sessionStorage (NOT localStorage)
7. Every API call → Authorization: ******
8. Functions → fetch OIDC keys from Entra (cached 1 h)
9. Functions → validate JWT (issuer, audience, expiry, signature)
10. Functions → extract UserContext (userId, tenantId, role)
11. Functions → enforce role on each endpoint
```

### Cosmos DB Containers

| Container | Partition Key | What it holds |
|---|---|---|
| `tenants` | `/id` | School and org tenant records; RBAC groups |
| `users` | `/tenantId` | User profiles with role, adminLevel, permissions |
| `events` | `/organizationId` | Volunteer event listings |
| `registrations` | `/eventId` | Student event sign-ups |
| `serviceLogs` | `/studentId` | Hour submissions (Pending → Approved/Rejected) |
| `pendingApprovals` | `/schoolId` | Queue for school admin review |
| `notifications` | `/userId` | In-app messages (HoursApproved, HoursRejected) |

---

## 3 · STAKEHOLDERS

### Role Hierarchy

```
PlatformAdmin  (highest)
    │
SchoolAdmin
    │
OrgStaff
    │
Student        (default)
```

### Stakeholder Descriptions & Access

#### Student
- **Who:** Arkansas school student or juvenile detention resident
- **Account:** Entra External ID account tied to their school's tenant (`schoolId`)
- **Access:** `dashboard.html`, `events.html`, service log view
- **Can:**
  - Browse and register for volunteer events
  - View own service hour log and total approved hours
  - Receive approval/rejection notifications
- **Cannot:** Create events, log hours, approve anything

#### OrgStaff (Organization Staff)
- **Who:** Staff at a nonprofit, community org, or juvenile detention center
- **Account:** Entra account tied to their organization's tenant (`organizationId`)
- **Access:** `org-portal.html`
- **Can:**
  - Create and edit their organization's events
  - Upload event photos (direct to Blob via SAS token)
  - Log service hours on behalf of students who attended
  - View registrations for their events
- **Cannot:** Approve hours, view other orgs' events in admin view

#### SchoolAdmin
- **Who:** School counselor, JDC staff coordinator, department head
- **Account:** Entra account with `SchoolAdmin` role claim; scoped to their `schoolId`/tenant
- **Access:** `admin-portal.html`
- **Can:**
  - View pending approval queue (filtered to their school)
  - Approve or reject service log submissions with optional note
  - View student rosters and hour totals
- **Cannot:** Create events, manage other schools, access platform settings

#### PlatformAdmin (SuperAdmin)
- **Who:** Arkansas Serve platform operator
- **Account:** Entra account with `@arkansasserve.com` email domain — role is elevated server-side automatically
- **Access:** `platform-admin.html`, `admin-backend.html`
- **Can:**
  - Manage all tenants (create, update school/org records)
  - View and manage all users across all tenants
  - Assign user roles and admin levels
  - Manage tenant groups and event scope rules
  - Create/reset demo users (for onboarding / demos)
  - See all pending approvals across all schools
- **Cannot:** Nothing is blocked at system level

### Stakeholder × Azure Element Access Map

```
                    SWA   Functions   Cosmos   Blob   Entra
Student             R         R*        —       —      A
OrgStaff            R         R*        —       W†     A
SchoolAdmin         R         R*        —       —      A
PlatformAdmin       R         R*        —       W†     A

R  = reads HTML/JS assets
R* = calls /api/* endpoints (always mediated by Functions)
W† = direct upload via SAS token (Functions-issued, time-limited)
A  = authenticates via PKCE flow
—  = no direct access; Functions access on their behalf
```

### Account Options by Stakeholder

| Role | Sign-in Method | Account Created By |
|---|---|---|
| Student | School email / Entra B2C form | School admin or self-registration (TBD per school) |
| OrgStaff | Org email / Entra B2C form | PlatformAdmin creates tenant; user self-registers or is invited |
| SchoolAdmin | School email with elevated role | PlatformAdmin assigns role via admin backend |
| PlatformAdmin | `@arkansasserve.com` email | Hardcoded domain; server-side elevation on first login |

> **Role assignment mechanism:** Role is stored in the `users` Cosmos document (`adminLevel`, `role`). On each `/api/users/me` call, the server checks the email domain and updates role if needed. The JWT claim `extension_Role` is also read; the strongest of token claim vs. DB record wins.

---

## 4 · CORE DATA FLOWS (DIAGRAMS)

### Flow A — Student Registers for an Event

```
Student clicks "Sign Up"
  → api.js: POST /api/registrations { eventId, organizationId }
    → Functions: validate JWT (must be Student or PlatformAdmin)
    → Cosmos: read event (check status=Open, slots available)
    → Cosmos: check not already registered
    → Cosmos: create EventRegistration document
    → Cosmos: increment event.currentSlots (optimistic concurrency, up to 5 retries)
  → 201 Created → frontend shows confirmation
```

### Flow B — OrgStaff Logs Service Hours

```
OrgStaff fills hour form
  → [optional] POST /api/events/upload-token { fileName }
    → Functions: generate 15-min SAS token
    → Browser: PUT directly to Blob Storage (no Functions in path)
    → URL returned → attached to service log payload
  → POST /api/servicelogs { studentId, hoursLogged, eventId, ... }
    → Functions: validate JWT (must be OrgStaff or PlatformAdmin)
    → Cosmos: create ServiceLog (status=Pending)
    → Cosmos: create PendingApproval in school's partition
  → 201 Created
```

### Flow C — SchoolAdmin Reviews Hours

```
SchoolAdmin views queue
  → GET /api/approvals
    → Functions: validate JWT (must be SchoolAdmin or PlatformAdmin)
    → Cosmos: query pendingApprovals where schoolId = ctx.TenantId
  → Admin clicks Approve / Reject
  → PATCH /api/servicelogs/{id} { studentId, status, reviewNote }
    → Functions: validate JWT; confirm schoolId matches
    → Cosmos: update ServiceLog.status
    → Cosmos: delete PendingApproval document
    → Cosmos: create Notification for student
  → 200 OK
```

### Flow D — New Tenant / School Onboarding

```
PlatformAdmin
  → POST /api/admin/tenants { name, type, contactEmail, ... }
    → Functions: validate JWT (must be PlatformAdmin/SuperAdmin)
    → Cosmos: create Tenant document
  → PATCH /api/admin/backend/users/{id}/access { adminLevel, organizationId }
    → Functions: update User document with new role
  → School admin can now log in and see their school's queue
```

---

## 5 · PRODUCTION READINESS — STEPS TO TAKE

### 5a. Broad Steps (in order)

1. **Fix build targets** — align .NET version across all config files
2. **Provision Azure infrastructure** — run Bicep, capture outputs
3. **Configure Entra External ID** — app registration, scopes, custom claims
4. **Wire CI/CD** — confirm secrets in GitHub; validate end-to-end deploy
5. **Configure app settings** — paste Bicep outputs into SWA/Functions config
6. **Validate all role flows** — test each stakeholder persona manually
7. **Seed production data** — create first tenant(s), PlatformAdmin account
8. **Connect custom domain** — `arkansasserve.com` → SWA
9. **Enable monitoring** — confirm App Insights receives data
10. **Go live**

---

### 5b. Particular Steps (actionable, specific)

#### ① Fix Version Inconsistency

- `backend/ArkansasServe.Functions/ArkansasServe.Functions.csproj` targets `net8.0`
- `frontend/staticwebapp.config.json` sets `apiRuntime: dotnet-isolated:8.0`
- `.github/workflows/azure-static-web-apps.yml` installs `.NET 8.0.x`
- **Action:** Decide: stay on .NET 8 (supported LTS) or upgrade to .NET 10.
  - If .NET 10: update `csproj`, `staticwebapp.config.json`, and both workflow files.
  - Recommendation: stay on .NET 8 until SWA managed API officially supports .NET 10 runtime.

#### ② Resolve CI/CD Deployment Path Mismatch

- **Problem:** Two deployment workflows exist simultaneously:
  - `azure-static-web-apps.yml` — deploys frontend only; does NOT deploy Functions
  - `main_func-arkansas-serve-arksrv.yml` — deploys Functions as a **standalone** Function App (not SWA managed API); package path is repo root, not `backend/ArkansasServe.Functions`
- **Decision required:** Choose one of:
  - **Option A (SWA Managed API):** Delete standalone Functions workflow; add `api_location: backend/ArkansasServe.Functions` to `azure-static-web-apps.yml`. SWA handles Functions internally. Simpler, but Functions scale within SWA limits.
  - **Option B (Separate Function App):** Keep standalone workflow; fix `AZURE_FUNCTIONAPP_PACKAGE_PATH` to `backend/ArkansasServe.Functions`. Frontend calls the separate Function App via a proxy or CORS config. More control.
- **Current state:** Neither is fully correct. Neither will successfully deploy a working backend.

#### ③ Provision Azure Resources with Bicep

```bash
az login
az account set --subscription "<SUBSCRIPTION_ID>"
az group create --name rg-arkansas-serve-prod --location southcentralus
az deployment group create \
  --resource-group rg-arkansas-serve-prod \
  --template-file infra/main.bicep \
  --parameters \
    staticWebAppName=swa-arkansas-serve \
    cosmosAccountName=cosmos-arkansas-serve-prod \
    storageAccountName=starkserve \
    entraTenantId=<ENTRA_TENANT_ID> \
    entraClientId=<CLIENT_ID> \
    entraAudience="api://<CLIENT_ID>"
```

- Captures outputs: `staticWebAppDefaultHostname`, `cosmosEndpoint`, `appInsightsConnectionString`

#### ④ Configure Entra External ID

1. Create external tenant at `entra.microsoft.com` if not done
2. Register app:
   - Redirect URI: `https://<SWA_HOSTNAME>/auth-callback.html`
   - Platform: Single-page application (SPA / PKCE)
   - Scopes: expose `User_Impersonation` API scope
   - Client ID in `auth.js` must match registered app
3. Add custom attribute: `extension_Role` (string)
4. Configure user flow to include `extension_Role` in access token claims
5. Confirm issuer URL format: `https://<TENANT_ID>.ciamlogin.com/<TENANT_ID>/`
   - Already handled in `AuthMiddleware.cs` (3 issuer variants checked)

#### ⑤ Set GitHub Secrets

| Secret | Value |
|---|---|
| `AZURE_STATIC_WEB_APPS_API_TOKEN` | From Azure portal → SWA → Manage deployment token |
| `AZUREAPPSERVICE_CLIENTID_*` | Service principal client ID for Functions deploy |
| `AZUREAPPSERVICE_TENANTID_*` | Azure AD tenant ID (not Entra External ID tenant) |
| `AZUREAPPSERVICE_SUBSCRIPTIONID_*` | Azure subscription ID |

#### ⑥ Set Application Settings (SWA or Function App)

```
CosmosDb__ConnectionString      = <from Bicep output / portal>
CosmosDb__DatabaseName          = arkansas-serve-db
BlobStorage__ConnectionString   = <from Bicep output / portal>
Entra__TenantId                 = 2d72a425-cd59-4b55-a8d6-a67c1ed565c6
Entra__ClientId                 = 16150d6e-7d28-4c6b-91b3-4ec839fff75f
Entra__Audience                 = api://16150d6e-7d28-4c6b-91b3-4ec839fff75f
APPLICATIONINSIGHTS_CONNECTION_STRING = <from Bicep output>
```

> Note: `TENANT_ID` and `CLIENT_ID` are already hardcoded in `frontend/js/auth.js`. Keep them in sync.

#### ⑦ Verify Cosmos DB Containers

- Bicep creates all 7 containers automatically
- Confirm in Azure portal → Cosmos DB → Data Explorer:
  - `tenants`, `users`, `events`, `registrations`, `serviceLogs`, `pendingApprovals`, `notifications`

#### ⑧ Seed PlatformAdmin Account

1. Sign in to the app with an `@arkansasserve.com` email
2. This triggers `GET /api/users/me` → server auto-creates user with `adminLevel=SuperAdmin`
3. Confirm in Cosmos `users` container that the document has `role: PlatformAdmin`

#### ⑨ Seed First Tenant

1. Sign in as PlatformAdmin
2. Navigate to Platform Admin → Create Tenant
3. Create one school tenant and one org tenant for testing

#### ⑩ Custom Domain

```bash
az staticwebapp hostname set \
  --name swa-arkansas-serve \
  --resource-group rg-arkansas-serve-prod \
  --hostname arkansasserve.com
```

- Add CNAME/TXT records at DNS registrar as prompted
- SWA provides free managed TLS

#### ⑪ Validate Role Flows (before launch)

Test each flow with the correct user type:

- [ ] Student: log in → browse events → register → view my logs
- [ ] OrgStaff: log in → create event → upload photo → log hours for a student
- [ ] SchoolAdmin: log in → view pending approvals → approve one → reject one
- [ ] PlatformAdmin: log in → view all tenants → view admin backend → reset demo users

#### ⑫ Pending Implementation (not yet built)

- `frontend/js/ui.js` — empty stub; shared UI helpers not yet implemented
- `frontend/components/event-card.html` — empty stub
- `frontend/components/navbar.html` — empty stub
- Student self-registration flow (account creation in Entra for new students)
- Report exports (hours by student, school, org)
- Email notifications (currently only in-app)
- ~~Cosmos DB Change Feed triggers~~ — **not used.** Service-log side effects run inline in
  `ServiceLogFunctions` (retry + reconciliation on read), not via a change-feed processor.
  Dropped to avoid an always-on background worker on the Consumption plan — *not* an SWA-tier
  limitation (the SWA is Standard). The `leases` container is provisioned but unused.

---

## 6 · HOW AZURE FACILITATES EXECUTION AT PRODUCTION

```
User's browser
  │
  ├─ HTTPS request to arkansasserve.com
  │     │
  │     └─► Azure Static Web Apps (SWA)
  │           ├─ Serves static HTML/CSS/JS (CDN-backed, global edge)
  │           ├─ Routes /api/* to managed Azure Functions runtime
  │           └─ Enforces security headers (CSP, X-Frame-Options, etc.)
  │
  ├─ Login flow → redirects to Entra External ID
  │     └─► ciamlogin.com issues JWT access token → back to browser
  │
  ├─ Authenticated API call → /api/*
  │     └─► Azure Functions (isolated worker, .NET 8)
  │           ├─ AuthMiddleware validates JWT (fetches OIDC keys from Entra, cached)
  │           ├─ Extracts role, tenantId, userId from token
  │           ├─ Executes business logic
  │           ├─ Reads/writes Cosmos DB (serverless, pay-per-request)
  │           └─ Logs to Application Insights
  │
  └─ File upload (event photos)
        ├─ Frontend requests SAS token → GET /api/events/upload-token
        ├─ Functions generate time-limited SAS URI
        └─ Browser uploads directly to Azure Blob Storage (bypasses Functions)
              └─ Public blob URL stored in Cosmos event document
```

### Azure Service Responsibilities at Production

| Service | Responsibility at Runtime |
|---|---|
| **SWA** | Global CDN for static assets; TLS termination; /api/* proxy; security headers |
| **Azure Functions** | All API logic; auth enforcement; Cosmos reads/writes; SAS generation |
| **Cosmos DB** | Persistent data; serverless (no idle cost); 7 containers; session consistency |
| **Blob Storage** | Event photos; Standard LRS; public blob read for photos, private for everything else |
| **Entra External ID** | Login UI; token issuance; OIDC discovery endpoint |
| **Application Insights** | Real-time telemetry; exceptions; dependency tracking; alerts |
| **Log Analytics** | 30-day log retention; query workspace for App Insights |

---

*Last updated: 2026-07-01*
