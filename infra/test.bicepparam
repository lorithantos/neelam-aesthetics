// The test deployment for development: a separate resource group, made-up clients, no client data.
using 'main.bicep'

param prefix = 'neelamtest'

// The only deployment where the prototype's permissive access may run.
param environmentName = 'Test'

param clients = [
  'test-salon-one'
  'test-salon-two'
]
