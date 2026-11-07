#requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$SetupPath, [string]$MsiPath, [string]$LegacyProjectPath)
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
if (-not $LegacyProjectPath -or -not (Test-Path -LiteralPath (Join-Path $LegacyProjectPath 'Build-Bootstrapper.ps1') -PathType Leaf)) {
    throw 'Supply -LegacyProjectPath with the isolated v1.7.3 source export prepared by New-ReleaseBundle.ps1.'
}
$LegacyProjectPath=(Resolve-Path -LiteralPath $LegacyProjectPath).Path
$wix=Join-Path $project '.tools\wix-7.0.1\wix.exe'
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
function Test-LicenseConsent([string]$path, [string]$caseDirectory, [string]$productCode, [string]$installationDirectory) {
    Check ([Environment]::UserInteractive -and (Get-Process -Id $PID).SessionId -ne 0) 'native license consent is tested in an interactive Windows session'
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    if (-not ('BootstrapperConsentNative' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class BootstrapperConsentNative {
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowEnabled(IntPtr window);
}
'@
    }
    $label = 'native setup license: '
    $log = Join-Path $caseDirectory 'license-cancel.log'
    $installName = $themeStrings.SelectSingleNode('//*[local-name()="String" and @Id="InstallInstallButton"]').Value.Replace('&','')
    $acceptName = $themeStrings.SelectSingleNode('//*[local-name()="String" and @Id="InstallAcceptCheckbox"]').Value.Replace('&','')
    $closeName = $themeStrings.SelectSingleNode('//*[local-name()="String" and @Id="InstallCancelButton"]').Value.Replace('&','')
    $consentExtract = Join-Path $caseDirectory 'consent-extracted'
    & $wix burn extract $path -oba $consentExtract -acceptEula wix7
    if ($LASTEXITCODE -ne 0) { throw ($label + 'cannot extract the isolated bundle identity') }
    [xml]$consentManifest = [IO.File]::ReadAllText((Join-Path $consentExtract 'manifest.xml'))
    $bundleCode = $consentManifest.SelectSingleNode('//*[local-name()="Registration"]').Code
    Check ($bundleCode -match '^\{[0-9A-Fa-f-]{36}\}$' -and -not (Read-UninstallEntry $bundleCode) -and
        -not (Read-UninstallEntry $productCode) -and -not (Test-Path -LiteralPath $installationDirectory)) ($label + 'isolated product and bundle are absent before opening the wizard')
    # This one test deliberately displays the interactive consent page; it never invokes Install.
    $process = Start-Process -FilePath $path -ArgumentList @('/install','/norestart','/log',('"'+$log+'"')) -WindowStyle Normal -PassThru
    $owned = @{}
    $owned[$process.Id] = $process.StartTime
    $window = $null
    function Update-OwnedProcesses {
        $all = @(Get-CimInstance Win32_Process)
        do {
            $added = $false
            $activeParents = @{}
            foreach ($entry in $owned.GetEnumerator()) {
                $parent = Get-Process -Id $entry.Key -ErrorAction SilentlyContinue
                if ($parent) { try { if ($parent.StartTime -eq $entry.Value) { $activeParents[$entry.Key] = $true } } finally { $parent.Dispose() } }
            }
            foreach ($item in $all) {
                if ($activeParents.ContainsKey([int]$item.ParentProcessId) -and -not $owned.ContainsKey([int]$item.ProcessId)) {
                    try { $child = Get-Process -Id $item.ProcessId -ErrorAction Stop; $owned[[int]$item.ProcessId] = $child.StartTime; $child.Dispose(); $added = $true } catch [Microsoft.PowerShell.Commands.ProcessCommandException] { }
                }
            }
        } while ($added)
    }
    function Find-Control($owner, [string]$name) {
        $condition = New-Object Windows.Automation.AndCondition (
            (New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::ClassNameProperty,'Button')),
            (New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::NameProperty,$name)))
        return $owner.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
    }
    function Wait-Control($owner, [string]$name) {
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        do {
            $control = Find-Control $owner $name
            if ($control -and -not $control.Current.IsOffscreen) { return $control }
            Start-Sleep -Milliseconds 150
        } while ([DateTime]::UtcNow -lt $deadline)
        throw ($label + 'visible control not found: ' + $name)
    }
    function Get-ControlHandle($control) {
        $handle = [IntPtr]$control.Current.NativeWindowHandle
        [uint32]$controlProcess = 0
        [void][BootstrapperConsentNative]::GetWindowThreadProcessId($handle,[ref]$controlProcess)
        if ($handle -eq [IntPtr]::Zero -or -not $owned.ContainsKey([int]$controlProcess) -or $control.Current.ClassName -ne 'Button') { throw ($label + 'control does not belong to the isolated native wizard') }
        return $handle
    }
    function Invoke-ConsentControl($control) {
        # WiX owner-drawn buttons may be exposed as UIA Panes without Invoke/Toggle patterns.
        # BM_CLICK follows the native button path; the Install control is explicitly excluded.
        if ($control.Current.Name -eq $installName) { throw ($label + 'Install must never be invoked by the consent test') }
        if (-not [BootstrapperConsentNative]::PostMessage((Get-ControlHandle $control),0x00F5,[IntPtr]::Zero,[IntPtr]::Zero)) { throw ($label + 'native button click could not be posted') }
    }
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        do {
            Update-OwnedProcesses
            $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.Condition]::TrueCondition)
            foreach ($candidate in $windows) {
                try {
                    if ($owned.ContainsKey($candidate.Current.ProcessId) -and (Find-Control $candidate $acceptName)) { $window = $candidate; break }
                } catch [Windows.Automation.ElementNotAvailableException] { }
            }
            if (-not $window) { Start-Sleep -Milliseconds 200 }
        } while (-not $window -and [DateTime]::UtcNow -lt $deadline)
        if (-not $window) {
            $diagnostic = @()
            foreach ($candidate in $windows) {
                try {
                    if ($owned.ContainsKey($candidate.Current.ProcessId)) {
                        $diagnostic += [pscustomobject]@{ProcessId=$candidate.Current.ProcessId;Name=$candidate.Current.Name;ClassName=$candidate.Current.ClassName;Controls=@($candidate.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition) | ForEach-Object { [pscustomobject]@{Name=$_.Current.Name;Type=$_.Current.ControlType.ProgrammaticName;ClassName=$_.Current.ClassName;Offscreen=$_.Current.IsOffscreen} })}
                    }
                } catch [Windows.Automation.ElementNotAvailableException] { }
            }
            $diagnostic | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $caseDirectory 'license-ui-failure.json') -Encoding UTF8
        }
        Check ($null -ne $window) ($label + 'wizard belongs to the isolated setup process tree')
        $accept = Wait-Control $window $acceptName
        $install = Wait-Control $window $installName
        $acceptHandle = Get-ControlHandle $accept
        $installHandle = Get-ControlHandle $install
        Check ([BootstrapperConsentNative]::SendMessage($acceptHandle,0x00F0,[IntPtr]::Zero,[IntPtr]::Zero).ToInt64() -eq 0 -and
            -not $install.Current.IsEnabled -and -not [BootstrapperConsentNative]::IsWindowEnabled($installHandle)) ($label + 'Install is disabled before explicit consent')
        $documentText = $null
        foreach ($element in $window.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)) {
            $pattern = $null
            if (-not $element.Current.IsOffscreen -and $element.TryGetCurrentPattern([Windows.Automation.TextPattern]::Pattern,[ref]$pattern)) {
                $text = $pattern.DocumentRange.GetText(-1)
                if ($text.Contains('MIT License')) { $documentText = $text; break }
            }
        }
        Check ($documentText -and $documentText.Replace("`r`n","`n").Replace("`r","`n").TrimEnd([char]10) -ceq $expectedLicense) ($label + 'visible rich-text viewer contains all canonical terms')
        Invoke-ConsentControl $accept
        $deadline = [DateTime]::UtcNow.AddSeconds(5)
        while (-not $install.Current.IsEnabled -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
        Check ([BootstrapperConsentNative]::SendMessage($acceptHandle,0x00F0,[IntPtr]::Zero,[IntPtr]::Zero).ToInt64() -eq 1 -and
            $install.Current.IsEnabled -and [BootstrapperConsentNative]::IsWindowEnabled($installHandle)) ($label + 'checking consent enables Install')
        Invoke-ConsentControl $accept
        $deadline = [DateTime]::UtcNow.AddSeconds(5)
        while ($install.Current.IsEnabled -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
        Check ([BootstrapperConsentNative]::SendMessage($acceptHandle,0x00F0,[IntPtr]::Zero,[IntPtr]::Zero).ToInt64() -eq 0 -and
            -not $install.Current.IsEnabled -and -not [BootstrapperConsentNative]::IsWindowEnabled($installHandle)) ($label + 'revoking consent disables Install again')
        $close = Wait-Control $window $closeName
        Invoke-ConsentControl $close
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
            Update-OwnedProcesses
            foreach ($owner in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.Condition]::TrueCondition)) {
                try {
                    if (-not $owned.ContainsKey($owner.Current.ProcessId)) { continue }
                    $dialogCondition = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::ClassNameProperty,'#32770')
                    foreach ($candidate in @($owner) + @($owner.FindAll([Windows.Automation.TreeScope]::Descendants,$dialogCondition))) {
                        if ($candidate.Current.ClassName -ne '#32770' -or -not $owned.ContainsKey($candidate.Current.ProcessId)) { continue }
                        $yesCondition = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::AutomationIdProperty,'6')
                        $yes = $candidate.FindFirst([Windows.Automation.TreeScope]::Descendants,$yesCondition)
                        if ($yes -and $yes.Current.IsEnabled) { Invoke-ConsentControl $yes }
                    }
                } catch [Windows.Automation.ElementNotAvailableException] { }
            }
            Start-Sleep -Milliseconds 150
            $process.Refresh()
        }
        Check ($process.HasExited) ($label + 'Close cancels the wizard without running Install')
        Check (-not (Read-UninstallEntry $bundleCode) -and -not (Read-UninstallEntry $productCode) -and
            -not (Test-Path -LiteralPath $installationDirectory)) ($label + 'cancellation creates no isolated bundle, MSI registration or application files')
        $cancelLog = [IO.File]::ReadAllText($log)
        Check ($cancelLog -notmatch 'Applying execute package:' -and $cancelLog -notmatch 'Registering bundle dependency provider:') ($label + 'cancellation applies no prerequisite, MSI or bundle registration')
    } finally {
        # Only stop descendants of this isolated test wizard, validating process start times against PID reuse.
        foreach ($entry in $owned.GetEnumerator()) {
            $remaining = Get-Process -Id $entry.Key -ErrorAction SilentlyContinue
            if ($remaining) { try { if ($remaining.StartTime -eq $entry.Value) { $remaining.Kill(); [void]$remaining.WaitForExit(5000) } } finally { $remaining.Dispose() } }
        }
        $process.Dispose()
    }
}
$extract=Join-Path $run 'extracted'
& $wix burn extract $SetupPath -oba (Join-Path $extract 'UX') -o (Join-Path $extract 'AttachedContainer') -acceptEula wix7
if($LASTEXITCODE -ne 0){throw 'Cannot extract the production bootstrapper.'}
$embedded=@(Get-ChildItem -LiteralPath (Join-Path $extract 'AttachedContainer') -File -Recurse)
Check ($embedded.Count -eq 1 -and $embedded[0].Name -eq [IO.Path]::GetFileName($MsiPath) -and
    (Get-FileHash -LiteralPath $embedded[0].FullName).Hash -eq (Get-FileHash -LiteralPath $MsiPath).Hash) 'setup contains only the exact verified MSI'
$license = Join-Path $extract 'UX\license.rtf'
Check (Test-Path -LiteralPath $license -PathType Leaf) 'setup includes its license text for offline viewing'
Check ((Get-FileHash -LiteralPath $license).Hash -eq (Get-FileHash -LiteralPath (Join-Path $project 'installer\license.rtf')).Hash) 'setup embeds the exact generated RTF'
Add-Type -AssemblyName System.Windows.Forms
$licenseBox = New-Object System.Windows.Forms.RichTextBox
try {
    $licenseBox.LoadFile($license)
    $sourceRoot = Split-Path -Parent $project
    $expectedLicense = [IO.File]::ReadAllText((Join-Path $sourceRoot 'LICENSE')) + [Environment]::NewLine +
        [Environment]::NewLine + [IO.File]::ReadAllText((Join-Path $sourceRoot 'THIRD-PARTY-NOTICES.txt'))
    $expectedLicense = $expectedLicense.Replace("`r`n", "`n").Replace("`r", "`n").TrimEnd([char]10)
    $renderedLicense = $licenseBox.Text.Replace("`r`n", "`n").Replace("`r", "`n").TrimEnd([char]10)
    Check ($renderedLicense -ceq $expectedLicense) 'RTF preserves all canonical MIT, WiX and Microsoft terms'
} finally { $licenseBox.Dispose() }
[xml]$theme = [IO.File]::ReadAllText((Join-Path $extract 'UX\thm.xml'))
[xml]$themeStrings = [IO.File]::ReadAllText((Join-Path $extract 'UX\thm.wxl'))
[xml]$applicationData = [IO.File]::ReadAllText((Join-Path $extract 'UX\BootstrapperApplicationData.xml'))
$licenseInformation = $applicationData.SelectSingleNode('//*[local-name()="WixStdbaInformation"]')
Check ($null -ne $licenseInformation -and $licenseInformation.LicenseFile -ceq 'license.rtf' -and
    -not $licenseInformation.HasAttribute('LicenseUrl')) 'bootstrapper uses the embedded RTF license without a hidden hyperlink licence'
Check ($null -ne $theme.SelectSingleNode('//*[local-name()="Richedit" and @Name="EulaRichedit"]') -and
    $null -ne $theme.SelectSingleNode('//*[local-name()="Checkbox" and @Name="EulaAcceptCheckbox"]') -and
    $null -eq $theme.SelectSingleNode('//*[@Name="EulaHyperlink"]')) 'license page has an offline rich-text viewer and an explicit acceptance checkbox'
$manifestFiles=@(Get-ChildItem -LiteralPath $extract -Filter '*.xml' -Recurse -File|Where-Object{[IO.File]::ReadAllText($_.FullName).Contains('DetectCondition="NETFRAMEWORK45')})
Check ($manifestFiles.Count -eq 1) 'production bootstrapper exposes a pinned .NET detection manifest'
[xml]$manifest=[IO.File]::ReadAllText($manifestFiles[0].FullName)
Check ($manifest.DocumentElement.EngineVersion -eq '7.0.1.0' -and
    [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $extract 'UX\wixstdba.exe')).FileVersion -eq '7.0.1.0') 'setup embeds the pinned WiX 7.0.1 engine and native bootstrapper application'
$registration=$manifest.SelectSingleNode('//*[local-name()="Registration"]')
$msiPackage=$manifest.SelectSingleNode('//*[local-name()="MsiPackage" and @Id="NumaControlMsi"]')
Check ($registration.Scope -eq 'perUser' -and $msiPackage.Scope -eq 'perUser') 'bundle and chained application remain per-user after the WiX migration'
$netfx=$manifest.SelectSingleNode('//*[local-name()="ExePackage" and @Id="NetFx48Web"]')
Check ($null -ne $netfx -and $netfx.DetectCondition -match '^NETFRAMEWORK45\s*>=\s*528040$') 'NET Framework 4.8+ is detected by the documented minimum Release DWORD'
$minimum=[int]([regex]::Match($netfx.DetectCondition,'\d+$').Value)
foreach($case in @(@{Release=0;Present=$false},@{Release=461808;Present=$false},@{Release=528039;Present=$false},@{Release=528040;Present=$true},@{Release=528049;Present=$true},@{Release=533325;Present=$true})){
 Check (($case.Release -ge $minimum) -eq $case.Present) ('compiled detection boundary / Release '+$case.Release+' / present '+$case.Present)
}
Check ($netfx.Permanent -eq 'yes' -and $netfx.Scope -eq 'perMachine') 'shared .NET runtime is permanent and never removed with the application'
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
    & (Join-Path $LegacyProjectPath 'Build-Installer.ps1') -ProductVersion $previousVersion -OutputPath $previousMsi -IsolationId $caseId -FixtureExe $fixtureExe
    & (Join-Path $LegacyProjectPath 'Build-Bootstrapper.ps1') -ProductVersion $previousVersion -MsiPath $previousMsi -OutputPath $previousSetup -IsolationId $caseId -IsolationDirectory $isolatedDirectory -LegacyMsiEntry:$legacyMsiVisible
    & (Join-Path $project 'Build-Installer.ps1') -ProductVersion $version -OutputPath $isolatedMsi -IsolationId $caseId
    & (Join-Path $project 'Build-Bootstrapper.ps1') -ProductVersion $version -MsiPath $isolatedMsi -OutputPath $isolatedSetup -IsolationId $caseId -IsolationDirectory $isolatedDirectory
    $previousProduct = (Get-Content -LiteralPath ($previousMsi + '.json') -Raw | ConvertFrom-Json).ProductCode
    $currentProduct = (Get-Content -LiteralPath ($isolatedMsi + '.json') -Raw | ConvertFrom-Json).ProductCode
    if ($previousVersion -eq '1.7.0') { Test-LicenseConsent $isolatedSetup $case $currentProduct $isolatedDirectory }
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
Test-SetupUpgrade '1.7.3' $false
[pscustomobject]@{Version=$version;SetupSha256=(Get-FileHash -LiteralPath $SetupPath).Hash;Passed=$checks.Count;Checks=$checks.ToArray();MissingRuntimeInstallation='Detection boundaries and download verified; actual installation on a clean Windows without .NET requires a separate VM.'}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $PSScriptRoot 'artifacts\bootstrapper-results.json') -Encoding UTF8
