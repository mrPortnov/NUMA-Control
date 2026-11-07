#requires -Version 5.1
param([string]$MsiPath, [string]$LegacyProjectPath)
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
if (-not $LegacyProjectPath -or -not (Test-Path -LiteralPath (Join-Path $LegacyProjectPath 'Build-Installer.ps1') -PathType Leaf)) {
    throw 'Supply -LegacyProjectPath with the isolated v1.7.3 source export prepared by New-ReleaseBundle.ps1.'
}
$LegacyProjectPath=(Resolve-Path -LiteralPath $LegacyProjectPath).Path
if(-not $MsiPath) { $MsiPath=Join-Path $project 'NUMA-Control-1.7.4-setup-x64.msi' }
$MsiPath=[IO.Path]::GetFullPath($MsiPath)
$isolationId=[Guid]::NewGuid().ToString('N'); $run=Join-Path $PSScriptRoot ('artifacts\installer-'+$isolationId); [void](New-Item -ItemType Directory -Path $run)
$installFolder=Join-Path $run 'Приложение с пробелами'; $menuFolder=Join-Path $run 'Start menu'
$desktopFolder=Join-Path $run 'Desktop'
$oldMsi=Join-Path $run 'older.msi'; $failMsi=Join-Path $run 'rollback.msi'
$production=Get-Content -LiteralPath ($MsiPath+'.json') -Raw|ConvertFrom-Json
$payload=Join-Path $run 'production'
& (Join-Path $project '.tools\wix-7.0.1\wix.exe') msi decompile $MsiPath -x $payload -o (Join-Path $run 'production.wxs') -acceptEula wix7
if($LASTEXITCODE -ne 0) { throw 'Cannot extract the production MSI.' }
$isolatedMsi=Join-Path $run 'current-isolated.msi'
& (Join-Path $project 'Build-Installer.ps1') -ProductVersion $production.ProductVersion -OutputPath $isolatedMsi -IsolationId $isolationId
$fixtureSource=Join-Path $run 'previous.cs'
$fixtureExe=Join-Path $run 'previous.exe'
[IO.File]::WriteAllText($fixtureSource,'[assembly: System.Reflection.AssemblyFileVersion("1.7.3.0")] static class PreviousFixture { static void Main() {} }')
& (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') /nologo /target:winexe /platform:x64 "/out:$fixtureExe" $fixtureSource
if ($LASTEXITCODE -ne 0) { throw 'Cannot compile the isolated v1.7.3 executable fixture.' }
& (Join-Path $LegacyProjectPath 'Build-Installer.ps1') -ProductVersion '1.7.3' -OutputPath $oldMsi -IsolationId $isolationId -FixtureExe $fixtureExe
& (Join-Path $project 'Build-Installer.ps1') -ProductVersion '1.4.98' -OutputPath $failMsi -InjectRollback -IsolationId $isolationId
$current=Get-Content -LiteralPath ($isolatedMsi+'.json') -Raw|ConvertFrom-Json
if($current.ApplicationSHA256 -ne $production.ApplicationSHA256) { throw 'Production MSI must be rebuilt after the EXE changed.' }
$MsiPath=$isolatedMsi
$older=Get-Content -LiteralPath ($oldMsi+'.json') -Raw|ConvertFrom-Json
$failure=Get-Content -LiteralPath ($failMsi+'.json') -Raw|ConvertFrom-Json
$installer=New-Object -ComObject WindowsInstaller.Installer
$checks=[Collections.Generic.List[object]]::new(); $owned=[Collections.Generic.List[string]]::new()
function Com-Method($target,[string]$name,[object[]]$invokeArguments) { return $target.GetType().InvokeMember($name,[Reflection.BindingFlags]::InvokeMethod,$null,$target,$invokeArguments) }
function Com-Property($target,[string]$name,[object[]]$invokeArguments) { return $target.GetType().InvokeMember($name,[Reflection.BindingFlags]::GetProperty,$null,$target,$invokeArguments) }
function Product-State([string]$code) { try { return [int](Com-Property $script:installer 'ProductState' @($code)) } catch { return -1 } }
function Check([bool]$ok,[string]$name) { $checks.Add([pscustomobject]@{Name=$name;Passed=$ok}); if(-not $ok) { throw "Installer test: $name" }; Write-Host "PASS $name" }
function Run-Msi([string[]]$options,[string]$log,[int[]]$expected=@(0)) {
    $arguments=@($options)+@('/qn','/norestart','/l*v',('"'+$log+'"'))
    $process=Start-Process -FilePath (Join-Path $env:WINDIR 'System32\msiexec.exe') -ArgumentList $arguments -WindowStyle Hidden -PassThru
    try {
        if(-not $process.WaitForExit(60000)) { throw 'Windows Installer timed out; leave its log for investigation.' }
        if($process.ExitCode -notin $expected) { throw "Windows Installer returned $($process.ExitCode). Log: $log" }
        return $process.ExitCode
    } finally { $process.Dispose() }
}
try {
    $files=[ordered]@{ApplicationExe='NUMA-Control.exe';ApplicationConfig='NUMA-Control.exe.config';Readme='README.md';ReadmeRu='README.ru.md';UserGuide='USER-GUIDE.md';UserGuideRu='USER-GUIDE.ru.md';License='..\LICENSE';ThirdPartyNotices='..\THIRD-PARTY-NOTICES.txt'}
    $actual=@(Get-ChildItem -LiteralPath (Join-Path $payload 'File') -File -Recurse | Select-Object -ExpandProperty Name)
    Check ($actual.Count -gt 0 -and -not (Compare-Object @($files.Keys) $actual -CaseSensitive)) 'production MSI ships only the application and bilingual documents'
    foreach($file in $files.GetEnumerator()) {
        $extracted=Join-Path $payload ('File\'+$file.Key)
        Check ((Get-FileHash -LiteralPath $extracted).Hash -eq (Get-FileHash -LiteralPath (Join-Path $project $file.Value)).Hash) ('production MSI matches the verified payload: '+$file.Value)
    }
    Check ($current.UpgradeCode -ne $production.UpgradeCode -and $current.IsolationId -eq $isolationId) 'test package has isolated product, upgrade, component and registry identities'
    foreach($package in @($current,$older,$failure)) { if((Product-State $package.ProductCode) -eq 5) { throw 'A matching product is already installed. Refusing to alter a user installation.' } }
    $database=Com-Method $installer 'OpenDatabase' @($MsiPath,0)
    try {
        $view=Com-Method $database 'OpenView' @('SELECT `Condition` FROM `LaunchCondition`')
        try { [void](Com-Method $view 'Execute' @()); $conditions=@(); while($record=Com-Method $view 'Fetch' @()) { try { $conditions+=Com-Property $record 'StringData' @(1) } finally {[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record)} }; Check ($conditions -contains 'NOT ALLUSERS') 'MSI enforces current-user installation' } finally {[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)}
    } finally {[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)}
    [void](New-Item -ItemType Directory -Path (Join-Path $installFolder 'data') -Force)
    $sentinel=Join-Path $installFolder 'data\preserved-profile.json'; [IO.File]::WriteAllText($sentinel,'{"preserve":true}')
    $properties=@(('INSTALLFOLDER="'+$installFolder+'"'),('APPLICATIONMENUFOLDER="'+$menuFolder+'"'),('APPLICATIONDESKTOPFOLDER="'+$desktopFolder+'"'))
    $owned.Add($failure.ProductCode)
    [void](Run-Msi (@('/i',('"'+$failMsi+'"'))+$properties) (Join-Path $run 'rollback.log') @(1603))
    Check ((Product-State $failure.ProductCode) -ne 5 -and -not(Test-Path -LiteralPath (Join-Path $installFolder 'NUMA-Control.exe'))) 'failed installation rolls back registered product and copied EXE'
    Check ((Get-Content -LiteralPath (Join-Path $run 'rollback.log') -Raw) -match 'FileCopy' -and (Test-Path -LiteralPath $sentinel)) 'rollback ran file operations and preserved pre-existing user data'
    $owned.Add($current.ProductCode)
    [void](Run-Msi (@('/i',('"'+$MsiPath+'"'),'DESKTOPCHOICE=0')+$properties) (Join-Path $run 'desktop-no.log'))
    Check (-not(Test-Path -LiteralPath (Join-Path $desktopFolder 'NUMA Control.lnk'))) 'declining the desktop shortcut creates no desktop link'
    Check (-not(Test-Path -LiteralPath (Join-Path $installFolder 'data\profiles.json'))) 'fresh MSI supplies no seeded profile library'
    [void](Run-Msi @('/x',$current.ProductCode) (Join-Path $run 'desktop-no-uninstall.log'))
    $owned.Add($older.ProductCode)
    [void](Run-Msi (@('/i',('"'+$oldMsi+'"'),'DESKTOPCHOICE=1')+$properties) (Join-Path $run 'install-old.log'))
    Check ((Product-State $older.ProductCode) -eq 5 -and (Get-FileHash -LiteralPath (Join-Path $installFolder 'NUMA-Control.exe')).Hash -eq (Get-FileHash -LiteralPath $fixtureExe).Hash) 'WiX 3 v1.7.3 package installs its distinct executable into a Unicode path with spaces'
    Check ((Get-ChildItem -LiteralPath $menuFolder -Filter '*.lnk').Count -eq 2) 'launch and uninstall shortcuts are created in isolated menu directory'
    Check (Test-Path -LiteralPath (Join-Path $desktopFolder 'NUMA Control.lnk')) 'accepting the desktop shortcut creates the launch link in isolated desktop directory'
    $owned.Add($current.ProductCode)
    [void](Run-Msi @('/i',('"'+$MsiPath+'"')) (Join-Path $run 'upgrade.log'))
    Check ((Product-State $older.ProductCode) -ne 5 -and (Product-State $current.ProductCode) -eq 5) 'WiX 7 major upgrade replaces the WiX 3 v1.7.3 product registration'
    Check ((Get-FileHash -LiteralPath (Join-Path $installFolder 'NUMA-Control.exe')).Hash -eq $current.ApplicationSHA256) 'installed executable matches release SHA256'
    Check ((Get-FileHash -LiteralPath (Join-Path $installFolder 'LICENSE.txt')).Hash -eq (Get-FileHash -LiteralPath (Join-Path $project '..\LICENSE')).Hash) 'installed MIT license matches the canonical source'
    Check ((Get-FileHash -LiteralPath (Join-Path $installFolder 'THIRD-PARTY-NOTICES.txt')).Hash -eq (Get-FileHash -LiteralPath (Join-Path $project '..\THIRD-PARTY-NOTICES.txt')).Hash) 'installed third-party terms match the canonical source'
    $documents=@('README.md','README.ru.md','USER-GUIDE.md','USER-GUIDE.ru.md')
    foreach($document in $documents) {
        Check ((Test-Path -LiteralPath (Join-Path $installFolder $document)) -and (Get-FileHash -LiteralPath (Join-Path $installFolder $document)).Hash -eq (Get-FileHash -LiteralPath (Join-Path $project $document)).Hash) ('installed bilingual documentation matches source: '+$document)
    }
    Check (-not(Test-Path -LiteralPath (Join-Path $installFolder 'TEST-REPORT.md'))) 'installer excludes internal test reports'
    Check ((Get-Content -LiteralPath $sentinel -Raw) -eq '{"preserve":true}') 'upgrade preserves application data'
    Check (Test-Path -LiteralPath (Join-Path $desktopFolder 'NUMA Control.lnk')) 'upgrade remembers desktop shortcut choice and its directory'
    [void](Run-Msi @('/i',('"'+$oldMsi+'"')) (Join-Path $run 'downgrade.log') @(1603))
    Check ((Product-State $current.ProductCode) -eq 5 -and (Product-State $older.ProductCode) -ne 5) 'downgrade is rejected without removing current installation'
    $config=Join-Path $installFolder 'NUMA-Control.exe.config'; Remove-Item -LiteralPath $config
    [void](Run-Msi @('/fa',$current.ProductCode) (Join-Path $run 'repair.log'))
    Check ((Test-Path -LiteralPath $config) -and (Test-Path -LiteralPath $sentinel)) 'repair restores a missing program file without touching data'
    [void](Run-Msi @('/x',$current.ProductCode) (Join-Path $run 'uninstall.log'))
    Check ((Product-State $current.ProductCode) -ne 5 -and -not(Test-Path -LiteralPath (Join-Path $installFolder 'NUMA-Control.exe'))) 'uninstaller removes product registration and program files'
    Check ((Test-Path -LiteralPath $sentinel) -and @(Get-ChildItem -LiteralPath $menuFolder -Filter '*.lnk' -ErrorAction SilentlyContinue).Count -eq 0) 'uninstaller preserves profiles and removes its menu shortcuts'
    Check (-not(Test-Path -LiteralPath (Join-Path $desktopFolder 'NUMA Control.lnk'))) 'uninstaller removes installed desktop launch link'
    @{Date=(Get-Date -Format o);Version=$current.ProductVersion;ReleaseSha256=$current.ApplicationSHA256;InstallerSha256=$production.SHA256;TestInstallerSha256=$current.SHA256;IsolationId=$isolationId;Passed=$checks.Count;Checks=$checks.ToArray();Logs=$run;Note='Real MSI install, upgrade, repair, rollback and uninstall from the production authoring and identical EXE payload. Product/upgrade/component GUIDs and registry namespace are isolated for this run. Production MSI itself receives full ICE validation. Temporary test products are removed.'}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $PSScriptRoot 'artifacts\installer-results.json') -Encoding UTF8
} finally {
    foreach($code in $owned) { if((Product-State $code) -eq 5) { try {[void](Run-Msi @('/x',$code) (Join-Path $run ('cleanup-'+$code.Trim('{}')+'.log')))} catch {Write-Warning $_} } }
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
}
