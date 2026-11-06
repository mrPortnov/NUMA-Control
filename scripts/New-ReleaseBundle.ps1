#requires -Version 5.1
param(
    [Parameter(Mandatory=$true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [switch]$WithInstaller,
    [switch]$ScanWithDefender,
    [switch]$SmokeTest
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $root
try {
    $commit = & git rev-parse --verify HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Commit the reviewed source before building a release.' }
    $status = @(& git status --porcelain)
    if ($LASTEXITCODE -ne 0 -or $status.Count -gt 0) { throw 'The tracked source and visible untracked files must be clean.' }
    & (Join-Path $PSScriptRoot 'Test-RepositorySafety.ps1') -History
    $output = Join-Path $root ('output\releases\v' + $Version)
    if (Test-Path -LiteralPath $output) { throw 'Release directory already exists. Inspect it; do not silently replace a release.' }
    if ($WithInstaller) {
        $scanner = Join-Path $root '.tools\gitleaks\gitleaks.exe'
        if (-not (Test-Path -LiteralPath $scanner -PathType Leaf)) { throw 'Gitleaks is required for release verification.' }
        & $scanner git --no-banner --redact --log-opts='--all' .
        if ($LASTEXITCODE -ne 0) { throw 'History secret scan failed.' }
    }
    $workspace = Join-Path $root ('tmp\r' + [Guid]::NewGuid().ToString('N').Substring(0,8))
    $releaseRoot = Join-Path $workspace 'release'
    [void](New-Item -ItemType Directory -Path $workspace -Force)
    Write-Host ('Isolated build and checks: ' + $workspace)
    $snapshotZip = Join-Path $workspace 'source.zip'
    & git archive --format=zip ('--output=' + $snapshotZip) HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot export the committed source.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $snapshot = Join-Path $workspace 's'
    [IO.Compression.ZipFile]::ExtractToDirectory($snapshotZip, $snapshot)
    $project = Join-Path $snapshot 'NumaControl'
    $assemblySource = [IO.File]::ReadAllText((Join-Path $project 'src\AssemblyInfo.cs'))
    if ($assemblySource -notmatch ('AssemblyFileVersion\("' + [regex]::Escape($Version) + '\.0"\)')) { throw 'Requested version does not match the committed AssemblyFileVersion.' }
    & (Join-Path $project 'Build.ps1')
    $exe = Join-Path $project 'NUMA-Control.exe'
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion -ne ($Version + '.0')) { throw 'Built EXE has an unexpected version.' }
    $payload = Join-Path $workspace 'portable'
    [void](New-Item -ItemType Directory -Path $payload -Force)
    $names = @('NUMA-Control.exe','NUMA-Control.exe.config','Open-NUMA.cmd','README.md','README.ru.md','USER-GUIDE.md','USER-GUIDE.ru.md')
    foreach ($name in $names) { Copy-Item -LiteralPath (Join-Path $project $name) -Destination (Join-Path $payload $name) }
    $sourceRecord = @('Product: NUMA Control',('Version: ' + $Version),('Source commit: ' + $commit),'Package: portable x64; no user data or raw diagnostic reports','Build: Windows x64, .NET Framework; unsigned')
    [IO.File]::WriteAllLines((Join-Path $payload 'SOURCE.txt'), $sourceRecord, [Text.Encoding]::ASCII)
    $payloadFiles = @(Get-ChildItem -LiteralPath $payload -File | Sort-Object Name)
    $manifest = @($payloadFiles | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + ' *' + $_.Name })
    [IO.File]::WriteAllLines((Join-Path $payload 'SHA256.txt'), $manifest, [Text.Encoding]::ASCII)
    [void](New-Item -ItemType Directory -Path $releaseRoot -Force)
    if ($WithInstaller) {
        $wixSource = Join-Path $root 'NumaControl\.tools\wix-3.14.1'
        if (-not (Test-Path -LiteralPath (Join-Path $wixSource 'candle.exe'))) { throw 'Official WiX 3.14.1 tools are required for -WithInstaller.' }
        $wixTarget = Join-Path $project '.tools\wix-3.14.1'
        [void](New-Item -ItemType Directory -Path (Split-Path -Parent $wixTarget) -Force)
        Copy-Item -LiteralPath $wixSource -Destination $wixTarget -Recurse
        & (Join-Path $project 'Build-Installer.ps1') -ProductVersion $Version -OutputPath (Join-Path $releaseRoot ('NUMA-Control-' + $Version + '-setup-x64.msi'))
        & (Join-Path $project 'Build-Bootstrapper.ps1') -ProductVersion $Version -MsiPath (Join-Path $releaseRoot ('NUMA-Control-' + $Version + '-setup-x64.msi')) -OutputPath (Join-Path $releaseRoot ('NUMA-Control-' + $Version + '-setup-x64.exe'))
    }
    $archivePath = Join-Path $releaseRoot ('NUMA-Control-' + $Version + '-portable-x64.zip')
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, $archivePath, [IO.Compression.CompressionLevel]::Optimal, $false)
    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $expected = @($names + 'SOURCE.txt' + 'SHA256.txt' | Sort-Object)
        $actual = @($archive.Entries | ForEach-Object FullName | Sort-Object)
        if (Compare-Object -ReferenceObject $expected -DifferenceObject $actual) { throw 'Unexpected release ZIP contents.' }
        foreach ($entry in $archive.Entries) {
            $stream = $entry.Open()
            $algorithm = [Security.Cryptography.SHA256]::Create()
            try { $entryHash = [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-','') }
            finally { $stream.Dispose(); $algorithm.Dispose() }
            if ($entryHash -ne (Get-FileHash -LiteralPath (Join-Path $payload $entry.FullName) -Algorithm SHA256).Hash) { throw 'Release ZIP integrity check failed.' }
        }
    } finally { $archive.Dispose() }
    $distributions = @(Get-ChildItem -LiteralPath $releaseRoot -File | Where-Object { $_.Extension -in @('.zip','.msi','.exe') } | Sort-Object Name)
    $distributionHashes = @($distributions | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + ' *' + $_.Name })
    [IO.File]::WriteAllLines((Join-Path $releaseRoot 'SHA256SUMS.txt'), $distributionHashes, [Text.Encoding]::ASCII)
    [IO.File]::WriteAllLines((Join-Path $releaseRoot 'SOURCE.txt'), $sourceRecord, [Text.Encoding]::ASCII)
    if ($WithInstaller) {
        & $scanner dir --no-banner --redact $payload
        if ($LASTEXITCODE -ne 0) { throw 'Portable payload secret scan failed.' }
        $tests = Join-Path $project 'tests'
        & (Join-Path $tests 'Run-Tests.ps1') -SkipBuild
        $result = Get-Content -LiteralPath (Join-Path $tests 'artifacts\latest-results.json') -Raw | ConvertFrom-Json
        if ($result.Passed -le 0 -or $result.Version -ne ($Version + '.0') -or $result.ReleaseSha256 -ne (Get-FileHash -LiteralPath $exe).Hash) {
            throw 'Application test results do not match the packaged executable.'
        }
        & (Join-Path $tests 'Run-Resilience.ps1') -SoakSeconds 120
        & (Join-Path $tests 'Stress-Test.ps1') -Seconds 10
        & (Join-Path $tests 'Audit-Themes.ps1') -Seconds 10 -Switches 32 -SteadySeconds 120
        $baseName = 'NUMA-Control-' + $Version + '-setup-x64'
        $msi = Join-Path $releaseRoot ($baseName + '.msi')
        $setup = Join-Path $releaseRoot ($baseName + '.exe')
        & (Join-Path $tests 'Test-Installer.ps1') -MsiPath $msi
        & (Join-Path $tests 'Test-Bootstrapper.ps1') -SetupPath $setup -MsiPath $msi
    }
    if ($SmokeTest) { & (Join-Path $project 'tests\Check-Launch.ps1') -MeasureSeconds 15 }
    if ($ScanWithDefender) {
        $defender = Get-ChildItem -Path "$env:ProgramData\Microsoft\Windows Defender\Platform\*\MpCmdRun.exe" -File |
            Sort-Object { [version]($_.Directory.Name -split '-')[0] } -Descending | Select-Object -First 1
        if (-not $defender) { throw 'Microsoft Defender command line scanner is unavailable.' }
        & $defender.FullName -Scan -ScanType 3 -File $releaseRoot -DisableRemediation
        if ($LASTEXITCODE -ne 0) { throw 'Microsoft Defender scan failed or detected a threat.' }
    }
    foreach ($file in $distributions) {
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + ' *' + $file.Name
        if ($hash -cnotin $distributionHashes) { throw 'A distribution changed after the initial checksum.' }
    }
    [void](New-Item -ItemType Directory -Path $output)
    foreach ($name in @($distributions.Name) + @('SHA256SUMS.txt','SOURCE.txt')) {
        Copy-Item -LiteralPath (Join-Path $releaseRoot $name) -Destination $output
    }
    Write-Host ('Verified release bundle: ' + $output)
    Write-Host ('Source commit: ' + $commit)
} finally { Pop-Location }
