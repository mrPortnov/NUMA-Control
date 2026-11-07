#requires -Version 5.1
param([switch]$AcceptEula)
$ErrorActionPreference = 'Stop'
if (-not $AcceptEula) { throw 'Review the WiX 7 OSMF EULA, then pass -AcceptEula to prepare its official binaries.' }
$ProgressPreference = 'SilentlyContinue'
$tools = Join-Path $PSScriptRoot '.tools'
$downloads = Join-Path $tools 'downloads'
[void](New-Item -ItemType Directory -Path $downloads -Force)
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-VerifiedDownload([string]$Name, [string]$Url, [string]$Sha256) {
    $file = Join-Path $downloads $Name
    if ((Test-Path -LiteralPath $file -PathType Leaf) -and (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -eq $Sha256) {
        return $file
    }
    $temporary = $file + '.' + [Guid]::NewGuid().ToString('N') + '.download'
    try {
        Invoke-WebRequest -UseBasicParsing -TimeoutSec 180 -Uri $Url -OutFile $temporary
        if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $Sha256) {
            throw ('Downloaded tool checksum mismatch: ' + $Name)
        }
        Move-Item -LiteralPath $temporary -Destination $file -Force
    } finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary }
    }
    return $file
}

function Expand-VerifiedZip([string]$Archive, [string]$Destination) {
    $destinationRoot = [IO.Path]::GetFullPath($Destination).TrimEnd('\') + '\'
    [void](New-Item -ItemType Directory -Path $destinationRoot -Force)
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        foreach ($entry in $zip.Entries) {
            $target = [IO.Path]::GetFullPath((Join-Path $destinationRoot $entry.FullName))
            if (-not $target.StartsWith($destinationRoot, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Tool archive contains a path outside its destination.'
            }
            if ($entry.FullName.EndsWith('/')) { [void](New-Item -ItemType Directory -Path $target -Force) }
            else {
                [void](New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force)
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
            }
        }
    } finally { $zip.Dispose() }
}

$originalTls = [Net.ServicePointManager]::SecurityProtocol
try {
    [Net.ServicePointManager]::SecurityProtocol = $originalTls -bor [Net.SecurityProtocolType]::Tls12
    # WiX 3 is retained exclusively for extraction and pre-migration installer fixtures.
    $legacyArchive = Get-VerifiedDownload 'wix314-binaries.zip' 'https://github.com/wixtoolset/wix3/releases/download/wix3141rtm/wix314-binaries.zip' '6ac824e1642d6f7277d0ed7ea09411a508f6116ba6fae0aa5f2c7daa2ff43d31'
    $legacy = Join-Path $tools 'wix-3.14.1'
    Expand-VerifiedZip $legacyArchive $legacy

    # The official CLI MSI contains the .NET Framework 4.7.2 build of wix.exe.
    # Extracting its files avoids machine installation and does not require a .NET SDK.
    $cliMsi = Get-VerifiedDownload 'wix-cli-x64-7.0.1.msi' 'https://github.com/wixtoolset/wix/releases/download/v7.0.1/wix-cli-x64.msi' '93673c89ab33b1fd3f07ad5e609b7cae694bd716b2914259b61a41c02303b64c'
    $current = Join-Path $tools 'wix-7.0.1'
    $extracted = Join-Path $tools ('extract-' + [Guid]::NewGuid().ToString('N'))
    [void](New-Item -ItemType Directory -Path $extracted)
    try {
        $authoringPath = Join-Path $extracted 'cli.wxs'
        & (Join-Path $legacy 'dark.exe') -nologo -x $extracted $cliMsi -o $authoringPath
        if ($LASTEXITCODE -ne 0) { throw 'Cannot extract the pinned WiX CLI MSI.' }
        [xml]$authoring = Get-Content -LiteralPath $authoringPath -Raw -Encoding UTF8
        $bin = $authoring.SelectSingleNode('//*[local-name()="Directory" and @Id="BinFolder"]')
        if (-not $bin) { throw 'The WiX CLI MSI has no expected binary directory.' }
        foreach ($file in $bin.SelectNodes('.//*[local-name()="File"]')) {
            $relative = $file.GetAttribute('Name')
            for ($node = $file.ParentNode; $node -ne $bin; $node = $node.ParentNode) {
                if ($node.LocalName -eq 'Directory') { $relative = Join-Path $node.GetAttribute('Name') $relative }
            }
            $target = [IO.Path]::GetFullPath((Join-Path $current $relative))
            if (-not $target.StartsWith([IO.Path]::GetFullPath($current) + '\', [StringComparison]::OrdinalIgnoreCase)) {
                throw 'The WiX CLI MSI contains an invalid file destination.'
            }
            [void](New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force)
            Copy-Item -LiteralPath $file.GetAttribute('Source') -Destination $target -Force
        }
    } finally {
        $resolved = [IO.Path]::GetFullPath($extracted)
        if ($resolved.StartsWith([IO.Path]::GetFullPath($tools) + '\', [StringComparison]::OrdinalIgnoreCase) -and
            (Split-Path -Leaf $resolved) -match '^extract-[0-9a-f]{32}$') {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }

    $extensions = @(
        @{ Name = 'WixToolset.UI.wixext'; Hash = '4bcb634063d9db9f085f80dae9766083b0f6251aa951c8084ea6b7c3e0399293'; Assembly = 'WixToolset.UI.wixext.dll' },
        @{ Name = 'WixToolset.Bal.wixext'; Hash = 'b86e8c4e708b112ad9df250373158ef33e7857a25c98539260f3970ac57c9543'; Assembly = 'WixToolset.BootstrapperApplications.wixext.dll' },
        @{ Name = 'WixToolset.Netfx.wixext'; Hash = 'd440735c16863b54d13d90ecfefd060121dfd923a12e5d5a3e0b6b82848abf72'; Assembly = 'WixToolset.Netfx.wixext.dll' }
    )
    foreach ($extension in $extensions) {
        $id = $extension.Name.ToLowerInvariant()
        $package = Get-VerifiedDownload ($id + '.7.0.1.nupkg') ("https://api.nuget.org/v3-flatcontainer/$id/7.0.1/$id.7.0.1.nupkg") $extension.Hash
        $directory = Join-Path (Join-Path $current 'extensions') $extension.Name
        Expand-VerifiedZip $package $directory
        if (-not (Test-Path -LiteralPath (Join-Path $directory ('wixext7\' + $extension.Assembly)) -PathType Leaf)) {
            throw ('Expected WiX extension assembly missing: ' + $extension.Name)
        }
    }
    $version = & (Join-Path $current 'wix.exe') --version
    if ($LASTEXITCODE -ne 0 -or $version -notmatch '^7\.0\.1(?:\+|$)') { throw 'The prepared WiX CLI version does not match 7.0.1.' }
    Write-Host ('Prepared WiX ' + $version + ' and pinned extensions. Legacy tools are available for upgrade tests.')
} finally { [Net.ServicePointManager]::SecurityProtocol = $originalTls }
