$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot
$sourcePath = Join-Path $root 'Docs\SplashScreen\SplashScreen.png'
$source = [Drawing.Image]::FromFile($sourcePath)
try {
    if ($source.Width -ne 1920 -or $source.Height -ne 1080) { throw 'Expected a 1920x1080 startup source.' }
    Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $root 'LaunchPass\Assets\Branding\StartupSplash.png')
    foreach ($scale in @(100,125,150,200,400)) {
        $width = [int](620 * $scale / 100)
        $height = [int](300 * $scale / 100)
        $bitmap = [Drawing.Bitmap]::new($width, $height)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([Drawing.Color]::Black)
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $ratio = [Math]::Min($width / $source.Width, $height / $source.Height)
            $fitWidth = [int][Math]::Round($source.Width * $ratio)
            $fitHeight = [int][Math]::Round($source.Height * $ratio)
            $rectangle = [Drawing.Rectangle]::new([int](($width-$fitWidth)/2), [int](($height-$fitHeight)/2), $fitWidth, $fitHeight)
            $graphics.DrawImage($source, $rectangle)
            $bitmap.Save((Join-Path $root "LaunchPass\Assets\SplashScreen.scale-$scale.png"), [Drawing.Imaging.ImageFormat]::Png)
            Write-Output "Native splash scale-$scale : ${width}x${height}; aspect ratio preserved on black"
        } finally { $graphics.Dispose(); $bitmap.Dispose() }
    }
} finally { $source.Dispose() }
