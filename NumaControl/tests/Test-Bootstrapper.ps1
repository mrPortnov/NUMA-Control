#requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$SetupPath)
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
$SetupPath=[IO.Path]::GetFullPath($SetupPath)
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
$extract=Join-Path $run 'extracted'
& (Join-Path $project '.tools\wix-3.14.1\dark.exe') -nologo -x $extract $SetupPath
if($LASTEXITCODE -ne 0){throw 'Cannot extract the production bootstrapper.'}
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
& (Join-Path $project 'Build-Installer.ps1') -ProductVersion $version -OutputPath $isolatedMsi -IsolationId $id
& (Join-Path $project 'Build-Bootstrapper.ps1') -ProductVersion $version -MsiPath $isolatedMsi -OutputPath $isolatedSetup -IsolationId $id -IsolationDirectory $isolatedDirectory
$installed=$false
try{
 Run-Setup $isolatedSetup @('/install') (Join-Path $run 'install.log');$installed=$true
 $installedExe=Join-Path $isolatedDirectory 'app\NUMA-Control.exe'
 Check (Test-Path -LiteralPath $installedExe) 'setup installs the isolated MSI payload into its own test directory'
 Check ((Get-FileHash -LiteralPath $installedExe).Hash -eq (Get-FileHash -LiteralPath (Join-Path $project 'NUMA-Control.exe')).Hash) 'setup installs the exact tested application EXE'
 $installLog=[IO.File]::ReadAllText((Join-Path $run 'install.log'))
 Check ($installLog -match 'Detected package: NetFx48Web, state: Present' -and $installLog -notmatch 'Applying execute package: NetFx48Web') 'normal installation skips prerequisite download/installation when .NET is present'
 $data=Join-Path $isolatedDirectory 'app\data';[void](New-Item -ItemType Directory -Path $data -Force)
 [IO.File]::WriteAllText((Join-Path $data 'preserved.json'),'{"fixture":true}')
 Run-Setup $isolatedSetup @('/uninstall') (Join-Path $run 'uninstall.log');$installed=$false
 Check (-not(Test-Path -LiteralPath $installedExe)) 'setup uninstall removes its isolated application files'
 Check (Test-Path -LiteralPath (Join-Path $data 'preserved.json')) 'setup uninstall preserves user-generated data'
}finally{if($installed){Run-Setup $isolatedSetup @('/uninstall') (Join-Path $run 'cleanup.log')}}
[pscustomobject]@{Version=$version;SetupSha256=(Get-FileHash -LiteralPath $SetupPath).Hash;Passed=$checks.Count;Checks=$checks.ToArray();MissingRuntimeInstallation='Detection boundaries and download verified; actual installation on a clean Windows without .NET requires a separate VM.'}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $PSScriptRoot 'artifacts\bootstrapper-results.json') -Encoding UTF8
