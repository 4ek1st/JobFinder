param([string]$DotNet = 'dotnet', [switch]$Verify)
$ErrorActionPreference = 'Stop'
$buildRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$targetDir = Join-Path $buildRoot 'dist'
$version = & $DotNet --version
if ($LASTEXITCODE -ne 0 -or [int]($version.Split('.')[0]) -lt 10) { throw '.NET SDK 10 or later is required.' }
& $DotNet publish (Join-Path $buildRoot 'JobFinder\JobFinder.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $targetDir --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath (Join-Path $buildRoot 'README.ru.md') -Destination $targetDir
Copy-Item -LiteralPath (Join-Path $buildRoot 'THIRD-PARTY-NOTICES') -Destination $targetDir -Recurse -Force
if ($Verify) {
    $env:JOBFINDER_UPDATE_TEST_DOTNET = (Get-Command $DotNet -ErrorAction Stop).Source
    $fixtureDir = Join-Path $buildRoot 'work\fixtures'
    & (Join-Path $buildRoot 'Fetch-Fixtures.ps1') -Output $fixtureDir
    & $DotNet run --project (Join-Path $buildRoot 'JobFinder.Tests\JobFinder.Tests.csproj') -c Release -- $fixtureDir --live --extended
    if ($LASTEXITCODE -ne 0) { throw 'Source verification failed.' }
    $qaDir = Join-Path $buildRoot 'work\ui-verification'
    $check = Start-Process -FilePath (Join-Path $targetDir 'JobFinder.exe') -ArgumentList @('--verify', ('"' + $qaDir + '"'), ('"' + $fixtureDir + '"')) -WindowStyle Hidden -PassThru -Wait
    if ($check.ExitCode -ne 0) { throw ('UI verification failed. See ' + $qaDir) }
}
Write-Output ('Ready: ' + (Join-Path $targetDir 'JobFinder.exe'))
