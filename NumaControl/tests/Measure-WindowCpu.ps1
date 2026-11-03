#requires -Version 5.1
param([Parameter(Mandatory=$true)][int]$TargetProcessId,[Parameter(Mandatory=$true)][string]$Phase,[int]$Seconds=30)
$ErrorActionPreference='Stop'
if($Seconds -lt 10 -or $Seconds -gt 120) { throw 'Use 10 to 120 seconds.' }
$target=[Diagnostics.Process]::GetProcessById($TargetProcessId)
try {
    $target.Refresh(); $cpuStart=$target.TotalProcessorTime.TotalSeconds; $lastCpu=$cpuStart; $lastTime=0.0
    $clock=[Diagnostics.Stopwatch]::StartNew(); $startUtc=[DateTime]::UtcNow
    $samples=[Collections.Generic.List[object]]::new()
    while($clock.Elapsed.TotalSeconds -lt $Seconds) {
        Start-Sleep -Milliseconds 1000; $target.Refresh()
        if($target.HasExited) { throw 'Target exited during measurement.' }
        $cpu=$target.TotalProcessorTime.TotalSeconds; $time=$clock.Elapsed.TotalSeconds
        $samples.Add([pscustomobject]@{TimeUtc=[DateTime]::UtcNow.ToString('o');ElapsedSeconds=$time;CpuSeconds=$cpu;CpuOneCorePercent=100*($cpu-$lastCpu)/($time-$lastTime)})
        $lastCpu=$cpu; $lastTime=$time
    }
    $result=[pscustomobject]@{Phase=$Phase;StartUtc=$startUtc.ToString('o');EndUtc=[DateTime]::UtcNow.ToString('o');Seconds=$lastTime;CpuSeconds=$lastCpu-$cpuStart;OneCorePercent=100*($lastCpu-$cpuStart)/$lastTime;MachinePercent=100*($lastCpu-$cpuStart)/$lastTime/[Environment]::ProcessorCount;PeakOneSecondCorePercent=($samples|Measure-Object CpuOneCorePercent -Maximum).Maximum;Samples=$samples.ToArray()}
    $result|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $PSScriptRoot ('artifacts\window-cpu-'+$Phase+'.json')) -Encoding UTF8
    $result|Select-Object Phase,OneCorePercent,MachinePercent,PeakOneSecondCorePercent|ConvertTo-Json
} finally { $target.Dispose() }
