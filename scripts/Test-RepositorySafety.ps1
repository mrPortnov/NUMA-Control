#requires -Version 5.1
param([switch]$Worktree, [switch]$History)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $root
try {
    $index = @(& git -c core.quotepath=false ls-files --stage)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read the Git index.' }
    $paths = @(foreach ($entry in $index) {
        if ($entry -notmatch '^100(?:644|755) [a-f0-9]+ 0\t(.+)$') { throw 'The index must contain only regular files and no merge conflicts.' }
        $Matches[1]
    })
    if ($Worktree) {
        $paths += @(& git -c core.quotepath=false ls-files --others --exclude-standard)
        if ($LASTEXITCODE -ne 0) { throw 'Cannot list untracked files.' }
    }
    $paths = @($paths | Sort-Object -Unique)
    if ($paths.Count -eq 0) { throw 'No repository files to inspect. Use -Worktree before staging the first import.' }
    $documentPaths = @('README.md','README.ru.md','CHANGELOG.md','CHANGELOG.ru.md',
        'NumaControl/README.md','NumaControl/README.ru.md','NumaControl/USER-GUIDE.md','NumaControl/USER-GUIDE.ru.md')
    $licensePaths = @('LICENSE','THIRD-PARTY-NOTICES.txt')
    $screenshots = @('library-standard','settings-standard','diagnostics-standard','library-light','library-dark','library-bios',
        'profile-editor-standard','cpu-selection-standard')
    $imagePaths = @(foreach ($language in @('en','ru')) {
        foreach ($name in $screenshots) { "docs/screenshots/$language-$name.png" }
    })
    $findings = New-Object 'System.Collections.Generic.List[string]'
    $publicFiles = @{}
    function Test-SourcePath([string]$path) {
        if ($path -cin $documentPaths -or $path -cin $licensePaths -or $path -cin $imagePaths) { return $true }
        if ($path -match '\.md$' -or $path -cnotmatch '^(?:NumaControl/(?:(?:src|tests|installer|assets)/)?[A-Za-z0-9_.-]+|scripts/[A-Za-z0-9_.-]+\.ps1|\.github/(?:workflows/[A-Za-z0-9_.-]+\.yml|dependabot\.yml)|\.githooks/pre-commit|\.gitignore|\.gitattributes)$') {
            $findings.Add($path + ': outside the current application import scope')
            return $false
        }
        if ($path -match '(?i)(^|/)(?:\.env(?:\..*)?|credentials\.json|secrets\.json|hosts\.yml|profiles[^/]*\.json|processor\.json|id_rsa[^/]*|id_ed25519[^/]*|[^/]+\.local\.[^/]+)$|\.(?:exe|dll|zip|msi|wixpdb|wixobj|pdb|log|dmp|bak|lnk|pem|key|pfx|p12|jks|pdf)$|^NumaControl/SHA256\.txt$') {
            $findings.Add($path + ': local data, credential file, old report or build artifact')
            return $false
        }
        return $true
    }
    if ($History) {
        $historical = @(& git -c core.quotepath=false log --all --name-only --format= --no-renames --diff-filter=ACMR | Where-Object { $_ } | Sort-Object -Unique)
        if ($LASTEXITCODE -ne 0) { throw 'Cannot list paths in Git history.' }
        foreach ($path in $historical) { [void](Test-SourcePath $path) }
    }
    $patterns = [ordered]@{
        'GitHub token' = '\b(?:github_pat_[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9]{20,})\b'
        'API token' = '\b(?:sk-(?:proj-|svcacct-)?[A-Za-z0-9_-]{24,}|glpat-[A-Za-z0-9_-]{20,}|xox[baprs]-[A-Za-z0-9-]{20,})\b'
        'AWS access key' = '\b(?:AKIA|ASIA)[A-Z0-9]{16}\b'
        'Private key' = '-----BEGIN (?:[A-Z0-9 ]+ )?PRIVATE KEY-----'
        'Embedded credential URL' = 'https?://[^/\s:@]+:[^/\s@]+@'
        'Literal credential assignment' = '(?im)\b(?:password|passwd|client_secret|api_key|access_token)\b\s*[:=]\s*["''][^"''\r\n]{8,}["'']'
        'Personal Windows path' = '(?i)\b[A-Z]:[\\/]+Users[\\/]+(?!Public(?:[\\/]|$)|Default(?:[\\/]|$)|<)[^\\/\s"'']+'
    }
    foreach ($path in $paths) {
        if (-not (Test-SourcePath $path)) { continue }
        $extension = [IO.Path]::GetExtension($path).ToLowerInvariant()
        $binary = $extension -in @('.png', '.bmp', '.ico')
        if (-not $binary -and $extension -notin @('.md','.cs','.ps1','.cmd','.xaml','.xml','.manifest','.config','.wxs','.svg','.yml','.yaml','.json') -and $path -notin (@('.gitignore','.gitattributes','.githooks/pre-commit') + $licensePaths)) {
            $findings.Add($path + ': unreviewed file type')
            continue
        }
        if ($Worktree) {
            $fullPath = Join-Path $root $path
            if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { continue }
            $file = Get-Item -LiteralPath $fullPath -Force
            if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { $findings.Add($path + ': symbolic link or reparse point'); continue }
            $length = $file.Length
            if (-not $binary) { $content = [IO.File]::ReadAllText($fullPath) }
            if ($extension -eq '.ps1' -and $content -match '[^\x00-\x7F]') {
                $bytes = [IO.File]::ReadAllBytes($fullPath)
                if ($bytes.Length -lt 3 -or $bytes[0] -ne 0xEF -or $bytes[1] -ne 0xBB -or $bytes[2] -ne 0xBF) {
                    $findings.Add($path + ': non-ASCII PowerShell script must have a UTF-8 BOM for Windows PowerShell 5.1')
                }
            }
        } else {
            $objectName = ':' + $path
            $sizeOutput = & git cat-file -s $objectName
            if ($LASTEXITCODE -ne 0) { throw ('Cannot inspect index entry: ' + $path) }
            $length = [long]$sizeOutput
            if (-not $binary) {
                $content = (& git show $objectName) -join "`n"
                if ($LASTEXITCODE -ne 0) { throw ('Cannot read index entry: ' + $path) }
            }
        }
        if ($length -gt 5MB) { $findings.Add($path + ': exceeds the 5 MiB source-file limit'); continue }
        if ($path -cin ($documentPaths + $licensePaths + $imagePaths) -and $length -gt 0) { $publicFiles[$path] = if ($binary) { $true } else { $content } }
        if ($binary) { continue }
        foreach ($entry in $patterns.GetEnumerator()) {
            if ([regex]::IsMatch($content, $entry.Value)) { $findings.Add($path + ': ' + $entry.Key) }
        }
    }
    foreach ($path in ($documentPaths + $licensePaths + $imagePaths)) {
        if (-not $publicFiles.ContainsKey($path)) { $findings.Add($path + ': required public file missing or empty') }
    }
    foreach ($path in @('README.md','README.ru.md')) {
        if (-not $publicFiles.ContainsKey($path)) { continue }
        $language = if ($path -eq 'README.md') { 'en' } else { 'ru' }
        $readme = $publicFiles[$path]
        $images = @([regex]::Matches($readme, '!\[[^\]]+\]\(([^)]+)\)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
        $expectedImages = @($imagePaths | Where-Object { $_ -like "docs/screenshots/$language-*" })
        if ($images.Count -eq 0 -or (Compare-Object $expectedImages $images -CaseSensitive)) { $findings.Add($path + ': missing or unexpected localized screenshot') }
        $themeSections = @($readme -split '(?m)^## ' | Where-Object { $_ -match '/(?:en|ru)-library-(?:light|dark|bios)\.png' })
        if ($themeSections.Count -ne 1 -or $themeSections[0] -notmatch '/(?:en|ru)-library-standard\.png') {
            $findings.Add($path + ': theme previews must share a section with the standard theme')
        }
        if ($readme -notmatch '\bVAC\b') { $findings.Add($path + ': missing VAC compatibility notice') }
    }
    $versions = @(foreach ($path in @('CHANGELOG.md','CHANGELOG.ru.md')) {
        if (-not $publicFiles.ContainsKey($path)) { continue }
        foreach ($match in [regex]::Matches($publicFiles[$path], '(?m)^## \[?(\d+\.\d+\.\d+)\b')) {
            $match.Groups[1].Value
            break
        }
    })
    if ($versions.Count -ne 2 -or $versions[0] -cne $versions[1]) { $findings.Add('CHANGELOG: latest English and Russian versions differ') }
    foreach ($path in $documentPaths) {
        if (-not $publicFiles.ContainsKey($path)) { continue }
        foreach ($match in [regex]::Matches($publicFiles[$path], '(?<!!)\[[^\]]+\]\(([^)\s]+)\)')) {
            $target = ($match.Groups[1].Value -split '#', 2)[0]
            if (-not $target -or $target -match '^https://') { continue }
            $base = Split-Path -Parent (Join-Path $root $path)
            $fullPath = [IO.Path]::GetFullPath((Join-Path $base $target))
            if (-not $fullPath.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -or
                $fullPath.Substring($root.Length + 1).Replace('\','/') -cnotin $paths) {
                $findings.Add($path + ': local document link does not point to a repository file')
            }
        }
    }
    if ($findings.Count -gt 0) {
        foreach ($finding in $findings) { Write-Host ('BLOCKED ' + $finding) -ForegroundColor Red }
        throw ('Repository safety check failed: ' + $findings.Count + ' finding(s). Values are intentionally hidden.')
    }
    $mode = if ($Worktree) { 'worktree' } else { 'Git index' }
    Write-Host ('Repository safety check passed: ' + $paths.Count + ' files (' + $mode + '). Manual review is still required.')
} finally { Pop-Location }
