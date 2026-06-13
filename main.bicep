// ============================================================
// Arkansas Serve — Azure Infrastructure
// Region: Central US | Subscription: Pay-As-You-Go
// Target cost: under $50/mo
// Deploy: az deployment group create --resource-group rg-arkansas-serve --template-file main.bicep --parameters @params.json
// ============================================================

@description('Deployment environment tag')
@allowed(['dev', 'staging', 'prod'])
param environment string = 'prod'

@description('Short suffix to ensure globally unique resource names')
@minLength(3)
@maxLength(8)
param uniqueSuffix string

@description('Location for all resources')
param location string = 'centralus'

@description('GitHub repo for Static Web App CI/CD (format: owner/repo)')
param githubRepoUrl string

@description('GitHub branch to deploy from')
param githubBranch string = 'main'

// ============================================================
// Variables
// ============================================================
var appName = 'arkansas-serve'
var tags = {
  project: appName
  environment: environment
  managedBy: 'bicep'
}

// Resource name conventions — globally unique where required
var cosmosAccountName    = 'cosmos-${appName}-${uniqueSuffix}'
var storageAccountName   = 'st${replace(appName, '-', '')}${uniqueSuffix}'  // storage names: lowercase, no hyphens
var staticWebAppName     = 'swa-${appName}-${uniqueSuffix}'
var functionAppName      = 'func-${appName}-${uniqueSuffix}'
var appServicePlanName   = 'asp-${appName}-${uniqueSuffix}'
var appInsightsName      = 'appi-${appName}-${uniqueSuffix}'
var logAnalyticsName     = 'log-${appName}-${uniqueSuffix}'

// Cosmos DB database and containers
var cosmosDatabaseName   = 'arkansas-serve-db'

// ============================================================
// Log Analytics Workspace (required by Application Insights)
// Free tier: 5 GB/day ingestion included
// ============================================================
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: logAnalyticsName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'   // pay-per-GB, first 5 GB/day free
    }
    retentionInDays: 30   // minimum retention; keeps costs minimal
  }
}

// ============================================================
// Application Insights
// Free tier: 5 GB/month ingestion, 90-day retention
// ============================================================
resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
    IngestionMode: 'LogAnalytics'
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

// ============================================================
// Cosmos DB — NoSQL API, FREE TIER enabled
// Free forever: 1,000 RU/s + 25 GB storage per subscription
// NOTE: Only one free-tier Cosmos DB allowed per subscription.
//       If you already have one, set enableFreeTier = false below.
// ============================================================
resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2024-02-15-preview' = {
  name: cosmosAccountName
  location: location
  tags: tags
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    enableFreeTier: true                // <-- locks in the free 1,000 RU/s + 25 GB
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session' // good balance of consistency vs cost
    }
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: false           // zone redundancy costs extra; skip until you have a contract
      }
    ]
    capabilities: []
    backupPolicy: {
      type: 'Periodic'                   // free continuous backup not available on free tier
      periodicModeProperties: {
        backupIntervalInMinutes: 240     // every 4 hours
        backupRetentionIntervalInHours: 8
        backupStorageRedundancy: 'Local' // cheapest option
      }
    }
    // Disable public network access later once you've added VNet integration
    publicNetworkAccess: 'Enabled'
  }
}

// Cosmos DB database — shared throughput (all containers share the 1,000 RU/s)
resource cosmosDatabase 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-02-15-preview' = {
  parent: cosmosAccount
  name: cosmosDatabaseName
  properties: {
    resource: {
      id: cosmosDatabaseName
    }
    options: {
      throughput: 1000  // shared across all containers; stays within free tier
    }
  }
}

// Container: Tenants (schools + organizations)
resource containerTenants 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-02-15-preview' = {
  parent: cosmosDatabase
  name: 'Tenants'
  properties: {
    resource: {
      id: 'Tenants'
      partitionKey: {
        paths: ['/id']
        kind: 'Hash'
      }
      indexingPolicy: {
        indexingMode: 'consistent'
        includedPaths: [{ path: '/*' }]
        excludedPaths: [
          { path: '/description/?'}  // exclude long text fields from index to save RU
          { path: '/_etag/?'}
        ]
      }
    }
  }
}

// Container: Users (students, org staff, school admins, platform admins)
resource containerUsers 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-02-15-preview' = {
  parent: cosmosDatabase
  name: 'Users'
  properties: {
    resource: {
      id: 'Users'
      partitionKey: {
        paths: ['/tenantId']   // all users for a school/org in one logical partition
        kind: 'Hash'
      }
      indexingPolicy: {
        indexingMode: 'consistent'
        includedPaths: [{ path: '/*' }]
        excludedPaths: [{ path: '/_etag/?'}]
      }
    }
  }
}

// Container: Events (volunteer opportunities)
resource containerEvents 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-02-15-preview' = {
  parent: cosmosDatabase
  name: 'Events'
  properties: {
    resource: {
      id: 'Events'
      partitionKey: {
        paths: ['/organizationId']
        kind: 'Hash'
      }
      indexingPolicy: {
        indexingMode: 'consistent'
        includedPaths: [{ path: '/*' }]
        excludedPaths: [
          { path: '/description/?'}
          { path: '/_etag/?'}
        ]
      }
    }
  }
}

// Container: EventRegistrations (student sign-ups per event)
resource containerRegistrations 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-02-15-preview' = {
  parent: cosmosDatabase
  name: 'EventRegistrations'
  properties: {
    resource: {
      id: 'EventRegistrations'
      partitionKey: {
        paths: ['/eventId']
        kind: 'Hash'
      }
    }
  }
}

// Container: ServiceLogs (the core hours-tracking records)
resource containerServiceLogs 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-02-15-preview' = {
  parent: cosmosDatabase
  name: 'ServiceLogs'
  properties: {
    resource: {
      id: 'ServiceLogs'
      partitionKey: {
        paths: ['/studentId']   // student's full history in one partition
        kind: 'Hash'
      }
      // TTL disabled — service logs are permanent records
      defaultTtl: -1
    }
  }
}

// Container: PendingApprovals (change-feed-maintained index for school admins)
resource containerPendingApprovals 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-02-15-preview' = {
  parent: cosmosDatabase
  name: 'PendingApprovals'
  properties: {
    resource: {
      id: 'PendingApprovals'
      partitionKey: {
        paths: ['/schoolId']   // "show all pending approvals for my school" is one partition read
        kind: 'Hash'
      }
    }
  }
}

// Container: Notifications
resource containerNotifications 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-02-15-preview' = {
  parent: cosmosDatabase
  name: 'Notifications'
  properties: {
    resource: {
      id: 'Notifications'
      partitionKey: {
        paths: ['/userId']
        kind: 'Hash'
      }
      defaultTtl: 2592000   // auto-delete notifications after 30 days to save storage
    }
  }
}

// ============================================================
// Storage Account — Blob Storage for files
// Cost: ~$0.018/GB/mo for LRS; expect $1–4/mo at your scale
// ============================================================
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: storageAccountName
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'   // locally redundant; cheapest. Upgrade to GRS with a contract.
  }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false    // all blobs private; serve via SAS tokens from Functions
    allowSharedKeyAccess: true
  }
}

// Blob containers
resource blobServiceDef 'Microsoft.Storage/storageAccounts/blobServices@2023-01-01' = {
  parent: storageAccount
  name: 'default'
}

resource containerEventPhotos 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  parent: blobServiceDef
  name: 'event-photos'
  properties: { publicAccess: 'None' }
}

resource containerVerificationDocs 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  parent: blobServiceDef
  name: 'verification-docs'
  properties: { publicAccess: 'None' }
}

resource containerOrgLogos 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  parent: blobServiceDef
  name: 'org-logos'
  properties: { publicAccess: 'None' }
}

// ============================================================
// App Service Plan — Consumption (serverless) for Functions
// Cost: free up to 1M executions + 400K GB-s/mo
// ============================================================
resource appServicePlan 'Microsoft.Web/serverfarms@2023-01-01' = {
  name: appServicePlanName
  location: location
  tags: tags
  sku: {
    name: 'Y1'      // Y1 = Consumption plan (serverless)
    tier: 'Dynamic'
  }
  kind: 'functionapp'
}

// ============================================================
// Azure Functions App — C# (.NET 8 isolated)
// ============================================================
resource functionApp 'Microsoft.Web/sites@2023-01-01' = {
  name: functionAppName
  location: location
  tags: tags
  kind: 'functionapp'
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      netFrameworkVersion: 'v8.0'
      use32BitWorkerProcess: false
      cors: {
        allowedOrigins: [
          'https://${staticWebAppName}.azurestaticapps.net'
          // Add your custom domain here once configured, e.g.:
          // 'https://serve.yourorganization.org'
        ]
        supportCredentials: true
      }
      appSettings: [
        {
          name: 'AzureWebJobsStorage'
          value: 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};AccountKey=${storageAccount.listKeys().keys[0].value};EndpointSuffix=core.windows.net'
        }
        {
          name: 'FUNCTIONS_EXTENSION_VERSION'
          value: '~4'
        }
        {
          name: 'FUNCTIONS_WORKER_RUNTIME'
          value: 'dotnet-isolated'
        }
        {
          name: 'APPINSIGHTS_INSTRUMENTATIONKEY'
          value: appInsights.properties.InstrumentationKey
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsights.properties.ConnectionString
        }
        {
          name: 'CosmosDb__ConnectionString'
          value: cosmosAccount.listConnectionStrings().connectionStrings[0].connectionString
        }
        {
          name: 'CosmosDb__DatabaseName'
          value: cosmosDatabaseName
        }
        {
          name: 'BlobStorage__ConnectionString'
          value: 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};AccountKey=${storageAccount.listKeys().keys[0].value};EndpointSuffix=core.windows.net'
        }
        {
          name: 'WEBSITE_RUN_FROM_PACKAGE'
          value: '1'
        }
      ]
    }
  }
}

// ============================================================
// Azure Static Web Apps — Blazor WASM PWA
// Free tier: 100 GB bandwidth, 1 custom domain, no SLA
// Upgrade to Standard ($9/mo) when you need SLA + more staging slots
// ============================================================
resource staticWebApp 'Microsoft.Web/staticSites@2023-01-01' = {
  name: staticWebAppName
  location: location   // Static Web Apps available in centralus
  tags: tags
  sku: {
    name: 'Free'
    tier: 'Free'
  }
  properties: {
    repositoryUrl: githubRepoUrl
    branch: githubBranch
    buildProperties: {
      appLocation: '/src/ArkansasServe.Client'      // path to your Blazor WASM project
      apiLocation: '/src/ArkansasServe.Functions'   // path to your Functions project
      outputLocation: 'wwwroot'                     // Blazor WASM output folder
    }
  }
}

// ============================================================
// Outputs — values you'll need for local dev and CI/CD config
// ============================================================
output staticWebAppUrl string = 'https://${staticWebApp.properties.defaultHostname}'
output functionAppUrl string = 'https://${functionApp.properties.defaultHostName}'
output cosmosAccountName string = cosmosAccount.name
output storageAccountName string = storageAccount.name
output appInsightsConnectionString string = appInsights.properties.ConnectionString
output staticWebAppDeployToken string = staticWebApp.listSecrets().properties.apiKey
