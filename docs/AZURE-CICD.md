# Azure Linux CI/CD (GitHub Actions)

Workflow file:

- .github/workflows/azure-linux-webapp.yml

## What it does

1. Logs into Azure using GitHub OIDC.
2. Builds and pushes the container image to Azure Container Registry (ACR).
3. Updates Azure Linux Web App container image.
4. Applies required production app settings (including SQL Server DSN).
5. Restarts the app.

## Required GitHub repository variables

- ACR_NAME
- ACR_LOGIN_SERVER (example: myregistry.azurecr.io)
- AZURE_RESOURCE_GROUP
- AZURE_WEBAPP_NAME

## Required GitHub repository secrets

- AZURE_CLIENT_ID
- AZURE_TENANT_ID
- AZURE_SUBSCRIPTION_ID
- ACR_USERNAME
- ACR_PASSWORD
- UMBRACO_DB_DSN

## One-time Azure setup

Use scripts/Setup-AzureGithubOidc.ps1 to create/update the Azure AD application and federated credential for this repo.

## Notes

- This workflow assumes your App Service is Linux container-based.
- The SQL Server connection string is injected as an app setting from GitHub secrets.
- If you use managed identity for ACR pull, you can remove ACR username/password steps later.
