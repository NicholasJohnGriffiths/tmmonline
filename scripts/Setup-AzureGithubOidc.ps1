param(
    [Parameter(Mandatory = $true)]
    [string]$SubscriptionId,

    [Parameter(Mandatory = $true)]
    [string]$ResourceGroup,

    [Parameter(Mandatory = $true)]
    [string]$GithubOrg,

    [Parameter(Mandatory = $true)]
    [string]$GithubRepo,

    [string]$AppName = "gh-tmmonline-deploy",
    [string]$Branch = "main"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Write-Host "Selecting subscription..."
az account set --subscription $SubscriptionId | Out-Null

Write-Host "Ensuring AAD app exists..."
$appId = az ad app list --display-name $AppName --query "[0].appId" -o tsv

if ([string]::IsNullOrWhiteSpace($appId)) {
    $appId = az ad app create --display-name $AppName --query appId -o tsv
    az ad sp create --id $appId | Out-Null
}

$spObjectId = az ad sp show --id $appId --query id -o tsv

Write-Host "Assigning Contributor role on resource group scope..."
$scope = az group show --name $ResourceGroup --query id -o tsv

# This may already exist; ignore failures for duplicate assignment.
try {
    az role assignment create --assignee-object-id $spObjectId --assignee-principal-type ServicePrincipal --role Contributor --scope $scope | Out-Null
}
catch {
    Write-Host "Role assignment already exists or could not be created: $($_.Exception.Message)"
}

$federatedName = "github-$GithubOrg-$GithubRepo-$Branch"
$federatedSubject = "repo:${GithubOrg}/${GithubRepo}:ref:refs/heads/${Branch}"

Write-Host "Creating or updating federated credential..."
$payload = @{
    name = $federatedName
    issuer = "https://token.actions.githubusercontent.com"
    subject = $federatedSubject
    audiences = @("api://AzureADTokenExchange")
} | ConvertTo-Json -Depth 5

$tempFile = New-TemporaryFile
$payload | Out-File -FilePath $tempFile -Encoding utf8

try {
    az ad app federated-credential create --id $appId --parameters "@$tempFile" | Out-Null
}
catch {
    Write-Host "Federated credential may already exist. Continuing."
}

Remove-Item $tempFile -Force

$tenantId = az account show --query tenantId -o tsv

Write-Host "Setup complete. Configure these GitHub secrets:"
Write-Host "AZURE_CLIENT_ID=$appId"
Write-Host "AZURE_TENANT_ID=$tenantId"
Write-Host "AZURE_SUBSCRIPTION_ID=$SubscriptionId"

Write-Host "Also set repository variables and ACR/DB secrets per docs/AZURE-CICD.md"
