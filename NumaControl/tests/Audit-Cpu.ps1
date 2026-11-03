#requires -Version 5.1
param([string]$Label = 'baseline-1.1', [int]$Seconds = 30, [string]$HarnessDirectory, [switch]$Visible, [switch]$SoftwareOnly)
$ErrorActionPreference = 'Stop'
if($Seconds -lt 15 -or $Seconds -gt 120) { throw 'Use 15 to 120 seconds.' }
$artifacts = Join-Path $PSScriptRoot 'artifacts'
if(-not $HarnessDirectory) { $HarnessDirectory=(Get-ChildItem -LiteralPath $artifacts -Directory | Sort-Object LastWriteTime -Descending | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'test-results.json') } | Select-Object -First 1).FullName }
$harness=Join-Path $HarnessDirectory 'NumaTests.exe'
$fixture=(Get-ChildItem -LiteralPath $HarnessDirectory -Filter 'NumaFixture*.exe' | Select-Object -First 1).FullName
$data=Join-Path $artifacts ('cpu-audit-'+[Guid]::NewGuid().ToString('N')); [void](New-Item -ItemType Directory -Path $data)
$privateFixture=Join-Path $data ('NumaAuditFixture'+[Guid]::NewGuid().ToString('N').Substring(0,8)+'.exe')
Copy-Item -LiteralPath $fixture -Destination $privateFixture; $fixture=$privateFixture
$logical=[Environment]::ProcessorCount
$fullMask=if($logical -eq 64) { [uint64]::MaxValue } else { ([uint64]1 -shl $logical)-1 }
$gameMask=if($logical -gt 4) { $fullMask -bxor ([uint64]3 -bor ([uint64]3 -shl ($logical-2))) } else { $fullMask }
$profile=@{Version=1;IdleIntervalMs=2000;ActiveIntervalMs=1000;Paused=$false;Games=@(
    @{Id=[Guid]::NewGuid().ToString('N');Name='CPU audit game';ProcessName=[IO.Path]::GetFileNameWithoutExtension($fixture);ExePath=$fixture;LaunchTarget=$fixture;Arguments='';Mask=$gameMask.ToString('X');Enabled=$true;HighPriority=$true;Color='#8C83FF'},
    @{Id=[Guid]::NewGuid().ToString('N');Name='Idle audit A';ProcessName='NumaCpuAuditMissingA';ExePath='';LaunchTarget='';Arguments='';Mask=$gameMask.ToString('X');Enabled=$true;HighPriority=$true;Color='#8C83FF'},
    @{Id=[Guid]::NewGuid().ToString('N');Name='Idle audit B';ProcessName='NumaCpuAuditMissingB';ExePath='';LaunchTarget='';Arguments='';Mask=$gameMask.ToString('X');Enabled=$true;HighPriority=$true;Color='#8C83FF'}
)}
$profile | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $data 'profiles.json') -Encoding UTF8
function Send-Audit([string]$command) {
    $pipe=[IO.Pipes.NamedPipeClientStream]::new('.',$script:pipeName,[IO.Pipes.PipeDirection]::InOut)
    try { $pipe.Connect(5000); $bytes=[Text.Encoding]::UTF8.GetBytes($command+"`n"); $pipe.Write($bytes,0,$bytes.Length); $pipe.Flush(); if($pipe.ReadByte() -ne 1) { throw 'Missing acknowledgement' } } finally { $pipe.Dispose() }
}
function Measure-Audit([string]$name,[string]$command,[int]$duration=$Seconds) {
    if($command) { Send-Audit $command }; Start-Sleep -Seconds 3
    $script:monitor.Refresh(); $cpuStart=$script:monitor.TotalProcessorTime.TotalSeconds
    $startUtc=[DateTime]::UtcNow; $clock=[Diagnostics.Stopwatch]::StartNew(); $lastCpu=$cpuStart; $lastTime=0.0
    $samples=[Collections.Generic.List[object]]::new()
    while($clock.Elapsed.TotalSeconds -lt $duration) {
        Start-Sleep -Milliseconds 1000; $script:monitor.Refresh(); if($script:monitor.HasExited) { throw "Audit process exited: $($script:monitor.ExitCode)" }
        $time=$clock.Elapsed.TotalSeconds; $cpu=$script:monitor.TotalProcessorTime.TotalSeconds
        $samples.Add([pscustomobject]@{TimeUtc=[DateTime]::UtcNow.ToString('o');Seconds=$time;CumulativeCpuSeconds=$cpu;CpuSeconds=$cpu-$lastCpu;IntervalSeconds=$time-$lastTime;OneCorePercent=100*($cpu-$lastCpu)/($time-$lastTime);MachinePercent=100*($cpu-$lastCpu)/($time-$lastTime)/[Environment]::ProcessorCount;WorkingMiB=$script:monitor.WorkingSet64/1MB})
        $lastCpu=$cpu; $lastTime=$time
    }
    $elapsed=$clock.Elapsed.TotalSeconds; $cpuTotal=$lastCpu-$cpuStart; $endUtc=[DateTime]::UtcNow
    $historyPath=Join-Path $script:data 'cpu-history.json'
    if(Test-Path -LiteralPath $historyPath) { Remove-Item -LiteralPath $historyPath }
    Send-Audit 'launch cpu-history'
    $historyWait=[Diagnostics.Stopwatch]::StartNew()
    while(-not(Test-Path -LiteralPath $historyPath)) { if($historyWait.Elapsed.TotalSeconds -gt 10) { throw 'History command did not finish.' }; Start-Sleep -Milliseconds 50 }
    $history=Get-Content -LiteralPath $historyPath -Raw | ConvertFrom-Json
    $internal=@($history | Where-Object { $t=if($_.TimeUtc -is [DateTime]) { $_.TimeUtc.ToUniversalTime() } else { [DateTime]::Parse($_.TimeUtc,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime() }; $t -ge $startUtc.AddSeconds(5) -and $t -le $endUtc })
    $internalMean=if($internal.Count) { ($internal | Measure-Object CpuOneCorePercent -Average).Average } else { $null }
    if($internal.Count -eq 0) { throw "No internal CPU samples in phase $name" }
    $record=[pscustomobject]@{Phase=$name;StartUtc=$startUtc.ToString('o');EndUtc=$endUtc.ToString('o');CpuStartSeconds=$cpuStart;ElapsedSeconds=$elapsed;CpuSeconds=$cpuTotal;OneCorePercent=100*$cpuTotal/$elapsed;MachinePercent=100*$cpuTotal/$elapsed/[Environment]::ProcessorCount;PeakOneSecondCorePercent=($samples|Measure-Object OneCorePercent -Maximum).Maximum;MeanWorkingMiB=($samples|Measure-Object WorkingMiB -Average).Average;InternalMeanCorePercent=$internalMean;Samples=$samples.ToArray();InternalSamples=$internal}
    Write-Host "$name : external $([Math]::Round($record.OneCorePercent,3))% core / $([Math]::Round($record.MachinePercent,4))% machine; internal $([Math]::Round([double]$internalMean,3))% core"
    return $record
}
$monitor=$null; $game=$null; $pipeName=$null
try {
    $mode=if($Visible) { '--monitor-visible' } else { '--monitor' }
    if($SoftwareOnly) { if(-not $Visible) { throw 'SoftwareOnly requires Visible.' }; $mode='--monitor-software-visible' }
    $monitor=Start-Process -FilePath $harness -ArgumentList @($mode,('"'+$data+'"'),[Guid]::NewGuid().ToString('N')) -WindowStyle Hidden -PassThru
    $ready=Join-Path $data 'ready.txt'; $waiting=[Diagnostics.Stopwatch]::StartNew()
    while(-not(Test-Path -LiteralPath $ready)) { if($waiting.Elapsed.TotalSeconds -gt 20) { throw 'Monitor startup timeout' }; Start-Sleep -Milliseconds 100 }
    $pipeName=Get-Content -LiteralPath $ready -Raw
    $records=@(); $records+=Measure-Audit 'tray-idle' ''
    $records+=Measure-Audit 'main-window-idle' 'launch main'
    $records+=Measure-Audit 'diagnostics-idle' 'launch diagnostics'
    $records+=Measure-Audit 'diagnostics-refresh-10Hz' 'launch refresh-burst' 20
    Send-Audit 'launch hide'
    $game=Start-Process -FilePath $fixture -WindowStyle Hidden -PassThru
    $records+=Measure-Audit 'tray-game-active' ''
    $records+=Measure-Audit 'diagnostics-game-active' 'launch diagnostics'
    $records+=Measure-Audit 'known-60-percent-core-burst' 'launch cpu-burst' 20
    $tested=Get-Content -LiteralPath (Join-Path $HarnessDirectory 'test-results.json') -Raw | ConvertFrom-Json
    $report=@{Date=(Get-Date -Format o);Label=$Label;Version=$tested.Version;ReleaseSha256=$tested.ReleaseSha256;OnScreenWindows=[bool]$Visible;SoftwareTestOverride=[bool]$SoftwareOnly;Processors=[Environment]::ProcessorCount;Harness=$harness;DataDirectory=$data;Notes='Independent cumulative process CPU time sampled externally at 1 Hz; internal samples exclude the first boundary interval. Game fixture sleeps. The 60 percent phase intentionally burns CPU only in test code. No UI automation during idle phases.';Records=$records}
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $artifacts ('cpu-audit-'+$Label+'.json')) -Encoding UTF8
    Write-Host "CPU audit complete: $Label"
} finally {
    if($monitor -and -not $monitor.HasExited) { try { if($pipeName) { Send-Audit 'launch stop'; [void]$monitor.WaitForExit(5000) } } finally { if(-not $monitor.HasExited) { Stop-Process -Id $monitor.Id -Force } } }
    if($game -and -not $game.HasExited) { Stop-Process -Id $game.Id -Force }
}
