#Requires -Version 7.0
<#
  生成应用图标 src/ShunSeek.App/Assets/app.ico

  方案 A：深石墨色圆角方块（超椭圆，不是正圆弧）+ 白色几何放大镜。
  纯色平涂，没有渐变、没有描边光晕，小尺寸也认得出。
  多尺寸（256/96/64/48/32/16）各自绘制后打进一个 ICO；小尺寸把线加粗一点，避免缩下去就散。
#>
param([string]$OutputPath = (Join-Path $PSScriptRoot '..\src\ShunSeek.App\Assets\app.ico'))

Add-Type -AssemblyName System.Drawing

$sizes = 256, 96, 64, 48, 32, 16
$pngs = @()

$ink = [System.Drawing.Color]::FromArgb(255, 26, 28, 32)     # 底：#1A1C20
$white = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)

foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    # 超椭圆底（n=3.2，比方圆更饱满，比正方更柔和）
    $cx = $size / 2.0
    $cy = $size / 2.0
    $a = $size * 0.5
    $n = 3.2
    $p = 2.0 / $n
    $pts = New-Object 'System.Collections.Generic.List[System.Drawing.PointF]'
    $segments = if ($size -ge 32) { 128 } else { 64 }
    for ($i = 0; $i -lt $segments; $i++) {
        $t = 2 * [Math]::PI * $i / $segments
        $cos = [Math]::Cos($t)
        $sin = [Math]::Sin($t)
        $px = $cx + $a * [Math]::Sign($cos) * [Math]::Pow([Math]::Abs($cos), $p)
        $py = $cy + $a * [Math]::Sign($sin) * [Math]::Pow([Math]::Abs($sin), $p)
        $pts.Add([System.Drawing.PointF]::new([single]$px, [single]$py))
    }
    $pts.Add($pts[0])

    $fill = New-Object System.Drawing.SolidBrush $ink
    $g.FillPolygon($fill, [System.Drawing.PointF[]]$pts.ToArray())

    # 放大镜：镜片 + 45° 手柄
    $lensR = $size * 0.200
    $lcx = $size * 0.440
    $lcy = $size * 0.440
    # 小尺寸线要相对粗一点，否则 16px 缩下去只剩灰点
    $strokeRatio = if ($size -le 32) { 0.095 } else { 0.072 }
    $stroke = [Math]::Max(1.4, $size * $strokeRatio)

    $pen = New-Object System.Drawing.Pen $white, ([single]$stroke)
    $pen.StartCap = 'Round'
    $pen.EndCap = 'Round'
    $pen.LineJoin = 'Round'

    $g.DrawEllipse($pen, [single]($lcx - $lensR), [single]($lcy - $lensR), [single](2 * $lensR), [single](2 * $lensR))

    $from = $lensR * 0.72
    $to = $lensR * 1.58
    $g.DrawLine($pen,
        [single]($lcx + $from * 0.707), [single]($lcy + $from * 0.707),
        [single]($lcx + $to * 0.707), [single]($lcy + $to * 0.707))

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , @{ Size = $size; Bytes = $ms.ToArray() }

    $ms.Dispose(); $pen.Dispose(); $fill.Dispose(); $g.Dispose(); $bmp.Dispose()
}

# 组装 ICO（PNG 负载）
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$pngs.Count)

$offset = 6 + 16 * $pngs.Count
foreach ($png in $pngs) {
    $dim = if ($png.Size -ge 256) { 0 } else { $png.Size }
    $bw.Write([byte]$dim)
    $bw.Write([byte]$dim)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]$png.Bytes.Length)
    $bw.Write([UInt32]$offset)
    $offset += $png.Bytes.Length
}
foreach ($png in $pngs) { $bw.Write($png.Bytes) }

$dir = Split-Path $OutputPath -Parent
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
[System.IO.File]::WriteAllBytes($OutputPath, $out.ToArray())
$bw.Dispose(); $out.Dispose()
Write-Output "图标已生成：$((Get-Item $OutputPath).FullName)（$($sizes -join '/')）"
