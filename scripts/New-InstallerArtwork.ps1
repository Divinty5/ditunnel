# Rebuild the installer's decorative background. No external fonts or downloads.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$outputDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'installer\Assets'
[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$bitmap = [Drawing.Bitmap]::new(1491, 1080)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
try {
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([Drawing.ColorTranslator]::FromHtml('#100E18'))
    # Keep the text area quiet; concentrate the purple glow behind the brand icon.
    foreach ($glow in @(@(1080, -270, 900, 650, 79), @(830, 530, 1100, 780, 26))) {
        $path = [Drawing.Drawing2D.GraphicsPath]::new()
        $path.AddEllipse($glow[0], $glow[1], $glow[2], $glow[3])
        $brush = [Drawing.Drawing2D.PathGradientBrush]::new($path)
        $brush.CenterColor = [Drawing.Color]::FromArgb($glow[4], 142, 83, 225)
        $brush.SurroundColors = @([Drawing.Color]::FromArgb(0, 142, 83, 225))
        $graphics.FillPath($brush, $path)
        $brush.Dispose()
        $path.Dispose()
    }
    # Thin, restrained arcs echo the tunnel in the application icon.
    for ($index = 0; $index -lt 5; $index++) {
        $pen = [Drawing.Pen]::new([Drawing.Color]::FromArgb(24, 169, 126, 230), 2)
        $offset = $index * 65
        $graphics.DrawBezier($pen, 1030 + $offset, 150, 850 + $offset, 530, 1300 + $offset, 630, 1490, 830 + $offset)
        $pen.Dispose()
    }
    $line = [Drawing.Pen]::new([Drawing.Color]::FromArgb(82, 141, 98, 207), 2)
    $graphics.DrawLine($line, 60, 166, 1431, 166)
    $line.Dispose()
    $bitmap.Save((Join-Path $outputDirectory 'wizard-background.png'), [Drawing.Imaging.ImageFormat]::Png)
} finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}
