#requires -Version 5.1
param([string]$ProductVersion='1.6.1',[string]$OutputPath,[switch]$InjectRollback,[string]$IsolationId)
$ErrorActionPreference='Stop'
if($ProductVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Use a three-part MSI version.' }
if($IsolationId -and $IsolationId -notmatch '^[0-9a-f]{32}$') { throw 'IsolationId must be a lowercase GUID without separators.' }
$toolDirectory=Join-Path $PSScriptRoot '.tools\wix-3.14.1'
if(-not(Test-Path -LiteralPath (Join-Path $toolDirectory 'candle.exe'))) { throw 'WiX 3.14.1 portable tools are required in .tools\wix-3.14.1. Download wix314-binaries.zip from the official wix3141rtm GitHub release.' }
& (Join-Path $PSScriptRoot 'installer\Build-Branding.ps1')
$build=Join-Path $PSScriptRoot 'installer\build'; [void](New-Item -ItemType Directory -Path $build -Force)
$hash=[Security.Cryptography.MD5]::Create()
try { $productCode=([Guid]::new($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes('NUMA Control MSI '+$ProductVersion+$IsolationId)))).ToString('B').ToUpperInvariant() } finally {$hash.Dispose()}
if(-not $OutputPath) { $OutputPath=Join-Path $PSScriptRoot ('NUMA-Control-'+$ProductVersion+'-setup-x64.msi') }
$object=Join-Path $build ('product-'+$ProductVersion+'.wixobj')
$authoring=Join-Path $PSScriptRoot 'installer\Product.wxs'
$upgradeCode='{5B491C32-5ADB-44C9-8E40-EA269B744AA9}'
if($IsolationId) {
    function Isolated-Guid([string]$seed) { $algorithm=[Security.Cryptography.MD5]::Create(); try { return ([Guid]::new($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($IsolationId+$seed)))).ToString('D').ToUpperInvariant() } finally {$algorithm.Dispose()} }
    $isolatedSource=Get-Content -LiteralPath $authoring -Raw
    $privateUpgrade=Isolated-Guid 'upgrade'; $upgradeCode='{'+$privateUpgrade+'}'
    $isolatedSource=$isolatedSource.Replace('5B491C32-5ADB-44C9-8E40-EA269B744AA9',$privateUpgrade).Replace('Software\NUMA Control\Installer',('Software\NUMA Control\Tests\'+$IsolationId+'\Installer'))
    $isolatedSource=[regex]::Replace($isolatedSource,'Guid="([0-9A-Fa-f-]{36})"',{param($match) 'Guid="'+(Isolated-Guid $match.Groups[1].Value)+'"'})
    $authoring=Join-Path $build ('isolated-'+$ProductVersion+'-'+$IsolationId+'.wxs'); [IO.File]::WriteAllText($authoring,$isolatedSource,[Text.UTF8Encoding]::new($false))
    $object=Join-Path $build ('isolated-'+$ProductVersion+'-'+$IsolationId+'.wixobj')
}
$defines=@(('-dProjectRoot='+$PSScriptRoot), ('-dProductVersion='+$ProductVersion), ('-dProductCode='+$productCode))
if($InjectRollback) { $defines+='-dInjectRollback=1' }
& (Join-Path $toolDirectory 'candle.exe') -nologo -arch x64 @defines -out $object $authoring
if($LASTEXITCODE -ne 0) { throw 'MSI authoring compilation failed.' }
# ICE90/91 assume a package can switch to per-machine scope. This package is
# exclusively per-user and has an explicit NOT ALLUSERS launch condition.
& (Join-Path $toolDirectory 'light.exe') -nologo -cultures:ru-ru -sice:ICE90 -sice:ICE91 -ext (Join-Path $toolDirectory 'WixUIExtension.dll') -out $OutputPath $object
if($LASTEXITCODE -ne 0) { throw 'MSI linking or validation failed.' }
[pscustomobject]@{ProductVersion=$ProductVersion;ProductCode=$productCode;UpgradeCode=$upgradeCode;IsolationId=$IsolationId;File=[IO.Path]::GetFileName($OutputPath);SHA256=(Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash;ApplicationSHA256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'NUMA-Control.exe') -Algorithm SHA256).Hash;Scope='Current user';DataPolicy='Keep user-generated data on uninstall'}|ConvertTo-Json|Set-Content -LiteralPath ($OutputPath+'.json') -Encoding UTF8
Write-Host "Installer built: $OutputPath"
