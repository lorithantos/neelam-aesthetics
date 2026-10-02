// Neelam campaign tool: a Blazor Server app on App Service, saving to Blob Storage.
//
// Access is by managed identity only. The storage account has shared-key access turned off, so
// account keys, connection strings and account SAS tokens do not work at all; the web app reaches
// storage with its system-assigned identity and an RBAC role on one container.
//
// Saves are date/time-stamped blobs and deleting one must leave no record of it, so blob
// versioning, soft delete, change feed and point-in-time restore are all off. A delete is final.

@description('Short lowercase letters-only name used to derive resource names (3-11 characters).')
@minLength(3)
@maxLength(11)
param prefix string = 'neelam'

param location string = resourceGroup().location

@description('App Service plan SKU. Blazor Server keeps a live connection per user, so Basic or above.')
param planSku string = 'B1'

@description('Optional: object ID of a person or group to grant the same blob access for local development with their own sign-in (az login). Leave empty in production.')
param developerPrincipalId string = ''

var suffix = uniqueString(resourceGroup().id)
var storageName = take(toLower('${prefix}${suffix}'), 24)
var containerName = 'campaigns'

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

resource container 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: containerName
  properties: {
    publicAccess: 'None'
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
      linuxFxVersion: 'DOTNETCORE|8.0'
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      http20Enabled: true
      webSocketsEnabled: true
      alwaysOn: true
      // Addresses only. No connection strings: the app refuses to start if it finds one.
      appSettings: [
        {
          name: 'Storage__BlobServiceUri'
          value: storage.properties.primaryEndpoints.blob
        }
        {
          name: 'Storage__Container'
          value: containerName
        }
      ]
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

resource siteBlobAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(container.id, site.id, blobDataContributor)
  scope: container
  properties: {
    roleDefinitionId: blobDataContributor
    principalId: site.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource developerBlobAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(developerPrincipalId)) {
  name: guid(container.id, developerPrincipalId, blobDataContributor)
  scope: container
  properties: {
    roleDefinitionId: blobDataContributor
    principalId: developerPrincipalId
  }
}

output siteName string = site.name
output siteUrl string = 'https://${site.properties.defaultHostName}'
output blobServiceUri string = storage.properties.primaryEndpoints.blob
output containerName string = containerName
