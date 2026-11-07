#requires -Version 5.1
param([Parameter(Mandatory = $true)][string]$Path)

$ErrorActionPreference = 'Stop'
$result = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
$visual = $result.Visual
if ($null -eq $visual -or $visual.Schema -ne 1 -or ($visual.Schema -isnot [int] -and $visual.Schema -isnot [long]) -or
    ($visual.Checks -isnot [int] -and $visual.Checks -isnot [long]) -or
    $visual.Checks -le 0 -or $visual.Checks -gt $result.Passed) {
    throw 'The mandatory visual verification result is missing or invalid.'
}

$expected = @(foreach ($language in @('ru', 'en')) {
    foreach ($theme in @('standard', 'win11-light', 'win11-dark', 'ami-bios')) {
        foreach ($size in @('960x650', '1220x870', '1440x960')) {
            "$language/$theme/$size"
        }
    }
})
$cases = @($visual.Cases)
if ($cases.Count -ne $expected.Count -or (Compare-Object -ReferenceObject $expected -DifferenceObject $cases -CaseSensitive)) {
    throw 'Visual verification must cover every language, theme and window size.'
}

$summary = 'Visual verification passed: {0} checks across {1} language/theme/window-size combinations.' -f $visual.Checks, $cases.Count
Write-Host $summary
if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $summary -Encoding utf8 }
