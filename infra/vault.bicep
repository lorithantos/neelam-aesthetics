// The deployment's Key Vault, for the one secret the app may be given (owner, 2026-10-09: "For now I
// will donate my credits"): the AI proofread's Anthropic API key, until the proofread moves to
// Microsoft Foundry and the site's own identity. The owner sets the secret with the CLI (WIP.md); no
// template ever holds its value. Each site gets the key only as an App Service Key Vault reference
// (site.bicep), which App Service resolves with that site's identity, and CredentialGuard lets it
// into that one setting alone.
//
// Access by Azure RBAC only, no access policies; each reader holds Key Vault Secrets User here and
// nothing more. The person who sets the key holds Key Vault Secrets Officer here (below); this
// template grants no one else anything here. Purge protection is on, so a deleted secret cannot be
// purged early nor the name taken over. Soft delete is always on for a vault and cannot be turned
// off: it keeps a deleted secret for the retention period. The vault holds the key only, never client data. Public network access as the
// deployment's other resources.
//
// Everything that decides a resource's id is a deployment-time value, so what-if can look inside.

@description('The vault\'s name: 3-24 letters, digits and hyphens.')
param name string

param location string

@description('The sites that read the key, by name (deployment-time, for the role assignments\' ids) and identity.')
param readerSiteName string

param readerPrincipalId string

@description('The staging site, when there is one; empty names for none.')
param stagingSiteName string = ''

param stagingPrincipalId string = ''

@description('The person who adds and rotates the key, by Entra object ID; empty grants no one.')
param keyOfficerPrincipalId string = ''

// Built-in role: Key Vault Secrets User (read secret values; nothing else).
var keyVaultSecretsUser = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '4633458b-17de-408a-b874-0445c86b69e6'
)

// Built-in role: Key Vault Secrets Officer, every action on the vault's secrets (secrets/*) but none
// on who may use them. It is the smallest built-in role that can set a secret, so add the key or
// rotate it (a new version), and it is more than that: it can also read the key and delete it. Purge
// protection still stops a deleted key being purged early. No custom role: one person, one vault.
var keyVaultSecretsOfficer = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
)

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: name
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    accessPolicies: []
    enablePurgeProtection: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enabledForDeployment: false
    enabledForDiskEncryption: false
    enabledForTemplateDeployment: false
    publicNetworkAccess: 'Enabled'
  }
}

resource siteReadsKey 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, readerSiteName, keyVaultSecretsUser)
  scope: vault
  properties: {
    roleDefinitionId: keyVaultSecretsUser
    principalId: readerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource stagingReadsKey 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(stagingSiteName)) {
  name: guid(vault.id, stagingSiteName, keyVaultSecretsUser)
  scope: vault
  properties: {
    roleDefinitionId: keyVaultSecretsUser
    principalId: stagingPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// The person who adds and rotates the key (owner, 2026-10-10: "I should be allowed to add a key and
// to rotate one"), on this vault alone; no one where none is named. Who holds it in production is
// the owner's open decision (WIP.md).
resource officerSetsKey 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(keyOfficerPrincipalId)) {
  name: guid(vault.id, keyOfficerPrincipalId, keyVaultSecretsOfficer)
  scope: vault
  properties: {
    roleDefinitionId: keyVaultSecretsOfficer
    principalId: keyOfficerPrincipalId
    principalType: 'User'
  }
}
