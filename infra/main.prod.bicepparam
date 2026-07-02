using './main.bicep'

// Entra External ID (CIAM: SalineServe.onmicrosoft.com). Public identifiers, not secrets.
param entraTenantId = '2d72a425-cd59-4b55-a8d6-a67c1ed565c6'
param entraClientId = '16150d6e-7d28-4c6b-91b3-4ec839fff75f'
param entraAudience = 'api://16150d6e-7d28-4c6b-91b3-4ec839fff75f'

// All other parameters default to the verified live resource names in main.bicep.
