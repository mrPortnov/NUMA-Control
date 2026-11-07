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
function Test-SetupUpgrade([string]$previousVersion, [bool]$legacyMsiVisible) {
    $case = Join-Path $run ('upgrade-' + $previousVersion)
    [void](New-Item -ItemType Directory -Path $case)
    $caseId = [Guid]::NewGuid().ToString('N')
    $label = "setup $previousVersion to ${version}: "
    $isolatedMsi = Join-Path $case 'isolated.msi'
    $isolatedSetup = Join-Path $case 'isolated-setup.exe'
    $isolatedDirectory = Join-Path $case 'installation'
    $previousMsi = Join-Path $case 'previous.msi'
    $previousSetup = Join-Path $case 'previous-setup.exe'
    $fixtureSource = Join-Path $case 'previous.cs'
    $fixtureExe = Join-Path $case 'previous.exe'
    $fixture = '[assembly: System.Reflection.AssemblyFileVersion("{0}.0")] static class PreviousFixture {{ static void Main() {{}} }}' -f $previousVersion
    [IO.File]::WriteAllText($fixtureSource, $fixture)
    & (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') /nologo /target:winexe /platform:x64 "/out:$fixtureExe" $fixtureSource
    if ($LASTEXITCODE -ne 0) { throw ($label + 'could not compile the older application fixture') }
    & (Join-Path $project 'Build-Installer.ps1') -ProductVersion $previousVersion -OutputPath $previousMsi -IsolationId $caseId -FixtureExe $fixtureExe
    & (Join-Path $project 'Build-Bootstrapper.ps1') -ProductVersion $previousVersion -MsiPath $previousMsi -OutputPath $previousSetup -IsolationId $caseId -IsolationDirectory $isolatedDirectory -LegacyMsiEntry:$legacyMsiVisible
    & (Join-Path $project 'Build-Installer.ps1') -ProductVersion $version -OutputPath $isolatedMsi -IsolationId $caseId
    & (Join-Path $project 'Build-Bootstrapper.ps1') -ProductVersion $version -MsiPath $isolatedMsi -OutputPath $isolatedSetup -IsolationId $caseId -IsolationDirectory $isolatedDirectory
    $previousProduct = (Get-Content -LiteralPath ($previousMsi + '.json') -Raw | ConvertFrom-Json).ProductCode
    $currentProduct = (Get-Content -LiteralPath ($isolatedMsi + '.json') -Raw | ConvertFrom-Json).ProductCode
    $installed = $false
    $previousInstalled = $false
    try {
        $previousLog = Join-Path $case 'previous-install.log'
        Run-Setup $previousSetup @('/install') $previousLog
        $previousInstalled = $true
        $previousBundle = Read-BundleId $previousLog
        $previousBundleEntry = Read-UninstallEntry $previousBundle
        $previousMsiEntry = Read-UninstallEntry $previousProduct
        $expectedEntries = if ($legacyMsiVisible) { 'two visible uninstall entries' } else { 'one visible entry and a hidden MSI' }
        Check ($previousBundleEntry -and -not $previousBundleEntry.Hidden -and $previousMsiEntry -and
            $previousMsiEntry.Hidden -eq (-not $legacyMsiVisible)) ($label + 'older setup starts with ' + $expectedEntries)
        $installedExe = Join-Path $isolatedDirectory 'app\NUMA-Control.exe'
        Check ((Get-FileHash -LiteralPath $installedExe).Hash -eq (Get-FileHash -LiteralPath $fixtureExe).Hash) ($label + 'older setup installs its distinct executable')
        $data = Join-Path $isolatedDirectory 'app\data'
        [void](New-Item -ItemType Directory -Path $data)
        $savedData = Join-Path $data 'preserved.json'
        [IO.File]::WriteAllText($savedData, '{"fixture":true}')
        $currentLog = Join-Path $case 'install.log'
        Run-Setup $isolatedSetup @('/install') $currentLog
        $installed = $true
        $currentBundle = Read-BundleId $currentLog
        Check ((Get-FileHash -LiteralPath $installedExe).Hash -eq (Get-FileHash -LiteralPath (Join-Path $project 'NUMA-Control.exe')).Hash) ($label + 'setup upgrades to the exact tested application EXE')
        Check ((Get-Content -LiteralPath $savedData -Raw) -eq '{"fixture":true}') ($label + 'setup upgrade preserves user-generated data')
        Check (-not (Read-UninstallEntry $previousBundle) -and -not (Read-UninstallEntry $previousProduct)) ($label + 'setup upgrade removes both older registrations')
        $previousInstalled = $false
        $currentBundleEntry = Read-UninstallEntry $currentBundle
        $currentMsiEntry = Read-UninstallEntry $currentProduct
        Check ($currentBundleEntry -and -not $currentBundleEntry.Hidden -and $currentMsiEntry -and $currentMsiEntry.Hidden) ($label + 'setup registers one visible entry and keeps the chained MSI hidden')
        Check (@(Get-ChildItem -LiteralPath (Join-Path $isolatedDirectory 'menu') -Filter '*.lnk' -File).Count -eq 1) ($label + 'setup leaves only the application launch shortcut')
        $installLog = [IO.File]::ReadAllText($currentLog)
        Check ($installLog -match 'Detected package: NetFx48Web, state: Present' -and $installLog -notmatch 'Applying execute package: NetFx48Web') ($label + 'setup skips the .NET download and installation when it is present')
        Run-Setup $isolatedSetup @('/uninstall') (Join-Path $case 'uninstall.log')
        $installed = $false
        Check (-not (Test-Path -LiteralPath $installedExe)) ($label + 'setup uninstall removes its isolated application files')
        Check (-not (Read-UninstallEntry $currentBundle) -and -not (Read-UninstallEntry $currentProduct)) ($label + 'setup uninstall removes the bundle and MSI registrations')
        Check (Test-Path -LiteralPath $savedData) ($label + 'setup uninstall preserves user-generated data')
    } finally {
        if ($installed) { Run-Setup $isolatedSetup @('/uninstall') (Join-Path $case 'cleanup.log') }
        if ($previousInstalled) { Run-Setup $previousSetup @('/uninstall') (Join-Path $case 'previous-cleanup.log') }
    }
}
Test-SetupUpgrade '1.7.0' $true
Test-SetupUpgrade '1.7.2' $false
[pscustomobject]@{Version=$version;SetupSha256=(Get-FileHash -LiteralPath $SetupPath).Hash;Passed=$checks.Count;Checks=$checks.ToArray();MissingRuntimeInstallation='Detection boundaries and download verified; actual installation on a clean Windows without .NET requires a separate VM.'}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $PSScriptRoot 'artifacts\bootstrapper-results.json') -Encoding UTF8
