#requires -Version 5.1
param([string]$ProductVersion='1.7.1',[Parameter(Mandatory=$true)][string]$MsiPath,[string]$OutputPath,[string]$IsolationId,[string]$IsolationDirectory,[switch]$LegacyMsiEntry)
$ErrorActionPreference='Stop'
if($ProductVersion -notmatch '^\d+\.\d+\.\d+$'){throw 'Use a three-part release version.'}
if($LegacyMsiEntry -and -not $IsolationId){throw 'A visible chained MSI is available only for an isolated upgrade test.'}
$msiVisible=if($LegacyMsiEntry){'yes'}else{'no'}
$MsiPath=[IO.Path]::GetFullPath($MsiPath)
if(-not(Test-Path -LiteralPath $MsiPath -PathType Leaf)){throw 'Build the matching MSI first.'}
$tools=Join-Path $PSScriptRoot '.tools\wix-3.14.1'
foreach($file in @('candle.exe','light.exe','WixBalExtension.dll','WixNetFxExtension.dll')){if(-not(Test-Path -LiteralPath (Join-Path $tools $file))){throw 'Official WiX 3.14.1 tools and extensions are required.'}}
if(-not $OutputPath){$OutputPath=Join-Path $PSScriptRoot ('NUMA-Control-'+$ProductVersion+'-setup-x64.exe')}
$build=Join-Path $PSScriptRoot 'installer\build';[void](New-Item -ItemType Directory -Path $build -Force)
$object=Join-Path $build ('bundle-'+$ProductVersion+'.wixobj')
$extensions=@('-ext',(Join-Path $tools 'WixBalExtension.dll'),'-ext',(Join-Path $tools 'WixNetFxExtension.dll'))
$bundleUpgradeCode='6D2F3039-E291-4844-9B78-89C0BED9797C'
$isolationDefines=@()
if($IsolationId -or $IsolationDirectory){
 if($IsolationId -notmatch '^[0-9a-f]{32}$' -or -not $IsolationDirectory){throw 'Both a GUID isolation ID and a test directory are required.'}
 $algorithm=[Security.Cryptography.MD5]::Create();try{$bundleUpgradeCode=([Guid]::new($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes('NUMA Control bundle test '+$IsolationId)))).ToString('D')}finally{$algorithm.Dispose()}
 $isolationDefines+=('-dIsolationDirectory='+[IO.Path]::GetFullPath($IsolationDirectory))
 $object=Join-Path $build ('bundle-test-'+$IsolationId+'.wixobj')
}
& (Join-Path $tools 'candle.exe') -nologo @extensions @isolationDefines ('-dBundleUpgradeCode='+$bundleUpgradeCode) ('-dProjectRoot='+$PSScriptRoot) ('-dProductVersion='+$ProductVersion) ('-dMsiPath='+$MsiPath) ('-dMsiVisible='+$msiVisible) -out $object (Join-Path $PSScriptRoot 'installer\Bundle.wxs')
if($LASTEXITCODE -ne 0){throw 'Bootstrapper authoring compilation failed.'}
& (Join-Path $tools 'light.exe') -nologo @extensions -out $OutputPath $object
if($LASTEXITCODE -ne 0){throw 'Bootstrapper linking failed.'}
Write-Host ('Bootstrapper built: '+$OutputPath)
