#requires -Version 5.1
param([string]$ProductVersion='1.7.4',[string]$OutputPath,[switch]$InjectRollback,[string]$IsolationId,[string]$FixtureExe)
$ErrorActionPreference='Stop'
if($ProductVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Use a three-part MSI version.' }
if($IsolationId -and $IsolationId -notmatch '^[0-9a-f]{32}$') { throw 'IsolationId must be a lowercase GUID without separators.' }
if($FixtureExe -and -not $IsolationId) { throw 'A replacement executable is available only for an isolated installer test.' }
$application=if($FixtureExe) { (Resolve-Path -LiteralPath $FixtureExe).Path } else { Join-Path $PSScriptRoot 'NUMA-Control.exe' }
$toolDirectory=Join-Path $PSScriptRoot '.tools\wix-7.0.1'
$wix=Join-Path $toolDirectory 'wix.exe'
$uiExtension=Join-Path $toolDirectory 'extensions\WixToolset.UI.wixext\wixext7\WixToolset.UI.wixext.dll'
foreach($tool in @($wix,$uiExtension)) { if(-not(Test-Path -LiteralPath $tool -PathType Leaf)) { throw 'WiX 7.0.1 tools are required. Run Get-WixTools.ps1 -AcceptEula first.' } }
& (Join-Path $PSScriptRoot 'installer\Build-Branding.ps1')
$build=Join-Path $PSScriptRoot 'installer\build'; [void](New-Item -ItemType Directory -Path $build -Force)
$hash=[Security.Cryptography.MD5]::Create()
try { $productCode=([Guid]::new($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes('NUMA Control MSI '+$ProductVersion+$IsolationId)))).ToString('B').ToUpperInvariant() } finally {$hash.Dispose()}
if(-not $OutputPath) { $OutputPath=Join-Path $PSScriptRoot ('NUMA-Control-'+$ProductVersion+'-setup-x64.msi') }
$authoring=Join-Path $PSScriptRoot 'installer\Product.wxs'
$upgradeCode='{5B491C32-5ADB-44C9-8E40-EA269B744AA9}'
if($IsolationId) {
    function Isolated-Guid([string]$seed) { $algorithm=[Security.Cryptography.MD5]::Create(); try { return ([Guid]::new($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($IsolationId+$seed)))).ToString('D').ToUpperInvariant() } finally {$algorithm.Dispose()} }
    $isolatedSource=[IO.File]::ReadAllText($authoring)
    $privateUpgrade=Isolated-Guid 'upgrade'; $upgradeCode='{'+$privateUpgrade+'}'
    $isolatedSource=$isolatedSource.Replace('5B491C32-5ADB-44C9-8E40-EA269B744AA9',$privateUpgrade).Replace('Software\NUMA Control\Installer',('Software\NUMA Control\Tests\'+$IsolationId+'\Installer'))
    $isolatedSource=[regex]::Replace($isolatedSource,'Guid="([0-9A-Fa-f-]{36})"',{param($match) 'Guid="'+(Isolated-Guid $match.Groups[1].Value)+'"'})
    $authoring=Join-Path $build ('isolated-'+$ProductVersion+'-'+$IsolationId+'.wxs'); [IO.File]::WriteAllText($authoring,$isolatedSource,[Text.UTF8Encoding]::new($false))
}
$defines=@('-d',('ProjectRoot='+$PSScriptRoot),'-d',('ApplicationExe='+$application),'-d',('ProductVersion='+$ProductVersion),'-d',('ProductCode='+$productCode))
if($InjectRollback) { $defines+=@('-d','InjectRollback=1') }
# ICE90/91 assume a package can switch to per-machine scope. This package is
# exclusively per-user and has an explicit NOT ALLUSERS launch condition.
& $wix build -acceptEula wix7 -arch x64 @defines -culture ru-ru -ext $uiExtension -intermediatefolder $build -out $OutputPath $authoring
if($LASTEXITCODE -ne 0) { throw 'MSI build failed.' }
& $wix msi validate -acceptEula wix7 -sice ICE90 -sice ICE91 $OutputPath
if($LASTEXITCODE -ne 0) { throw 'MSI validation failed.' }
[pscustomobject]@{ProductVersion=$ProductVersion;ProductCode=$productCode;UpgradeCode=$upgradeCode;IsolationId=$IsolationId;File=[IO.Path]::GetFileName($OutputPath);SHA256=(Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash;ApplicationSHA256=(Get-FileHash -LiteralPath $application -Algorithm SHA256).Hash;Scope='Current user';DataPolicy='Keep user-generated data on uninstall'}|ConvertTo-Json|Set-Content -LiteralPath ($OutputPath+'.json') -Encoding UTF8
Write-Host "Installer built: $OutputPath"
