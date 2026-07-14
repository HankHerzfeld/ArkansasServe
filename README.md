# Arkansas Serve

Arkansas Serve is a volunteer service-hour tracking platform for Arkansas schools and
juvenile detention/court departments. It lets organizations run service events, lets
students log volunteer hours, and lets school administrators review and approve those
hours — all while keeping sensitive records for minors and court-involved youth private
and secure.

## Features

- **Event management** — organization staff create, edit, and manage service events.
- **Hour submission & check-in** — staff check students in and submit service hours.
- **Approval workflow** — school admins review, approve, or reject submitted hours.
- **Role-based access** — Student, Org Staff, School Admin, and Platform Admin roles,
  each with distinct, server-enforced permissions.
- **Reporting** — school admins view rosters and service-hour reports.
- **Privacy by design** — strict CSP, no tracking/analytics, and no PII in URLs or
  client-side storage.

## Tech Stack

| Layer | Technology |
|---|---|
| Backend | C# / .NET 8 (LTS), Azure Functions v4 (isolated worker model) |
| Database | Azure Cosmos DB for NoSQL |
| File storage | Azure Blob Storage |
| Auth | Microsoft Entra External ID (PKCE flow) |
| Frontend | Vanilla JS (ES2020+), modern CSS, HTML5 — no frameworks |
| Hosting | Azure Static Web Apps |
| Monitoring | Azure Application Insights + Log Analytics |
| IaC | Bicep + Azure CLI |

## Project Structure

```
ArkansasServe/
├── docs/                       Setup guide and data-flow documentation
├── backend/
│   └── ArkansasServe.Functions/   Azure Functions app (.NET 8, isolated worker)
│       ├── Models/                Cosmos DB document models
│       ├── Middleware/            Auth middleware (token + role validation)
│       ├── Services/              CosmosService, BlobService
│       └── Functions/             HTTP-triggered API endpoints
└── frontend/                   Static Web App (vanilla JS, CSS, HTML)
    ├── styles/                    Stylesheets
    └── js/                        auth.js, api.js, and feature modules
```

## Roles & Permissions

| Action | Student | Org Staff | School Admin | Platform Admin |
|---|---|---|---|---|
| Browse events | ✅ | ✅ | ✅ | ✅ |
| Sign up for event | ✅ | ❌ | ❌ | ✅ |
| Create / edit event | ❌ | ✅ | ❌ | ✅ |
| Check in students | ❌ | ✅ | ❌ | ✅ |
| Submit service hours | ❌ | ✅ | ❌ | ✅ |
| View own service log | ✅ | ❌ | ❌ | ✅ |
| Approve / reject hours | ❌ | ❌ | ✅ | ✅ |
| View school roster & reports | ❌ | ❌ | ✅ | ✅ |
| Manage all tenants/users | ❌ | ❌ | ❌ | ✅ |

## Getting Started

Prerequisites:

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Azure Functions Core Tools v4](https://learn.microsoft.com/azure/azure-functions/functions-run-local)
- [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli)
- An Azure subscription with Cosmos DB, Blob Storage, and Entra External ID configured

### Run the backend locally

```bash
cd backend/ArkansasServe.Functions
func start
```

Configure your secrets (Cosmos DB and Blob Storage connection strings, Entra settings)
in `backend/ArkansasServe.Functions/local.settings.json`. This file is never committed.

### Run the frontend locally

The frontend is a set of static files. Serve the `frontend/` directory with any static
file server (for example the Azure Static Web Apps CLI) and point it at the local
Functions endpoint.

For full, step-by-step setup instructions, see [`docs/setup-guide.md`](docs/setup-guide.md).

## Documentation

- [`docs/setup-guide.md`](docs/setup-guide.md) — local environment and project setup.
- [`docs/data-flow-plan.md`](docs/data-flow-plan.md) — data collection and distribution flows.

## Security & Privacy

This platform handles records for minors and court-involved youth. Contributors must:

- Validate and sanitize all user input server-side.
- Enforce roles and tenant scoping (`organizationId`, `schoolId`) against token claims —
  never trust the client.
- Keep PII out of URLs, query strings, client-side storage, and telemetry.
- Use short-lived SAS tokens for blob access; never expose private blobs publicly.

## Contributing

Commit messages follow the `type(scope): short description` convention
(e.g. `feat(events): add category filter`). See
[`.github/copilot-instructions.md`](.github/copilot-instructions.md) for the full coding
standards covering C#, JavaScript, CSS, HTML, accessibility, and security.
