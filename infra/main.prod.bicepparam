using './main.bicep'

// Entra External ID (CIAM) tenant: arkansasserve.onmicrosoft.com.
// Public identifiers, not secrets.
//
// Auth moved off the SalineServe tenant (2d72a425-...) to the purpose-built
// External ID directory. Client = "Arkansas Serve Web" SPA registration there;
// frontend/js/auth.js CLIENT_ID must hold the same value.
param entraTenantId = '434cf17d-6ab5-48c3-be4a-5541ed0e74d0'
param entraClientId = '21ceb138-99f1-40b6-a73d-01965a1b6f06'
param entraAudience = 'api://21ceb138-99f1-40b6-a73d-01965a1b6f06'

// Bootstrap: elevate @arkansasserve.com sign-ins to PlatformAdmin while seeding
// the first admin account. Set back to '' and redeploy once seeding is done.
param platformAdminEmailDomain = 'arkansasserve.com'
