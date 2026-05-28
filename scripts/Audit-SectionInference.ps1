param([string]$ConnectionString)

function Get-TargetSectionSlug([string]$legacyUrl) {
    try {
        $resp = Invoke-WebRequest -UseBasicParsing -Uri $legacyUrl -TimeoutSec 30
        $html = [string]$resp.Content
    } catch {
        return $null
    }

    $patterns = @(
        '<div[^>]*class="[^"]*article-topic[^"]*"[^>]*>.*?<a[^>]*href="(?<href>[^"]+)"',
        "<div[^>]*class='[^']*article-topic[^']*'[^>]*>.*?<a[^>]*href='(?<href>[^']+)'"
    )

    $href = $null
    foreach($pattern in $patterns){
        $match = [regex]::Match($html, $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::Singleline)
        if($match.Success){ $href = $match.Groups['href'].Value; break }
    }

    if([string]::IsNullOrWhiteSpace($href)){ return $null }

    try {
        $resolved = [Uri]::new([Uri]::new('https://tmmonline.nz'), $href)
        $segments = $resolved.AbsolutePath.Trim('/').Split('/', [System.StringSplitOptions]::RemoveEmptyEntries)
        if($segments.Length -eq 0){ return $null }
        $slug = $segments[0].ToLowerInvariant()
        switch($slug){
            'better-business' { return 'conference' }
            'conference' { return 'conference' }
            'property-news' { return 'property-news' }
            'people' { return 'people' }
            'news-bites' { return 'news-bites' }
            default { return $null }
        }
    } catch {
        return $null
    }
}

$query = @"
WITH articleProps AS (
    SELECT DISTINCT
        n.id AS NodeId,
        n.text AS NodeName,
        MAX(CASE WHEN pt.alias = 'legacySourceUrl' THEN COALESCE(CAST(pd.textValue AS nvarchar(max)), CAST(pd.varcharValue AS nvarchar(max))) END) OVER (PARTITION BY n.id) AS LegacySourceUrl,
        MAX(CASE WHEN pt.alias = 'articleTags' THEN COALESCE(CAST(pd.textValue AS nvarchar(max)), CAST(pd.varcharValue AS nvarchar(max))) END) OVER (PARTITION BY n.id) AS ArticleTags
    FROM umbracoNode n
    INNER JOIN umbracoContent c ON c.nodeId = n.id
    INNER JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId
    INNER JOIN umbracoContentVersion cv ON cv.nodeId = n.id
    INNER JOIN umbracoPropertyData pd ON pd.versionId = cv.id
    INNER JOIN cmsPropertyType pt ON pt.id = pd.propertyTypeId
    WHERE ct.alias = 'articlePage'
      AND pt.alias IN ('legacySourceUrl','articleTags')
)
SELECT DISTINCT NodeId, NodeName, LegacySourceUrl, ArticleTags
FROM articleProps
WHERE LegacySourceUrl IS NOT NULL AND LegacySourceUrl <> '';
"@

$cn = [System.Data.SqlClient.SqlConnection]::new($ConnectionString)
$cn.Open()
$cmd = $cn.CreateCommand(); $cmd.CommandText = $query; $cmd.CommandTimeout = 120
$r = $cmd.ExecuteReader()
$articles = New-Object System.Collections.Generic.List[object]
while($r.Read()){
    $articles.Add([pscustomobject]@{
        NodeId = [int]$r['NodeId']
        NodeName = $r['NodeName'].ToString()
        LegacySourceUrl = $r['LegacySourceUrl'].ToString()
        ArticleTags = $r['ArticleTags'].ToString()
    }) | Out-Null
}
$r.Close(); $cn.Close()

$rows = foreach($article in $articles){
    $inferred = Get-TargetSectionSlug $article.LegacySourceUrl
    [pscustomobject]@{
        NodeId = $article.NodeId
        NodeName = $article.NodeName
        LegacySourceUrl = $article.LegacySourceUrl
        CurrentTags = $article.ArticleTags
        InferredSection = $inferred
    }
}

Write-Output ('Audited articles with legacySourceUrl: ' + $rows.Count)
Write-Output 'Inferred section counts:'
$rows | Group-Object InferredSection | Sort-Object Count -Descending | ForEach-Object {
    $name = if([string]::IsNullOrWhiteSpace($_.Name)){'<null>'}else{$_.Name}
    Write-Output ($name + ' | ' + $_.Count)
}

Write-Output 'Conference/property-news inferred rows:'
$cp = $rows | Where-Object { $_.InferredSection -in @('conference','property-news') }
if(-not $cp){
    Write-Output 'None inferred from legacy pages.'
} else {
    $cp | Select-Object NodeId,NodeName,InferredSection,CurrentTags,LegacySourceUrl | Format-Table -AutoSize | Out-String | Write-Output
}

Write-Output 'Conference/property-news mismatches:'
$mismatch = $cp | Where-Object { -not $_.CurrentTags -or ($_.CurrentTags.ToLowerInvariant() -notmatch '(conference|property-news)') }
if(-not $mismatch){
    Write-Output 'No mismatches detected.'
} else {
    $mismatch | Select-Object NodeId,NodeName,InferredSection,CurrentTags,LegacySourceUrl | Format-Table -AutoSize | Out-String | Write-Output
}
