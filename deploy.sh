#!/bin/bash
# ============================================================
# Arkansas Serve — Azure Setup Script
# Run this once, in order, from a machine with:
#   - Azure CLI installed (az --version)
#   - Bicep CLI installed (az bicep version) or auto-installs
# ============================================================

set -e   # stop on any error

# ---- 1. LOGIN & SET SUBSCRIPTION ----
echo ">>> Logging in to Azure..."
az login

# List subscriptions and confirm you're on the right one
az account list --output table

# If you have more than one subscription, set the right one:
# az account set --subscription "YOUR_SUBSCRIPTION_NAME_OR_ID"


# ---- 2. REGISTER REQUIRED PROVIDERS ----
# These are idempotent — safe to run even if already registered
echo ">>> Registering resource providers..."
az provider register --namespace Microsoft.DocumentDB   --wait
az provider register --namespace Microsoft.Web          --wait
az provider register --namespace Microsoft.Storage      --wait
az provider register --namespace Microsoft.Insights     --wait
az provider register --namespace Microsoft.OperationalInsights --wait


# ---- 3. CREATE THE RESOURCE GROUP ----
echo ">>> Creating resource group..."
az group create \
  --name rg-arkansas-serve \
  --location centralus \
  --tags project=arkansas-serve managedBy=bicep

# Verify it exists
az group show --name rg-arkansas-serve --output table


# ---- 4. VALIDATE THE BICEP TEMPLATE (dry run — no resources created) ----
echo ">>> Validating Bicep template..."
az deployment group validate \
  --resource-group rg-arkansas-serve \
  --template-file main.bicep \
  --parameters @params.json

echo ">>> Validation passed."


# ---- 5. PREVIEW CHANGES (what will be created) ----
# This is a "what-if" — shows you exactly what Azure will create/modify
echo ">>> Running what-if preview..."
az deployment group what-if \
  --resource-group rg-arkansas-serve \
  --template-file main.bicep \
  --parameters @params.json


# ---- 6. DEPLOY ----
# Review the what-if output above before running this step.
echo ">>> Deploying resources (this takes 3–5 minutes)..."
az deployment group create \
  --resource-group rg-arkansas-serve \
  --template-file main.bicep \
  --parameters @params.json \
  --name "arkansas-serve-initial-deploy" \
  --output table

echo ">>> Deployment complete."


# ---- 7. CAPTURE OUTPUTS ----
# Save the output values you'll need for local dev and GitHub secrets
echo ">>> Capturing deployment outputs..."
az deployment group show \
  --resource-group rg-arkansas-serve \
  --name "arkansas-serve-initial-deploy" \
  --query properties.outputs \
  --output json > ./deployment-outputs.json

echo ">>> Outputs saved to deployment-outputs.json"
echo ""
echo "IMPORTANT: deployment-outputs.json contains secrets."
echo "Add it to .gitignore immediately — do NOT commit it."
cat ./deployment-outputs.json


# ---- 8. SET UP GITHUB SECRETS FOR CI/CD ----
# The Static Web App deploy token (from deployment-outputs.json) goes in GitHub:
# GitHub repo → Settings → Secrets and variables → Actions → New repository secret
# Name:  AZURE_STATIC_WEB_APPS_API_TOKEN
# Value: (staticWebAppDeployToken value from deployment-outputs.json)
#
# When Static Web Apps is linked to your GitHub repo, Azure automatically
# adds a GitHub Actions workflow file to your repo that builds and deploys
# on every push to main.
echo ""
echo ">>> Next step: add the staticWebAppDeployToken to GitHub Actions secrets."
echo "    See README.md for instructions."


# ---- 9. SET UP MICROSOFT ENTRA EXTERNAL ID ----
# Entra External ID cannot be fully provisioned via Bicep/ARM yet —
# you need the portal for the initial external tenant setup.
# Steps:
#   1. Go to portal.azure.com → Microsoft Entra ID → Overview → Manage tenants
#   2. Click "Create" → choose "External" tenant type
#   3. Name it e.g. "Arkansas Serve External" in region Central US
#   4. Link it to your Azure subscription
#   5. Add identity providers:
#      - Microsoft accounts (covers school M365/Entra tenants)
#      - Google (covers Google Workspace for Education)
#   6. Note the Tenant ID and set it in your Function App config:
#      az functionapp config appsettings set \
#        --resource-group rg-arkansas-serve \
#        --name func-arkansas-serve-arksrv \
#        --settings "EntraExternalId__TenantId=YOUR_EXTERNAL_TENANT_ID"
#      Then add ClientId and ClientSecret after registering your app in Entra.

echo ""
echo ">>> All done. See comments in this script for the Entra External ID setup steps."
