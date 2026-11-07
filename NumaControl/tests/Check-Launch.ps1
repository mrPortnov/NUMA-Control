#requires -Version 5.1
param([int]$MeasureSeconds=0)
$ErrorActionPreference='Stop'
if($MeasureSeconds -ne 0 -and ($MeasureSeconds -lt 15 -or $MeasureSeconds -gt 120)){throw 'Use zero or 15-120 seconds for the resource measurement.'}
if(Get-Process -Name 'NUMA-Control' -ErrorAction SilentlyContinue) { throw 'A user instance is already running; do not interfere with it during this standalone smoke test.' }
$project=Split-Path -Parent $PSScriptRoot
$directory=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('artifacts\launch-'+[Guid]::NewGuid().ToString('N'))))
[void](New-Item -ItemType Directory -Path (Join-Path $directory 'data') -Force)
foreach($file in @('NUMA-Control.exe','NUMA-Control.exe.config')) { Copy-Item -LiteralPath (Join-Path $project $file) -Destination $directory }
@{Version=1;IdleIntervalMs=2000;ActiveIntervalMs=2000;Paused=$false;Games=@()}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $directory 'data\profiles.json') -Encoding UTF8
$processorPath=[IO.Path]::GetFullPath((Join-Path $directory 'data\processor.json'))
function Read-LaunchState($process,[string]$mode,[string]$snapshotPath) {
    $process.Refresh()
    if($process.HasExited) { throw "Actual release EXE exited: $mode" }
    $visible=$process.MainWindowHandle -ne [IntPtr]::Zero
    if($mode -eq 'tray' -and $visible) { throw 'Wrong window visibility: tray' }
    return [pscustomobject]@{Window=$visible;Responding=$process.Responding;Snapshot=(Test-Path -LiteralPath $snapshotPath -PathType Leaf)}
}
function Wait-LaunchReady($process,[string]$mode,[string]$snapshotPath,[double]$timeoutSeconds=20) {
    $deadline=[Diagnostics.Stopwatch]::StartNew()
    do {
        $state=Read-LaunchState $process $mode $snapshotPath
        if($deadline.Elapsed.TotalSeconds -ge $timeoutSeconds) {
            throw "Startup readiness timed out: $mode; window=$($state.Window); freshProcessor=$($state.Snapshot); responding=$($state.Responding)"
        }
        if($state.Window -eq ($mode -eq 'ordinary') -and $state.Snapshot -and $state.Responding) {
            return [pscustomobject]@{State=$state;Seconds=$deadline.Elapsed.TotalSeconds}
        }
        Start-Sleep -Milliseconds 100
    } while($true)
}
function Assert-LaunchStable($process,[string]$mode,[string]$snapshotPath) {
    $state=Read-LaunchState $process $mode $snapshotPath
    if($state.Window -ne ($mode -eq 'ordinary')) { throw "Wrong window visibility: $mode" }
    if(-not $state.Responding) { throw "Actual release EXE is not responding: $mode" }
    if(-not $state.Snapshot) { throw 'Hardware snapshot was not saved next to the executable.' }
    return $state
}
$results=@()
foreach($mode in @('ordinary','tray')) {
    $p=$null
    try {
        $dataDirectory=Join-Path $directory 'data'
        if(-not $processorPath.StartsWith(($directory.TrimEnd('\')+'\'),[StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetDirectoryName($processorPath) -ne $dataDirectory -or [IO.Path]::GetFileName($processorPath) -cne 'processor.json' -or
            ((Get-Item -LiteralPath $dataDirectory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Refusing to reset a processor snapshot outside this isolated launch directory.'
        }
        if(Test-Path -LiteralPath $processorPath) {
            if(-not(Test-Path -LiteralPath $processorPath -PathType Leaf)) { throw 'The isolated processor snapshot path is not a file.' }
            Remove-Item -LiteralPath $processorPath -Force
        }
        $parameters=@{FilePath=(Join-Path $directory 'NUMA-Control.exe');PassThru=$true}
        if($mode -eq 'tray') { $parameters.ArgumentList=@('--tray'); $parameters.WindowStyle='Hidden' }
        $p=Start-Process @parameters
        $ready=Wait-LaunchReady $p $mode $processorPath
        $stabilization=[Diagnostics.Stopwatch]::StartNew()
        do {
            $state=Assert-LaunchStable $p $mode $processorPath
            Start-Sleep -Milliseconds 100
        } while($stabilization.Elapsed.TotalSeconds -lt 4)
        $stabilization.Stop()
        $state=Assert-LaunchStable $p $mode $processorPath
        $window=$state.Window
        $resources=$null
        if($MeasureSeconds -gt 0) {
            $startCpu=$p.TotalProcessorTime.TotalSeconds;$clock=[Diagnostics.Stopwatch]::StartNew();$samples=@()
            while($clock.Elapsed.TotalSeconds -lt $MeasureSeconds) {
                Start-Sleep -Seconds 1;$state=Assert-LaunchStable $p $mode $processorPath
                $samples+=[pscustomobject]@{PrivateMiB=$p.PrivateMemorySize64/1MB;WorkingMiB=$p.WorkingSet64/1MB;Handles=$p.HandleCount}
            }
            $cpu=100*($p.TotalProcessorTime.TotalSeconds-$startCpu)/$clock.Elapsed.TotalSeconds
            $resources=[pscustomobject]@{DurationSeconds=$clock.Elapsed.TotalSeconds;CpuOneCorePercent=$cpu;CpuMachinePercent=$cpu/[Environment]::ProcessorCount;PeakPrivateMiB=($samples|Measure-Object PrivateMiB -Maximum).Maximum;PeakWorkingMiB=($samples|Measure-Object WorkingMiB -Maximum).Maximum;PeakHandles=($samples|Measure-Object Handles -Maximum).Maximum}
            if($cpu -gt 5 -or $resources.PeakPrivateMiB -gt 512 -or $resources.PeakWorkingMiB -gt 768 -or $resources.PeakHandles -gt 6000){throw 'Actual release EXE exceeded idle resource gates'}
        }
        $results+=[pscustomobject]@{Mode=$mode;Window=$window;PortableData=$true;ReadinessSeconds=$ready.Seconds;StabilizationSeconds=$stabilization.Elapsed.TotalSeconds;Resources=$resources;Passed=$true}
    } finally { if($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force }; if($p) { $p.Dispose() } }
}
@{Date=(Get-Date -Format o);SHA256=(Get-FileHash -LiteralPath (Join-Path $project 'NUMA-Control.exe') -Algorithm SHA256).Hash;MeasureSeconds=$MeasureSeconds;Processors=[Environment]::ProcessorCount;Note='Actual production EXE in isolated portable folder, empty library, normal and tray startup. External process CPU time. No UI Automation.';Checks=$results}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $PSScriptRoot 'artifacts\launch-results.json') -Encoding UTF8
$results|ConvertTo-Json
