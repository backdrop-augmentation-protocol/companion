param(
    [int]$Port = 4311
)

$ErrorActionPreference = 'Stop'
$desktop = Join-Path $PSScriptRoot 'Bap.Companion.Desktop.exe'
if (Test-Path -LiteralPath $desktop) {
    & $desktop --port $Port
    exit $LASTEXITCODE
}

$server = Join-Path $PSScriptRoot 'Bap.Companion.Server.exe'
if (-not (Test-Path -LiteralPath $server)) { throw "A Companion executable was not found. Run publish-companion.ps1 first." }
& $server --urls "http://127.0.0.1:$Port"
exit $LASTEXITCODE
