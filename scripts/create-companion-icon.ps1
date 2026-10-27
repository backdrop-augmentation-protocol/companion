param(
    [string] $Source = (Join-Path $PSScriptRoot '..\src\Companion.Desktop.Wpf\Assets\bap-logo.png'),
    [string] $Output = (Join-Path $PSScriptRoot '..\src\Companion.Desktop.Wpf\Assets\bap-companion.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sourceImage = [System.Drawing.Image]::FromFile([System.IO.Path]::GetFullPath($Source))
$frames = [System.Collections.Generic.List[object]]::new()
try {
    foreach ($size in 16, 24, 32, 48, 64, 128, 256) {
        $bitmap = [System.Drawing.Bitmap]::new($size, $size)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                $graphics.DrawImage($sourceImage, 0, 0, $size, $size)
            } finally { $graphics.Dispose() }
            $stream = [System.IO.MemoryStream]::new()
            try {
                $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
                $frames.Add([pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() })
            } finally { $stream.Dispose() }
        } finally { $bitmap.Dispose() }
    }
} finally { $sourceImage.Dispose() }

$destination = [System.IO.Path]::GetFullPath($Output)
$file = [System.IO.File]::Create($destination)
try {
    $writer = [System.IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$frames.Count)
        $offset = 6 + 16 * $frames.Count
        foreach ($frame in $frames) {
            $writer.Write([byte]($frame.Size -band 255))
            $writer.Write([byte]($frame.Size -band 255))
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$frame.Bytes.Length)
            $writer.Write([uint32]$offset)
            $offset += $frame.Bytes.Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
    } finally { $writer.Dispose() }
} finally { $file.Dispose() }

Write-Output "Created $destination with $($frames.Count) sizes"
