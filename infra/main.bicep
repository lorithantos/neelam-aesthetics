// Neelam campaign tool: a Blazor Server app on App Service, saving to Blob Storage.
//
// One deployment serves every client. Each client has its own container, laid out the same way;
// the app keeps clients apart in code, from the Entra groups and app role in the sign-in token (README,
// "Clients and access").
//
// Access is by managed identity only. The storage account has shared-key access turned off, so
// account keys, connection strings and account SAS tokens do not work at all; the web app reaches
// storage with its system-assigned identity and RBAC roles on exactly the containers and tables
// below. The only person ever granted data access is the developer, and only on the test
// deployment, whose clients are made up (developerPrincipalId); production grants no person data access.
//
// Saves are date/time-stamped blobs and deleting one must leave no record of it, so blob
// versioning, soft delete, change feed and point-in-time restore are all off. A delete is final.

@description('Short lowercase letters-only name used to derive resource names (3-11 characters).')
@minLength(3)
@maxLength(11)
param prefix string = 'neelam'

@description('The clients this deployment serves. Each name is that client\'s container: 3-63 lowercase letters, digits and single hyphens, never "settings". Onboarding a client is adding its name here and redeploying. Set in a .bicepparam file, not defaulted.')
@minLength(1)
param clients string[]

@description('Production holds real client data. Test is the development deployment with made-up clients; only there may the prototype\'s permissive access run (the app refuses it in Production).')
@allowed([
  'Production'
  'Test'
])
param environmentName string = 'Production'

@description('The developer\'s Entra object ID, given full blob and table data access to this storage account so local runs and checks can see what the app writes. Honoured on the test deployment only: Production grants no person data access, whatever is passed here.')
param developerPrincipalId string = ''

param location string = resourceGroup().location

@description('App Service plan SKU. Blazor Server keeps a live connection per user, so Basic or above.')
param planSku string = 'B1'

var suffix = uniqueString(resourceGroup().id)

// The one client the prototype works as, on the test deployment only; empty everywhere else.
// The app refuses prototype access in Production anyway.
var prototypeClient = environmentName == 'Test' ? clients[0] : ''

// The developer's data access exists on the test deployment alone, and only when one is named.
var developerAccess = environmentName == 'Test' && !empty(developerPrincipalId)
var storageName = take(toLower('${prefix}${suffix}'), 24)

// Built-in role: Storage Blob Data Contributor (read, write, delete blobs; no keys, no account control).
var blobDataContributor = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
)

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    allowSharedKeyAccess: false // no keys: connection strings and account SAS are refused by the service
    defaultToOAuthAuthentication: true
    allowBlobPublicAccess: false
    allowCrossTenantReplication: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    accessTier: 'Hot'
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    // A deleted save must be gone, not recoverable from a version, snapshot or log.
    isVersioningEnabled: false
    deleteRetentionPolicy: {
      enabled: false
    }
    containerDeleteRetentionPolicy: {
      enabled: false
    }
    changeFeed: {
      enabled: false
    }
    restorePolicy: {
      enabled: false
    }
  }
}

// Built-in role: Storage Table Data Contributor (read, write, delete entities; no keys).
var tableDataContributor = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
)

// One container per client, laid out the same way: drafts/, templates/, catalog/, policy/.
resource clientContainers 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = [
  for client in clients: {
    parent: blobService
    name: client
    properties: {
      publicAccess: 'None'
    }
  }
]

// Each client's look, at settings/{client}/{stamp}.json: kept apart from the client containers so
// working on a look never needs access to a client's own data.
resource settingsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'settings'
  properties: {
    publicAccess: 'None'
  }
}

// Metadata: clients, support grants, approvals and dismissals, and the activity trail (one row per action,
// deletions included). Membership is Entra's, not a table's. Never an index of saves and never their
// content, so a deleted save's contents still cannot be recovered. knownItems holds each client's known
// items (treatments, benefit lines, tiers), one partition per client.
resource tableService 'Microsoft.Storage/storageAccounts/tableServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

var tableNames = [
  'clients'
  'supportGrants'
  'approvals'
  'activity'
  'knownItems'
]

resource tables 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01' = [
  for name in tableNames: {
    parent: tableService
    name: name
  }
]

// Built-in role: Monitoring Metrics Publisher (send telemetry; read nothing).
var metricsPublisher = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '3913510d-42f4-4e42-8a64-420c390055eb'
)

// Monitoring. Both accept Entra-signed telemetry only, so the connection string the site is given
// addresses Application Insights without being able to write to it. The string is filled in from
// the resource at deploy time and lives only in the site's settings, never in this repository.
// Telemetry may name a save's blob; it never holds a save's contents.
resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${prefix}-logs-${suffix}'
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
    features: {
      disableLocalAuth: true
    }
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${prefix}-insights-${suffix}'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
    DisableLocalAuth: true
  }
}

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${prefix}-plan'
  location: location
  kind: 'linux'
  sku: {
    name: planSku
  }
  properties: {
    reserved: true // Linux
  }
}

resource site 'Microsoft.Web/sites@2023-12-01' = {
  name: '${prefix}-${suffix}'
  location: location
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    clientAffinityEnabled: true // Blazor Server keeps each user on the instance holding their circuit
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      http20Enabled: true
      webSocketsEnabled: true
      alwaysOn: true
      // Addresses only. No keys, SAS or passwords: the app refuses to start if it finds one. The
      // Application Insights connection string carries none, since ingestion is Entra-only.
      // Only the test deployment names a prototype client (see prototypeClient).
      appSettings: concat([
        {
          name: 'Storage__BlobServiceUri'
          value: storage.properties.primaryEndpoints.blob
        }
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: environmentName
        }
        {
          name: 'Storage__TableServiceUri'
          value: storage.properties.primaryEndpoints.table
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: insights.properties.ConnectionString
        }
      ], empty(prototypeClient) ? [] : [
        {
          name: 'Prototype__Client'
          value: prototypeClient
        }
      ])
    }
  }
}

// Username/password publishing is another stored credential; deploy with Entra ID instead.
resource ftpPublishing 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2023-12-01' = {
  parent: site
  name: 'ftp'
  properties: {
    allow: false
  }
}

resource scmPublishing 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2023-12-01' = {
  parent: site
  name: 'scm'
  properties: {
    allow: false
  }
}

// The app's data access: blob access on each listed client container and on settings, table
// access on each table. All scoped to the container or table, never the account,
// so a container that is not in the clients list is out of the app's reach.
resource siteClientAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for (client, i) in clients: {
    name: guid(clientContainers[i].id, site.id, blobDataContributor)
    scope: clientContainers[i]
    properties: {
      roleDefinitionId: blobDataContributor
      principalId: site.identity.principalId
      principalType: 'ServicePrincipal'
    }
  }
]

resource siteSettingsAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(settingsContainer.id, site.id, blobDataContributor)
  scope: settingsContainer
  properties: {
    roleDefinitionId: blobDataContributor
    principalId: site.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource siteTableAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for (name, i) in tableNames: {
    name: guid(tables[i].id, site.id, tableDataContributor)
    scope: tables[i]
    properties: {
      roleDefinitionId: tableDataContributor
      principalId: site.identity.principalId
      principalType: 'ServicePrincipal'
    }
  }
]

// The developer's access, on the test deployment only (developerAccess): the whole account, since
// its clients are made up and development needs to read and clear everything the app writes.
resource developerBlobAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (developerAccess) {
  name: guid(storage.id, developerPrincipalId, blobDataContributor)
  scope: storage
  properties: {
    roleDefinitionId: blobDataContributor
    principalId: developerPrincipalId
    principalType: 'User'
  }
}

resource developerTableAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (developerAccess) {
  name: guid(storage.id, developerPrincipalId, tableDataContributor)
  scope: storage
  properties: {
    roleDefinitionId: tableDataContributor
    principalId: developerPrincipalId
    principalType: 'User'
  }
}

resource siteTelemetry 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(insights.id, site.id, metricsPublisher)
  scope: insights
  properties: {
    roleDefinitionId: metricsPublisher
    principalId: site.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// No keys at all, as a rule of this resource group rather than a habit of this template: built-in
// Azure Policy definitions that refuse any resource left accepting key or local authentication.
// They cover services this app does not use yet, so adding one later cannot bring keys with it.
// Deploying this needs Owner or Resource Policy Contributor on the resource group, and a new
// assignment can take up to 30 minutes to start refusing.
var denyKeyAuthPolicies = [
  { id: '8c6a50c6-9ffd-4ae7-986f-5fa6111f9a54', name: 'Storage accounts: no shared key access' }
  { id: '199d5677-e4d9-4264-9465-efe1839c06bd', name: 'Application Insights: Entra ingestion only' }
  { id: 'e15effd4-2278-4c65-a0da-4d6f6d1890e2', name: 'Log Analytics: Entra ingestion only' }
  { id: 'cfb11c26-f069-4c14-8e36-56c394dae5af', name: 'Service Bus: no local authentication' }
  { id: '5d4e3c65-4873-47be-94f3-6f8b953a3598', name: 'Event Hubs: no local authentication' }
  { id: '5450f5bd-9c72-4390-a9c4-a7aba4edfdd2', name: 'Cosmos DB: no local authentication' }
  { id: 'abda6d70-9778-44e7-84a8-06713e6db027', name: 'Azure SQL: Entra-only authentication at creation' }
  { id: 'b3a22bc9-66de-45fb-98fa-00f5df42f41a', name: 'Azure SQL: Entra-only authentication stays on' }
  { id: 'b08ab3ca-1062-4db3-8803-eec9cae605d6', name: 'App Configuration: no local authentication' }
  { id: '71ef260a-8f18-47b7-abcb-62d0673d94dc', name: 'Azure AI services: no key access' }
  { id: '6300012e-e9a4-4649-b41f-a85f5c43be91', name: 'Azure AI Search: no local authentication' }
  { id: 'ae9fb87f-8a17-4428-94a4-8135d431055c', name: 'Event Grid topics: no local authentication' }
  { id: '8bfadddb-ee1c-4639-8911-a38cb8e0b3bd', name: 'Event Grid domains: no local authentication' }
]

resource denyKeyAuth 'Microsoft.Authorization/policyAssignments@2024-04-01' = [
  for policy in denyKeyAuthPolicies: {
    name: guid(resourceGroup().id, policy.id)
    properties: {
      displayName: 'Deny - ${policy.name}'
      policyDefinitionId: tenantResourceId('Microsoft.Authorization/policyDefinitions', policy.id)
      enforcementMode: 'Default'
      parameters: {
        effect: { value: 'Deny' }
      }
    }
  }
]

// App Service has no built-in policy that can refuse basic publishing credentials, only report
// them. ftpPublishing and scmPublishing above turn them off; these make a later change show as
// non-compliant.
var auditPublishingPolicies = [
  { id: '871b205b-57cf-4e1e-a234-492616998bf7', name: 'App Service: no FTP basic authentication' }
  { id: 'aede300b-d67f-480a-ae26-4b3dfb1a1fdc', name: 'App Service: no SCM basic authentication' }
  { id: 'ec71c0bc-6a45-4b1f-9587-80dc83e6898c', name: 'App Service slots: no FTP basic authentication' }
  { id: '847ef871-e2fe-4e6e-907e-4adbf71de5cf', name: 'App Service slots: no SCM basic authentication' }
]

resource auditPublishing 'Microsoft.Authorization/policyAssignments@2024-04-01' = [
  for policy in auditPublishingPolicies: {
    name: guid(resourceGroup().id, policy.id)
    properties: {
      displayName: 'Audit - ${policy.name}'
      policyDefinitionId: tenantResourceId('Microsoft.Authorization/policyDefinitions', policy.id)
      enforcementMode: 'Default'
      parameters: {
        effect: { value: 'AuditIfNotExists' }
      }
    }
  }
]

output siteName string = site.name
output siteUrl string = 'https://${site.properties.defaultHostName}'
output blobServiceUri string = storage.properties.primaryEndpoints.blob
output tableServiceUri string = storage.properties.primaryEndpoints.table

@description('The client the prototype works as, written to the site as Prototype__Client; empty outside the test deployment.')
output prototypeClient string = prototypeClient
