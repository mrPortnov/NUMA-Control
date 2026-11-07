#requires -Version 5.1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$helper = Join-Path $PSScriptRoot 'Wait-ReleaseCodeQL.ps1'
$artifacts = Join-Path $root ('tmp/codeql-gate-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
$mockSource = Join-Path $artifacts 'MockGitHub.cs'
@'
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Web.Script.Serialization;

internal static class MockGitHub
{
    private static readonly string Commit = new string('a', 40);
    private static object Run(int id, string sha, string status, string conclusion, string eventName)
    {
        return new { id = id, head_sha = sha, status = status, conclusion = conclusion, @event = eventName };
    }
    private static object Job(string name, string conclusion)
    {
        return new { name = name, status = "completed", conclusion = conclusion };
    }
    internal static int Main(string[] args)
    {
        if (args.Length != 4 || args[0] != "api" || args[1] != "--method" || args[2] != "GET") return 90;
        string scenario = Environment.GetEnvironmentVariable("CODEQL_MOCK_SCENARIO");
        string endpoint = args[3];
        if (!endpoint.StartsWith("repos/fixtureOwner/fixtureRepo/actions/", StringComparison.Ordinal)) return 91;
        if (scenario == "api_error")
        {
            Console.Error.WriteLine("Confidential native error " + Environment.GetEnvironmentVariable("GH_TOKEN"));
            return 2;
        }
        if (scenario == "malformed")
        {
            Console.WriteLine("Invalid response " + Environment.GetEnvironmentVariable("GH_TOKEN"));
            return 0;
        }
        if (scenario == "hang") Thread.Sleep(10000);
        object result;
        if (endpoint.Contains("/jobs?"))
        {
            if (!endpoint.Contains("/runs/42/jobs?filter=latest&per_page=100")) return 92;
            var jobs = new List<object>();
            jobs.Add(Job("CodeQL (csharp)", scenario == "skipped" ? "skipped" : "success"));
            if (scenario != "missing_job") jobs.Add(Job("CodeQL (actions)", "success"));
            if (scenario == "duplicate_job") jobs.Add(Job("CodeQL (actions)", "success"));
            result = new { jobs = jobs.ToArray() };
        }
        else
        {
            if (!endpoint.EndsWith("workflows/codeql.yml/runs?head_sha=" + Commit + "&per_page=100")) return 93;
            var runs = new List<object>();
            string status = "completed";
            string conclusion = "success";
            string sha = scenario == "wrong_sha" ? new string('b', 40) : Commit;
            string eventName = scenario == "pr_only" ? "pull_request" : "workflow_dispatch";
            if (scenario == "pending_success")
            {
                string counterPath = Environment.GetEnvironmentVariable("CODEQL_MOCK_COUNTER");
                int count = File.Exists(counterPath) ? int.Parse(File.ReadAllText(counterPath)) : 0;
                File.WriteAllText(counterPath, (count + 1).ToString());
                if (count == 0) { status = "in_progress"; conclusion = null; }
            }
            if (scenario == "failure") conclusion = "failure";
            if (scenario == "cancelled") conclusion = "cancelled";
            if (scenario == "latest_failure")
            {
                runs.Add(Run(42, Commit, "completed", "success", "push"));
                runs.Add(Run(43, Commit, "completed", "failure", "workflow_dispatch"));
            }
            else if (scenario != "missing_run")
            {
                runs.Add(Run(42, sha, status, conclusion, eventName));
                if (scenario == "old_failure") runs.Add(Run(41, Commit, "completed", "failure", "push"));
                if (scenario == "pr_ignored") runs.Add(Run(43, Commit, "completed", "failure", "pull_request"));
            }
            result = new { workflow_runs = runs.ToArray() };
        }
        Console.WriteLine(new JavaScriptSerializer().Serialize(result));
        return 0;
    }
}
'@ | Set-Content -LiteralPath $mockSource -Encoding utf8

$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319'
}
$mockExecutable = Join-Path $artifacts 'gh.exe'
& (Join-Path $framework 'csc.exe') /nologo /target:exe ("/out:$mockExecutable") /reference:System.Web.Extensions.dll $mockSource > (Join-Path $artifacts 'compiler.log') 2>&1
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $mockExecutable)) { throw 'Cannot compile the isolated GitHub API fixture.' }

$shell = (Get-Command powershell.exe -CommandType Application -ErrorAction Stop).Source
$commit = 'a' * 40
$sentinel = 'MOCK_SECRET_FOR_OUTPUT_CHECK'
$passCases = @('success', 'pending_success', 'old_failure', 'pr_ignored')
$timeoutCases = @('wrong_sha', 'missing_run', 'pr_only', 'hang')
$cases = @('success', 'pending_success', 'wrong_sha', 'failure', 'missing_job', 'skipped', 'cancelled', 'missing_run', 'pr_only', 'latest_failure', 'old_failure', 'pr_ignored', 'duplicate_job', 'api_error', 'malformed', 'hang')
$results = @()
foreach ($case in $cases) {
    $timeout = 10
    if ($case -eq 'pending_success') { $timeout = 40 }
    elseif ($case -in $timeoutCases) { $timeout = 1 }
    $summaryPath = Join-Path $artifacts ($case + '.summary.txt')
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $shell
    $info.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $helper + '" -Repository fixtureOwner/fixtureRepo -Commit ' + $commit + ' -TimeoutSeconds ' + $timeout
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    # Only the child receives a fake CLI and token; the calling session is unchanged.
    $info.EnvironmentVariables['PATH'] = $artifacts + ';' + $env:PATH
    $info.EnvironmentVariables['GH_TOKEN'] = $sentinel
    $info.EnvironmentVariables['GH_DEBUG'] = ''
    $info.EnvironmentVariables['GITHUB_STEP_SUMMARY'] = $summaryPath
    $info.EnvironmentVariables['CODEQL_MOCK_SCENARIO'] = $case
    $info.EnvironmentVariables['CODEQL_MOCK_COUNTER'] = Join-Path $artifacts ($case + '.counter')
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    $clock = [Diagnostics.Stopwatch]::StartNew()
    try {
        if (-not $process.Start()) { throw 'Cannot start an isolated CodeQL gate test.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(($timeout + 15) * 1000)) {
            try { $process.Kill() } catch { }
            throw ('CodeQL gate regression hung: ' + $case)
        }
        $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        $output | Set-Content -LiteralPath (Join-Path $artifacts ($case + '.log')) -Encoding utf8
        $succeeded = $process.ExitCode -eq 0
        if ($succeeded -ne ($case -in $passCases)) { throw ('Unexpected CodeQL gate result: ' + $case) }
        if ($output.Contains($sentinel)) { throw ('CodeQL gate exposed native API output: ' + $case) }
        if ($succeeded) {
            $summary = Get-Content -LiteralPath $summaryPath -Raw
            if ($summary -notmatch ('CodeQL verified for ' + $commit) -or $summary -notmatch 'workflow run 42\.') {
                throw ('CodeQL gate success summary is incomplete: ' + $case)
            }
        } elseif (Test-Path -LiteralPath $summaryPath) { throw ('A failed CodeQL gate wrote a success summary: ' + $case) }
        if ($case -in $timeoutCases -and $output -notmatch 'Timed out') { throw ('CodeQL gate did not reach its expected timeout: ' + $case) }
        if ($case -eq 'hang' -and $clock.Elapsed.TotalSeconds -ge 10) { throw 'The API process was not stopped by the gate deadline.' }
        if ($case -eq 'pending_success') {
            $polls = [int](Get-Content -LiteralPath $info.EnvironmentVariables['CODEQL_MOCK_COUNTER'] -Raw)
            if ($polls -lt 2 -or $clock.Elapsed.TotalSeconds -lt 15) { throw 'The CodeQL gate did not poll a pending workflow.' }
        }
        $results += [pscustomobject]@{ Case = $case; Passed = $true; ExitCode = $process.ExitCode; Seconds = [Math]::Round($clock.Elapsed.TotalSeconds, 2) }
        Write-Host ('PASS CodeQL gate: ' + $case)
    } finally { $process.Dispose() }
}
$results | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $artifacts 'results.json') -Encoding utf8
Write-Host ('CodeQL release gate: {0} regression scenarios passed.' -f $results.Count)
