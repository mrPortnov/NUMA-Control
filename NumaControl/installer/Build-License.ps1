#requires -Version 5.1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$text = [IO.File]::ReadAllText((Join-Path $root 'LICENSE')) + [Environment]::NewLine +
    [Environment]::NewLine + [IO.File]::ReadAllText((Join-Path $root 'THIRD-PARTY-NOTICES.txt'))
$rtf = [Text.StringBuilder]::new()
[void]$rtf.Append('{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\f0\fs18 ')
foreach ($character in ($text.Replace("`r`n", "`n").Replace("`r", "`n")).ToCharArray()) {
    $code = [int]$character
    if ($code -eq 10) { [void]$rtf.Append("\par`r`n") }
    elseif ($code -eq 9) { [void]$rtf.Append('\tab ') }
    elseif ($character -eq '\' -or $character -eq '{' -or $character -eq '}') { [void]$rtf.Append('\').Append($character) }
    elseif ($code -gt 127) {
        $signed = if ($code -gt 32767) { $code - 65536 } else { $code }
        [void]$rtf.Append('\u').Append($signed).Append('?')
    }
    else { [void]$rtf.Append($character) }
}
[void]$rtf.Append('}')
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'license.rtf'),$rtf.ToString(),[Text.Encoding]::ASCII)
