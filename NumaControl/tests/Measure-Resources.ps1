#requires -Version 5.1
param([int]$SecondsPerPhase = 20)
$ErrorActionPreference = 'Stop'
if ($SecondsPerPhase -lt 10 -or $SecondsPerPhase -gt 60) { throw 'Use 10 to 60 seconds per phase.' }
$artifacts = Join-Path $PSScriptRoot 'artifacts'
$latestRun = Get-ChildItem -LiteralPath $artifacts -Directory | Sort-Object LastWriteTime -Descending | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'test-results.json') } | Select-Object -First 1
if (-not $latestRun) { throw 'Run the integration tests first.' }
$harness = Join-Path $latestRun.FullName 'NumaTests.exe'
$fixture = (Get-ChildItem -LiteralPath $latestRun.FullName -Filter 'NumaFixture*.exe' | Select-Object -First 1).FullName
$data = Join-Path $artifacts ('measure-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $data)
$identity = [Guid]::NewGuid().ToString('N')
$id1 = [Guid]::NewGuid().ToString('N')
$profiles = @{ Version=1; IdleIntervalMs=2000; ActiveIntervalMs=1000; Paused=$false; Games=@(
    @{Id=$id1;Name='Affinity test';ProcessName=[IO.Path]::GetFileNameWithoutExtension($fixture);ExePath=$fixture;LaunchTarget=$fixture;Arguments='';Mask='4';Enabled=$true;Color='#8C83FF'},
    @{Id=[Guid]::NewGuid().ToString('N');Name='Idle test 2';ProcessName='NumaNoGameA';ExePath='';LaunchTarget='';Arguments='';Mask='4';Enabled=$true;Color='#8C83FF'},
    @{Id=[Guid]::NewGuid().ToString('N');Name='Idle test 3';ProcessName='NumaNoGameB';ExePath='';LaunchTarget='';Arguments='';Mask='4';Enabled=$true;Color='#8C83FF'}
)}
$profiles | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $data 'profiles.json') -Encoding UTF8
$monitor = $null
$game = $null
function Send-LocalCommand([string]$pipeName, [string]$command) {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(5000)
        $bytes = [Text.Encoding]::UTF8.GetBytes($command + "`n")
        $pipe.Write($bytes,0,$bytes.Length)
        $pipe.Flush()
        if ($pipe.ReadByte() -ne 1) { throw 'No IPC acknowledgement.' }
    } finally { $pipe.Dispose() }
}
function Measure-Phase([string]$phase) {
    $monitor.Refresh()
    $beforeCpu = $monitor.TotalProcessorTime.TotalSeconds
    $beforeHandles = $monitor.HandleCount
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $working = [Collections.Generic.List[double]]::new()
    $private = [Collections.Generic.List[double]]::new()
    for ($sample=0; $sample -lt $SecondsPerPhase; $sample++) {
        Start-Sleep -Seconds 1
        $monitor.Refresh()
        if ($monitor.HasExited) { throw 'Monitor exited during measurement.' }
        $working.Add($monitor.WorkingSet64 / 1MB)
        $private.Add($monitor.PrivateMemorySize64 / 1MB)
    }
    $cpu = $monitor.TotalProcessorTime.TotalSeconds - $beforeCpu
    $record = [pscustomobject]@{ Phase=$phase;ElapsedSeconds=$clock.Elapsed.TotalSeconds;CpuSeconds=$cpu;OneCorePercent=100*$cpu/$clock.Elapsed.TotalSeconds;WholeMachinePercent=100*$cpu/$clock.Elapsed.TotalSeconds/[Environment]::ProcessorCount;MeanWorkingMiB=($working|Measure-Object -Average).Average;MeanPrivateMiB=($private|Measure-Object -Average).Average;HandleCountBefore=$beforeHandles;HandleCountAfter=$monitor.HandleCount }
    Write-Host "$phase : CPU $([Math]::Round($record.WholeMachinePercent,4))% ; Working $([Math]::Round($record.MeanWorkingMiB,1)) MiB"
    return $record
}
try {
    $monitor = Start-Process -FilePath $harness -ArgumentList @('--monitor',('"'+$data+'"'),$identity) -PassThru -WindowStyle Hidden
    $readyPath = Join-Path $data 'ready.txt'
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $readyPath)) { if($deadline.Elapsed.TotalSeconds -gt 15) { throw 'Monitor did not become ready.' }; Start-Sleep -Milliseconds 100 }
    $pipeName = Get-Content -LiteralPath $readyPath -Raw
    Start-Sleep -Seconds 3
    $records = @()
    $records += Measure-Phase 'Tray, three profiles, no game'
    $game = Start-Process -FilePath $fixture -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 3
    $records += Measure-Phase 'Tray, test game active'
    Send-LocalCommand $pipeName 'show'
    Start-Sleep -Seconds 3
    Send-LocalCommand $pipeName 'launch hide'
    Start-Sleep -Seconds 3
    $records += Measure-Phase 'Tray after opening and releasing window, game active'
    $report = @{Date=(Get-Date -Format o);LogicalProcessors=[Environment]::ProcessorCount;Notes='Same AppController, WatchEngine, tray and IPC code as release; separate test entry point and isolated profiles. Sleeping fixture, not a game or an FPS benchmark.';Records=$records}
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $artifacts 'performance-results.json') -Encoding UTF8
    Write-Host "Results: $artifacts\performance-results.json"
} finally {
    if ($monitor -and -not $monitor.HasExited) {
        if ($pipeName) { Send-LocalCommand $pipeName 'launch stop'; [void]$monitor.WaitForExit(5000) }
        if (-not $monitor.HasExited) { Stop-Process -Id $monitor.Id -Force }
    }
    if ($game -and -not $game.HasExited) { Stop-Process -Id $game.Id -Force }
}
