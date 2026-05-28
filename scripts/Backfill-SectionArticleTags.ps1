Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Set-Location "$PSScriptRoot\..\TMMOnline.Web"

$secretLines = dotnet user-secrets list | Out-String
$conn = $null
foreach ($line in ($secretLines -split [Environment]::NewLine)) {
    if ($line -match '^ConnectionStrings:umbracoDbDSN\s*=\s*(.+)$') {
        $conn = $Matches[1].Trim()
        break
    }
}

if ([string]::IsNullOrWhiteSpace($conn)) {
    throw 'No user secret connection string found'
}

function Get-TargetSectionSlug {
    param([string]$LegacyUrl)

    try {
        $resp = Invoke-WebRequest -UseBasicParsing -Uri $LegacyUrl -TimeoutSec 45
        $html = [string]$resp.Content
    }
    catch {
        return $null
    }

    $patterns = @(
        '<div[^>]*class="[^"]*article-topic[^"]*"[^>]*>.*?<a[^>]*href="(?<href>[^"]+)"',
        "<div[^>]*class='[^']*article-topic[^']*'[^>]*>.*?<a[^>]*href='(?<href>[^']+)'"
    )

    $href = $null
    foreach ($pattern in $patterns) {
        $match = [regex]::Match(
            $html,
            $pattern,
            [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::Singleline)
        if ($match.Success) {
            $href = $match.Groups['href'].Value
            break
        }
    }

    if ([string]::IsNullOrWhiteSpace($href)) {
        return $null
    }

    try {
        $resolved = [Uri]::new([Uri]::new('https://tmmonline.nz'), $href)
        $segments = $resolved.AbsolutePath.Trim('/').Split('/', [System.StringSplitOptions]::RemoveEmptyEntries)
        if ($segments.Length -eq 0) {
            return $null
        }

        switch ($segments[0].ToLowerInvariant()) {
            'better-business' { return 'conference' }
            'conference' { return 'conference' }
            'people' { return 'people' }
            'property-news' { return 'property-news' }
            'news-bites' { return 'news-bites' }
            default { return $null }
        }
    }
    catch {
        return $null
    }
}

$query = @"
SELECT DISTINCT
        n.id AS NodeId,
        n.text AS NodeName,
        COALESCE(CAST(pd.textValue AS nvarchar(max)), CAST(pd.varcharValue AS nvarchar(max))) AS LegacySourceUrl
FROM umbracoNode n
INNER JOIN umbracoContent c ON c.nodeId = n.id
INNER JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId
INNER JOIN umbracoContentVersion cv ON cv.nodeId = n.id
INNER JOIN umbracoPropertyData pd ON pd.versionId = cv.id
INNER JOIN cmsPropertyType pt ON pt.id = pd.propertyTypeId
WHERE ct.alias = 'articlePage'
    AND pt.alias = 'legacySourceUrl'
    AND COALESCE(CAST(pd.textValue AS nvarchar(max)), CAST(pd.varcharValue AS nvarchar(max))) IS NOT NULL
    AND COALESCE(CAST(pd.textValue AS nvarchar(max)), CAST(pd.varcharValue AS nvarchar(max))) <> '';
"@

$cn = [System.Data.SqlClient.SqlConnection]::new($conn)
$cn.Open()
$cmd = $cn.CreateCommand()
$cmd.CommandText = $query
$cmd.CommandTimeout = 120
$r = $cmd.ExecuteReader()
$articles = New-Object System.Collections.Generic.List[object]
while ($r.Read()) {
    $articles.Add([pscustomobject]@{
        NodeId = [int]$r['NodeId']
        NodeName = $r['NodeName'].ToString()
        LegacySourceUrl = $r['LegacySourceUrl'].ToString()
    }) | Out-Null
}
$r.Close()

$updates = New-Object System.Collections.Generic.List[object]
foreach ($article in $articles) {
    $sectionSlug = Get-TargetSectionSlug -LegacyUrl $article.LegacySourceUrl
    if ([string]::IsNullOrWhiteSpace($sectionSlug)) {
        continue
    }

    $updates.Add([pscustomobject]@{
        NodeId = $article.NodeId
        NodeName = $article.NodeName
        Tags = $sectionSlug
    }) | Out-Null
}

$updates = $updates |
    Group-Object NodeId |
    ForEach-Object { $_.Group | Select-Object -First 1 }

$tx = $cn.BeginTransaction()
try {
    foreach ($update in $updates) {
        $updateCmd = $cn.CreateCommand()
        $updateCmd.Transaction = $tx
        $updateCmd.CommandTimeout = 120
        $updateCmd.CommandText = @"
UPDATE pd
SET pd.textValue = @tags,
    pd.varcharValue = NULL,
    pd.decimalValue = NULL,
    pd.intValue = NULL,
    pd.dateValue = NULL
FROM umbracoPropertyData pd
INNER JOIN cmsPropertyType pt ON pt.id = pd.propertyTypeId
INNER JOIN umbracoContentVersion cv ON cv.id = pd.versionId
WHERE pt.alias = 'articleTags'
  AND cv.nodeId = @nodeId;
"@
        [void]$updateCmd.Parameters.Add((New-Object System.Data.SqlClient.SqlParameter('@tags', [System.Data.SqlDbType]::NVarChar, -1)))
        $updateCmd.Parameters['@tags'].Value = $update.Tags
        [void]$updateCmd.Parameters.Add((New-Object System.Data.SqlClient.SqlParameter('@nodeId', [System.Data.SqlDbType]::Int)))
        $updateCmd.Parameters['@nodeId'].Value = $update.NodeId
        [void]$updateCmd.ExecuteNonQuery()
    }

    $tx.Commit()
}
catch {
    $tx.Rollback()
    throw
}
finally {
    $cn.Close()
}

Write-Output ("Updated article tags for $($updates.Count) nodes.")
$updates | Select-Object -First 50 | ForEach-Object { "$($_.NodeName) | $($_.Tags)" }
