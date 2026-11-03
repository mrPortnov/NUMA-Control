#requires -Version 5.1
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The x64 .NET Framework compiler was not found.' }
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
$references = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll', 'System.Xaml.dll')
$arguments = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/codepage:65001', '/warn:4', '/warnaserror+', '/main:NumaControl.Program', "/out:$PSScriptRoot\NUMA-Control.exe", "/win32manifest:$PSScriptRoot\src\app.manifest", "/win32icon:$PSScriptRoot\src\app.ico")
foreach ($reference in $references) { $arguments += "/reference:$framework\$reference" }
foreach ($reference in @('WindowsBase.dll', 'PresentationCore.dll', 'PresentationFramework.dll')) { $arguments += "/reference:$framework\WPF\$reference" }
foreach ($resource in @('Theme.xaml', 'MainWindow.xaml', 'app.ico')) { $arguments += "/resource:$PSScriptRoot\src\$resource,NumaControl.$resource" }
& $compiler @arguments @sources
if ($LASTEXITCODE -ne 0) { throw 'Build failed. Close NUMA Control through its tray menu before rebuilding.' }
Write-Host "Built: $PSScriptRoot\NUMA-Control.exe"
