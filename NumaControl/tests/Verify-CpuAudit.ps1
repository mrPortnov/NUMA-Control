#requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$Report)
$ErrorActionPreference='Stop'
$audit=Get-Content -LiteralPath $Report -Raw|ConvertFrom-Json
$checks=[Collections.Generic.List[object]]::new()
function Check([bool]$passed,[string]$name) { $checks.Add([pscustomobject]@{Name=$name;Passed=$passed}); if(-not $passed) { throw "CPU regression: $name" } }
Check ($audit.OnScreenWindows -eq $true) 'Final audit uses real on-screen windows'
Check ($audit.Processors -gt 0) 'Machine CPU denominator is positive'
foreach($phase in $audit.Records) {
    Check ([Math]::Abs($phase.OneCorePercent/$audit.Processors-$phase.MachinePercent) -lt 0.000001) ($phase.Phase+': external normalization')
    Check ($phase.InternalSamples.Count -gt 0) ($phase.Phase+': internal samples present')
    if($phase.Phase -notlike 'known-*') {
        $limit=if($phase.Phase -eq 'diagnostics-refresh-10Hz') { 10 } else { 5 }
        Check ($phase.OneCorePercent -lt $limit) ($phase.Phase+': mean CPU below '+$limit+' percent of one logical CPU')
        Check ([Math]::Abs($phase.InternalMeanCorePercent-$phase.OneCorePercent) -lt [Math]::Max(1.0,$phase.OneCorePercent*0.5)) ($phase.Phase+': internal mean agrees with independent process CPU time within interval-boundary tolerance')
    } else {
        Check ($phase.PeakOneSecondCorePercent -gt 30 -and $phase.PeakOneSecondCorePercent -lt 150) 'Known CPU burn is actually measured externally'
        Check (($phase.InternalSamples|Measure-Object CpuOneCorePercent -Maximum).Maximum -gt 30) 'Known CPU burn is detected internally without hiding the burst'
    }
    foreach($sample in $phase.InternalSamples) {
        Check ($sample.CpuIntervalSeconds -ge 5) ($phase.Phase+': CPU interval is at least five seconds')
        Check ([Math]::Abs($sample.CpuOneCorePercent/$audit.Processors-$sample.CpuMachinePercent) -lt 0.000001) ($phase.Phase+': internal normalization')
    }
}
$output=Join-Path (Split-Path -Parent $Report) 'cpu-regression-results.json'
@{Date=(Get-Date -Format o);Report=(Split-Path -Leaf $Report);ReleaseSha256=$audit.ReleaseSha256;Passed=$checks.Count;Checks=$checks.ToArray();Note='Idle threshold: 5 percent of one logical CPU; repeated Refresh: 10 percent. Deliberate CPU fixture is excluded from the low-load gate. Do not run with an active UI Automation polling session.'}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $output -Encoding UTF8
Write-Host "PASS $($checks.Count) CPU regression checks"
