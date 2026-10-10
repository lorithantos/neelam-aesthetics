// Neelam campaign tool: a Blazor Server app on App Service, saving to Blob Storage.
//
// One deployment serves every client. Each client has its own container, laid out the same way;
// the app keeps clients apart in code, from the Entra groups and app role in the sign-in token (README,
// "Clients and access").
//
// Access is by managed identity only. The storage account has shared-key access turned off, so
// account keys, connection strings and account SAS tokens do not work at all; the web app reaches
// storage with its system-assigned identity and RBAC roles on exactly its containers and tables
// (storage.bicep). The only person ever granted data access is the developer, and only on the test
// deployment, whose clients are made up (developerPrincipalId); production grants no person data access.
//
// Saves are date/time-stamped blobs and deleting one must leave no record of it, so blob
// versioning, soft delete, change feed and point-in-time restore are all off. A delete is final.
//
// The test deployment also has a staging site (stagingClients): a second web app on the same plan,
// with its own identity and its own storage account, where each build is tried before the client's
// site gets it. Sites and accounts each come from one definition (site.bicep, storage.bicep), so
// staging cannot differ from the client's site in a security setting, and its identity holds no
// role on the client's account.

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

@description('The developer\'s Entra object ID, given full blob and table data access to this storage account so local runs and checks can see what the app writes, and Key Vault Secrets Officer on the vault to add and rotate the proofread\'s key. Honoured on the test deployment only: Production grants no person data access, whatever is passed here.')
param developerPrincipalId string = ''

@description('The clients of the staging site, which tries each build before the client\'s site gets it; empty for no staging site. Honoured on the test deployment only: Production gets no staging site, whatever is passed here. Its first client is the one its prototype works as.')
param stagingClients string[] = []

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
var siteName = '${prefix}-${suffix}'

// The staging site exists on the test deployment alone, and only when it names clients. It has its
// own account, so its identity, its undo sweep and the build on trial never touch the client's data:
// table roles cannot be scoped to a partition, so a shared account would let it read the client's rows.
var staging = environmentName == 'Test' && !empty(stagingClients)
var stagingStorageName = take(toLower('${prefix}stg${suffix}'), 24)
var stagingSiteName = '${prefix}-staging-${suffix}'
var stagingPrototypeClient = stagingClients[?0] ?? ''

// The developer's access, the same on each account, and the key's setter on the vault; empty
// outside the test deployment.
var developerOnAccounts = developerAccess ? developerPrincipalId : ''

// The AI proofread's key (owner, 2026-10-09: "For now I will donate my credits"): the one secret this
// app may be given, the Anthropic API key, kept in this vault and nowhere else. The owner sets the
// secret with the CLI (WIP.md); this template never holds a value. Each site gets it as an App Service
// Key Vault reference, which App Service resolves with that site's own identity, and CredentialGuard
// lets it into that one setting alone. Both sites read the one secret: one key, one bill, one cap.
var vaultName = take('${prefix}-kv-${suffix}', 24)
var proofreadSecretName = 'anthropic-api-key'
var proofreadKeyUri = 'https://${vaultName}${environment().suffixes.keyvaultDns}/secrets/${proofreadSecretName}/'

// The metadata tables, in every account: clients, support grants, approvals and dismissals, and the
// activity trail (one row per action, deletions included). Membership is Entra's, not a table's. Never
// an index of saves and never their content, so a deleted save's contents still cannot be recovered.
// knownItems holds each client's known items (treatments, benefit lines, tiers), one partition per client.
var tableNames = [
  'clients'
  'supportGrants'
  'approvals'
  'activity'
  'knownItems'
]

// Monitoring. Both accept Entra-signed telemetry only, so the connection string the site is given
// addresses Application Insights without being able to write to it. The string is filled in from
// the resource at deploy time and lives only in the site's settings, never in this repository.
// Telemetry may name a save's blob; it never holds a save's contents. The staging site reports here
// too: telemetry carries ids only, and each request is marked with the site it came from.
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

// The vault and who reads it: each site's own identity, Key Vault Secrets User on this vault alone
// (vault.bicep). The staging site reads the same secret as the client's site. Who sets it: the
// developer, Key Vault Secrets Officer on this vault alone, on the test deployment only; production
// names no one until the owner decides who (WIP.md, open decisions).
module vault 'vault.bicep' = {
  name: 'vault'
  params: {
    name: vaultName
    location: location
    readerSiteName: siteName
    readerPrincipalId: site.outputs.principalId
    stagingSiteName: staging ? stagingSiteName : ''
    stagingPrincipalId: staging ? stagingSite!.outputs.principalId : ''
    keyOfficerPrincipalId: developerOnAccounts
  }
}

// The client's site and its account. Their names and resource ids are the ones they had before they
// moved into site.bicep and storage.bicep, so the move changes nothing that is deployed.
module site 'site.bicep' = {
  name: 'site'
  params: {
    name: siteName
    location: location
    planId: plan.id
    environmentName: environmentName
    prototypeClient: prototypeClient
    storageName: storageName
    insightsName: insights.name
    proofreadKeyUri: proofreadKeyUri
  }
}

module storage 'storage.bicep' = {
  name: 'storage'
  params: {
    name: storageName
    location: location
    clients: clients
    tableNames: tableNames
    appId: resourceId('Microsoft.Web/sites', siteName)
    appPrincipalId: site.outputs.principalId
    developerPrincipalId: developerOnAccounts
  }
}

// The staging site and its own account, on the same plan: the test deployment only (staging).
module stagingSite 'site.bicep' = if (staging) {
  name: 'stagingSite'
  params: {
    name: stagingSiteName
    location: location
    planId: plan.id
    environmentName: environmentName
    prototypeClient: stagingPrototypeClient
    storageName: stagingStorageName
    insightsName: insights.name
    proofreadKeyUri: proofreadKeyUri
  }
}

module stagingStorage 'storage.bicep' = if (staging) {
  name: 'stagingStorage'
  params: {
    name: stagingStorageName
    location: location
    clients: stagingClients
    tableNames: tableNames
    appId: resourceId('Microsoft.Web/sites', stagingSiteName)
    appPrincipalId: stagingSite!.outputs.principalId
    developerPrincipalId: developerOnAccounts
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
// them. ftpPublishing and scmPublishing in site.bicep turn them off; these make a later change show as
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

output siteName string = siteName
output siteUrl string = site.outputs.url
output blobServiceUri string = storage.outputs.blobServiceUri
output tableServiceUri string = storage.outputs.tableServiceUri

@description('The client the prototype works as, written to the site as Prototype__Client; empty outside the test deployment.')
output prototypeClient string = prototypeClient

@description('The staging site\'s name; empty where there is none.')
output stagingSiteName string = staging ? stagingSiteName : ''

@description('The vault holding the AI proofread\'s key: the owner sets the secret anthropic-api-key there.')
output vaultName string = vaultName
