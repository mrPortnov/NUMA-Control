#requires -Version 5.1
param([switch]$Worktree)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $root
try {
    $paths = @(& git -c core.quotepath=false ls-files --cached)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read the Git index.' }
    if ($Worktree) {
        $paths += @(& git -c core.quotepath=false ls-files --others --exclude-standard)
        if ($LASTEXITCODE -ne 0) { throw 'Cannot list untracked files.' }
    }
    $paths = @($paths | Sort-Object -Unique)
    if ($paths.Count -eq 0) { throw 'No repository files to inspect. Use -Worktree before staging the first import.' }
    $findings = New-Object 'System.Collections.Generic.List[string]'
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
        $publicDocument = $path -match '^(?:(?:README|CHANGELOG)|NumaControl/(?:README|USER-GUIDE))(?:\.ru)?\.md$';
        if (($path.EndsWith('.md', [StringComparison]::OrdinalIgnoreCase) -and -not $publicDocument) -or $path -notmatch '^(?:NumaControl/|docs/screenshots/[^/]+\.png$|scripts/|\.github/|\.githooks/|(?:README|CHANGELOG)(?:\.ru)?\.md$|\.gitignore$|\.gitattributes$)') {
            $findings.Add($path + ': outside the current application import scope')
            continue
        }
        if ($path -match '(?i)(^|/)(?:data|reports|artifacts|bug-reports|legacy-backup|\.tools|\.ssh|\.aws|\.codex|\.agents|build|bin|obj)(/|$)|(^|/)(?:\.env(?:\..*)?|credentials\.json|secrets\.json|hosts\.yml|profiles[^/]*\.json|processor\.json|id_rsa[^/]*|id_ed25519[^/]*)$|\.(?:exe|dll|zip|msi|wixpdb|wixobj|pdb|log|dmp|bak|lnk|pem|key|pfx|p12|jks|pdf)$|^NumaControl/(?:CPU-AUDIT\.md|SHA256\.txt)$') {
            $findings.Add($path + ': local data, credential file, old report or build artifact')
            continue
        }
        $extension = [IO.Path]::GetExtension($path).ToLowerInvariant()
        $binary = $extension -in @('.png', '.bmp', '.ico')
        if (-not $binary -and $extension -notin @('.md','.cs','.ps1','.cmd','.xaml','.xml','.manifest','.config','.wxs','.svg','.yml','.yaml','.json') -and $path -notin @('.gitignore','.gitattributes','.githooks/pre-commit')) {
            $findings.Add($path + ': unreviewed file type')
            continue
        }
        if ($Worktree) {
            $fullPath = Join-Path $root $path
            if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { continue }
            $length = (Get-Item -LiteralPath $fullPath).Length
            if (-not $binary) { $content = [IO.File]::ReadAllText($fullPath) }
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
        if ($binary) { continue }
        foreach ($entry in $patterns.GetEnumerator()) {
            if ([regex]::IsMatch($content, $entry.Value)) { $findings.Add($path + ': ' + $entry.Key) }
        }
    }
    if ($findings.Count -gt 0) {
        foreach ($finding in $findings) { Write-Host ('BLOCKED ' + $finding) -ForegroundColor Red }
        throw ('Repository safety check failed: ' + $findings.Count + ' finding(s). Values are intentionally hidden.')
    }
    $mode = if ($Worktree) { 'worktree' } else { 'Git index' }
    Write-Host ('Repository safety check passed: ' + $paths.Count + ' files (' + $mode + '). Manual review is still required.')
} finally { Pop-Location }
