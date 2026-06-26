targetScope = 'resourceGroup'

@description('Deployment location for all resources.')
param location string = resourceGroup().location

@description('Short environment suffix (for example: dev, test, prod).')
param environmentName string = 'prod'

@description('Static Web App name.')
param staticWebAppName string

@description('Globally unique Cosmos DB account name.')
param cosmosAccountName string

@description('Cosmos DB database name used by the Functions app.')
param cosmosDatabaseName string = 'arkansas-serve-db'

@description('Globally unique Storage account name.')
param storageAccountName string

@description('Application Insights resource name.')
param appInsightsName string = 'ai-arkserve-${environmentName}'

@description('Log Analytics workspace resource name.')
param logAnalyticsWorkspaceName string = 'log-arkserve-${environmentName}'

@description('Static Web App SKU tier.')
@allowed([
  'Free'
  'Standard'
])
param staticWebAppSku string = 'Standard'

@description('Microsoft Entra tenant ID used for token validation.')
param entraTenantId string

@description('Microsoft Entra app registration client ID expected by the API.')
param entraClientId string

@description('Expected audience claim for access tokens.')
param entraAudience string

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
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

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

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  name: 'default'
  parent: storage
}

resource eventPhotosContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  name: 'event-photos'
  parent: blobService
  properties: {
    publicAccess: 'Blob'
  }
}

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' = {
  name: cosmosAccountName
  location: location
  kind: 'GlobalDocumentDB'
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
    capabilities: [
      {
        name: 'EnableServerless'
      }
    ]
    enableAutomaticFailover: false
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

var cosmosContainerSpecs = [
  {
    name: 'tenants'
    partitionKeyPath: '/id'
  }
  {
    name: 'users'
    partitionKeyPath: '/tenantId'
  }
  {
    name: 'events'
    partitionKeyPath: '/organizationId'
  }
  {
    name: 'registrations'
    partitionKeyPath: '/eventId'
  }
  {
    name: 'serviceLogs'
    partitionKeyPath: '/studentId'
  }
  {
    name: 'pendingApprovals'
    partitionKeyPath: '/schoolId'
  }
  {
    name: 'notifications'
    partitionKeyPath: '/userId'
  }
]

resource cosmosContainers 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = [for containerSpec in cosmosContainerSpecs: {
  name: containerSpec.name
  parent: cosmosDb
  properties: {
    resource: {
      id: containerSpec.name
      partitionKey: {
        paths: [
          containerSpec.partitionKeyPath
        ]
        kind: 'Hash'
      }
    }
  }
}]

resource staticWebApp 'Microsoft.Web/staticSites@2024-04-01' = {
  name: staticWebAppName
  location: location
  sku: {
    name: staticWebAppSku
    tier: staticWebAppSku
  }
  properties: {}
}

var cosmosConnectionString = cosmos.listConnectionStrings().connectionStrings[0].connectionString
var storageAccountKey = storage.listKeys().keys[0].value
var storageConnectionString = 'DefaultEndpointsProtocol=https;AccountName=${storage.name};AccountKey=${storageAccountKey};EndpointSuffix=${environment().suffixes.storage}'

resource staticWebAppAppSettings 'Microsoft.Web/staticSites/config@2024-04-01' = {
  name: 'appsettings'
  parent: staticWebApp
  properties: {
    CosmosDb__ConnectionString: cosmosConnectionString
    CosmosDb__DatabaseName: cosmosDatabaseName
    BlobStorage__ConnectionString: storageConnectionString
    Entra__TenantId: entraTenantId
    Entra__ClientId: entraClientId
    Entra__Audience: entraAudience
    APPLICATIONINSIGHTS_CONNECTION_STRING: appInsights.properties.ConnectionString
  }
}

output staticWebAppDefaultHostname string = staticWebApp.properties.defaultHostname
output appInsightsConnectionString string = appInsights.properties.ConnectionString
output cosmosEndpoint string = cosmos.properties.documentEndpoint
output storageAccountId string = storage.id
