#requires -Version 5.1
param([string]$ProductVersion='1.7.4',[Parameter(Mandatory=$true)][string]$MsiPath,[string]$OutputPath,[string]$IsolationId,[string]$IsolationDirectory,[switch]$LegacyMsiEntry)
$ErrorActionPreference='Stop'
if($ProductVersion -notmatch '^\d+\.\d+\.\d+$'){throw 'Use a three-part release version.'}
if($LegacyMsiEntry -and -not $IsolationId){throw 'A visible chained MSI is available only for an isolated upgrade test.'}
$msiVisible=if($LegacyMsiEntry){'yes'}else{'no'}
$MsiPath=[IO.Path]::GetFullPath($MsiPath)
if(-not(Test-Path -LiteralPath $MsiPath -PathType Leaf)){throw 'Build the matching MSI first.'}
$tools=Join-Path $PSScriptRoot '.tools\wix-7.0.1'
& (Join-Path $PSScriptRoot 'installer\Build-License.ps1')
$wix=Join-Path $tools 'wix.exe'
$bal=Join-Path $tools 'extensions\WixToolset.Bal.wixext\wixext7\WixToolset.BootstrapperApplications.wixext.dll'
$netfx=Join-Path $tools 'extensions\WixToolset.Netfx.wixext\wixext7\WixToolset.Netfx.wixext.dll'
foreach($file in @($wix,$bal,$netfx)){if(-not(Test-Path -LiteralPath $file -PathType Leaf)){throw 'WiX 7.0.1 tools are required. Run Get-WixTools.ps1 -AcceptEula first.'}}
if(-not $OutputPath){$OutputPath=Join-Path $PSScriptRoot ('NUMA-Control-'+$ProductVersion+'-setup-x64.exe')}
$build=Join-Path $PSScriptRoot 'installer\build';[void](New-Item -ItemType Directory -Path $build -Force)
$extensions=@('-ext',$bal,'-ext',$netfx)
$bundleUpgradeCode='6D2F3039-E291-4844-9B78-89C0BED9797C'
$isolationDefines=@()
if($IsolationId -or $IsolationDirectory){
 if($IsolationId -notmatch '^[0-9a-f]{32}$' -or -not $IsolationDirectory){throw 'Both a GUID isolation ID and a test directory are required.'}
 $algorithm=[Security.Cryptography.MD5]::Create();try{$bundleUpgradeCode=([Guid]::new($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes('NUMA Control bundle test '+$IsolationId)))).ToString('D')}finally{$algorithm.Dispose()}
 $isolationDefines+=@('-d',('IsolationDirectory='+[IO.Path]::GetFullPath($IsolationDirectory)))
}
$defines=@('-d',('BundleUpgradeCode='+$bundleUpgradeCode),'-d',('ProjectRoot='+$PSScriptRoot),'-d',('ProductVersion='+$ProductVersion),'-d',('MsiPath='+$MsiPath),'-d',('MsiVisible='+$msiVisible))
& $wix build -acceptEula wix7 -arch x64 @extensions @isolationDefines @defines -intermediatefolder $build -out $OutputPath (Join-Path $PSScriptRoot 'installer\Bundle.wxs')
if($LASTEXITCODE -ne 0){throw 'Bootstrapper build failed.'}
Write-Host ('Bootstrapper built: '+$OutputPath)
