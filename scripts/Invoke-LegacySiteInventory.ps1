param(
    [string]$BaseUrl = "https://tmmonline.nz",
    [string]$OutputDir = "./migration-output",
    [int]$MaxPages = 500
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-AbsoluteUrl {
    param(
        [string]$Candidate,
        [Uri]$PageUri
    )

    if ([string]::IsNullOrWhiteSpace($Candidate)) {
        return $null
    }

    if ($Candidate.StartsWith("mailto:") -or $Candidate.StartsWith("tel:") -or $Candidate.StartsWith("javascript:") -or $Candidate.StartsWith("#")) {
        return $null
    }

    try {
        $uri = [Uri]::new($Candidate, [UriKind]::RelativeOrAbsolute)
        if (-not $uri.IsAbsoluteUri) {
            $uri = [Uri]::new($PageUri, $uri)
        }

        # Normalize by removing fragments.
        $builder = [UriBuilder]::new($uri)
        $builder.Fragment = ""
        return $builder.Uri.AbsoluteUri.TrimEnd('/')
    }
    catch {
        return $null
    }
}

$rootUri = [Uri]::new($BaseUrl)
$rootHost = $rootUri.Host

New-Item -Path $OutputDir -ItemType Directory -Force | Out-Null

$queue = [System.Collections.Generic.Queue[string]]::new()
$visited = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$pages = [System.Collections.Generic.List[object]]::new()
$media = [System.Collections.Generic.List[object]]::new()

$startUrl = $rootUri.AbsoluteUri.TrimEnd('/')
$queue.Enqueue($startUrl)

while ($queue.Count -gt 0 -and $visited.Count -lt $MaxPages) {
    $current = $queue.Dequeue()
    if ($visited.Contains($current)) {
        continue
    }

    $visited.Add($current) | Out-Null

    Write-Host "Crawling $current"

    try {
        $response = Invoke-WebRequest -Uri $current -UseBasicParsing -TimeoutSec 45
    }
    catch {
        $pages.Add([pscustomobject]@{
            Url = $current
            StatusCode = "ERROR"
            Title = ""
            InternalLinks = 0
            MediaLinks = 0
            Notes = $_.Exception.Message
        })
        continue
    }

    $html = [string]$response.Content
    $title = ""
    if ($html -match '(?is)<title>(?<title>.*?)</title>') {
        $title = ($Matches['title'] -replace '\s+', ' ').Trim()
    }

    $internalCount = 0
    $mediaCount = 0

    $linkMatches = [regex]::Matches($html, '(?is)<a\b[^>]*?\bhref\s*=\s*"(?<url>[^"]+)"')
    foreach ($linkMatch in $linkMatches) {
        $candidate = [string]$linkMatch.Groups['url'].Value
        $absolute = Resolve-AbsoluteUrl -Candidate $candidate -PageUri ([Uri]$current)
        if (-not $absolute) {
            continue
        }

        try {
            $uri = [Uri]::new($absolute)
        }
        catch {
            continue
        }

        if ($uri.Host -ieq $rootHost) {
            $internalCount++

            if (-not $visited.Contains($absolute) -and $queue.Count -lt ($MaxPages * 2)) {
                $queue.Enqueue($absolute)
            }
        }
    }

    $imgMatches = [regex]::Matches($html, '(?is)<img\b[^>]*>')
    foreach ($imgMatch in $imgMatches) {
        $imgTag = [string]$imgMatch.Value
        if ($imgTag -notmatch '(?is)\bsrc\s*=\s*"(?<src>[^"]+)"') {
            continue
        }

        $src = [string]$Matches['src']
        $alt = ""
        if ($imgTag -match '(?is)\balt\s*=\s*"(?<alt>[^"]*)"') {
            $alt = [string]$Matches['alt']
        }

        $absolute = Resolve-AbsoluteUrl -Candidate $src -PageUri ([Uri]$current)
        if (-not $absolute) {
            continue
        }

        $mediaCount++
        $media.Add([pscustomobject]@{
            SourcePage = $current
            MediaUrl = $absolute
            AltText = $alt
        })
    }

    $pages.Add([pscustomobject]@{
        Url = $current
        StatusCode = [int]$response.StatusCode
        Title = $title
        InternalLinks = $internalCount
        MediaLinks = $mediaCount
        Notes = ""
    })
}

$pagesPath = Join-Path $OutputDir "pages.csv"
$mediaPath = Join-Path $OutputDir "media.csv"
$summaryPath = Join-Path $OutputDir "summary.json"

$pages | Sort-Object Url -Unique | Export-Csv -Path $pagesPath -NoTypeInformation -Encoding UTF8
$media | Sort-Object MediaUrl -Unique | Export-Csv -Path $mediaPath -NoTypeInformation -Encoding UTF8

$summary = [pscustomobject]@{
    BaseUrl = $BaseUrl
    CrawledAtUtc = [DateTime]::UtcNow.ToString("o")
    PageCount = ($pages | Measure-Object).Count
    MediaCount = ($media | Measure-Object).Count
    Output = [pscustomobject]@{
        PagesCsv = $pagesPath
        MediaCsv = $mediaPath
    }
}

$summary | ConvertTo-Json -Depth 6 | Out-File -FilePath $summaryPath -Encoding UTF8

Write-Host "Inventory complete."
Write-Host "Pages: $pagesPath"
Write-Host "Media: $mediaPath"
Write-Host "Summary: $summaryPath"
