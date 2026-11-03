#requires -Version 5.1
param([int]$SoakSeconds = 300, [switch]$FaultsOnly, [string]$ResultName = 'resilience-results')
$ErrorActionPreference = 'Stop'
if($ResultName -notmatch '^[a-zA-Z0-9.-]+$') { throw 'ResultName must be a plain report name.' }
if ($SoakSeconds -lt 120 -or $SoakSeconds -gt 3600) { throw 'Use a soak duration between 120 and 3600 seconds.' }
$artifacts = Join-Path $PSScriptRoot 'artifacts'
$latestRun = Get-ChildItem -LiteralPath $artifacts -Directory | Sort-Object LastWriteTime -Descending | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'test-results.json') } | Select-Object -First 1
if (-not $latestRun) { throw 'Run the integration tests first.' }
$harness = Join-Path $latestRun.FullName 'NumaTests.exe'
$tested = Get-Content -LiteralPath (Join-Path $latestRun.FullName 'test-results.json') -Raw | ConvertFrom-Json
$fixture = (Get-ChildItem -LiteralPath $latestRun.FullName -Filter 'NumaFixture*.exe' | Select-Object -First 1).FullName
$run = Join-Path $artifacts ('resilience-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $run)
$privateFixture=Join-Path $run ('NumaSoakFixture'+[Guid]::NewGuid().ToString('N').Substring(0,8)+'.exe')
Copy-Item -LiteralPath $fixture -Destination $privateFixture; $fixture=$privateFixture
$faults = @()
foreach ($mode in @('ui-exception','ui-hang','engine-stop','memory','background-exception','task-exception')) {
    $directory = Join-Path $run $mode
    $child = Start-Process -FilePath $harness -ArgumentList @('--fault',('"'+$directory+'"'),$mode) -WindowStyle Hidden -PassThru
    $faults += [pscustomobject]@{Mode=$mode;Directory=$directory;Process=$child;Started=[Diagnostics.Stopwatch]::StartNew()}
}
$faultResults = @()
foreach ($fault in $faults) {
    try {
        if (-not $fault.Process.WaitForExit(30000)) { Stop-Process -Id $fault.Process.Id -Force; throw "Fault host did not terminate: $($fault.Mode)" }
        $marker = Join-Path $fault.Directory 'bug-reports\last-failure.json'
        if ($fault.Process.ExitCode -ne 70 -or -not (Test-Path -LiteralPath $marker)) { throw "Fault host exit/report failed: $($fault.Mode), exit $($fault.Process.ExitCode)" }
        $details = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
        $reportFile = Join-Path $fault.Directory ('bug-reports\'+$details.Report)
        if (-not (Test-Path -LiteralPath $reportFile)) { throw 'Missing full fault report.' }
        $faultResults += [pscustomobject]@{Mode=$fault.Mode;ExitCode=$fault.Process.ExitCode;Reason=$details.Reason;Report=$reportFile;Passed=$true}
        Write-Host "PASS emergency exit and report: $($fault.Mode) -> $($details.Reason)"
    } finally { $fault.Process.Dispose() }
}
if ($FaultsOnly) {
    @{Date=(Get-Date -Format o);Faults=$faultResults;Notes='Isolated subprocesses. Production exception hooks; memory and hang thresholds are lowered only by the test host.'} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $artifacts 'fault-results.json') -Encoding UTF8
    return
}
function Send-TestCommand([string]$name,[string]$command) {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.',$name,[IO.Pipes.PipeDirection]::InOut)
    try { $pipe.Connect(5000); $bytes=[Text.Encoding]::UTF8.GetBytes($command+"`n"); $pipe.Write($bytes,0,$bytes.Length); $pipe.Flush(); if($pipe.ReadByte() -ne 1) { throw 'IPC acknowledgement missing.' } } finally { $pipe.Dispose() }
}
$data = Join-Path $run 'soak'
[void](New-Item -ItemType Directory -Path $data)
$profiles = @{Version=1;IdleIntervalMs=2000;ActiveIntervalMs=1000;Paused=$false;Games=@(
    @{Id=[Guid]::NewGuid().ToString('N');Name='Soak fixture';ProcessName=[IO.Path]::GetFileNameWithoutExtension($fixture);ExePath=$fixture;LaunchTarget=$fixture;Arguments='';Mask='4';Enabled=$true;HighPriority=$true;Color='#8C83FF'},
    @{Id=[Guid]::NewGuid().ToString('N');Name='Empty soak A';ProcessName='NumaSoakMissingA';ExePath='';LaunchTarget='';Arguments='';Mask='4';Enabled=$true;HighPriority=$true;Color='#8C83FF'},
    @{Id=[Guid]::NewGuid().ToString('N');Name='Empty soak B';ProcessName='NumaSoakMissingB';ExePath='';LaunchTarget='';Arguments='';Mask='4';Enabled=$true;HighPriority=$true;Color='#8C83FF'}
)}
$profiles | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $data 'profiles.json') -Encoding UTF8
$identity=[Guid]::NewGuid().ToString('N'); $monitor=$null; $game=$null; $pipeName=$null
$samples=[Collections.Generic.List[object]]::new(); $cycles=0; $restarts=0; $applied=0
try {
    $monitor=Start-Process -FilePath $harness -ArgumentList @('--monitor',('"'+$data+'"'),$identity) -WindowStyle Hidden -PassThru
    $ready=Join-Path $data 'ready.txt'; $waiting=[Diagnostics.Stopwatch]::StartNew()
    while(-not(Test-Path -LiteralPath $ready)) { if($waiting.Elapsed.TotalSeconds -gt 20) { throw 'Soak monitor did not become ready.' }; Start-Sleep -Milliseconds 100 }
    $pipeName=Get-Content -LiteralPath $ready -Raw
    $clock=[Diagnostics.Stopwatch]::StartNew(); $lastCpu=0.0; $lastElapsed=0.0
    while($clock.Elapsed.TotalSeconds -lt $SoakSeconds) {
        $elapsed=$clock.Elapsed.TotalSeconds
        if($elapsed -lt 75) { Send-TestCommand $pipeName 'show'; Send-TestCommand $pipeName 'launch settings'; Send-TestCommand $pipeName 'launch diagnostics'; Send-TestCommand $pipeName 'launch hide'; $cycles++ }
        if(([int]$elapsed % 30) -lt 6) {
            if($game -and -not $game.HasExited) { Stop-Process -Id $game.Id -Force; $game.Dispose(); $game=$null }
            $game=Start-Process -FilePath $fixture -WindowStyle Hidden -PassThru; $restarts++
        }
        Start-Sleep -Seconds 5
        $monitor.Refresh(); if($monitor.HasExited) { throw "Soak monitor exited unexpectedly: $($monitor.ExitCode)" }
        if($game -and -not $game.HasExited) { $game.Refresh(); if($game.ProcessorAffinity.ToInt64() -ne 4) { throw 'Game affinity was not maintained during soak.' }; $applied++ }
        $now=$clock.Elapsed.TotalSeconds; $cpu=$monitor.TotalProcessorTime.TotalSeconds
        $samples.Add([pscustomobject]@{Seconds=$now;WorkingMiB=$monitor.WorkingSet64/1MB;PrivateMiB=$monitor.PrivateMemorySize64/1MB;Handles=$monitor.HandleCount;Threads=$monitor.Threads.Count;CpuMachinePercent=100*($cpu-$lastCpu)/[Math]::Max(0.001,($now-$lastElapsed))/[Environment]::ProcessorCount})
        $lastCpu=$cpu; $lastElapsed=$now
        if($samples.Count % 12 -eq 0) { Write-Host "Soak $([int]$now)/$SoakSeconds s, UI cycles $cycles, restarts $restarts, private $([Math]::Round($monitor.PrivateMemorySize64/1MB,1)) MiB" }
    }
    Send-TestCommand $pipeName 'launch report'
    $steady=@($samples | Where-Object Seconds -gt 90)
    $privateGrowth=if($steady.Count -gt 1) { $steady[-1].PrivateMiB-$steady[0].PrivateMiB } else { 0 }
    $handleGrowth=if($steady.Count -gt 1) { $steady[-1].Handles-$steady[0].Handles } else { 0 }
    if($privateGrowth -gt 32 -or $handleGrowth -gt 150) { throw "Unexpected steady-state growth: $privateGrowth MiB, $handleGrowth handles" }
    $result=@{Date=(Get-Date -Format o);Version=$tested.Version;ReleaseSha256=$tested.ReleaseSha256;Harness=$harness;Faults=$faultResults;Soak=@{DurationSeconds=$clock.Elapsed.TotalSeconds;UiCycles=$cycles;GameRestarts=$restarts;AffinityChecks=$applied;SteadyPrivateGrowthMiB=$privateGrowth;SteadyHandleGrowth=$handleGrowth;Passed=$true;Samples=$samples.ToArray()};Notes='Fault limits are lowered only in the test entry point; UI hang verifies the fallback exit timer. Production limits remain unchanged. Soak uses a sleeping fixture, not a real game.'}
    $result | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $artifacts ($ResultName+'.json')) -Encoding UTF8
    Write-Host "PASS resilience and soak: $artifacts\$ResultName.json"
} finally {
    if($monitor -and -not $monitor.HasExited) { try { if($pipeName) { Send-TestCommand $pipeName 'launch stop'; [void]$monitor.WaitForExit(5000) } } finally { if(-not $monitor.HasExited) { Stop-Process -Id $monitor.Id -Force } } }
    if($game -and -not $game.HasExited) { Stop-Process -Id $game.Id -Force; $game.Dispose() }
}
