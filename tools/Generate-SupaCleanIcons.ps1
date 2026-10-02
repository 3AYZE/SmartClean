param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "..\src\SmartClean.WinUI\Assets")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

function New-RoundedRectanglePath {
    param(
        [System.Drawing.RectangleF]$Rect,
        [single]$Radius
    )
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = [single]($Radius * 2)
    $path.AddArc($Rect.X, $Rect.Y, $d, $d, 180, 90)
    $path.AddArc($Rect.Right - $d, $Rect.Y, $d, $d, 270, 90)
    $path.AddArc($Rect.Right - $d, $Rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($Rect.X, $Rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-SupaCleanFrame {
    param(
        [int]$Size,
        [bool]$IncludeSparkle = $true
    )

    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

        $margin = [single]([Math]::Max(1, $Size * 0.055))
        $tile = [System.Drawing.RectangleF]::new($margin, $margin, $Size - 2*$margin, $Size - 2*$margin)
        $radius = [single]($Size * 0.205)
        $path = New-RoundedRectanglePath -Rect $tile -Radius $radius
        try {
            $start = [System.Drawing.PointF]::new(0, $Size)
            $finish = [System.Drawing.PointF]::new($Size, 0)
            $blue = [System.Drawing.Color]::FromArgb(255, 7, 91, 255)
            $cyan = [System.Drawing.Color]::FromArgb(255, 50, 221, 235)
            $gradient = [System.Drawing.Drawing2D.LinearGradientBrush]::new($start, $finish, $blue, $cyan)
            try {
                $graphics.FillPath($gradient, $path)
            }
            finally { $gradient.Dispose() }
        }
        finally { $path.Dispose() }

        # Keep the mark intentionally simple and thick so it survives 16px tray rendering.
        $fontName = if (([System.Drawing.FontFamily]::Families.Name -contains "Segoe UI")) { "Segoe UI" } else { "Arial" }
        $font = [System.Drawing.Font]::new($fontName, [single]($Size * 0.63),
            [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $brush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
        $format = [System.Drawing.StringFormat]::new()
        try {
            $format.Alignment = [System.Drawing.StringAlignment]::Center
            $format.LineAlignment = [System.Drawing.StringAlignment]::Center
            $textRect = [System.Drawing.RectangleF]::new(0, [single]($Size * 0.035), $Size, [single]($Size * 0.91))
            $graphics.DrawString("S", $font, $brush, $textRect, $format)
        }
        finally {
            $format.Dispose()
            $brush.Dispose()
            $font.Dispose()
        }

        if ($IncludeSparkle -and $Size -ge 24) {
            $cx = [single]($Size * 0.765)
            $cy = [single]($Size * 0.255)
            $outer = [single]([Math]::Max(2, $Size * 0.070))
            $inner = [single]([Math]::Max(1, $Size * 0.022))
            $points = [System.Drawing.PointF[]]@(
                [System.Drawing.PointF]::new($cx, $cy - $outer),
                [System.Drawing.PointF]::new($cx + $inner, $cy - $inner),
                [System.Drawing.PointF]::new($cx + $outer, $cy),
                [System.Drawing.PointF]::new($cx + $inner, $cy + $inner),
                [System.Drawing.PointF]::new($cx, $cy + $outer),
                [System.Drawing.PointF]::new($cx - $inner, $cy + $inner),
                [System.Drawing.PointF]::new($cx - $outer, $cy),
                [System.Drawing.PointF]::new($cx - $inner, $cy - $inner)
            )
            $sparkle = [System.Drawing.Drawing2D.GraphicsPath]::new()
            $sparkleBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
            try {
                $sparkle.AddPolygon($points)
                $graphics.FillPath($sparkleBrush, $sparkle)
            }
            finally {
                $sparkleBrush.Dispose()
                $sparkle.Dispose()
            }
        }

        $stream = [System.IO.MemoryStream]::new()
        try {
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            return [PSCustomObject]@{ Size = $Size; Data = $stream.ToArray() }
        }
        finally { $stream.Dispose() }
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Write-Ico {
    param(
        [string]$Path,
        [object[]]$Frames
    )
    $stream = [System.IO.FileStream]::new($Path, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
    $writer = [System.IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$Frames.Count)
        $offset = 6 + 16 * $Frames.Count
        foreach ($frame in $Frames) {
            $dimension = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$frame.Data.Length)
            $writer.Write([uint32]$offset)
            $offset += $frame.Data.Length
        }
        foreach ($frame in $Frames) { $writer.Write([byte[]]$frame.Data) }
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

$appSizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$appFrames = @($appSizes | ForEach-Object { New-SupaCleanFrame -Size $_ -IncludeSparkle:($_ -ge 24) })
$traySizes = @(16, 20, 24, 32, 48)
$trayFrames = @($traySizes | ForEach-Object { New-SupaCleanFrame -Size $_ -IncludeSparkle:($_ -ge 24) })

$appIcon = Join-Path $OutputDirectory "SupaClean.ico"
$trayIcon = Join-Path $OutputDirectory "SupaClean-Tray.ico"
Write-Ico -Path $appIcon -Frames $appFrames
Write-Ico -Path $trayIcon -Frames $trayFrames

# Also keep a 256px transparent PNG for future settings/about surfaces.
$preview = New-SupaCleanFrame -Size 256 -IncludeSparkle $true
[System.IO.File]::WriteAllBytes((Join-Path $OutputDirectory "SupaClean-Icon.png"), $preview.Data)

foreach ($file in @($appIcon, $trayIcon)) {
    if (!(Test-Path $file) -or (Get-Item $file).Length -lt 1024) {
        throw "SupaClean icon generation failed: $file"
    }
    $icon = [System.Drawing.Icon]::new($file)
    try {
        if ($icon.Width -lt 16 -or $icon.Height -lt 16) { throw "Invalid icon: $file" }
    }
    finally { $icon.Dispose() }
}

Write-Host "Generated SupaClean desktop/taskbar icon: $appIcon"
Write-Host "Generated SupaClean tray/background icon: $trayIcon"
