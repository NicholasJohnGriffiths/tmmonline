# TMMOnline Umbraco Rebuild

New Umbraco 17 web application for rebuilding https://tmmonline.nz with Razor views, HTML, JavaScript, SQL Server storage, and Azure Linux hosting.

## Solution Structure

- `TMMOnline.slnx`
- `TMMOnline.Web/` - Umbraco application (.NET 10)

## Local Development

Prerequisites:

1. .NET SDK 10.x
2. SQL Server (local SQL Server, Azure SQL, or SQL Server container)

Run:

```powershell
cd d:\Dev\TMMOnline\TMMOnline.Web
dotnet run
```

First launch opens the Umbraco installer.

## SQL Server Configuration

The app uses connection key `ConnectionStrings:umbracoDbDSN`.

Set this in one of these ways:

1. `appsettings.json` (placeholder already added)
2. User Secrets for local development
3. Environment variable (recommended for production):

```text
ConnectionStrings__umbracoDbDSN=Server=<server>.database.windows.net,1433;Database=<db>;User ID=<user>;Password=<password>;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;
ConnectionStrings__umbracoDbDSN_ProviderName=Microsoft.Data.SqlClient
```

## Azure Linux Hosting

This repo includes `TMMOnline.Web/Dockerfile` for Linux container deployment.

Recommended Azure setup:

1. Create Azure SQL Database.
2. Create Azure App Service (Linux, container-based).
3. Build and push container image from `TMMOnline.Web/Dockerfile`.
4. Configure app settings:
   - `ConnectionStrings__umbracoDbDSN`
   - `ConnectionStrings__umbracoDbDSN_ProviderName=Microsoft.Data.SqlClient`
   - `ASPNETCORE_ENVIRONMENT=Production`
5. Ensure HTTPS-only is enabled on App Service.

## Content and Media Migration

Use the checklist in `docs/CONTENT-MIGRATION.md` to port pages and images from https://tmmonline.nz.

Use the inventory crawler to create migration CSV files:

```powershell
cd d:\Dev\TMMOnline
.\scripts\Invoke-LegacySiteInventory.ps1 -BaseUrl "https://tmmonline.nz" -OutputDir ".\migration-output" -MaxPages 800
```

See usage details in `docs/MIGRATION-INVENTORY-USAGE.md`.

## Umbraco Template and Document Type Scaffold

Core Razor templates are pre-created:

- `TMMOnline.Web/Views/HomePage.cshtml`
- `TMMOnline.Web/Views/SectionPage.cshtml`
- `TMMOnline.Web/Views/ArticlePage.cshtml`

Document type aliases and property blueprint are in `docs/UMBRACO-DOCUMENT-TYPES.md`.

## CI/CD to Azure Linux

GitHub Actions workflow:

- `.github/workflows/azure-linux-webapp.yml`

Setup guidance:

- `docs/AZURE-CICD.md`
- `scripts/Setup-AzureGithubOidc.ps1`

## Notes

- `Program.cs` is configured for forwarded headers, which is important behind Azure Linux reverse proxies.
- Review package vulnerability warnings during restore and upgrade Umbraco templates/packages when approved.
