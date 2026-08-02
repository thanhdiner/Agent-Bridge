Add-Type -AssemblyName System.Drawing

$width = 512
$height = 512
$bmp = New-Object System.Drawing.Bitmap($width, $height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

$g.Clear([System.Drawing.Color]::Transparent)

# Draw Orange Rounded Rectangle (Corner radius ~96px)
$orangeColor = [System.Drawing.Color]::FromArgb(255, 244, 124, 32)
$brush = New-Object System.Drawing.SolidBrush($orangeColor)

$rect = New-Object System.Drawing.RectangleF(16, 16, 480, 480)
$radius = 96.0

$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddArc($rect.X, $rect.Y, $radius, $radius, 180, 90)
$path.AddArc($rect.Right - $radius, $rect.Y, $radius, $radius, 270, 90)
$path.AddArc($rect.Right - $radius, $rect.Bottom - $radius, $radius, $radius, 0, 90)
$path.AddArc($rect.X, $rect.Bottom - $radius, $radius, $radius, 90, 90)
$path.CloseFigure()

$g.FillPath($brush, $path)

# Draw Crisp White Vector Icon in center: Shapes / Code Bridge
$whitePen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 28)
$whitePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$whitePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$whitePen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

# Draw left bracket <
$g.DrawLine($whitePen, 190, 180, 130, 256)
$g.DrawLine($whitePen, 130, 256, 190, 332)

# Draw right bracket >
$g.DrawLine($whitePen, 322, 180, 382, 256)
$g.DrawLine($whitePen, 382, 256, 322, 332)

# Center node / connector
$whiteBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
$g.FillEllipse($whiteBrush, 232, 232, 48, 48)

$g.Dispose()

$pngPath = "src\AgentBridge.Desktop\Assets\agentbridge-icon-512.png"
$bmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)

# Save as .ico
$hIcon = $bmp.GetHicon()
$icon = [System.Drawing.Icon]::FromHandle($hIcon)
$icoPath = "src\AgentBridge.Desktop\Assets\agentbridge.ico"
$fs = [System.IO.File]::Create($icoPath)
$icon.Save($fs)
$fs.Close()

$bmp.Dispose()
Write-Host "Generated new orange & white icon files successfully!"
