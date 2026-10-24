param(
    [string]$PackageRoot = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'
$source = [System.IO.Path]::GetFullPath($PackageRoot)
$installRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\BAP Companion'
$installParent = Split-Path -Parent $installRoot
$startMenu = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Microsoft\Windows\Start Menu\Programs'
$shortcutPath = Join-Path $startMenu 'BAP Companion.lnk'
$desktopName = 'Bap.Companion.Desktop.exe'
$serverName = 'Bap.Companion.Server.exe'

if ($source.TrimEnd('\') -eq $installRoot.TrimEnd('\')) {
    throw 'The package source must be separate from the installed application.'
}
foreach ($required in @($desktopName, (Join-Path 'server' $serverName))) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $required) -PathType Leaf)) {
        throw "Incomplete Companion package: $required is missing."
    }
}

$installPrefix = $installRoot.TrimEnd('\') + '\'
$runningFromInstall = @(Get-CimInstance Win32_Process | Where-Object {
    $_.Name -in @($desktopName, $serverName) -and
    $_.ExecutablePath -and
    $_.ExecutablePath.StartsWith($installPrefix, [System.StringComparison]::OrdinalIgnoreCase)
})
if ($runningFromInstall.Count -gt 0) {
    throw 'Exit the installed BAP Companion from its tray menu before updating it.'
}

New-Item -ItemType Directory -Path $installParent -Force | Out-Null
$suffix = [Guid]::NewGuid().ToString('N')
$staging = Join-Path $installParent "BAP Companion.staging-$suffix"
$backup = Join-Path $installParent "BAP Companion.backup-$suffix"
Copy-Item -LiteralPath $source -Destination $staging -Recurse -Force

$existingData = Join-Path $installRoot 'server\data'
if (Test-Path -LiteralPath $existingData -PathType Container) {
    Copy-Item -LiteralPath $existingData -Destination (Join-Path $staging 'server\data') -Recurse -Force
}

$hadPreviousInstall = Test-Path -LiteralPath $installRoot
if ($hadPreviousInstall) { Move-Item -LiteralPath $installRoot -Destination $backup }
try {
    Move-Item -LiteralPath $staging -Destination $installRoot
    New-Item -ItemType Directory -Path $startMenu -Force | Out-Null
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcutPath)
    $shortcut.TargetPath = Join-Path $installRoot $desktopName
    $shortcut.WorkingDirectory = $installRoot
    $shortcut.Arguments = ''
    $shortcut.Description = 'BAP Companion'
    $shortcut.Save()
}
catch {
    if (Test-Path -LiteralPath $backup) {
        if (Test-Path -LiteralPath $installRoot) {
            Move-Item -LiteralPath $installRoot -Destination (Join-Path $installParent "BAP Companion.failed-$suffix")
        }
        Move-Item -LiteralPath $backup -Destination $installRoot
    }
    throw
}

if ($hadPreviousInstall) {
    Remove-Item -LiteralPath $backup -Recurse -Force
}
Write-Output "Installed BAP Companion: $installRoot"
Write-Output "Start menu shortcut: $shortcutPath"
