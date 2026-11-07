#requires -Version 5.1
param(
    [string]$Repository = $env:GITHUB_REPOSITORY,
    [string]$Commit = $env:GITHUB_SHA,
    [ValidateRange(1, 600)][int]$TimeoutSeconds = 600
)

$ErrorActionPreference = 'Stop'
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or $Commit -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'Supply the GitHub repository and the exact 40-character release commit.'
}
$Commit = $Commit.ToLowerInvariant()
$gh = (Get-Command gh.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$clock = [Diagnostics.Stopwatch]::StartNew()
$dispatchHelp = 'Dispatch the CodeQL workflow on a branch whose HEAD matches this commit, then retry release verification.'

function Read-CodeQLApi([string]$Endpoint) {
    $remaining = [int][Math]::Floor(($TimeoutSeconds - $clock.Elapsed.TotalSeconds) * 1000)
    if ($remaining -le 0) { throw ('Timed out waiting for CodeQL. ' + $dispatchHelp) }
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $gh
    $info.Arguments = 'api --method GET "' + $Endpoint + '"'
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    try {
        if (-not $process.Start()) { throw 'Cannot start the GitHub CLI for CodeQL verification.' }
        # Drain both streams while keeping native API errors out of public logs.
        $output = $process.StandardOutput.ReadToEndAsync()
        $discardedError = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($remaining)) {
            try { $process.Kill() } catch { }
            throw ('Timed out reading CodeQL from GitHub. ' + $dispatchHelp)
        }
        if ($process.ExitCode -ne 0) { throw 'GitHub could not provide CodeQL verification. Check workflow access and retry.' }
        try { return ($output.GetAwaiter().GetResult() | ConvertFrom-Json) }
        catch { throw 'GitHub returned an invalid CodeQL response.' }
    } finally { $process.Dispose() }
}

Write-Host ('Waiting for CodeQL on release commit ' + $Commit + '.')
while ($clock.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
    $response = Read-CodeQLApi ('repos/' + $Repository + '/actions/workflows/codeql.yml/runs?head_sha=' + $Commit + '&per_page=100')
    if ($null -eq $response -or $null -eq $response.workflow_runs) { throw 'The CodeQL workflow run list is missing.' }
    # PR runs analyze a merge commit, so only direct checkouts of this commit qualify.
    $runs = @($response.workflow_runs | Where-Object {
        $_.head_sha -ceq $Commit -and $_.event -cin @('push', 'workflow_dispatch')
    } | Sort-Object -Property @{ Expression = { [long]$_.id }; Descending = $true })
    if ($runs.Count -gt 0) {
        $run = $runs[0]
        if ($run.id -notmatch '^[1-9][0-9]*$') { throw 'The CodeQL workflow run has an invalid identity.' }
        if ($run.status -ceq 'completed') {
            if ($run.conclusion -cne 'success') { throw 'The latest CodeQL workflow for this release commit did not succeed.' }
            $result = Read-CodeQLApi ('repos/' + $Repository + '/actions/runs/' + $run.id + '/jobs?filter=latest&per_page=100')
            if ($null -eq $result -or $null -eq $result.jobs) { throw 'The completed CodeQL workflow has no job results.' }
            foreach ($name in @('CodeQL (csharp)', 'CodeQL (actions)')) {
                $jobs = @($result.jobs | Where-Object { $_.name -ceq $name })
                if ($jobs.Count -ne 1 -or $jobs[0].status -cne 'completed' -or $jobs[0].conclusion -cne 'success') {
                    throw ('CodeQL requires one completed, successful ' + $name + ' job for this release commit.')
                }
            }
            $summary = 'CodeQL verified for {0}. C# and GitHub Actions jobs passed in workflow run {1}.' -f $Commit, $run.id
            Write-Host $summary
            if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $summary -Encoding utf8 }
            return
        }
        if ($run.status -cnotin @('queued', 'in_progress', 'waiting', 'requested', 'pending')) {
            throw 'The CodeQL workflow has an unexpected status.'
        }
    }
    $remaining = [Math]::Max(0, $TimeoutSeconds - $clock.Elapsed.TotalSeconds)
    Start-Sleep -Milliseconds ([int][Math]::Min(15000, $remaining * 1000))
}
throw ('Timed out waiting for CodeQL on the exact release commit. ' + $dispatchHelp)
