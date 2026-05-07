Add-Type -AssemblyName System.Drawing
$input = "Resources\app_icon.png"
$output = "Resources\app_icon.ico"

if (Test-Path $input) {
    $img = [System.Drawing.Image]::FromFile((Resolve-Path $input).Path)
    
    # Resize to 256x256
    $newBmp = New-Object System.Drawing.Bitmap(256, 256)
    $g = [System.Drawing.Graphics]::FromImage($newBmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.DrawImage($img, 0, 0, 256, 256)
    $g.Dispose()
    
    $ms = New-Object System.IO.MemoryStream
    $newBmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngBytes = $ms.ToArray()
    $ms.Dispose()
    $newBmp.Dispose()
    $img.Dispose()

    $fs = [System.IO.File]::Create((Resolve-Path "Resources").Path + "\app_icon.ico")
    $bw = New-Object System.IO.BinaryWriter($fs)
    
    # ICO Header
    $bw.Write([int16]0)
    $bw.Write([int16]1)
    $bw.Write([int16]1)
    
    # Icon Dir Entry
    $bw.Write([byte]0) # 256px
    $bw.Write([byte]0) # 256px
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([int16]1)
    $bw.Write([int16]32)
    $bw.Write([int32]$pngBytes.Length)
    $bw.Write([int32]22)
    
    $bw.Write($pngBytes)
    
    $bw.Dispose()
    $fs.Dispose()
    Write-Host "ICO resized to 256x256 and created successfully!"
}
