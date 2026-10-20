param(
    [string]$Runtime = 'win-x64',
    [switch]$SelfContained,
    [string]$Output = ''
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\Companion.Server\Companion.Server.csproj'
$destination = if ($Output) { [System.IO.Path]::GetFullPath($Output) } else { Join-Path $repo "artifacts\companion-$Runtime" }

if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
New-Item -ItemType Directory -Path $destination -Force | Out-Null

$publishArgs = @('publish', $project, '--configuration', 'Release', '--runtime', $Runtime, '--output', $destination, '--self-contained', ($SelfContained.IsPresent.ToString().ToLowerInvariant()))
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'start-companion.ps1') -Destination $destination -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'start-companion.cmd') -Destination $destination -Force
Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination (Join-Path $destination 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $repo 'README.zh-CN.md') -Destination (Join-Path $destination 'README.zh-CN.md') -Force
Write-Output "Published Companion package: $destination"
