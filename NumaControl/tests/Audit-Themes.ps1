#requires -Version 5.1
param([int]$Seconds=20,[int]$Switches=80,[int]$SteadySeconds=360)
$ErrorActionPreference='Stop'
if($Seconds -lt 10 -or $Switches -lt 8 -or $SteadySeconds -lt 120) {throw 'Use meaningful phase, switch and steady-state durations.'}
$artifacts=Join-Path $PSScriptRoot 'artifacts'
$harnessFolder=(Get-ChildItem -LiteralPath $artifacts -Directory|Sort-Object LastWriteTime -Descending|Where-Object {Test-Path -LiteralPath (Join-Path $_.FullName 'test-results.json')}|Select-Object -First 1).FullName
$tested=Get-Content -LiteralPath (Join-Path $harnessFolder 'test-results.json') -Raw|ConvertFrom-Json
$harness=Join-Path $harnessFolder 'NumaTests.exe'
$iconSource=(Get-ChildItem -LiteralPath $harnessFolder -Filter 'NumaIconFixture*.exe'|Select-Object -First 1).FullName
$run=Join-Path $artifacts ('themes-'+[Guid]::NewGuid().ToString('N')); [void](New-Item -ItemType Directory -Path $run)
$games=@(); $prefix='NumaTheme'+[Guid]::NewGuid().ToString('N').Substring(0,6)
for($i=0;$i -lt 100;$i++) {
    $exe=Join-Path $run ($prefix+$i+'.exe'); Copy-Item -LiteralPath $iconSource -Destination $exe
    $games+=@{Id=[Guid]::NewGuid().ToString('N');Name='Игра '+$i;ProcessName=[IO.Path]::GetFileNameWithoutExtension($exe);ExePath=$exe;LaunchTarget=$exe;Arguments='';Mask='4';Enabled=$true;HighPriority=$true;Color='#8C83FF'}
}
@{Version=1;IdleIntervalMs=2000;ActiveIntervalMs=2000;Paused=$false;Theme='standard';Games=$games}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $run 'profiles.json') -Encoding UTF8
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class ThemeGuiResources { [DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr process, uint flags); }'
function Send-Theme([string]$command) {
    $pipe=[IO.Pipes.NamedPipeClientStream]::new('.',$script:pipeName,[IO.Pipes.PipeDirection]::InOut)
    try {$pipe.Connect(10000);$bytes=[Text.Encoding]::UTF8.GetBytes($command+"`n");$pipe.Write($bytes,0,$bytes.Length);$pipe.Flush();if($pipe.ReadByte() -ne 1){throw 'No monitor ACK'}} finally {$pipe.Dispose()}
}
function Select-Theme([string]$id) {
    $ready=Join-Path $script:run 'theme-ready.txt'; if(Test-Path -LiteralPath $ready){Remove-Item -LiteralPath $ready}
    Send-Theme ('launch theme '+$id); $clock=[Diagnostics.Stopwatch]::StartNew()
    while(-not(Test-Path -LiteralPath $ready)) {if($clock.Elapsed.TotalSeconds -gt 20 -or $script:monitor.HasExited){throw 'Theme switch failed'}; Start-Sleep -Milliseconds 50}
    if((Get-Content -LiteralPath $ready -Raw) -ne $id){throw 'Unexpected theme acknowledgement'}
}
function Measure-Theme([string]$name,[int]$duration) {
    $script:monitor.Refresh(); $startCpu=$script:monitor.TotalProcessorTime.TotalSeconds; $clock=[Diagnostics.Stopwatch]::StartNew();$samples=@()
    while($clock.Elapsed.TotalSeconds -lt $duration) {
        Start-Sleep -Seconds 1; $script:monitor.Refresh(); if($script:monitor.HasExited){throw ('Theme monitor exited in '+$name)}
        $samples+=[pscustomobject]@{Seconds=$clock.Elapsed.TotalSeconds;PrivateMiB=$script:monitor.PrivateMemorySize64/1MB;WorkingMiB=$script:monitor.WorkingSet64/1MB;Handles=$script:monitor.HandleCount;Gdi=[ThemeGuiResources]::GetGuiResources($script:monitor.Handle,0);User=[ThemeGuiResources]::GetGuiResources($script:monitor.Handle,1)}
        if($samples[-1].PrivateMiB -ge 512 -or $samples[-1].WorkingMiB -ge 768 -or $samples[-1].Handles -ge 6000){throw ('Production resource limit reached: '+$name)}
        if($samples.Count % 60 -eq 0){Write-Host ($name+': '+[int]$clock.Elapsed.TotalSeconds+' / '+$duration+' s')}
    }
    $cpu=100*($script:monitor.TotalProcessorTime.TotalSeconds-$startCpu)/$clock.Elapsed.TotalSeconds
    if($cpu -gt 5){throw ('Idle theme CPU exceeds 5 percent of one logical CPU: '+$name+' '+$cpu)}
    $record=[pscustomobject]@{Phase=$name;DurationSeconds=$clock.Elapsed.TotalSeconds;CpuOneCorePercent=$cpu;CpuMachinePercent=$cpu/[Environment]::ProcessorCount;PeakPrivateMiB=($samples|Measure-Object PrivateMiB -Maximum).Maximum;PeakWorkingMiB=($samples|Measure-Object WorkingMiB -Maximum).Maximum;Samples=$samples;Passed=$true}
    Write-Host ($name+': '+[Math]::Round($cpu,2)+'% core, peak private '+[Math]::Round($record.PeakPrivateMiB,1)+' MiB');return $record
}
$monitor=$null; $pipeName=$null; $records=@()
try {
    $monitor=Start-Process -FilePath $harness -ArgumentList @('--monitor-visible',('"'+$run+'"'),[Guid]::NewGuid().ToString('N')) -WindowStyle Hidden -PassThru
    $ready=Join-Path $run 'ready.txt'; $clock=[Diagnostics.Stopwatch]::StartNew(); while(-not(Test-Path -LiteralPath $ready)){if($clock.Elapsed.TotalSeconds -gt 25 -or $monitor.HasExited){throw 'Theme monitor failed to start'};Start-Sleep -Milliseconds 100};$pipeName=Get-Content -LiteralPath $ready -Raw
    foreach($id in @('standard','win11-light','win11-dark','ami-bios')) {
        Select-Theme $id; Start-Sleep -Seconds 5
        Send-Theme 'launch main'; Start-Sleep -Seconds 2;$records+=Measure-Theme ($id+'-library') $Seconds
        Send-Theme 'launch diagnostics';Start-Sleep -Seconds 2;$records+=Measure-Theme ($id+'-diagnostics') $Seconds
        Send-Theme 'launch hide';Start-Sleep -Seconds 2;$records+=Measure-Theme ($id+'-tray') $Seconds
    }
    $clock=[Diagnostics.Stopwatch]::StartNew();for($i=0;$i -lt $Switches;$i++){Select-Theme (@('standard','win11-light','win11-dark','ami-bios')[$i%4]);Start-Sleep -Milliseconds 100}
    $switchSeconds=$clock.Elapsed.TotalSeconds;Select-Theme 'standard';Send-Theme 'launch hide';Start-Sleep -Seconds 5
    $steady=Measure-Theme 'after-repeated-theme-switches' $SteadySeconds
    $window=@($steady.Samples|Where-Object Seconds -ge 60);$privateGrowth=$window[-1].PrivateMiB-$window[0].PrivateMiB;$handlesGrowth=$window[-1].Handles-$window[0].Handles;$gdiGrowth=$window[-1].Gdi-$window[0].Gdi;$userGrowth=$window[-1].User-$window[0].User
    if($privateGrowth -gt 32 -or $handlesGrowth -gt 150 -or $gdiGrowth -gt 10 -or $userGrowth -gt 10){throw ('Sustained theme resource growth: '+$privateGrowth+' MiB / '+$handlesGrowth+' handles / '+$gdiGrowth+' GDI / '+$userGrowth+' USER')}
    Send-Theme 'launch report'
    @{Date=(Get-Date -Format o);Version=$tested.Version;ReleaseSha256=$tested.ReleaseSha256;DataDirectory=$run;Harness=$harness;Profiles=100;IconFiles=@(Get-ChildItem -LiteralPath (Join-Path $run 'icons') -Filter '*.png').Count;Records=$records;Switches=$Switches;SwitchSeconds=$switchSeconds;Steady=$steady;SteadyPrivateGrowthMiB=$privateGrowth;SteadyHandleGrowth=$handlesGrowth;SteadyGdiGrowth=$gdiGrowth;SteadyUserGrowth=$userGrowth;Passed=$true;Limits=@{PrivateMiB=512;WorkingMiB=768;Handles=6000};Note='100 profiles with real EXE icons. Four themes, visible library/diagnostics and tray. Resource values measured externally. No UI Automation. No production limit increase.'}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $artifacts 'theme-resources-1.5.json') -Encoding UTF8
    Write-Host 'PASS all theme resource phases and repeated-switch soak'
} finally {
    if($monitor){if(-not $monitor.HasExited){try {if($pipeName){Send-Theme 'launch stop';[void]$monitor.WaitForExit(6000)}}finally{if(-not $monitor.HasExited){Stop-Process -Id $monitor.Id -Force}}};$monitor.Dispose()}
}
