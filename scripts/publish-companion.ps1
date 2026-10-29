param(
    [string]$Runtime = 'win-x64',
    [switch]$SelfContained,
    [switch]$LegacyUi,
    [string]$Output = ''
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$serverProject = Join-Path $repo 'src\Companion.Server\Companion.Server.csproj'
$desktopProject = if ($LegacyUi) {
    Join-Path $repo 'src\Companion.Desktop\Companion.Desktop.csproj'
} else {
    Join-Path $repo 'src\Companion.Desktop.Wpf\Companion.Desktop.Wpf.csproj'
}
$destination = if ($Output) { [System.IO.Path]::GetFullPath($Output) } else { Join-Path $repo "artifacts\companion-$Runtime" }

if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
New-Item -ItemType Directory -Path $destination -Force | Out-Null

$serverDestination = Join-Path $destination 'server'
New-Item -ItemType Directory -Path $serverDestination -Force | Out-Null
$publishArgs = @('publish', $serverProject, '--configuration', 'Release', '--runtime', $Runtime, '--output', $serverDestination, '--self-contained', ($SelfContained.IsPresent.ToString().ToLowerInvariant()))
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$desktopArgs = @('publish', $desktopProject, '--configuration', 'Release', '--runtime', $Runtime, '--output', $destination, '--self-contained', ($SelfContained.IsPresent.ToString().ToLowerInvariant()))
& dotnet @desktopArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'start-companion.ps1') -Destination $destination -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'start-companion.cmd') -Destination $destination -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install-companion.ps1') -Destination $destination -Force
Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination (Join-Path $destination 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $repo 'README.zh-CN.md') -Destination (Join-Path $destination 'README.zh-CN.md') -Force
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $destination 'LICENSE') -Force
Write-Output "Published Companion package: $destination"
