param(
    [int]$Port = 4311
)

$ErrorActionPreference = 'Stop'
$server = Join-Path $PSScriptRoot 'Bap.Companion.Server.exe'
if (-not (Test-Path -LiteralPath $server)) {
    throw "Bap.Companion.Server.exe was not found next to this launcher. Run publish-companion.ps1 first."
}

& $server --urls "http://127.0.0.1:$Port"
exit $LASTEXITCODE
