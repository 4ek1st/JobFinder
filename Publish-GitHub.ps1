param(
    [Parameter(Mandatory)][string]$Version,
    [string]$DotNet = 'dotnet',
    [string]$VerificationDirectory,
    [switch]$UseExistingBuild,
    [switch]$PrepareOnly,
    [switch]$PublishPrepared,
    [switch]$ResumeDraft
)
$ErrorActionPreference = 'Stop'
$publisherRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$repository = '4ek1st/JobFinder'
$projectPath = Join-Path $publisherRoot 'JobFinder/JobFinder.csproj'
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$' -or ![Version]::TryParse($Version,[ref]([Version]$null))) { throw 'Use a stable numeric three/four-part version.' }
$project = [xml](Get-Content -Raw -LiteralPath $projectPath)
if ([string]$project.Project.PropertyGroup.Version -ne $Version) { throw 'Set the project Version before publishing.' }
if (!(Get-Command gh -ErrorAction SilentlyContinue)) { throw 'GitHub CLI is required for publication.' }
$account = & gh api user --jq '.login'
if ($LASTEXITCODE -ne 0 -or $account.Trim() -ne '4ek1st') { throw 'Publish with the repository owner account.' }
$buildDirectory = Join-Path $publisherRoot 'dist'
if ($PrepareOnly -and $PublishPrepared) { throw 'Choose preparation or publication of prepared assets.' }
$releaseDirectory = Join-Path $publisherRoot ('work/releases/v' + $Version)
if (!$PublishPrepared) {
if (!$UseExistingBuild) {
    & $DotNet publish $projectPath -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $buildDirectory --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}
$executable = Join-Path $buildDirectory 'JobFinder.exe'
$fileVersion = [Version](Get-Item -LiteralPath $executable).VersionInfo.FileVersion
$expected = [Version]$Version
if ($fileVersion.Major -ne $expected.Major -or $fileVersion.Minor -ne $expected.Minor -or $fileVersion.Build -ne $expected.Build -or $fileVersion.Revision -ne [Math]::Max(0,$expected.Revision)) { throw 'Executable version does not match.' }
if (Test-Path -LiteralPath $releaseDirectory) { throw 'Release preparation already exists; preserve it and use a new version or inspect it first.' }
$packageDirectory = Join-Path $releaseDirectory 'package'
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
Copy-Item -LiteralPath $executable -Destination $packageDirectory
foreach ($name in @('README.md','README.de.md','README.ru.md','CHANGELOG.md','THIRD-PARTY-NOTICES')) { Copy-Item -LiteralPath (Join-Path $publisherRoot $name) -Destination $packageDirectory -Recurse }
if ($VerificationDirectory) {
    $evidenceDirectory = Join-Path $packageDirectory 'verification'
    New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null
    $publicEvidence = @('ui-verification.json','update-ui-verification.json','verification-core.json','verification-installer.json','source-catalog.json','release-verification.json','JobFinder-update-available-dark.png','JobFinder-update-available-light.png','JobFinder-update-available-narrow-de.png','JobFinder-update-downloading-dark.png','JobFinder-update-about-dark.png','JobFinder-031-compact-dark.png','JobFinder-040-sources-dark.png')
    foreach ($file in Get-ChildItem -LiteralPath $VerificationDirectory -File) {
        if ($file.Name -cnotin $publicEvidence) { continue }
        if ($file.Extension -eq '.json') {
            $evidenceText = Get-Content -Raw -LiteralPath $file.FullName
            if ($evidenceText -match '(?i)[a-z]:[\\/]{1,2}Users[\\/]{1,2}|gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|chatgpt\.com/c/|-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----|"(executable|transactionDirectory|installDirectory|backupDirectory|receiptPath|planPath)"\s*:') { throw ('Private content in verification artifact: ' + $file.Name) }
        }
        Copy-Item -LiteralPath $file.FullName -Destination $evidenceDirectory
    }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$package = Join-Path $releaseDirectory 'JobFinder-portable-win-x64.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($packageDirectory,$package,[IO.Compression.CompressionLevel]::Optimal,$false)
$packageHash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
$exeHash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = [ordered]@{ schemaVersion = 1; version = $Version; tag = 'v' + $Version; packageFile = 'JobFinder-portable-win-x64.zip'; packageSize = (Get-Item -LiteralPath $package).Length; packageSha256 = $packageHash; executableSha256 = $exeHash; minimumUpdaterVersion = '0.4.1' }
$manifestPath = Join-Path $releaseDirectory 'update-manifest.json'
$manifestBytes = [Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Depth 4))
[IO.File]::WriteAllBytes($manifestPath,$manifestBytes)
$keyDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'JobFinder-Publisher/keys'
$keyPath = Join-Path $keyDirectory '4ek1st-JobFinder-rsa3072.dpapi'
if (!(Test-Path -LiteralPath $keyPath)) { throw 'Author signing key is unavailable. Do not replace the trusted key for an existing installation.' }
Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
$entropy = [Text.Encoding]::UTF8.GetBytes('JobFinder-GitHub-release-v1')
$privateBytes = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($keyPath),$entropy,[Security.Cryptography.DataProtectionScope]::CurrentUser)
$signer = [Security.Cryptography.RSA]::Create()
try {
    $readCount = 0
    $signer.ImportPkcs8PrivateKey($privateBytes,[ref]$readCount)
    if ($readCount -ne $privateBytes.Length) { throw 'Signing key format changed.' }
    $contractText = Get-Content -Raw -LiteralPath (Join-Path $publisherRoot 'JobFinder.Core/UpdateContracts.cs')
    $publicPem = [regex]::Match($contractText,'-----BEGIN PUBLIC KEY-----[\s\S]*?-----END PUBLIC KEY-----').Value
    $verifier = [Security.Cryptography.RSA]::Create()
    try {
        $verifier.ImportFromPem($publicPem)
        if ([Convert]::ToHexString($verifier.ExportSubjectPublicKeyInfo()) -ne [Convert]::ToHexString($signer.ExportSubjectPublicKeyInfo())) { throw 'Signing key differs from the program trusted public key.' }
        $signature = $signer.SignData($manifestBytes,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pss)
        if (!$verifier.VerifyData($manifestBytes,$signature,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pss)) { throw 'Manifest signature self-check failed.' }
        [IO.File]::WriteAllBytes((Join-Path $releaseDirectory 'update-manifest.sig'),$signature)
    } finally { $verifier.Dispose() }
} finally { [Array]::Clear($privateBytes); $signer.Dispose() }
$listedFiles = & git -C $publisherRoot ls-files
if ($LASTEXITCODE -ne 0 -or !$listedFiles) { throw 'Commit the reviewed source tree before publication.' }
foreach ($relative in $listedFiles) {
    if ($relative -match '(^|/)(data|bin|obj|dist|work|\.git)/|profile\.json|\.dpapi$|\.pfx$|\.key$|CHATGPT|design-qa|PLAN\.md$' -and $relative -ne 'UPDATE-PLAN.md') { throw ('Private/local file in source tree: ' + $relative) }
    $fullPath = Join-Path $publisherRoot $relative
    if ([IO.Path]::GetExtension($fullPath) -in @('.cs','.md','.ps1','.json','.xaml')) {
        $sourceText = Get-Content -Raw -LiteralPath $fullPath
        if ($sourceText -match 'gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|chatgpt\.com/c/|C:[\\/]Users[\\/]User[\\/](Documents|AppData)|-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----[\r\n]+[A-Za-z0-9+/=]{40,}') { throw ('Sensitive/local content in published source: ' + $relative) }
    }
}
$sourceArchive = Join-Path $releaseDirectory 'JobFinder-source.zip'
& git -C $publisherRoot archive --format=zip --prefix=JobFinder-source/ HEAD -o $sourceArchive
if ($LASTEXITCODE -ne 0) { throw 'Source archive failed.' }
$sumLines = @()
foreach ($path in @($package,$sourceArchive,$manifestPath,(Join-Path $releaseDirectory 'update-manifest.sig'))) { $sumLines += (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($path) }
$sumLines | Set-Content -LiteralPath (Join-Path $releaseDirectory 'SHA256SUMS.txt') -Encoding utf8
$notes = @"
Jobs, Praktikum und Ausbildung in Deutschland — in einer übersichtlichen Windows-App.

[**JobFinder für Windows herunterladen**](https://github.com/$repository/releases/download/v$Version/JobFinder-portable-win-x64.zip)

Windows 10 / 11 (64 Bit) · Deutsch / Russisch · Ohne Installation

- Angebote aus 21 Quellen in einer gemeinsamen Liste sowie 12 weitere Portale mit Suche auf ihrer Website.
- Stellenbeschreibung direkt rechts, Favoriten und ein Link zur Originalanzeige.
- Weitere Stellen werden beim Scrollen automatisch geladen.
- Quellenprobleme unter „Einstellungen → Quellenstatus“; ein gelber Punkt am Zahnrad weist darauf hin.
- Dunkles und helles Design. Deutsch ist die Standardsprache für neue Installationen.
- Updates über das Download-Symbol oben rechts. Favoriten und Einstellungen bleiben erhalten.

**Erster Start:** Archiv entpacken und **JobFinder.exe** öffnen.

**Du nutzt JobFinder bereits?** Klicke auf das Update-Symbol in der App. Beim manuellen Umzug behältst du deinen Ordner **data**.

[Mehr über JobFinder](https://github.com/$repository) · [Kurzanleitung](https://github.com/$repository/blob/main/README.de.md)
"@
$notesPath = Join-Path $releaseDirectory 'release-notes.md'
[IO.File]::WriteAllText($notesPath,$notes,[Text.UTF8Encoding]::new($false))
$prepared = [ordered]@{ version = $Version; commit = (& git -C $publisherRoot rev-parse HEAD).Trim(); assets = @{} }
foreach ($assetPath in @($package,$sourceArchive,$manifestPath,(Join-Path $releaseDirectory 'update-manifest.sig'),(Join-Path $releaseDirectory 'SHA256SUMS.txt'))) { $prepared.assets[[IO.Path]::GetFileName($assetPath)] = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant() }
$prepared | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $releaseDirectory 'prepared.json') -Encoding utf8
if ($PrepareOnly) { [ordered]@{ prepared = $true; version = $Version; directory = $releaseDirectory; packageSha256 = $packageHash; signed = $true } | ConvertTo-Json; return }
}
if ($PublishPrepared) {
    $prepared = Get-Content -Raw -LiteralPath (Join-Path $releaseDirectory 'prepared.json') | ConvertFrom-Json
    if ($prepared.version -ne $Version -or $prepared.commit -ne (& git -C $publisherRoot rev-parse HEAD).Trim()) { throw 'The prepared release source snapshot does not match this commit.' }
    foreach ($asset in $prepared.assets.PSObject.Properties) {
        $assetPath = Join-Path $releaseDirectory $asset.Name
        if ((Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.Value) { throw ('Prepared asset changed: ' + $asset.Name) }
    }
    $package = Join-Path $releaseDirectory 'JobFinder-portable-win-x64.zip'
    $sourceArchive = Join-Path $releaseDirectory 'JobFinder-source.zip'
    $manifestPath = Join-Path $releaseDirectory 'update-manifest.json'
    $notesPath = Join-Path $releaseDirectory 'release-notes.md'
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    if ($manifest.version -ne $Version -or $manifest.packageSha256 -ne (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Prepared manifest/package mismatch.' }
    $publicPem = [regex]::Match((Get-Content -Raw -LiteralPath (Join-Path $publisherRoot 'JobFinder.Core/UpdateContracts.cs')),'-----BEGIN PUBLIC KEY-----[\s\S]*?-----END PUBLIC KEY-----').Value
    $verifier = [Security.Cryptography.RSA]::Create()
    try {
        $verifier.ImportFromPem($publicPem)
        if (!$verifier.VerifyData([IO.File]::ReadAllBytes($manifestPath),[IO.File]::ReadAllBytes((Join-Path $releaseDirectory 'update-manifest.sig')),[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pss)) { throw 'Prepared signature verification failed.' }
    } finally { $verifier.Dispose() }
}
$status = & git -C $publisherRoot status --porcelain --untracked-files=normal
if ($status) { throw 'Commit the reviewed changes before pushing this release.' }
$origin = & git -C $publisherRoot remote get-url origin
if ($LASTEXITCODE -ne 0) {
    & gh repo create $repository --public --description 'Lightweight Windows job, Praktikum and Ausbildung finder for Germany with in-app updates.' --source $publisherRoot --remote origin
    if ($LASTEXITCODE -ne 0) { throw 'Repository creation failed.' }
} elseif ($origin.TrimEnd('/') -notin @('https://github.com/4ek1st/JobFinder.git','https://github.com/4ek1st/JobFinder','git@github.com:4ek1st/JobFinder.git')) { throw 'Unexpected Git remote.' }
& git -C $publisherRoot push -u origin main
if ($LASTEXITCODE -ne 0) { throw 'Source push failed.' }
$tag = 'v' + $Version
if ($ResumeDraft) {
    if (!$PublishPrepared) { throw 'Resume only a reviewed prepared draft.' }
    & git -C $publisherRoot diff --exit-code $tag HEAD -- JobFinder JobFinder.Core Directory.Build.props
    if ($LASTEXITCODE -ne 0) { throw 'An existing draft tag cannot be resumed after changing application code.' }
} else {
    & git -C $publisherRoot tag -a $tag -m ('JobFinder ' + $Version)
    if ($LASTEXITCODE -ne 0) { throw 'Tag creation failed.' }
}
& git -C $publisherRoot push origin $tag
if ($LASTEXITCODE -ne 0) { throw 'Tag push failed.' }
$assets = @($package,$sourceArchive,$manifestPath,(Join-Path $releaseDirectory 'update-manifest.sig'),(Join-Path $releaseDirectory 'SHA256SUMS.txt'))
if ($ResumeDraft) {
    $draft = & gh release view $tag --repo $repository --json databaseId,isDraft,tagName,url | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or !$draft.isDraft -or $draft.tagName -cne $tag) { throw 'Only an existing unpublished draft may be resumed.' }
    & gh release upload $tag @assets --repo $repository --clobber
} else {
    & gh release create $tag @assets --repo $repository --draft --verify-tag --title ('JobFinder ' + $Version) --notes-file $notesPath
}
if ($LASTEXITCODE -ne 0) { throw 'Draft release upload failed.' }
$draft = & gh release view $tag --repo $repository --json databaseId,isDraft,tagName,url | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or !$draft.isDraft -or $draft.tagName -cne $tag) { throw 'Draft release identity verification failed.' }
$snapshotJson = & gh api ('repos/' + $repository + '/releases/' + $draft.databaseId)
if ($LASTEXITCODE -ne 0) { throw 'Draft release read-back failed.' }
$snapshot = $snapshotJson | ConvertFrom-Json
$draftUri = [Uri]$snapshot.html_url
$draftSlug = $draftUri.Segments[-1]
if (!$snapshot.draft -or $snapshot.tag_name -cne $tag -or $snapshot.assets.Count -ne $assets.Count -or $draftUri.Scheme -cne 'https' -or $draftUri.Host -cne 'github.com' -or $draftUri.UserInfo -or $draftUri.Query -or $draftUri.Fragment -or $draftUri.AbsolutePath -cne ('/' + $repository + '/releases/tag/' + $draftSlug) -or ($draftSlug -cne $tag -and $draftSlug -cnotmatch '^untagged-[a-f0-9]+$')) { throw 'Draft release asset verification failed.' }
$seenAssets = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($asset in $snapshot.assets) {
    $localAsset = $assets | Where-Object {[IO.Path]::GetFileName($_) -ceq $asset.name}
    if (!$seenAssets.Add($asset.name) -or !$localAsset -or $asset.state -cne 'uploaded' -or (Get-Item -LiteralPath $localAsset).Length -ne $asset.size -or $asset.browser_download_url -cne ('https://github.com/' + $repository + '/releases/download/' + $draftSlug + '/' + $asset.name)) { throw ('Uploaded asset mismatch: ' + $asset.name) }
    if (!$asset.digest -or $asset.digest -ne ('sha256:' + (Get-FileHash -LiteralPath $localAsset -Algorithm SHA256).Hash.ToLowerInvariant())) { throw ('Uploaded asset digest mismatch: ' + $asset.name) }
}
& gh release edit $tag --repo $repository --draft=false --latest
if ($LASTEXITCODE -ne 0) { throw 'Stable release publication failed; draft remains recoverable.' }
$stableJson = & gh api ('repos/' + $repository + '/releases/' + $draft.databaseId)
if ($LASTEXITCODE -ne 0) { throw 'Stable release read-back failed.' }
$stable = $stableJson | ConvertFrom-Json
if ($stable.draft -or $stable.prerelease -or $stable.tag_name -cne $tag -or $stable.html_url -cne ('https://github.com/' + $repository + '/releases/tag/' + $tag)) { throw 'Stable release identity verification failed.' }
foreach ($asset in $stable.assets) {
    if ($asset.browser_download_url -cne ('https://github.com/' + $repository + '/releases/download/' + $tag + '/' + $asset.name)) { throw 'Stable asset URL does not match the updater contract.' }
}
& gh release view $tag --repo $repository --json url,tagName,isDraft,assets
if ($LASTEXITCODE -ne 0) { throw 'Read back the published release before reporting completion.' }
