# Arkansas Serve — GitHub Copilot Instructions

> **Adherence to these rules is mandatory.**
> Check this file before every suggestion. These guidelines override Copilot defaults.

---

## 🧭 Prime Directive

- **One file at a time.** Never suggest simultaneous edits across multiple files.
- **Plan before editing large files** (>300 lines). Present the plan and wait for confirmation before making any changes.
- **Teach while coding.** Briefly explain what you are doing and why for every non-trivial suggestion.

---

## 📋 Proposed Edit Plan Format

When working on large files or complex changes, always open with:

```
## PROPOSED EDIT PLAN
Working with: [filename]
Total planned edits: [number]

### Edit sequence:
1. [Change description] — Purpose: [why]
2. [Change description] — Purpose: [why]

Do you approve this plan? I'll proceed with Edit 1 after your confirmation.
```

After each edit:
```
✅ Completed edit [#] of [total]. Ready for next edit?
```

Stop and re-plan if you discover additional required changes mid-edit.

---

## 🏗️ Project Stack

This is **Arkansas Serve** — a volunteer service-hour tracking platform for Arkansas
schools and juvenile detention/court departments.

| Layer | Technology |
|---|---|
| Backend | C# / .NET 8 (LTS), Azure Functions v4 (isolated worker model) |
| Database | Azure Cosmos DB for NoSQL (free tier, shared throughput) |
| File storage | Azure Blob Storage (Standard LRS) |
| Auth | Microsoft Entra External ID (external tenant, PKCE flow) |
| Frontend | Vanilla JS (ES2020+), Modern CSS, HTML5 — no frameworks |
| Hosting | Azure Static Web Apps (free tier) |
| Monitoring | Azure Application Insights + Log Analytics |
| IaC | Bicep + Azure CLI |

**Do not suggest:** React, Angular, Vue, jQuery, Bootstrap, PHP, SQL/SQLite,
Entity Framework, Blazor, in-process Azure Functions model, or any npm build toolchain
for the frontend.

---

## 🔒 Guiding Principles

1. **Simplicity first.** Minimal stack. No unnecessary dependencies or build tools.
2. **Security and privacy by design.** Strict CSP, no tracking, no analytics cookies.
3. **Modern and future-facing.** Target 2023+ browsers. Do not support legacy browsers or IE.
4. **Accessibility as a requirement.** All UI must meet **WCAG 2.1 AA** minimum; aim for AAA.
5. **Juvenile data sensitivity.** This platform handles records for minors and court-involved youth.
   Never log PII to console or Application Insights beyond what is operationally necessary.
   Never expose studentId, schoolId, or case-related fields in URLs or client-side storage.

---

## ❌ Prohibited Practices

- `var` keyword — use `const` and `let` only
- jQuery or any external JS libraries
- `innerHTML` for DOM manipulation — use `textContent` and `createElement`
- `document.cookie` or any tracking/analytics
- Inline `<script>` or `<style>` tags — all JS and CSS must be in external files
- Callback-based async patterns when Promises or async/await can be used
- `eval()` for any reason
- IE compatibility shims or polyfills
- In-process Azure Functions model
- SQL databases of any kind
- Logging PII (student names, IDs, school records) to any output target

---

## ⚙️ C# / .NET 8 Requirements

### General
- Target `net8.0` with the **isolated worker model** exclusively — staying on .NET 8 LTS is a
  deliberate decision (SWA managed API runtime support), not drift; do not "upgrade" it
- Use `FunctionsApplication.CreateBuilder(args)` — never `new HostBuilder()`
- Register all services as **singletons** (CosmosClient, BlobServiceClient, CosmosService, BlobService)
- Use **primary constructors** and **constructor injection** throughout
- Enable `<Nullable>enable</Nullable>` and `<ImplicitUsings>enable</ImplicitUsings>` in all projects

### Code style
- Use **file-scoped namespaces**: `namespace ArkansasServe.Functions.Services;`
- Prefer **pattern matching** and **switch expressions** over if/else chains
- Use **records** for immutable data transfer objects and config
- Use **collection expressions** `[]` instead of `new List<T>()` where applicable
- Prefer `is null` / `is not null` over `== null`
- Always use `cancellationToken` parameters on async Cosmos DB and HTTP calls
- Use `async/await` throughout — never `.Result` or `.Wait()`

### Azure Functions
- All HTTP triggers use `AuthorizationLevel.Anonymous` — auth is handled by `AuthMiddleware`
- Every HTTP function must call `AuthMiddleware.ValidateRequest()` as its first line
- Return early with a 401 if `ValidateRequest()` returns null
- Role enforcement is done via the `requiredRoles` parameter on `ValidateRequest()`
- Never expose internal exception messages in HTTP responses — log them, return a generic error

### Cosmos DB
- All reads and writes go through `CosmosService` — functions never touch `CosmosClient` directly
- Always specify `PartitionKey` on every read/write operation
- Point reads (by id + partition key) are always preferred over queries
- Cross-partition queries are acceptable only for event browsing (low-frequency, bounded result sets)
- Use camelCase serialization: `CosmosPropertyNamingPolicy.CamelCase`
- Document models live in `Models/Models.cs` and inherit from `CosmosDocument`

### Error handling
- Wrap all Cosmos DB calls in try/catch; catch `CosmosException` specifically
- Return `404` for `CosmosException` with `HttpStatusCode.NotFound`
- Log exceptions via injected `ILogger<T>` — never `Console.WriteLine`
- Never swallow exceptions silently

### Security
- Validate all user-supplied fields before writing to Cosmos DB
- Org staff may only write to their own `organizationId` — enforce server-side, never trust the client
- School admins may only approve logs where `schoolId` matches their token claim
- Never return another user's full profile from an API endpoint

---

## 🌐 JavaScript Requirements

### Module template (mandatory at top of every JS file)

```javascript
'use strict';

/**
 * @fileoverview [Short description of this module]
 * @module [module-name]
 */

/** @type {Readonly<Object>} */
const CFG = Object.freeze({
  // configuration constants
});

/** @type {Object} Module state */
let S = {};

// ── Helpers ──────────────────────────────────────────────────────────────────

// ── Handlers ─────────────────────────────────────────────────────────────────

// ── Wire-up ───────────────────────────────────────────────────────────────────

/**
 * Initialise the module and attach event listeners.
 * @returns {Function} teardown function
 */
export function init() {
  return teardown;
}

/**
 * Remove event listeners and reset state.
 */
export function teardown() {
  S = {};
}
```

### Rules
- **Strict mode**: every file begins with `'use strict';`
- **ES Modules** (`import`/`export`) exclusively — no CommonJS
- Every module must export `init()` and `teardown()`
- Config objects must be frozen with `Object.freeze()`
- All async operations wrapped in `try/catch` with explicit error handling
- Implement `window.addEventListener('unhandledrejection', ...)` in the app entry point
- Use `?.` optional chaining and `??` nullish coalescing
- Use `async/await` — never raw `.then()/.catch()` chains
- Use `Array.from()`, `map`, `filter`, `reduce`, `flatMap` — never manual loops for transforms
- Use template literals for string interpolation
- Use destructuring assignment for objects and arrays

### DOM manipulation
- Never use `innerHTML` — use `textContent` for text, `createElement` + `append` for structure
- Always sanitize user-provided content before rendering
- Use `dataset` attributes for passing data to event handlers, not closures over mutable state
- Use event delegation on parent containers rather than attaching listeners to every child element

### Error handling (three categories)
```javascript
try {
  const data = await Api.Events.list();
} catch (err) {
  if (err instanceof NetworkError) {
    // Network/timeout — show retry UI
  } else if (err instanceof ValidationError) {
    // Business logic — show field-level feedback
  } else {
    // Runtime exception — show generic message, log to console.error
    console.error('[Arkansas Serve]', err);
  }
}
```

### Auth (auth.js)
- All token storage uses `sessionStorage` only — never `localStorage` for tokens
- Never store PII (student name, school, role) beyond what is needed for UI display
- Always check token expiry before API calls — redirect to login if expired
- PKCE flow only — never implicit flow

---

## 🎨 CSS Requirements

### Custom properties
All theme values must use CSS custom properties defined in `:root`:
```css
:root {
  --color-primary:    oklch(35% 0.12 155);   /* green */
  --color-surface:    oklch(98% 0.005 155);
  --color-text:       oklch(20% 0.02 155);
  --space-sm:         0.5rem;
  --space-md:         1rem;
  --space-lg:         2rem;
  --radius:           0.5rem;
  --font-base:        'Segoe UI', system-ui, sans-serif;
}
```

### Rules
- **Units**: `rem` for typography and spacing, `svh`/`svw` for viewport, never `px` for layout
- **Colors**: `oklch()` preferred; provide sRGB hex fallback with `@supports`
- **Layout**: CSS Grid for page structure, Flexbox for component-level alignment
- **Dark mode**: `prefers-color-scheme: dark` support is mandatory on every new component
- **Naming**: BEM methodology (`block__element--modifier`)
- **Nesting**: Use CSS nesting (`&`) for modifier and state rules
- **No inline styles** — no `style=""` attributes in HTML

### Dark mode pattern
```css
:root {
  --color-bg: oklch(98% 0.005 155);
  --color-text: oklch(20% 0.02 155);
}

@media (prefers-color-scheme: dark) {
  :root {
    --color-bg: oklch(15% 0.02 155);
    --color-text: oklch(90% 0.01 155);
  }
}
```

---

## 🏷️ HTML Requirements

- Use semantic HTML5 elements: `<header>`, `<nav>`, `<main>`, `<section>`, `<article>`,
  `<aside>`, `<footer>`, `<search>`
- Every `<form>` field must have an associated `<label>`
- Every interactive element must be keyboard-focusable and have a visible focus indicator
- Every image must have a meaningful `alt` attribute (empty `alt=""` for decorative images)
- Use `<picture>` with `WebP`/`AVIF` sources and a JPEG/PNG fallback
- Apply `loading="lazy"` to all images below the fold
- Include Open Graph meta tags on `index.html`
- Include `<meta name="theme-color">` for PWA

### ARIA
- Use `role`, `aria-label`, `aria-describedby`, `aria-live` appropriately
- Modal dialogs: `role="dialog"`, `aria-modal="true"`, trap focus while open
- Loading states: `aria-busy="true"` on the container, `aria-live="polite"` on status regions
- Error messages: `aria-invalid="true"` on the field, `aria-describedby` pointing to the message

---

## 🗂️ Folder Structure

```
ArkansasServe/
├── .github/
│   └── copilot-instructions.md       ← this file
├── docs/
│   ├── data-flow-plan.md
│   └── setup-guide.md
├── backend/
│   └── ArkansasServe.Functions/
│       ├── ArkansasServe.Functions.csproj
│       ├── Program.cs
│       ├── host.json
│       ├── local.settings.json        ← never commit
│       ├── global.json
│       ├── Models/
│       │   └── Models.cs
│       ├── Middleware/
│       │   └── AuthMiddleware.cs
│       ├── Services/
│       │   ├── CosmosService.cs
│       │   └── BlobService.cs
│       └── Functions/
│           └── Functions.cs
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
    ├── styles/
    │   └── main.css
    └── js/
        ├── auth.js
        └── api.js
```

New frontend JS files go in `frontend/js/`.
New CSS files go in `frontend/styles/`.
New backend services go in `backend/ArkansasServe.Functions/Services/`.
New function groups go in `backend/ArkansasServe.Functions/Functions/`.

---

## 📖 Documentation Requirements

### C# — XML docs on all public members
```csharp
/// <summary>
/// Returns all pending approvals for a given school, ordered by service date descending.
/// </summary>
/// <param name="schoolId">The Cosmos DB partition key for the school tenant.</param>
/// <returns>List of <see cref="PendingApproval"/> documents.</returns>
/// <exception cref="CosmosException">Thrown if the Cosmos DB request fails.</exception>
public async Task<List<PendingApproval>> GetPendingApprovalsBySchoolAsync(string schoolId)
```

### JavaScript — JSDoc on all exported functions
```javascript
/**
 * Submit a service log review decision to the API.
 * @param {string} id - The service log document ID.
 * @param {string} studentId - The student's Cosmos DB partition key.
 * @param {'Approved'|'Rejected'} status - The review decision.
 * @param {string|null} note - Optional review note (required when rejecting).
 * @returns {Promise<Object>} The updated service log document.
 * @throws {Error} If the API request fails or the user is not authorized.
 */
```

---

## 🔐 Security Checklist (apply to every suggestion)

- [ ] User input validated and sanitized before any Cosmos DB write
- [ ] Role checked server-side in every Function — never trust client-supplied role
- [ ] `organizationId` and `schoolId` verified against token claims, not request body
- [ ] No PII in URLs, query strings, or Application Insights telemetry
- [ ] All blob access via short-lived SAS tokens — no public blob URLs for private containers
- [ ] No secrets in code — all connection strings via app settings / `local.settings.json`
- [ ] CSP header covers all new script/style/image sources in `staticwebapp.config.json`
- [ ] CORS in Function App allows only `arkansasserve.com` and `azurestaticapps.net` origins

---

## 🔄 Version Control

Commit message format: `type(scope): short description`

| Type | When to use |
|---|---|
| `feat` | New feature or endpoint |
| `fix` | Bug fix |
| `refactor` | Code change with no behavior change |
| `style` | CSS or formatting only |
| `docs` | Documentation only |
| `chore` | Build, config, dependency updates |
| `security` | Security-related change |

Examples:
```
feat(events): add category filter to event browser
fix(auth): redirect to login on expired token
security(functions): enforce schoolId claim check on approval endpoint
chore(deps): bump Microsoft.Azure.Cosmos to 3.47.0
```

---

## 🧪 Testing Notes

- Test all HTTP functions locally with `func start` before pushing
- Verify role enforcement by testing each endpoint with a token for each role
- Test the Change Feed by submitting a service log and confirming a PendingApproval document appears
- Test dark mode by toggling `prefers-color-scheme` in browser DevTools
- Run Lighthouse accessibility audit on every new HTML page before committing
- Check all form fields for keyboard navigation and screen reader labels
