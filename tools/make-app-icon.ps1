# Reproduce the UI's Georgia Italic M and crimson dot with Windows drawing APIs.
# No downloaded package or generated artwork is needed.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$taskOutput = Join-Path (Split-Path -Parent $PSScriptRoot) 'assets'
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskMaster = New-Object System.Drawing.Bitmap 1024,1024
$taskGraphics = [Drawing.Graphics]::FromImage($taskMaster)
$taskGraphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
$taskGraphics.ScaleTransform(4,4)
$taskBackground = New-Object Drawing.Drawing2D.GraphicsPath
$taskBackground.AddArc(4,4,88,88,180,90)
$taskBackground.AddArc(164,4,88,88,270,90)
$taskBackground.AddArc(164,164,88,88,0,90)
$taskBackground.AddArc(4,164,88,88,90,90)
$taskBackground.CloseFigure()
$taskFill = New-Object Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml('#111014'))
$taskBorder = New-Object Drawing.Pen ([Drawing.ColorTranslator]::FromHtml('#49303b')),2
$taskGraphics.FillPath($taskFill,$taskBackground)
$taskGraphics.DrawPath($taskBorder,$taskBackground)
$taskGlyph = New-Object Drawing.Drawing2D.GraphicsPath
$taskFont = New-Object Drawing.FontFamily 'Georgia'
$taskGlyph.AddString('M',$taskFont,[int][Drawing.FontStyle]::Italic,200,[Drawing.PointF]::Empty,[Drawing.StringFormat]::GenericTypographic)
$taskBounds = $taskGlyph.GetBounds()
$taskTransform = New-Object Drawing.Drawing2D.Matrix
$taskTransform.Translate(-$taskBounds.X,-$taskBounds.Y)
$taskGlyph.Transform($taskTransform)
$taskTransform.Reset()
$taskTransform.Scale(176 / $taskBounds.Width,144 / $taskBounds.Height)
$taskGlyph.Transform($taskTransform)
$taskTransform.Reset()
$taskTransform.Translate(26,56)
$taskGlyph.Transform($taskTransform)
$taskWhite = New-Object Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml('#f2edef'))
$taskRed = New-Object Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml('#b83c4c'))
$taskGraphics.FillPath($taskWhite,$taskGlyph)
$taskGraphics.FillEllipse($taskRed,204,175,18,18)
$taskGraphics.Dispose()

$taskFrames = @()
foreach ($taskSize in @(16,20,24,32,40,48,64,128,256)) {
    $taskBitmap = New-Object Drawing.Bitmap $taskSize,$taskSize
    $taskCanvas = [Drawing.Graphics]::FromImage($taskBitmap)
    $taskCanvas.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $taskCanvas.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $taskCanvas.DrawImage($taskMaster,0,0,$taskSize,$taskSize)
    $taskCanvas.Dispose()
    $taskBuffer = New-Object IO.MemoryStream
    if ($taskSize -eq 256) {
        $taskBitmap.Save((Join-Path $taskOutput 'MiniumBrowser.png'),[Drawing.Imaging.ImageFormat]::Png)
        $taskBitmap.Save($taskBuffer,[Drawing.Imaging.ImageFormat]::Png)
    } else {
        # Small sizes use traditional 32-bit DIB frames for shell/.NET compatibility.
        $taskPixels = New-Object IO.BinaryWriter $taskBuffer
        $taskMaskStride = [int]([Math]::Ceiling($taskSize / 32.0) * 4)
        $taskPixels.Write([uint32]40)
        $taskPixels.Write([int32]$taskSize)
        $taskPixels.Write([int32]($taskSize * 2))
        $taskPixels.Write([uint16]1)
        $taskPixels.Write([uint16]32)
        $taskPixels.Write([uint32]0)
        $taskPixels.Write([uint32]($taskSize * $taskSize * 4 + $taskMaskStride * $taskSize))
        1..4 | ForEach-Object { $taskPixels.Write([uint32]0) }
        for ($taskY=$taskSize-1; $taskY -ge 0; $taskY--) {
            for ($taskX=0; $taskX -lt $taskSize; $taskX++) {
                $taskPixel=$taskBitmap.GetPixel($taskX,$taskY)
                $taskPixels.Write([byte]$taskPixel.B); $taskPixels.Write([byte]$taskPixel.G)
                $taskPixels.Write([byte]$taskPixel.R); $taskPixels.Write([byte]$taskPixel.A)
            }
        }
        for ($taskY=$taskSize-1; $taskY -ge 0; $taskY--) {
            $taskMask=New-Object byte[] $taskMaskStride
            for ($taskX=0; $taskX -lt $taskSize; $taskX++) {
                if ($taskBitmap.GetPixel($taskX,$taskY).A -eq 0) {
                    $taskIndex=[int][Math]::Floor($taskX/8.0)
                    $taskMask[$taskIndex]=$taskMask[$taskIndex] -bor (128 -shr ($taskX % 8))
                }
            }
            $taskPixels.Write($taskMask)
        }
        $taskPixels.Flush()
    }
    $taskFrames += [pscustomobject]@{Size=$taskSize;Bytes=$taskBuffer.ToArray()}
    $taskBuffer.Dispose(); $taskBitmap.Dispose()
}
$taskIco = [IO.File]::Create((Join-Path $taskOutput 'MiniumBrowser.ico'))
$taskWriter = New-Object IO.BinaryWriter $taskIco
$taskWriter.Write([uint16]0); $taskWriter.Write([uint16]1); $taskWriter.Write([uint16]$taskFrames.Count)
$taskOffset = 6 + 16 * $taskFrames.Count
foreach ($taskFrame in $taskFrames) {
    $taskDimension = if ($taskFrame.Size -eq 256) {0} else {$taskFrame.Size}
    $taskWriter.Write([byte]$taskDimension); $taskWriter.Write([byte]$taskDimension)
    $taskWriter.Write([byte]0); $taskWriter.Write([byte]0)
    $taskWriter.Write([uint16]1); $taskWriter.Write([uint16]32)
    $taskWriter.Write([uint32]$taskFrame.Bytes.Length); $taskWriter.Write([uint32]$taskOffset)
    $taskOffset += $taskFrame.Bytes.Length
}
foreach ($taskFrame in $taskFrames) { $taskWriter.Write([byte[]]$taskFrame.Bytes) }
$taskWriter.Dispose(); $taskIco.Dispose()
foreach ($taskObject in @($taskMaster,$taskBackground,$taskFill,$taskBorder,$taskGlyph,$taskFont,$taskTransform,$taskWhite,$taskRed)) { $taskObject.Dispose() }
Write-Output 'Created assets/MiniumBrowser.ico (16–256 px) and PNG preview.'
