// One web app on the deployment's plan: the Blazor Server app with its own system-assigned identity.
// main.bicep declares the client site from this definition and, on the test deployment, the staging
// site beside it on the same plan, so the two can never differ in a security setting.
//
// The site reaches its own storage account only (storage.bicep gives its identity the data roles
// there) and sends telemetry to the deployment's Application Insights.
//
// Everything that decides a resource's id must be a deployment-time value, so what-if can look
// inside this module.

@description('The web app\'s name.')
param name string

param location string

@description('The App Service plan the site runs on.')
param planId string

@description('Production holds real client data. Test is the development deployment with made-up clients; only there may the prototype\'s permissive access run (the app refuses it in Production).')
@allowed([
  'Production'
  'Test'
])
param environmentName string

@description('The one client the prototype works as, written as Prototype__Client; empty for none. main.bicep names one on the test deployment only.')
param prototypeClient string = ''

@description('The name of this site\'s own storage account (storage.bicep). Its endpoints follow from the name, so the site need not wait for the account, whose roles wait for the site.')
param storageName string

@description('The deployment\'s Application Insights, which this site reports to.')
param insightsName string

// Built-in role: Monitoring Metrics Publisher (send telemetry; read nothing).
var metricsPublisher = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '3913510d-42f4-4e42-8a64-420c390055eb'
)

// Both accept Entra-signed telemetry only, so the connection string the site is given addresses
// Application Insights without being able to write to it. The string is filled in from the resource
// at deploy time and lives only in the site's settings, never in this repository.
resource insights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: insightsName
}

resource site 'Microsoft.Web/sites@2023-12-01' = {
  name: name
  location: location
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: planId
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
          value: 'https://${storageName}.blob.${environment().suffixes.storage}/'
        }
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: environmentName
        }
        {
          name: 'Storage__TableServiceUri'
          value: 'https://${storageName}.table.${environment().suffixes.storage}/'
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

resource siteTelemetry 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(insights.id, site.id, metricsPublisher)
  scope: insights
  properties: {
    roleDefinitionId: metricsPublisher
    principalId: site.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

output principalId string = site.identity.principalId
output url string = 'https://${site.properties.defaultHostName}'
