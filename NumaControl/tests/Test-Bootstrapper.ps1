#requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$SetupPath, [string]$MsiPath)
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
$SetupPath=[IO.Path]::GetFullPath($SetupPath)
if(-not $MsiPath){$MsiPath=[IO.Path]::ChangeExtension($SetupPath,'.msi')}
$version=([version][Diagnostics.FileVersionInfo]::GetVersionInfo($SetupPath).FileVersion).ToString(3)
$id=[Guid]::NewGuid().ToString('N')
$run=Join-Path $PSScriptRoot ('artifacts\bootstrapper-'+$id)
[void](New-Item -ItemType Directory -Path $run)
$checks=New-Object 'System.Collections.Generic.List[object]'
function Check([bool]$ok,[string]$name){if(-not $ok){throw ('Bootstrapper test: '+$name)};$checks.Add([pscustomobject]@{Name=$name;Passed=$true});Write-Host ('PASS '+$name)}
function Run-Setup([string]$path,[string[]]$options,[string]$log){
 $arguments=@($options)+@('/quiet','/norestart','/log',('"'+$log+'"'))
 $process=Start-Process -FilePath $path -ArgumentList $arguments -WindowStyle Hidden -PassThru
 try{if(-not $process.WaitForExit(180000)){throw 'Bootstrapper timed out; retain its log for investigation.'};if($process.ExitCode -ne 0){throw ('Bootstrapper returned '+$process.ExitCode+'; inspect '+$log)}}finally{$process.Dispose()}
}
function Read-UninstallEntry([string]$id) {
 foreach($scope in @([Microsoft.Win32.RegistryHive]::CurrentUser,[Microsoft.Win32.RegistryHive]::LocalMachine)) {
  foreach($view in @([Microsoft.Win32.RegistryView]::Registry64,[Microsoft.Win32.RegistryView]::Registry32)) {
   $hive=[Microsoft.Win32.RegistryKey]::OpenBaseKey($scope,$view)
   try {
    $key=$hive.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall\'+$id)
    if($key){try{return [pscustomobject]@{Hidden=($key.GetValue('SystemComponent',0) -eq 1)}}finally{$key.Dispose()}}
   } finally {$hive.Dispose()}
  }
 }
}
function Read-BundleId([string]$log) {
 $match=[regex]::Match([IO.File]::ReadAllText($log),'registration key: SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\(\{[0-9a-fA-F-]{36}\})')
 if(-not $match.Success){throw 'Could not identify the isolated bundle registration from its install log.'}
 return $match.Groups[1].Value
}
$extract=Join-Path $run 'extracted'
& (Join-Path $project '.tools\wix-3.14.1\dark.exe') -nologo -x $extract $SetupPath
if($LASTEXITCODE -ne 0){throw 'Cannot extract the production bootstrapper.'}
$embedded=@(Get-ChildItem -LiteralPath (Join-Path $extract 'AttachedContainer') -File -Recurse)
Check ($embedded.Count -eq 1 -and $embedded[0].Name -eq [IO.Path]::GetFileName($MsiPath) -and
    (Get-FileHash -LiteralPath $embedded[0].FullName).Hash -eq (Get-FileHash -LiteralPath $MsiPath).Hash) 'setup contains only the exact verified MSI'
$manifestFiles=@(Get-ChildItem -LiteralPath $extract -Filter '*.xml' -Recurse -File|Where-Object{[IO.File]::ReadAllText($_.FullName).Contains('DetectCondition="NETFRAMEWORK45')})
Check ($manifestFiles.Count -eq 1) 'production bootstrapper exposes a pinned .NET detection manifest'
[xml]$manifest=[IO.File]::ReadAllText($manifestFiles[0].FullName)
$netfx=$manifest.SelectSingleNode('//*[local-name()="ExePackage" and @Id="NetFx48Web"]')
Check ($null -ne $netfx -and $netfx.DetectCondition -match '^NETFRAMEWORK45\s*>=\s*528040$') 'NET Framework 4.8+ is detected by the documented minimum Release DWORD'
$minimum=[int]([regex]::Match($netfx.DetectCondition,'\d+$').Value)
foreach($case in @(@{Release=0;Present=$false},@{Release=461808;Present=$false},@{Release=528039;Present=$false},@{Release=528040;Present=$true},@{Release=528049;Present=$true},@{Release=533325;Present=$true})){
 Check (($case.Release -ge $minimum) -eq $case.Present) ('compiled detection boundary / Release '+$case.Release+' / present '+$case.Present)
}
Check ($netfx.Permanent -eq 'yes' -and $netfx.PerMachine -eq 'yes') 'shared .NET runtime is permanent and never removed with the application'
$layout=Join-Path $run 'layout'
Run-Setup $SetupPath @('/layout',('"'+$layout+'"')) (Join-Path $run 'layout.log')
$downloaded=@(Get-ChildItem -LiteralPath $layout -Filter 'ndp48-web.exe' -Recurse -File)
Check ($downloaded.Count -eq 1) 'layout downloads the official .NET web installer without installing anything'
$signature=Get-AuthenticodeSignature -LiteralPath $downloaded[0].FullName
Check ($signature.Status -eq 'Valid' -and $signature.SignerCertificate.Subject -match 'Microsoft Corporation') 'downloaded prerequisite has a valid Microsoft Authenticode signature'
Check ((Get-FileHash -LiteralPath $downloaded[0].FullName -Algorithm SHA1).Hash -eq '4181398AA1FD5190155AC3A388434E5F7EA0B667') 'downloaded prerequisite matches the official WiX pinned payload'
$layoutLog=[IO.File]::ReadAllText((Join-Path $run 'layout.log'))
Check ($layoutLog -match 'https://go\.microsoft\.com/fwlink/\?LinkId=2085155') 'prerequisite acquisition uses the official Microsoft HTTPS link'
$registry=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,[Microsoft.Win32.RegistryView]::Registry32)
try{$key=$registry.OpenSubKey('SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full');try{$installedRelease=if($key){[int]$key.GetValue('Release',0)}else{0}}finally{if($key){$key.Dispose()}}}finally{$registry.Dispose()}
Check ($installedRelease -ge 528040) 'host has .NET 4.8+, so the live install test will not replace the system runtime'
$isolatedMsi=Join-Path $run 'isolated.msi';$isolatedSetup=Join-Path $run 'isolated-setup.exe';$isolatedDirectory=Join-Path $run 'installation'
$legacyMsi=Join-Path $run 'legacy.msi';$legacySetup=Join-Path $run 'legacy-setup.exe'
$fixtureSource=Join-Path $run 'legacy.cs';$fixtureExe=Join-Path $run 'legacy.exe'
[IO.File]::WriteAllText($fixtureSource,'[assembly: System.Reflection.AssemblyFileVersion("1.7.0.0")] static class LegacyFixture { static void Main() {} }')
& (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') /nologo /target:winexe /platform:x64 "/out:$fixtureExe" $fixtureSource
if($LASTEXITCODE -ne 0){throw 'Cannot compile the isolated older application fixture.'}
& (Join-Path $project 'Build-Installer.ps1') -ProductVersion '1.7.0' -OutputPath $legacyMsi -IsolationId $id -FixtureExe $fixtureExe
& (Join-Path $project 'Build-Bootstrapper.ps1') -ProductVersion '1.7.0' -MsiPath $legacyMsi -OutputPath $legacySetup -IsolationId $id -IsolationDirectory $isolatedDirectory -LegacyMsiEntry
& (Join-Path $project 'Build-Installer.ps1') -ProductVersion $version -OutputPath $isolatedMsi -IsolationId $id
& (Join-Path $project 'Build-Bootstrapper.ps1') -ProductVersion $version -MsiPath $isolatedMsi -OutputPath $isolatedSetup -IsolationId $id -IsolationDirectory $isolatedDirectory
$legacyProduct=(Get-Content -LiteralPath ($legacyMsi+'.json') -Raw|ConvertFrom-Json).ProductCode
$currentProduct=(Get-Content -LiteralPath ($isolatedMsi+'.json') -Raw|ConvertFrom-Json).ProductCode
$installed=$false;$legacyInstalled=$false
try{
 $legacyLog=Join-Path $run 'legacy-install.log'
 Run-Setup $legacySetup @('/install') $legacyLog;$legacyInstalled=$true
 $legacyBundle=Read-BundleId $legacyLog
 $legacyBundleEntry=Read-UninstallEntry $legacyBundle
 $legacyMsiEntry=Read-UninstallEntry $legacyProduct
 Check ($legacyBundleEntry -and -not $legacyBundleEntry.Hidden -and $legacyMsiEntry -and -not $legacyMsiEntry.Hidden) 'older setup reproduces two visible uninstall entries'
 $installedExe=Join-Path $isolatedDirectory 'app\NUMA-Control.exe'
 Check ((Get-FileHash -LiteralPath $installedExe).Hash -eq (Get-FileHash -LiteralPath $fixtureExe).Hash) 'older setup installs the distinct 1.7.0 executable'
 $data=Join-Path $isolatedDirectory 'app\data';[void](New-Item -ItemType Directory -Path $data -Force)
 $savedData=Join-Path $data 'preserved.json'
 [IO.File]::WriteAllText($savedData,'{"fixture":true}')
 $currentLog=Join-Path $run 'install.log'
 Run-Setup $isolatedSetup @('/install') $currentLog;$installed=$true
 $currentBundle=Read-BundleId $currentLog
 Check ((Get-FileHash -LiteralPath $installedExe).Hash -eq (Get-FileHash -LiteralPath (Join-Path $project 'NUMA-Control.exe')).Hash) 'setup upgrades to the exact tested application EXE'
 Check ((Get-Content -LiteralPath $savedData -Raw) -eq '{"fixture":true}') 'setup upgrade preserves user-generated data'
 Check (-not (Read-UninstallEntry $legacyBundle) -and -not (Read-UninstallEntry $legacyProduct)) 'setup upgrade removes both older uninstall entries'
 $legacyInstalled=$false
 $currentBundleEntry=Read-UninstallEntry $currentBundle
 $currentMsiEntry=Read-UninstallEntry $currentProduct
 Check ($currentBundleEntry -and -not $currentBundleEntry.Hidden -and $currentMsiEntry -and $currentMsiEntry.Hidden) 'setup registers one visible entry and keeps the chained MSI hidden'
 Check (@(Get-ChildItem -LiteralPath (Join-Path $isolatedDirectory 'menu') -Filter '*.lnk' -File).Count -eq 1) 'setup keeps the launch shortcut and removes the MSI-only uninstall shortcut'
 $installLog=[IO.File]::ReadAllText((Join-Path $run 'install.log'))
 Check ($installLog -match 'Detected package: NetFx48Web, state: Present' -and $installLog -notmatch 'Applying execute package: NetFx48Web') 'normal installation skips prerequisite download/installation when .NET is present'
 Run-Setup $isolatedSetup @('/uninstall') (Join-Path $run 'uninstall.log');$installed=$false
 Check (-not(Test-Path -LiteralPath $installedExe)) 'setup uninstall removes its isolated application files'
 Check (-not (Read-UninstallEntry $currentBundle) -and -not (Read-UninstallEntry $currentProduct)) 'setup uninstall removes the bundle and MSI registrations'
 Check (Test-Path -LiteralPath $savedData) 'setup uninstall preserves user-generated data'
}finally{
 if($installed){Run-Setup $isolatedSetup @('/uninstall') (Join-Path $run 'cleanup.log')}
 if($legacyInstalled){Run-Setup $legacySetup @('/uninstall') (Join-Path $run 'legacy-cleanup.log')}
}
[pscustomobject]@{Version=$version;SetupSha256=(Get-FileHash -LiteralPath $SetupPath).Hash;Passed=$checks.Count;Checks=$checks.ToArray();MissingRuntimeInstallation='Detection boundaries and download verified; actual installation on a clean Windows without .NET requires a separate VM.'}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $PSScriptRoot 'artifacts\bootstrapper-results.json') -Encoding UTF8
