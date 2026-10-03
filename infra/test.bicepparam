// The test deployment for development: a separate resource group, made-up clients, no client data.
using 'main.bicep'

param prefix = 'neelamtest'

param clients = [
  'test-salon-one'
  'test-salon-two'
]
