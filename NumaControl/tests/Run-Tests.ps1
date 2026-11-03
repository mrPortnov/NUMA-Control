#requires -Version 5.1
param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path -Parent $PSScriptRoot
if (-not $SkipBuild) { & (Join-Path $projectDirectory 'Build.ps1') }
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$outputDirectory = Join-Path $PSScriptRoot 'artifacts'
$runDirectory = Join-Path $outputDirectory ([Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $runDirectory -Force)
$harness = Join-Path $runDirectory 'NumaTests.exe'
$fixture = Join-Path $runDirectory ('NumaFixture' + [Guid]::NewGuid().ToString('N').Substring(0,8) + '.exe')
& $compiler /nologo /target:exe /platform:x64 /optimize+ "/out:$fixture" (Join-Path $PSScriptRoot 'Fixture.cs')
if ($LASTEXITCODE -ne 0) { throw 'Fixture compilation failed.' }
& (Join-Path $PSScriptRoot 'Build-IconFixture.ps1') -Directory $runDirectory
$arguments = @('/nologo','/target:exe','/platform:x64','/optimize+','/codepage:65001','/warn:4','/warnaserror+','/main:NumaControl.Tests',"/out:$harness")
foreach ($reference in @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Xaml.dll')) { $arguments += "/reference:$framework\$reference" }
foreach ($reference in @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll')) { $arguments += "/reference:$framework\WPF\$reference" }
foreach ($resource in @('Theme.xaml','MainWindow.xaml','app.ico')) { $arguments += "/resource:$projectDirectory\src\$resource,NumaControl.$resource" }
$sources = @(Get-ChildItem -LiteralPath (Join-Path $projectDirectory 'src') -Filter '*.cs' | ForEach-Object FullName)
$testSources = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*Tests.cs' | ForEach-Object FullName)
& $compiler @arguments @sources @testSources
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
& $harness $runDirectory $fixture
if ($LASTEXITCODE -ne 0) { throw "Tests failed. Details: $runDirectory" }
Copy-Item -LiteralPath (Join-Path $runDirectory 'test-results.json') -Destination (Join-Path $outputDirectory 'latest-results.json') -Force
Write-Host "Results: $outputDirectory\latest-results.json"
