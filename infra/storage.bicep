// One site's storage account: a container per client, the settings container, the metadata tables,
// and the data roles of the one site that uses it. main.bicep declares the client site's account and,
// on the test deployment, the staging site's account from this same definition, so the two can never
// differ in a security setting. Each account belongs to one site: a site's identity holds data roles
// on its own account and nothing on any other.
//
// Access is by managed identity only. The account has shared-key access turned off, so account keys,
// connection strings and account SAS tokens do not work at all; the site reaches storage with its
// system-assigned identity and RBAC roles on exactly the containers and tables below.
//
// Saves are date/time-stamped blobs and deleting one must leave no record of it, so blob versioning,
// soft delete, change feed and point-in-time restore are all off. A delete is final.
//
// Everything that decides a resource's id (names, the clients and tables lists, the developer
// condition) must be a deployment-time value: a runtime value there would stop what-if from looking
// inside this module. The site's principal id is runtime, and is only ever a property.

@description('The storage account\'s name.')
param name string

param location string

@description('The clients whose containers this account holds. Each name is that client\'s container.')
param clients string[]

@description('The metadata tables, each with the site\'s table role scoped to it.')
param tableNames string[]

@description('The resource id of the one site that uses this account. Names its role assignments.')
param appId string

@description('That site\'s system-assigned identity, given data roles on each container and table.')
param appPrincipalId string

@description('The developer\'s Entra object ID, given blob and table data access to the whole account; empty for none. main.bicep passes it on the test deployment only.')
param developerPrincipalId string = ''

// Built-in role: Storage Blob Data Contributor (read, write, delete blobs; no keys, no account control).
var blobDataContributor = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
)

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: name
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

resource tables 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01' = [
  for name in tableNames: {
    parent: tableService
    name: name
  }
]

// The site's data access: blob access on each listed client container and on settings, table
// access on each table. All scoped to the container or table, never the account,
// so a container that is not in the clients list is out of the site's reach.
resource siteClientAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for (client, i) in clients: {
    name: guid(clientContainers[i].id, appId, blobDataContributor)
    scope: clientContainers[i]
    properties: {
      roleDefinitionId: blobDataContributor
      principalId: appPrincipalId
      principalType: 'ServicePrincipal'
    }
  }
]

resource siteSettingsAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(settingsContainer.id, appId, blobDataContributor)
  scope: settingsContainer
  properties: {
    roleDefinitionId: blobDataContributor
    principalId: appPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource siteTableAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for (name, i) in tableNames: {
    name: guid(tables[i].id, appId, tableDataContributor)
    scope: tables[i]
    properties: {
      roleDefinitionId: tableDataContributor
      principalId: appPrincipalId
      principalType: 'ServicePrincipal'
    }
  }
]

// The developer's access, when main.bicep names one (the test deployment only): the whole account,
// since its clients are made up and development needs to read and clear everything the app writes.
resource developerBlobAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(developerPrincipalId)) {
  name: guid(storage.id, developerPrincipalId, blobDataContributor)
  scope: storage
  properties: {
    roleDefinitionId: blobDataContributor
    principalId: developerPrincipalId
    principalType: 'User'
  }
}

resource developerTableAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(developerPrincipalId)) {
  name: guid(storage.id, developerPrincipalId, tableDataContributor)
  scope: storage
  properties: {
    roleDefinitionId: tableDataContributor
    principalId: developerPrincipalId
    principalType: 'User'
  }
}

output blobServiceUri string = storage.properties.primaryEndpoints.blob
output tableServiceUri string = storage.properties.primaryEndpoints.table
