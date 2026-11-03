#requires -Version 5.1
param([string]$Label='stress',[int]$Seconds=25,[string]$HarnessDirectory,[switch]$Quick)
$ErrorActionPreference='Stop'
if($Seconds -lt 10 -or $Seconds -gt 120) { throw 'Seconds must be 10-120.' }
$artifacts=Join-Path $PSScriptRoot 'artifacts'
if(-not $HarnessDirectory) { $HarnessDirectory=(Get-ChildItem -LiteralPath $artifacts -Directory|Sort-Object LastWriteTime -Descending|Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'test-results.json') }|Select-Object -First 1).FullName }
$harness=Join-Path $HarnessDirectory 'NumaTests.exe'
$source=(Get-ChildItem -LiteralPath $HarnessDirectory -Filter 'NumaFixture*.exe'|Select-Object -First 1).FullName
$run=Join-Path $artifacts ('stress-'+[Guid]::NewGuid().ToString('N')); [void](New-Item -ItemType Directory -Path $run)
$cases=if($Quick) { @(@{Profiles=100;Processes=100}) } else { @(@{Profiles=3;Processes=0},@{Profiles=25;Processes=8},@{Profiles=100;Processes=0},@{Profiles=100;Processes=32},@{Profiles=100;Processes=100},@{Profiles=100;Processes=200}) }
$records=@()
function Send-Stress([string]$command) {
    $pipe=[IO.Pipes.NamedPipeClientStream]::new('.',$script:pipeName,[IO.Pipes.PipeDirection]::InOut)
    try { $pipe.Connect(10000); $bytes=[Text.Encoding]::UTF8.GetBytes($command+"`n"); $pipe.Write($bytes,0,$bytes.Length); $pipe.Flush(); if($pipe.ReadByte() -ne 1) { throw 'Missing pipe acknowledgement' } } finally { $pipe.Dispose() }
}
foreach($case in $cases) {
    $folder=Join-Path $run ($case.Profiles.ToString()+'-profiles-'+$case.Processes+'-processes'); [void](New-Item -ItemType Directory -Path $folder)
    $games=@(); $executables=@(); $prefix='NumaStress'+[Guid]::NewGuid().ToString('N').Substring(0,6)
    for($i=0;$i -lt $case.Profiles;$i++) {
        $exe=Join-Path $folder ($prefix+$i+'.exe'); Copy-Item -LiteralPath $source -Destination $exe; $executables+=$exe
        $games+=@{Id=[Guid]::NewGuid().ToString('N');Name='Stress game '+$i;ProcessName=[IO.Path]::GetFileNameWithoutExtension($exe);ExePath=$exe;LaunchTarget=$exe;Arguments='';Mask='4';Enabled=$true;HighPriority=$true;Color='#8C83FF'}
    }
    @{Version=1;IdleIntervalMs=2000;ActiveIntervalMs=1000;Paused=$false;Games=$games}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $folder 'profiles.json') -Encoding UTF8
    $children=[Collections.Generic.List[Diagnostics.Process]]::new(); $monitor=$null; $pipeName=$null
    try {
        for($i=0;$i -lt $case.Processes;$i++) { $children.Add((Start-Process -FilePath $executables[$i % $executables.Count] -WindowStyle Hidden -PassThru)) }
        $monitor=Start-Process -FilePath $harness -ArgumentList @('--monitor',('"'+$folder+'"'),[Guid]::NewGuid().ToString('N')) -WindowStyle Hidden -PassThru
        $ready=Join-Path $folder 'ready.txt'; $startup=[Diagnostics.Stopwatch]::StartNew()
        while(-not(Test-Path -LiteralPath $ready)) { if($startup.Elapsed.TotalSeconds -gt 25 -or $monitor.HasExited) { throw 'Stress monitor failed to start' }; Start-Sleep -Milliseconds 100 }
        $pipeName=Get-Content -LiteralPath $ready -Raw
        $settle=[Diagnostics.Stopwatch]::StartNew(); $applied=0
        do {
            Start-Sleep -Seconds 1; $applied=0
            foreach($child in $children) { $child.Refresh(); if(-not $child.HasExited -and $child.ProcessorAffinity.ToInt64() -eq 4) { $applied++ } }
            if($monitor.HasExited) { throw 'Stress monitor exited during warmup' }
        } while($applied -ne $case.Processes -and $settle.Elapsed.TotalSeconds -lt 45)
        if($applied -ne $case.Processes) { throw "Only $applied/$($case.Processes) target affinities applied" }; foreach($child in $children) { $child.Refresh(); if($child.PriorityClass -ne [Diagnostics.ProcessPriorityClass]::High) { throw 'High priority was not applied during warmup' } }
        Start-Sleep -Seconds 5
        $monitor.Refresh(); $initialCpu=$monitor.TotalProcessorTime.TotalSeconds; $initialPrivate=$monitor.PrivateMemorySize64/1MB; $initialHandles=$monitor.HandleCount
        $clock=[Diagnostics.Stopwatch]::StartNew(); $samples=@()
        while($clock.Elapsed.TotalSeconds -lt $Seconds) {
            Start-Sleep -Seconds 1; $monitor.Refresh(); if($monitor.HasExited) { throw "Stress monitor failed: $($monitor.ExitCode)" }
            $samples += [pscustomobject]@{Seconds=$clock.Elapsed.TotalSeconds;CpuSeconds=$monitor.TotalProcessorTime.TotalSeconds;PrivateMiB=$monitor.PrivateMemorySize64/1MB;WorkingMiB=$monitor.WorkingSet64/1MB;Handles=$monitor.HandleCount;Threads=$monitor.Threads.Count}
        }
        $cpu=$monitor.TotalProcessorTime.TotalSeconds-$initialCpu; $elapsed=$clock.Elapsed.TotalSeconds
        $verified=0; foreach($child in $children) { $child.Refresh(); if(-not $child.HasExited -and $child.ProcessorAffinity.ToInt64() -eq 4) { $verified++ } }
        if($verified -ne $case.Processes) { throw 'A target exited or lost its affinity' }; $verifiedPriorities=0; foreach($child in $children) { $child.Refresh(); if($child.PriorityClass -eq [Diagnostics.ProcessPriorityClass]::High) { $verifiedPriorities++ } }; if($verifiedPriorities -ne $case.Processes) { throw 'A target lost high priority' }
        Send-Stress 'launch report'; $reportFolder=Join-Path $folder 'bug-reports'; $wait=[Diagnostics.Stopwatch]::StartNew()
        while(-not(Test-Path -LiteralPath $reportFolder) -or @(Get-ChildItem -LiteralPath $reportFolder -Filter 'diagnostic-*.json' -ErrorAction SilentlyContinue).Count -eq 0) { if($wait.Elapsed.TotalSeconds -gt 10) { throw 'No diagnostic report' }; Start-Sleep -Milliseconds 100 }
        $diagnostic=Get-Content -LiteralPath (Get-ChildItem -LiteralPath $reportFolder -Filter 'diagnostic-*.json'|Select-Object -First 1).FullName -Raw|ConvertFrom-Json
        if(-not $diagnostic.Samples[-1].EngineAlive -or $diagnostic.Samples[-1].EngineErrors -ne 0) { throw 'Observer failed or reported errors during stress test' }
        $record=[pscustomobject]@{Profiles=$case.Profiles;Processes=$case.Processes;VerifiedAffinities=$verified;VerifiedPriorities=$verifiedPriorities;ElapsedSeconds=$elapsed;CpuOneCorePercent=100*$cpu/$elapsed;CpuMachinePercent=100*$cpu/$elapsed/[Environment]::ProcessorCount;PrivateMiB=$monitor.PrivateMemorySize64/1MB;PrivateGrowthMiB=$monitor.PrivateMemorySize64/1MB-$initialPrivate;HandleGrowth=$monitor.HandleCount-$initialHandles;EngineAlive=$diagnostic.Samples[-1].EngineAlive;EngineErrors=$diagnostic.Samples[-1].EngineErrors;Samples=$samples;Passed=$true}
        $records+=$record
        Write-Host "$($case.Profiles) profiles / $($case.Processes) processes: $([Math]::Round($record.CpuOneCorePercent,2))% core, $([Math]::Round($record.PrivateMiB,1)) MiB private, all affinities verified"
    } finally {
        if($monitor -and -not $monitor.HasExited) { try { if($pipeName) { Send-Stress 'launch stop'; [void]$monitor.WaitForExit(6000) } } finally { if(-not $monitor.HasExited) { Stop-Process -Id $monitor.Id -Force } } }
        foreach($child in $children) { try { if(-not $child.HasExited) { Stop-Process -Id $child.Id -Force } } finally { $child.Dispose() } }
        if($monitor) { $monitor.Dispose() }
    }
}
$tested=Get-Content -LiteralPath (Join-Path $HarnessDirectory 'test-results.json') -Raw|ConvertFrom-Json
@{Date=(Get-Date -Format o);Label=$Label;Version=$tested.Version;ReleaseSha256=$tested.ReleaseSha256;Records=$records;Note='Sleeping local test executables. Each case has unique process names and exact paths. Target app CPU measured separately from test-driver and fixture CPU. Not an FPS benchmark.'}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $artifacts ('stress-'+$Label+'.json')) -Encoding UTF8
