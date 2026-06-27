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
