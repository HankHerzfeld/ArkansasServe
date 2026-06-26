# Arkansas Serve Changebase Compatibility and Interaction Report

Date: 2026-06-26

## Scope
This report covers current uncommitted changes across backend, frontend, and infrastructure, with focus on:
- Azure push compatibility
- Newly added features and refactors
- Function-to-function, function-to-model, and service-to-database interactions
- Cosmos DB and Blob Storage read/write flows

## Azure Push Compatibility Verdict
Overall status: Conditionally ready.

What is confirmed working:
- Backend build succeeds for ArkansasServe.Functions.
- Static Web Apps workflow is present in .github/workflows/azure-static-web-apps.yml.
- Infrastructure template exists and validates in infra/main.bicep.
- Managed API app settings are provisioned by Bicep (Cosmos, Blob, Entra, App Insights keys).
- Functions are split into scaffolded files and compile.

Required before production push:
1. Rotate any previously exposed Cosmos and Storage account keys in Azure.
2. Ensure GitHub secret AZURE_STATIC_WEB_APPS_API_TOKEN is set.
3. Provide required Bicep parameters for entraTenantId, entraClientId, entraAudience.
4. Resolve behavior-level risks listed in the Compatibility Risks section.

## Compatibility Risks and Gaps
Severity legend: High / Medium / Low

1. High: Duplicate side effects in service log review pipeline.
- Files involved:
  - backend/ArkansasServe.Functions/Functions/ServiceLogFunctions.cs
  - backend/ArkansasServe.Functions/Functions/ChangeFeedFunction.cs
- Both paths create notifications and clear pending approvals for approved/rejected logs.
- Result: possible duplicate notifications and repeated writes on the same state transition.
- Recommendation: choose one source of truth for finalization logic, or add idempotency checks (for example, notification uniqueness by relatedId + type).

2. High: Registration cancellation likely cannot find event registration reliably.
- File: backend/ArkansasServe.Functions/Functions/RegistrationFunctions.cs
- Endpoint expects eventId query parameter on DELETE /api/registrations/{id}, but frontend call omits it.
- File: frontend/js/api.js (Registrations.cancel)
- Result: registration lookup may fail with not found.
- Recommendation: pass eventId in frontend cancel call or refactor backend lookup by id-only pattern.

3. Medium: Slot decrement path likely uses wrong partition key source.
- File: backend/ArkansasServe.Functions/Functions/RegistrationFunctions.cs
- Cancellation branch fetches event with reg.SchoolId where organizationId is expected.
- Result: event slot decrement may fail silently or target wrong partition.

4. Medium: Blob public-read behavior conflicts with account setting.
- File: infra/main.bicep
- Storage account sets allowBlobPublicAccess false, while event-photos container sets publicAccess Blob.
- Result: public URL assumptions in EventFunctions.GetUploadToken and BlobService.GetPublicBlobUrl may not hold.

5. Low: Editor-only stale diagnostic.
- File shown in Problems: backend/ArkansasServe.Functions/Program.cs
- Build succeeds despite stale FunctionsApplication diagnostic; compiler output is clean.

## New Features and Breakout Summary

### Infrastructure and Deployment
1. Added infrastructure template.
- File: infra/main.bicep
- Adds:
  - Log Analytics workspace
  - Application Insights workspace-based component
  - Storage account and event-photos container
  - Cosmos DB account, SQL database, and containers
  - Static Web App resource
  - Static Web App app settings wiring for backend runtime config

2. Existing SWA workflow used for CI/CD.
- File: .github/workflows/azure-static-web-apps.yml
- Deploys frontend and managed Functions API on push to main.

### Backend Runtime and Security
1. Program startup hardening and strict required configuration.
- File: backend/ArkansasServe.Functions/Program.cs
- Singleton registrations for CosmosClient, BlobServiceClient, CosmosService, BlobService.
- AuthConfig requires Entra settings.

2. Auth middleware hardening.
- File: backend/ArkansasServe.Functions/Middleware/AuthMiddleware.cs
- Signing key prefetch and cache
- Issuer signing key validation
- Client app validation (azp/appid)
- Case-insensitive required role checks
- Standardized forbidden response for authorization failures

3. Blob SAS hardening.
- File: backend/ArkansasServe.Functions/Services/BlobService.cs
- Validates account credentials and inputs
- Bounded SAS expiries
- Start time skew tolerance

4. Cosmos service resiliency updates.
- File: backend/ArkansasServe.Functions/Services/CosmosService.cs
- Optional cancellation tokens across methods
- Targeted CosmosException logging on write paths
- Lower-case container name alignment with infrastructure

### Refactor: Monolith to Scaffolded Files
1. Functions split from one file into per-area files.
- Deleted:
  - backend/ArkansasServe.Functions/Functions/Functions.cs
- Active files:
  - backend/ArkansasServe.Functions/Functions/UserFunctions.cs
  - backend/ArkansasServe.Functions/Functions/EventFunctions.cs
  - backend/ArkansasServe.Functions/Functions/RegistrationFunctions.cs
  - backend/ArkansasServe.Functions/Functions/ServiceLogFunctions.cs
  - backend/ArkansasServe.Functions/Functions/ApprovalFunctions.cs
  - backend/ArkansasServe.Functions/Functions/AdminFunctions.cs
  - backend/ArkansasServe.Functions/Functions/ChangeFeedFunction.cs
  - backend/ArkansasServe.Functions/Functions/HttpHelper.cs

2. Models split from one file into per-model files.
- Deleted:
  - backend/ArkansasServe.Functions/Models/Models.cs
- Active files:
  - backend/ArkansasServe.Functions/Models/CosmosDocument.cs
  - backend/ArkansasServe.Functions/Models/User.cs
  - backend/ArkansasServe.Functions/Models/Tenant.cs
  - backend/ArkansasServe.Functions/Models/Event.cs
  - backend/ArkansasServe.Functions/Models/EventRegistration.cs
  - backend/ArkansasServe.Functions/Models/ServiceLog.cs
  - backend/ArkansasServe.Functions/Models/PendingApproval.cs
  - backend/ArkansasServe.Functions/Models/Notification.cs

### Frontend Runtime Updates
1. API transport hardening and endpoint shaping.
- File: frontend/js/api.js
- Better network and non-JSON error handling
- Encoded path params
- Some endpoint assumptions changed (see risks)

2. Auth flow portability and storage simplification.
- File: frontend/js/auth.js
- Redirect/logout callbacks bound to current origin
- Profile derived from access token payload
- Session storage only for active token state

3. SWA config hardening.
- File: frontend/staticwebapp.config.json
- Added referrer policy and permissions policy
- CSP tightened with object-src/base-uri/frame/form-action directives

## Interaction Map (Functions, Models, Services)

### Shared flow
1. HTTP trigger enters function class.
2. Function calls AuthMiddleware.ValidateRequest (except change feed).
3. Function uses HttpHelper for response and body handling.
4. Function uses CosmosService and/or BlobService.
5. CosmosService reads/writes concrete model documents to specific containers.

### Function file responsibilities and dependencies

1. UserFunctions.cs
- Reads/Writes model: User
- Cosmos operations:
  - GetUserByExternalIdAsync (users)
  - UpsertUserAsync (users)
- External storage: none

2. EventFunctions.cs
- Reads/Writes model: Event, EventRegistration (read only in roster endpoint)
- Cosmos operations:
  - GetUpcomingEventsAsync (events)
  - GetEventAsync / UpdateEventAsync / CreateEventAsync (events)
  - GetRegistrationsByEventAsync (registrations)
- Blob operations:
  - GenerateUploadSasToken (write SAS)
  - GetPublicBlobUrl
- Data path:
  - Upload token endpoint returns SAS URL and final blob URL for event media.

3. RegistrationFunctions.cs
- Reads/Writes model: EventRegistration, Event
- Cosmos operations:
  - CreateRegistrationAsync, UpdateRegistrationAsync, GetRegistrationAsync, IsAlreadyRegisteredAsync (registrations)
  - GetEventAsync, GetEventWithETagAsync, UpdateEventAsync (events)
- External storage: none
- Data path:
  - Registration create increments event slots with optimistic concurrency.
  - Cancellation decrements slots with optimistic concurrency.

4. ServiceLogFunctions.cs
- Reads/Writes model: ServiceLog, PendingApproval, Notification
- Cosmos operations:
  - CreateServiceLogAsync, UpdateServiceLogAsync, GetServiceLogAsync, GetServiceLogsByStudentAsync (serviceLogs)
  - CreatePendingApprovalAsync, DeletePendingApprovalByLogIdAsync (pendingApprovals)
  - CreateNotificationAsync (notifications)
- External storage:
  - Optional verificationDocUrl field references blob content if present.

5. ApprovalFunctions.cs
- Reads model: PendingApproval
- Cosmos operations:
  - GetPendingApprovalsBySchoolAsync
  - GetAllPendingApprovalsAsync

6. AdminFunctions.cs
- Reads/Writes model: Tenant
- Cosmos operations:
  - GetAllTenantsAsync
  - CreateTenantAsync

7. ChangeFeedFunction.cs
- Trigger source model: ServiceLog change feed (serviceLogs)
- Writes models: PendingApproval, Notification
- Cosmos operations:
  - DeletePendingApprovalByLogIdAsync
  - CreatePendingApprovalAsync
  - CreateNotificationAsync
- Note: overlaps with ServiceLogFunctions side effects.

### Model-to-container mapping
1. User -> users (partition key: tenantId)
2. Tenant -> tenants (partition key: id)
3. Event -> events (partition key: organizationId)
4. EventRegistration -> registrations (partition key: eventId)
5. ServiceLog -> serviceLogs (partition key: studentId)
6. PendingApproval -> pendingApprovals (partition key: schoolId)
7. Notification -> notifications (partition key: userId)

### Blob interaction mapping
1. EventFunctions.GetUploadToken:
- Push flow: client uploads directly using SAS URL (write/create permissions).
- Pull flow: client displays finalUrl (depends on container access policy).

2. BlobService utility methods:
- GenerateUploadSasToken: short-lived write URL
- GenerateReadSasToken: short-lived read URL for private blobs
- GetPublicBlobUrl: static blob URL (requires effective anonymous read policy)

## Frontend to Backend Route Coupling
1. frontend/js/api.js -> backend/Functions/* endpoints.
2. frontend/js/auth.js -> token acquisition and Authorization header usage by api.js.
3. frontend/staticwebapp.config.json routes /api/* to managed Functions and applies CSP/security headers.

Potential route coupling issue:
- Registrations.cancel currently omits eventId expected by backend cancellation logic.

## File-to-File Dependency View
1. Program.cs
- Composes DI graph for AuthConfig, CosmosService, BlobService.

2. Middleware/AuthMiddleware.cs
- Consumed by all HTTP function classes.
- Produces UserContext used for authorization decisions.

3. Functions/HttpHelper.cs
- Shared by all HTTP function classes for JSON I/O and error formatting.

4. Services/CosmosService.cs
- Consumed by all function classes except none for pure blob helper endpoints.
- Central place for Cosmos reads/writes.

5. Services/BlobService.cs
- Consumed by EventFunctions for upload token/public URL operations.

6. Models/*
- Shared contract types between functions and CosmosService.

7. infra/main.bicep
- Provisions containers/settings expected by Program.cs, CosmosService, and ChangeFeedFunction.

8. frontend/js/api.js and frontend/js/auth.js
- Drive endpoint invocation patterns and auth token transmission.

## Azure Push Readiness Checklist
1. Build backend: pass.
2. Bicep template: present and wired for app settings.
3. Workflow file: present.
4. Remaining pre-push actions:
- Set AZURE_STATIC_WEB_APPS_API_TOKEN in GitHub.
- Validate and fix registration cancellation eventId contract.
- Resolve duplicate review side effects between ServiceLogFunctions and ChangeFeedFunction.
- Confirm desired blob public/private behavior and adjust infra accordingly.
- Rotate Azure keys if not already rotated.

## Recommended Next Fixes Before Production
1. Make cancellation endpoint id-only or require and pass eventId consistently.
2. Consolidate service-log finalization side effects into one path (prefer change feed or synchronous path, not both).
3. Decide blob access model:
- Public media: allow anonymous read at account/container policy.
- Private media: keep public off and return read SAS URLs only.
4. Add smoke tests for:
- Registration create/cancel slot accounting
- Review workflow notification count
- Upload token and blob read behavior
