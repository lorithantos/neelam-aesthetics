// The test deployment for development: a separate resource group, made-up clients, no client data.
using 'main.bicep'

param prefix = 'neelamtest'

// The only deployment where the prototype's permissive access may run.
param environmentName = 'Test'

// The developer's Entra object ID (az ad signed-in-user show --query id): full data access to
// this deployment's storage. main.bicepparam never sets it, and the template ignores it outside Test.
param developerPrincipalId = 'b08ab8d5-40dd-43f5-b1a4-a9c386572dee'

param clients = [
  'test-salon-one'
  'test-salon-two'
]
