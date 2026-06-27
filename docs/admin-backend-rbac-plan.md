# Admin Backend + RBAC Expansion Plan

Date: 2026-06-27

## Goal
Add a new admin backend page reachable from the dashboard by an admin-only button, with strict access segregation across these levels:
1. SuperAdmin (site-level)
2. OrganizationAdmin
3. GroupAdmin
4. EventAdmin
5. Student (no admin page button/access)

Also add super-admin-only demo user management with two demo users per role, including reset-at-will.

## High-Level Design

### 1) Data Schema Extensions (no new Cosmos containers)
Use existing `users` and `tenants` containers by extending existing document models.

#### User document additions
- `adminLevel`: one of `SuperAdmin`, `OrganizationAdmin`, `GroupAdmin`, `EventAdmin`, `Student`
- `organizationId`: explicit org assignment for admin scoping
- `groupIds`: groups this user can administer/organize within
- `eventAdminEventIds`: event IDs this user can administer
- `isDemoUser`: bool
- `demoUserType`: same enum values as adminLevel
- `permissions`: optional fine-grained capability flags

#### Tenant document additions
- `groups`: nested group definitions
  - `id`, `name`, `status`, `organizationId`
- `eventScopeRules`: optional event-level access rules by group/org
- `rbacEnabled`: bool

### 2) Backend API
Add admin-control endpoints in `AdminFunctions` to:
- get current admin access profile (effective scope)
- manage organization/group structure
- assign users to organization/group/admin level
- super-admin-only demo user operations:
  - list demo users grouped by role
  - reset demo users (recreate two users per level)

All endpoints enforce authorization using token + DB-backed scope checks.

### 3) Backend Access Rules
Implement hierarchical checks:
- SuperAdmin: all organizations, groups, events, demo management
- OrganizationAdmin: own organization; can manage groups/users/events within org
- GroupAdmin: only assigned groups
- EventAdmin: only assigned events (and org/group constraints)
- Student: denied admin endpoints

### 4) New Admin Backend Page
Add `frontend/admin-backend.html` with:
- role-aware sections
- organization/group/event management views
- super-admin-only nested demo-user panel
- assignment actions for user level + scope

### 5) Dashboard Button Behavior
Update `frontend/dashboard.html`:
- show "Admin Backend" button only when user effective level is not Student
- hide button for Student
- button routes to `/admin-backend.html`

### 6) Frontend API Client
Extend `frontend/js/api.js` with `AdminBackend` methods for new endpoints.

### 7) Validation and Deployment Readiness
- build backend (`dotnet build`)
- check file diagnostics for changed files
- ensure pushed changes trigger SWA workflow

## File Change Plan
1. `backend/ArkansasServe.Functions/Models/User.cs`
- add RBAC and demo-user fields

2. `backend/ArkansasServe.Functions/Models/Tenant.cs`
- add nested group + RBAC fields

3. `backend/ArkansasServe.Functions/Services/CosmosService.cs`
- add user lookup/update/list helpers for RBAC and demo users
- add tenant update methods for group management

4. `backend/ArkansasServe.Functions/Functions/AdminFunctions.cs`
- add RBAC profile, assignment, group management, demo user endpoints

5. `frontend/js/api.js`
- add `AdminBackend` API methods

6. `frontend/dashboard.html`
- show/hide admin backend button based on effective admin level

7. `frontend/admin-backend.html` (new)
- role-restricted admin backend UI + demo user nested panel

## Security Notes
- Server-side authorization is authoritative; frontend checks are only for UX.
- Students never receive admin data.
- Demo user management is super-admin only.
- Use generic API error messages for unauthorized actions.

## Demo User Reset Behavior
For each level (`SuperAdmin`, `OrganizationAdmin`, `GroupAdmin`, `EventAdmin`, `Student`), create two deterministic demo accounts tied to a demo organization namespace, and mark:
- `isDemoUser = true`
- `demoUserType = level`

A reset operation overwrites these demo accounts to a known state.

## Output
After implementation, provide:
- summary of all changed files
- setup steps for claims + organization bootstrap
- push-ready/deployment-ready status

## Implemented Change Log

### 1) Backend model/schema updates
1. `backend/ArkansasServe.Functions/Models/User.cs`
- Added `adminLevel`, `organizationId`, `groupIds`, `eventAdminEventIds`.
- Added `permissions` map for fine-grained controls.
- Added demo-user metadata fields: `isDemoUser`, `demoUserType`.

2. `backend/ArkansasServe.Functions/Models/Tenant.cs`
- Added tenant-level RBAC toggle: `rbacEnabled`.
- Added nested `groups` collection (`TenantGroup`).
- Added optional `eventScopeRules` (`EventScopeRule`).

### 2) Cosmos service/data layer updates
1. `backend/ArkansasServe.Functions/Services/CosmosService.cs`
- Converted service class to `partial` for extension-safe growth.
- Added admin-scope user retrieval helpers.
- Added demo-user list/delete/upsert helpers.
- Added tenant update helper (`UpdateTenantAsync`).

### 3) Admin backend API surface
1. `backend/ArkansasServe.Functions/Functions/AdminFunctions.cs`
- Kept existing tenant endpoints and added scoped checks.
- Added new admin backend endpoints:
  - `GET /api/admin/backend/context`
  - `GET /api/admin/backend/users`
  - `PATCH /api/admin/backend/users/{id}/access`
  - `GET /api/admin/backend/tenants/{tenantId}/groups`
  - `POST /api/admin/backend/tenants/{tenantId}/groups`
  - `PATCH /api/admin/backend/tenants/{tenantId}`
  - `GET /api/admin/backend/demo-users`
  - `POST /api/admin/backend/demo-users/reset`
- Added hierarchical authorization logic for:
  - `SuperAdmin`
  - `OrganizationAdmin`
  - `GroupAdmin`
  - `EventAdmin`
  - `Student`
- Added deterministic demo-user generation (2 users per level).
- Preserved legacy role compatibility while using new `adminLevel` for backend scope logic.

### 4) Authentication + bootstrap super-admin default
1. `backend/ArkansasServe.Functions/Middleware/AuthMiddleware.cs`
- Added hardcoded role override: users with `@arkansasserve.com` are treated as `PlatformAdmin` in request context.

2. `backend/ArkansasServe.Functions/Functions/UserFunctions.cs`
- On user bootstrap/read, users with `@arkansasserve.com` are persisted/promoted to:
  - `Role = PlatformAdmin`
  - `AdminLevel = SuperAdmin`

### 5) Frontend admin experience
1. `frontend/admin-backend.html` (new)
- Added admin backend page with:
  - tenant settings editor
  - nested group management
  - scoped user access management
  - super-admin-only demo-user section and reset action

2. `frontend/dashboard.html`
- Added admin backend button and visibility gating.
- Students do not see the admin backend button.
- Non-student admin levels get direct access button.

3. `frontend/js/api.js`
- Added `AdminBackend` API client namespace/methods for all new backend routes.

### 6) Documentation
1. `docs/admin-backend-rbac-plan.md`
- Created initial plan and now updated with full implementation log.

### 7) Build and diagnostics validation
1. Backend build status
- `dotnet build` succeeded after RBAC/admin backend and auth-domain updates.

2. Diagnostics
- No file diagnostics errors in edited backend/frontend/docs files at validation time.

### 8) Commit trail for this feature set
1. `c6874a1`
- `feat(admin): add scoped admin backend, RBAC schema, and demo-user controls`

2. `04be70d`
- `feat(auth): default @arkansasserve.com users to super admin`
