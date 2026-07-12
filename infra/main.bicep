targetScope = 'resourceGroup'

// =============================================================================
// Arkansas Serve — reconciled infrastructure
// Rewritten 2026-07-02 to match the VERIFIED live state of rg-arkansas-serve.
// See docs/production-cutover-plan.md for the audit that produced these values.
// Every default below is the real deployed name/config; run `what-if` before deploy.
// =============================================================================

@description('Deployment location for all resources.')
param location string = resourceGroup().location

// ---- Resource names (defaults = the actual deployed names) -------------------
@description('Static Web App name.')
param staticWebAppName string = 'swa-arkansas-serve-arksrv'

@description('Cosmos DB account name.')
param cosmosAccountName string = 'cosmos-arkansas-serve-arksrv'

@description('Cosmos DB database name used by the Functions app.')
param cosmosDatabaseName string = 'arkansas-serve-db'

@description('Shared (database-level) manual throughput in RU/s. Live = 1000.')
param cosmosSharedThroughput int = 1000

@description('Storage account name.')
param storageAccountName string = 'starkansasservearksrv'

@description('Origins allowed to upload directly to Blob Storage via SAS from the browser. Must include the site origin(s) — the frontend PUTs event photos / org logos straight to the blob endpoint, which needs a matching account-level CORS rule (defaults match the live rule).')
param blobCorsAllowedOrigins array = [
  'https://arkansasserve.com'
  'https://www.arkansasserve.com'
]

@description('Application Insights resource name.')
param appInsightsName string = 'appi-arkansas-serve-arksrv'

@description('Log Analytics workspace resource name.')
param logAnalyticsWorkspaceName string = 'log-arkansas-serve-arksrv'

@description('App Service Plan (Consumption) name for the Function App.')
param functionAppPlanName string = 'asp-arkansas-serve-arksrv'

@description('Function App name.')
param functionAppName string = 'func-arkansas-serve-arksrv'

@description('User-assigned managed identity used by GitHub Actions to deploy the Function App.')
param deployIdentityName string = 'oidc-msi-9fae'

@description('GitHub OIDC subject for the deploy identity federated credential.')
param githubOidcSubject string = 'repo:HankHerzfeld/ArkansasServe:ref:refs/heads/main'

@description('Static Web App SKU tier.')
@allowed([
  'Free'
  'Standard'
])
param staticWebAppSku string = 'Standard'

@description('GitHub repository linked to the Static Web App (preserves live linkage on deploy).')
param staticWebAppRepositoryUrl string = 'https://github.com/HankHerzfeld/ArkansasServe'

@description('Branch the Static Web App deploys from.')
param staticWebAppBranch string = 'main'

// ---- Entra External ID (CIAM: SalineServe.onmicrosoft.com) -------------------
@description('Microsoft Entra tenant ID used for token validation.')
param entraTenantId string

@description('Microsoft Entra app registration client ID expected by the API.')
param entraClientId string

@description('Expected audience claim for access tokens.')
param entraAudience string

@description('Optional bootstrap: emails on this domain are elevated to PlatformAdmin. Leave empty to disable (recommended after seeding the first admin).')
param platformAdminEmailDomain string = ''

@description('Azure Communication Services connection string for email notifications. Empty leaves email disabled (in-app notifications only).')
@secure()
param communicationConnectionString string = ''

@description('Verified ACS sender address for email, e.g. DoNotReply@<your-domain>. Empty leaves email disabled.')
param communicationSenderAddress string = ''

// Tags present on the originally Bicep-deployed resources; preserved to avoid stripping them.
var commonTags = {
  environment: 'prod'
  managedBy: 'bicep'
  project: 'arkansas-serve'
}

// =============================================================================
// Observability
// =============================================================================
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsWorkspaceName
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  kind: 'web'
  tags: commonTags
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
    IngestionMode: 'LogAnalytics'
  }
}

// =============================================================================
// Storage (event photos + Functions runtime state)
// =============================================================================
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
  }
}

// Account-level CORS so the browser can PUT directly to blob (event photos / org logos)
// with a SAS. Without this the preflight OPTIONS is blocked (no Access-Control-Allow-Origin)
// and uploads fail even though the SAS is valid. Reads use <img src> and don't need CORS,
// but GET/HEAD are included for any fetch-based read. Matches the live rule applied out-of-band.
resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  name: 'default'
  parent: storage
  properties: {
    cors: {
      corsRules: [
        {
          allowedOrigins: blobCorsAllowedOrigins
          allowedMethods: [
            'PUT'
            'GET'
            'OPTIONS'
            'HEAD'
          ]
          allowedHeaders: [
            '*'
          ]
          exposedHeaders: [
            '*'
          ]
          maxAgeInSeconds: 3600
        }
      ]
    }
  }
}

// All three containers are PRIVATE. The account sets allowBlobPublicAccess:false
// (above), so anonymous/public-read is impossible account-wide regardless of this
// per-container flag — every read must go through a short-lived SAS
// (BlobService.GenerateReadSasToken). Declaring the containers here so uploads to
// them succeed (a SAS write to a non-existent container 404s ContainerNotFound).
var blobContainerNames = [
  'event-photos'      // event card/detail photos
  'verification-docs' // org verification documents (private; admin read-SAS only)
  'org-logos'         // organization branding logos
]

resource blobContainers 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = [for name in blobContainerNames: {
  name: name
  parent: blobService
  properties: {
    publicAccess: 'None'
  }
}]

// =============================================================================
// Cosmos DB  (provisioned, database-level shared throughput — NOT serverless)
// =============================================================================
resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' = {
  name: cosmosAccountName
  location: location
  kind: 'GlobalDocumentDB'
  tags: commonTags
  properties: {
    databaseAccountOfferType: 'Standard'
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: false
      }
    ]
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
    }
    enableAutomaticFailover: true
    enableFreeTier: true // immutable; live account is on the free tier — must not be stripped
    minimalTlsVersion: 'Tls12'
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: false
  }
}

resource cosmosDb 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-05-15' = {
  name: cosmosDatabaseName
  parent: cosmos
  properties: {
    resource: {
      id: cosmosDatabaseName
    }
  }
}

// Database-level shared throughput as a dedicated child resource, so changing
// cosmosSharedThroughput actually takes effect on redeploy (options.throughput on the
// database above is honored only at creation, silently ignored on updates).
resource cosmosDbThroughput 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/throughputSettings@2024-05-15' = {
  name: 'default'
  parent: cosmosDb
  properties: {
    resource: {
      throughput: cosmosSharedThroughput
    }
  }
}

// Real deployed containers: PascalCase, plus the change-feed `leases` container.
// defaultTtl mirrors live: -1 (TTL enabled, no default expiry) on the three containers
// that have it. Omitting it would STRIP the setting on deploy (a data-retention change).
var cosmosContainerSpecs = [
  {
    name: 'Tenants'
    partitionKeyPath: '/id'
    defaultTtl: null
  }
  {
    name: 'Users'
    partitionKeyPath: '/tenantId'
    defaultTtl: null
  }
  {
    name: 'Events'
    partitionKeyPath: '/organizationId'
    defaultTtl: null
  }
  {
    name: 'EventRegistrations'
    partitionKeyPath: '/eventId'
    defaultTtl: null
  }
  {
    name: 'ServiceLogs'
    partitionKeyPath: '/studentId'
    defaultTtl: -1
  }
  {
    name: 'PendingApprovals'
    partitionKeyPath: '/schoolId'
    defaultTtl: null
  }
  {
    name: 'Notifications'
    partitionKeyPath: '/userId'
    defaultTtl: 2592000 // 30 days — notifications auto-expire; do not change to -1
  }
  // SuperAdmin remote access (#26): impersonation sessions + append-only audit trail,
  // both partitioned by adminUserId. Additive — new resources, no change to existing.
  {
    name: 'ImpersonationSessions'
    partitionKeyPath: '/adminUserId'
    defaultTtl: null
  }
  {
    name: 'AuditEvents'
    partitionKeyPath: '/adminUserId'
    defaultTtl: null
  }
  {
    name: 'leases'
    partitionKeyPath: '/id'
    defaultTtl: -1
  }
]

// The live containers all use Cosmos' DEFAULT indexing and conflict-resolution
// policies (verified 2026-07-09). Declaring them explicitly (rather than omitting)
// keeps `what-if` at NoChange for the containers — otherwise ARM returns the
// defaults and what-if reports a spurious "delete indexingPolicy" against the
// template's silence. Re-applying the identical default policy is a no-op (no
// reindex). If a container ever needs a custom index, override it per-spec.
var defaultIndexingPolicy = {
  indexingMode: 'consistent'
  automatic: true
  includedPaths: [
    {
      path: '/*'
    }
  ]
  excludedPaths: [
    {
      path: '/"_etag"/?'
    }
  ]
}
var defaultConflictResolutionPolicy = {
  mode: 'LastWriterWins'
  conflictResolutionPath: '/_ts'
}

resource cosmosContainers 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = [for spec in cosmosContainerSpecs: {
  name: spec.name
  parent: cosmosDb
  properties: {
    resource: union(
      {
        id: spec.name
        partitionKey: {
          paths: [
            spec.partitionKeyPath
          ]
          kind: 'Hash'
        }
        indexingPolicy: defaultIndexingPolicy
        conflictResolutionPolicy: defaultConflictResolutionPolicy
      },
      spec.defaultTtl == null ? {} : { defaultTtl: spec.defaultTtl }
    )
  }
}]

// =============================================================================
// Function App  (Windows Consumption, .NET 8 isolated)
// =============================================================================
resource functionPlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: functionAppPlanName
  location: location
  kind: 'functionapp'
  sku: {
    name: 'Y1'
    tier: 'Dynamic'
  }
  properties: {
    reserved: false // Windows
  }
}

var storageConnectionString = 'DefaultEndpointsProtocol=https;AccountName=${storage.name};AccountKey=${storage.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}'
var cosmosConnectionString = cosmos.listConnectionStrings().connectionStrings[0].connectionString

resource functionApp 'Microsoft.Web/sites@2023-12-01' = {
  name: functionAppName
  location: location
  kind: 'functionapp'
  tags: union(commonTags, {
    'hidden-link: /app-insights-resource-id': appInsights.id
  })
  properties: {
    serverFarmId: functionPlan.id
    httpsOnly: true
    clientAffinityEnabled: false
    publicNetworkAccess: 'Enabled'
    siteConfig: {
      netFrameworkVersion: 'v8.0'
      use32BitWorkerProcess: false
      ftpsState: 'FtpsOnly'
      minTlsVersion: '1.2'
      // Real SWA origin (linked-backend calls are same-origin; this only matters for any
      // direct browser call to the Function App). The previous hardcoded value pointed at a
      // non-existent hostname.
      cors: {
        allowedOrigins: [
          'https://${staticWebApp.properties.defaultHostname}'
        ]
        supportCredentials: true
      }
    }
  }
}

// App settings declared as a child resource. This collection REPLACES the live one,
// so it lists every existing setting plus the new CosmosDb__Containers__* mappings
// that make the code target the real PascalCase containers.
//
// ⚠ FULL REPLACEMENT HAZARD: any setting added out-of-band (e.g. via the portal) that is not
// listed here is wiped by the next infra deploy. The run-from-package settings the Functions
// workflow relies on are now declared below (matching live), so an infra deploy no longer
// breaks the running backend — but still keep this list in sync with the deploy pipeline, and
// prefer deploying infra BEFORE the Functions workflow (or re-running Functions afterward).
resource functionAppSettings 'Microsoft.Web/sites/config@2023-12-01' = {
  name: 'appsettings'
  parent: functionApp
  properties: {
    // --- runtime (existing) ---
    AzureWebJobsStorage: storageConnectionString
    AzureWebJobsSecretStorageType: 'Files'
    FUNCTIONS_EXTENSION_VERSION: '~4'
    FUNCTIONS_WORKER_RUNTIME: 'dotnet-isolated'
    // The Functions GitHub Actions deploy publishes as a run-from-package zip, so the LIVE
    // values are WEBSITE_RUN_FROM_PACKAGE=1 and WEBSITE_ENABLE_SYNC_UPDATE_SITE=true. These
    // are declared here (matching live) so the full-replacement app-settings write does NOT
    // flip the app off run-from-package or drop the sync flag and break the running backend.
    WEBSITE_RUN_FROM_PACKAGE: '1'
    WEBSITE_ENABLE_SYNC_UPDATE_SITE: 'true'
    // --- telemetry (existing) ---
    APPINSIGHTS_INSTRUMENTATIONKEY: appInsights.properties.InstrumentationKey
    APPLICATIONINSIGHTS_CONNECTION_STRING: appInsights.properties.ConnectionString
    // --- data (existing) ---
    CosmosDb__ConnectionString: cosmosConnectionString
    CosmosDb__DatabaseName: cosmosDatabaseName
    BlobStorage__ConnectionString: storageConnectionString
    // --- Entra (existing) ---
    Entra__TenantId: entraTenantId
    Entra__ClientId: entraClientId
    Entra__Audience: entraAudience
    Entra__PlatformAdminEmailDomain: platformAdminEmailDomain
    // --- email (Azure Communication Services; empty until an ACS resource is provisioned) ---
    Communication__ConnectionString: communicationConnectionString
    Communication__SenderAddress: communicationSenderAddress
    // --- NEW: map code logical names -> real container names (fixes 404/500s) ---
    CosmosDb__Containers__Tenants: 'Tenants'
    CosmosDb__Containers__Users: 'Users'
    CosmosDb__Containers__Events: 'Events'
    CosmosDb__Containers__Registrations: 'EventRegistrations'
    CosmosDb__Containers__ServiceLogs: 'ServiceLogs'
    CosmosDb__Containers__PendingApprovals: 'PendingApprovals'
    CosmosDb__Containers__Notifications: 'Notifications'
    CosmosDb__Containers__ImpersonationSessions: 'ImpersonationSessions'
    CosmosDb__Containers__AuditEvents: 'AuditEvents'
  }
}

// =============================================================================
// GitHub Actions deploy identity (user-assigned MI + federation + RBAC)
// =============================================================================
resource deployIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: deployIdentityName
  location: location
}

resource deployIdentityFederation 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  name: 'oidc-credential-bcc2'
  parent: deployIdentity
  properties: {
    issuer: 'https://token.actions.githubusercontent.com'
    subject: githubOidcSubject
    audiences: [
      'api://AzureADTokenExchange'
    ]
  }
}

// Website Contributor on the Function App so the deploy identity can publish.
//
// NOTE: this assignment ALREADY EXISTS in Azure (created manually, under a random GUID name).
// Re-declaring it here with a deterministic guid() name would make `what-if` show a Create and a
// deploy would FAIL with 409 RoleAssignmentExists (Azure rejects a second assignment of the same
// principal+role+scope). It is therefore left commented out. To bring it under IaC control, first
// delete the existing manual assignment, then uncomment and deploy.
//
// var websiteContributorRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'de139f84-1756-47ae-9be6-808fbbe84772')
//
// resource deployRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
//   name: guid(functionApp.id, deployIdentity.id, websiteContributorRoleId)
//   scope: functionApp
//   properties: {
//     roleDefinitionId: websiteContributorRoleId
//     principalId: deployIdentity.properties.principalId
//     principalType: 'ServicePrincipal'
//   }
// }

// =============================================================================
// Static Web App + linked backend  (frontend + /api/* proxy to the Function App)
// =============================================================================
// GitHub linkage declared to match live so an incremental deploy does not clear
// repositoryUrl/branch/provider. NOTE: deploys use AZURE_STATIC_WEB_APPS_API_TOKEN, not this
// ARM linkage. Verify on a scratch SWA before the first prod deploy — setting these without a
// repositoryToken on an existing site is expected to be a no-op but is not confirmed here.
resource staticWebApp 'Microsoft.Web/staticSites@2024-04-01' = {
  name: staticWebAppName
  location: location
  tags: commonTags
  sku: {
    name: staticWebAppSku
    tier: staticWebAppSku
  }
  properties: {
    provider: 'GitHub'
    repositoryUrl: staticWebAppRepositoryUrl
    branch: staticWebAppBranch
  }
}

// region matches the live-stored display form ('Central US') to avoid a permanent spurious
// what-if diff (location resolves to 'centralus'); both refer to the same region.
resource linkedBackend 'Microsoft.Web/staticSites/linkedBackends@2024-04-01' = {
  name: functionAppName
  parent: staticWebApp
  properties: {
    backendResourceId: functionApp.id
    region: 'Central US'
  }
}

// =============================================================================
// Outputs
// =============================================================================
output staticWebAppDefaultHostname string = staticWebApp.properties.defaultHostname
output functionAppDefaultHostname string = functionApp.properties.defaultHostName
output appInsightsConnectionString string = appInsights.properties.ConnectionString
output cosmosEndpoint string = cosmos.properties.documentEndpoint
output storageAccountId string = storage.id
output deployIdentityClientId string = deployIdentity.properties.clientId
