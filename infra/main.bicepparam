// Production: the real clients. Onboarding a client is adding its name here and redeploying,
// then adding its row to the clients table.
using 'main.bicep'

param clients = [
  'neelam-aesthetics'
]
