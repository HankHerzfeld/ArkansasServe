# Arkansas Serve — Data Collection & Distribution Plan

## Overview

Every piece of data in the platform follows one of four flows:
1. User submits something → Functions validate → Cosmos DB stores it
2. User requests something → Functions query → Cosmos DB returns it → User sees it
3. A state change (e.g. hours submitted) → Change Feed triggers → downstream records updated automatically
4. A file is uploaded → Blob Storage receives it → URL reference saved in Cosmos DB

---

## Role Permissions Matrix

| Action                        | Student | Org Staff | School Admin | Platform Admin |
|-------------------------------|---------|-----------|--------------|----------------|
| Browse events                 | ✅      | ✅        | ✅           | ✅             |
| Sign up for event             | ✅      | ❌        | ❌           | ✅             |
| Create / edit event           | ❌      | ✅        | ❌           | ✅             |
| Check in students             | ❌      | ✅        | ❌           | ✅             |
| Submit service hours          | ❌      | ✅        | ❌           | ✅             |
| View own service log          | ✅      | ❌        | ❌           | ✅             |
| Approve / reject hours        | ❌      | ❌        | ✅           | ✅             |
| View school roster & reports  | ❌      | ❌        | ✅           | ✅             |
| Manage all tenants/users      | ❌      | ❌        | ❌           | ✅             |

---

## Data Flows by Feature

### FLOW 1 — User Login

```
Browser → Entra External ID (SSO)
        → Token issued with: userId, tenantId, role claims
        → Browser stores token
        → All subsequent API calls include token in Authorization header
        → Azure Functions middleware validates token on every request
        → Role extracted from token → permissions enforced per endpoint
```

**What gets stored:** Nothing new on login. User profile is created once
on first login via the /api/users/me endpoint (upsert pattern).

**Cosmos DB container:** Users  (partition key: /tenantId)

---

### FLOW 2 — Org Staff Creates an Event

```
Org staff fills out event form (title, date, location, slots, description)
  → Optional: uploads a photo
      → Frontend requests upload URL from /api/events/upload-token
      → Functions generate a SAS token for Blob Storage (event-photos/)
      → Frontend uploads file DIRECTLY to Blob Storage using that token
      → Blob URL returned and attached to the event payload
  → Frontend POST /api/events  { title, date, location, slots, photoUrl, ... }
  → Functions validate:
      - Token is valid (authenticated)
      - Role is OrgStaff or PlatformAdmin
      - organizationId in token matches payload (can't post for another org)
  → Functions write Event document to Cosmos DB
      Partition key: /organizationId
  → 201 Created returned to frontend
  → Event appears immediately in the public event browser
```

**Cosmos DB container:** Events  (partition key: /organizationId)
**Blob container:** event-photos/

---

### FLOW 3 — Student Browses & Signs Up for an Event

```
Student opens Events page
  → Frontend GET /api/events?schoolId=xyz&status=upcoming
  → Functions query Events container (cross-partition, filtered by status + eligibleSchoolIds)
  → List of events returned → rendered as cards

Student clicks "Sign Up"
  → Frontend POST /api/registrations  { eventId, studentId (from token) }
  → Functions validate:
      - Student not already registered
      - Event not full (check currentSlots < maxSlots)
      - Event status is "Open"
  → EventRegistrations document written (partition key: /eventId)
  → Events document updated: currentSlots + 1
  → 201 Created → button changes to "Registered"
```

**Cosmos DB containers:** Events, EventRegistrations

---

### FLOW 4 — Org Staff Logs Service Hours (after event)

```
Org staff opens their event's attendance list
  → Frontend GET /api/events/{eventId}/registrations
  → Functions query EventRegistrations by eventId (single-partition read)
  → List of registered students returned

Staff marks which students attended + hours completed
  → Frontend POST /api/servicelogs
      { studentId, eventId, organizationId, schoolId, hoursLogged, notes }
  → Functions validate role (OrgStaff or PlatformAdmin)
  → ServiceLog document written with status: "Pending"
      Partition key: /studentId

  → [AUTOMATIC] Cosmos DB Change Feed fires
      → ChangeFeedFunction detects new ServiceLog with status "Pending"
      → Writes PendingApproval document to PendingApprovals container
          Partition key: /schoolId
      → School admin sees it immediately in their approval queue
```

**Cosmos DB containers:** ServiceLogs (partition key: /studentId),
                          PendingApprovals (partition key: /schoolId)

---

### FLOW 5 — School Admin Approves or Rejects Hours

```
School admin opens Approvals dashboard
  → Frontend GET /api/approvals?schoolId=xyz
  → Functions query PendingApprovals by schoolId (single-partition read — fast)
  → List of pending logs rendered with student name, org, hours, event

Admin clicks Approve or Reject (with optional note)
  → Frontend PATCH /api/servicelogs/{id}  { status: "Approved", reviewNote: "..." }
  → Functions validate:
      - Role is SchoolAdmin or PlatformAdmin
      - schoolId in token matches the log's schoolId
  → ServiceLog document updated: status → "Approved" or "Rejected"
  → [AUTOMATIC] Change Feed fires again
      → ChangeFeedFunction deletes the PendingApproval pointer (no longer needed)
      → Writes a Notification document for the student
          { userId: studentId, type: "HoursApproved", message: "..." }
  → Student sees updated hours total on their dashboard
```

**Cosmos DB containers:** ServiceLogs, PendingApprovals, Notifications

---

### FLOW 6 — Student Views Their Service History

```
Student opens their Dashboard
  → Frontend GET /api/students/me/servicelogs
  → Functions query ServiceLogs by studentId (single-partition read — fast)
  → All logs returned: approved, pending, rejected
  → Frontend calculates running total of approved hours
  → Renders history table + total hours badge
```

**Cosmos DB container:** ServiceLogs  (partition key: /studentId)

---

## API Endpoint Map

| Method | Endpoint                              | Role(s)              | Cosmos Container(s)          |
|--------|---------------------------------------|----------------------|------------------------------|
| GET    | /api/users/me                         | All                  | Users                        |
| PUT    | /api/users/me                         | All                  | Users                        |
| GET    | /api/events                           | All                  | Events                       |
| GET    | /api/events/{id}                      | All                  | Events                       |
| POST   | /api/events                           | OrgStaff, Admin      | Events                       |
| PUT    | /api/events/{id}                      | OrgStaff, Admin      | Events                       |
| GET    | /api/events/{id}/registrations        | OrgStaff, Admin      | EventRegistrations           |
| POST   | /api/events/upload-token              | OrgStaff, Admin      | — (SAS token only)           |
| POST   | /api/registrations                    | Student, Admin       | EventRegistrations, Events   |
| DELETE | /api/registrations/{id}               | Student, Admin       | EventRegistrations, Events   |
| POST   | /api/servicelogs                      | OrgStaff, Admin      | ServiceLogs                  |
| PATCH  | /api/servicelogs/{id}                 | SchoolAdmin, Admin   | ServiceLogs, PendingApprovals|
| GET    | /api/students/me/servicelogs          | Student              | ServiceLogs                  |
| GET    | /api/approvals                        | SchoolAdmin, Admin   | PendingApprovals             |
| GET    | /api/admin/tenants                    | Admin                | Tenants                      |
| POST   | /api/admin/tenants                    | Admin                | Tenants                      |

---

## Service-log side effects (inline, not Change Feed)

> **Implementation note (corrected).** This project does **not** run a `ChangeFeedFunction`.
> An earlier design proposed a Cosmos DB change-feed trigger, but that was dropped for a
> simpler synchronous model — **not** because of the Static Web App tier (the SWA is
> **Standard**, and a change-feed processor is a property of the Functions host, not the SWA).
> The processor was avoided to keep the Consumption Function App free of an always-on
> lease-based background worker. The `leases` container remains provisioned but unused.
>
> Instead, `ServiceLogFunctions` performs these side effects **inline** when a log is
> submitted/reviewed, via `TryCreatePendingApprovalAsync` / `TryReviewSideEffectsAsync`.
> Those calls are wrapped in a transient retry (`CosmosRetry`) and are **recoverable**: the
> pending-approval queue is reconciled against the Pending service logs on every admin read
> (`CosmosService.GetPendingApprovalsBySchoolReconciledAsync`), so a dropped create/delete
> self-heals rather than being lost.

| Event                          | Side effect (inline, synchronous)                 |
|-------------------------------|---------------------------------------------------|
| New ServiceLog, status=Pending | PendingApproval pointer created for school admin  |
| ServiceLog status → Approved  | PendingApproval deleted, Notification created     |
| ServiceLog status → Rejected  | PendingApproval deleted, Notification created     |

---

## File Storage Rules

All files go to Blob Storage. The database never stores the file itself —
only a URL string pointing to it.

| File type          | Blob container       | Who uploads      | Access method         |
|--------------------|----------------------|------------------|-----------------------|
| Event photos       | event-photos/        | Org staff        | Public read SAS URL   |
| Verification docs  | verification-docs/   | Org staff        | Private SAS token     |
| Org logos          | org-logos/           | Org staff/Admin  | Public read SAS URL   |

---

## Folder Structure

```
ArkansasServe/
├── docs/
│   └── data-flow-plan.md         ← this file
│
├── backend/
│   └── ArkansasServe.Functions/
│       ├── ArkansasServe.Functions.csproj
│       ├── Program.cs                        ← DI setup, middleware registration
│       ├── local.settings.json               ← local dev config (not committed)
│       ├── Models/
│       │   ├── User.cs
│       │   ├── Tenant.cs
│       │   ├── Event.cs
│       │   ├── EventRegistration.cs
│       │   ├── ServiceLog.cs
│       │   ├── PendingApproval.cs
│       │   └── Notification.cs
│       ├── Services/
│       │   ├── CosmosService.cs              ← all Cosmos DB reads/writes
│       │   └── BlobService.cs                ← SAS token generation
│       ├── Middleware/
│       │   └── AuthMiddleware.cs             ← token validation + role extraction
│       └── Functions/
│           ├── UserFunctions.cs
│           ├── EventFunctions.cs
│           ├── RegistrationFunctions.cs
│           ├── ServiceLogFunctions.cs
│           ├── ApprovalFunctions.cs
│           ├── AdminFunctions.cs
│           └── ChangeFeedFunction.cs
│
└── frontend/
    ├── index.html                ← public landing page + login redirect
    ├── dashboard.html            ← student dashboard
    ├── events.html               ← event browser (all roles)
    ├── org-portal.html           ← org staff portal
    ├── admin-portal.html         ← school/JDC admin approval queue
    ├── platform-admin.html       ← your admin panel
    ├── styles/
    │   └── main.css
    ├── js/
    │   ├── auth.js               ← Entra login/logout/token handling
    │   ├── api.js                ← all fetch calls to /api/*
    │   └── ui.js                 ← shared UI helpers
    └── components/
        ├── navbar.html
        └── event-card.html
```
