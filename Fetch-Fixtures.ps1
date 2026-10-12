param([string]$Output = (Join-Path $PSScriptRoot 'work\fixtures'))
$ErrorActionPreference = 'Stop'
$fixturePath = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Path $fixturePath -Force | Out-Null
$requests = @(
    @{ File = 'arbeitnow.txt'; Url = 'https://www.arbeitnow.com/api/job-board-api' },
    @{ File = 'praktikum-search.txt'; Url = 'https://www.praktikum.info/stellenangebote?text=Design&location=Berlin&radius=25' },
    @{ File = 'azubiyo-filtered.txt'; Url = 'https://www.azubiyo.de/ausbildung/?subject=Fachinformatiker&location=Berlin&radius=25' },
    @{ File = 'azubi-empty.txt'; Url = 'https://www.azubiyo.de/ausbildung/?subject=zzzznonexistent123456&location=Berlin&radius=25' }
)
foreach ($request in $requests) {
    Write-Output ('Fetching ' + $request.File)
    Invoke-WebRequest -Uri $request.Url -OutFile (Join-Path $fixturePath $request.File) -TimeoutSec 25 -UserAgent 'JobFinder/0.1 (personal user-initiated verification)'
    Start-Sleep -Seconds 2
}
# Capture the first posting and logo from these exact search responses, not from a guessed employer.
function Capture-Posting([string]$name, [string]$cardPattern, [string]$rootUrl, [string]$detailName, [string]$logoName) {
    $html = Get-Content -Raw -LiteralPath (Join-Path $fixturePath $name)
    $card = [regex]::Matches($html, '<a\b[^>]*>[\s\S]*?</a>') | Where-Object { $_.Value -match $cardPattern } | Select-Object -First 1
    if (!$card) { throw ('No source card in ' + $name) }
    $href = [Net.WebUtility]::HtmlDecode([regex]::Match($card.Value, 'href=["'']([^"'']+)["'']').Groups[1].Value)
    $uri = [Uri]::new([Uri]$rootUrl, $href)
    if ($uri.Scheme -ne 'https' -or $uri.Host -ne ([Uri]$rootUrl).Host) { throw 'Unexpected posting URL.' }
    Invoke-WebRequest -Uri $uri.AbsoluteUri -OutFile (Join-Path $fixturePath $detailName) -TimeoutSec 25 -UserAgent 'JobFinder/0.3 (personal user-initiated verification)'
    $image = [regex]::Match($card.Value, '<img\b[^>]*\ssrc=["'']([^"'']+)["'']').Groups[1].Value
    $imageUri = [Uri]::new([Uri]$rootUrl, [Net.WebUtility]::HtmlDecode($image))
    if ($imageUri.Scheme -ne 'https' -or $imageUri.Host -notin @('www.azubiyo.de','uno-production.imgix.net','d1e0s3eaddm99m.cloudfront.net')) { throw 'Unexpected logo URL.' }
    Start-Sleep -Seconds 2
    Invoke-WebRequest -Uri $imageUri.AbsoluteUri -OutFile (Join-Path $fixturePath $logoName) -TimeoutSec 15
}
Capture-Posting 'azubiyo-filtered.txt' 'href=["'']/stellenanzeigen/' 'https://www.azubiyo.de/' 'azubiyo-tu-detail.txt' 'logo-azubiyo.png'
Capture-Posting 'praktikum-search.txt' 'data-job-offer-target=["'']jobOfferTeaser' 'https://www.praktikum.info/' 'praktikum-detail.txt' 'logo-praktikum.jpg'
Write-Output $fixturePath
