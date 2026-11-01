Add-Type -AssemblyName System.Drawing

function New-RoundedPath([double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    if ($r -le 0) {
        $p.AddRectangle((New-Object System.Drawing.RectangleF([float]$x, [float]$y, [float]$w, [float]$h)))
        return $p
    }
    $d = $r * 2
    $p.AddArc([float]$x, [float]$y, [float]$d, [float]$d, 180, 90)
    $p.AddArc([float]($x + $w - $d), [float]$y, [float]$d, [float]$d, 270, 90)
    $p.AddArc([float]($x + $w - $d), [float]($y + $h - $d), [float]$d, [float]$d, 0, 90)
    $p.AddArc([float]$x, [float]($y + $h - $d), [float]$d, [float]$d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconImage([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap([int]$s, [int]$s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $u = [double]$s / 32.0

    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 28, 34, 44))
    $path = New-RoundedPath (0.5 * $u) (0.5 * $u) (31 * $u) (31 * $u) (7 * $u)
    $g.FillPath($bg, $path)
    $path.Dispose()

    $t = [Math]::Max(1.0, 3.0 * $u)
    $near = 6.0 * $u
    $far = 26.0 * $u
    $len = 20.0 * $u

    $accent = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 76, 141, 255))
    $g.FillRectangle($accent, [float]$near, [float]$near, [float]$len, [float]$t)
    $g.FillRectangle($accent, [float]$near, [float]($far - $t), [float]$len, [float]$t)
    $g.FillRectangle($accent, [float]$near, [float]$near, [float]$t, [float]$len)
    $g.FillRectangle($accent, [float]($far - $t), [float]$near, [float]$t, [float]$len)

    $core = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 230, 238, 248))
    $g.FillRectangle($core, [float](12 * $u), [float](12 * $u), [float](8 * $u), [float](8 * $u))

    $g.Dispose()
    $bg.Dispose()
    $accent.Dispose()
    $core.Dispose()
    return $bmp
}

function Get-IconDib([int]$s) {
    $bmp = New-IconImage $s
    $rect = New-Object System.Drawing.Rectangle(0, 0, $s, $s)
    $lock = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $lock.Stride
    $pixels = New-Object byte[] ($stride * $s)
    [System.Runtime.InteropServices.Marshal]::Copy($lock.Scan0, $pixels, 0, $pixels.Length)
    $bmp.UnlockBits($lock)
    $bmp.Dispose()

    $header = New-Object byte[] 40
    [Array]::Copy([BitConverter]::GetBytes([int]40), 0, $header, 0, 4)
    [Array]::Copy([BitConverter]::GetBytes([int]$s), 0, $header, 4, 4)
    [Array]::Copy([BitConverter]::GetBytes([int]($s * 2)), 0, $header, 8, 4)
    [Array]::Copy([BitConverter]::GetBytes([int16]1), 0, $header, 12, 2)
    [Array]::Copy([BitConverter]::GetBytes([int16]32), 0, $header, 14, 2)
    [Array]::Copy([BitConverter]::GetBytes([int]($s * $s * 4)), 0, $header, 20, 4)

    $xor = New-Object byte[] ($s * $s * 4)
    for ($y = 0; $y -lt $s; $y++) {
        $src = ($s - 1 - $y) * $stride
        [Array]::Copy($pixels, $src, $xor, $y * $s * 4, $s * 4)
    }

    $maskRow = [int]([Math]::Floor(($s + 31) / 32) * 4)
    $mask = New-Object byte[] ($maskRow * $s)

    $ms = New-Object System.IO.MemoryStream
    $ms.Write($header, 0, $header.Length)
    $ms.Write($xor, 0, $xor.Length)
    $ms.Write($mask, 0, $mask.Length)
    $result = $ms.ToArray()
    $ms.Dispose()
    return $result
}

$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
$entries = New-Object System.Collections.ArrayList

foreach ($s in $sizes) {
    $dib = Get-IconDib $s
    [void]$entries.Add([PSCustomObject]@{ Size = [int]$s; Data = $dib })
}

$out = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\app.ico'))
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter($fs)

$count = $entries.Count
$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$count)

$offset = 6 + 16 * $count
foreach ($e in $entries) {
    $dim = $e.Size
    if ($dim -ge 256) {
        $bw.Write([Byte]0)
        $bw.Write([Byte]0)
    }
    else {
        $bw.Write([Byte]$dim)
        $bw.Write([Byte]$dim)
    }
    $bw.Write([Byte]0)
    $bw.Write([Byte]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]$e.Data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $e.Data.Length
}

foreach ($e in $entries) {
    $bw.Write([byte[]]$e.Data)
}

$bw.Flush()
$bw.Close()
$fs.Close()

Write-Output ("wrote " + $out + " " + (Get-Item $out).Length + " bytes, " + $count + " sizes")
