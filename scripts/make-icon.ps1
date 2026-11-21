#Requires -Version 7.0
<#
  生成应用图标 src/ShunSeek.App/Assets/app.ico

  风格：手绘线稿。透明底，只有墨线——一圈手抖的方圆轮廓 + 一枚放大镜，
  线条画两遍（第二遍偏一点、更淡），像铅笔勾的稿子。不填色、不做渐变、不做投影。
  多尺寸（256/96/64/48/32/16）各自绘制后打进一个 ICO；小尺寸减少抖动、只描一遍，避免糊成马赛克。
#>
param([string]$OutputPath = (Join-Path $PSScriptRoot '..\src\ShunSeek.App\Assets\app.ico'))

Add-Type -AssemblyName System.Drawing

$inkColor = [System.Drawing.Color]::FromArgb(255, 32, 32, 34)

function New-WobbleCircle {
    param([double]$cx, [double]$cy, [double]$r, [double]$amp, [int]$segments, [double]$phase)
    $pts = New-Object 'System.Collections.Generic.List[System.Drawing.PointF]'
    for ($i = 0; $i -lt $segments; $i++) {
        $a = 2 * [Math]::PI * $i / $segments
        $w = 1 + $amp * (0.6 * [Math]::Sin(3 * $a + $phase) + 0.4 * [Math]::Sin(5 * $a + $phase * 2.7))
        $pts.Add([System.Drawing.PointF]::new([single]($cx + $r * $w * [Math]::Cos($a)),
                                             [single]($cy + $r * $w * [Math]::Sin($a))))
    }
    $pts.Add($pts[0])
    return , ([System.Drawing.PointF[]]$pts.ToArray())
}

# 手绘方圆轮廓：超椭圆采样 + 抖动（不是正圆弧，也不是正圆）
function New-WobbleSquircle {
    param([double]$cx, [double]$cy, [double]$a, [double]$n, [double]$amp, [int]$segments, [double]$phase)
    $pts = New-Object 'System.Collections.Generic.List[System.Drawing.PointF]'
    $p = 2.0 / $n
    for ($i = 0; $i -lt $segments; $i++) {
        $t = 2 * [Math]::PI * $i / $segments
        $cos = [Math]::Cos($t)
        $sin = [Math]::Sin($t)
        $w = 1 + $amp * (0.6 * [Math]::Sin(3 * $t + $phase) + 0.4 * [Math]::Sin(5 * $t + $phase * 2.1))
        $x = $cx + $a * $w * [Math]::Sign($cos) * [Math]::Pow([Math]::Abs($cos), $p)
        $y = $cy + $a * $w * [Math]::Sign($sin) * [Math]::Pow([Math]::Abs($sin), $p)
        $pts.Add([System.Drawing.PointF]::new([single]$x, [single]$y))
    }
    $pts.Add($pts[0])
    return , ([System.Drawing.PointF[]]$pts.ToArray())
}

$sizes = 256, 96, 64, 48, 32, 16
$pngs = @()

foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    $sketch = $size -ge 48            # 大尺寸画两遍，小的只描一遍，16px 才不糊
    $jitter = if ($size -ge 64) { 0.028 } elseif ($size -ge 32) { 0.012 } else { 0.0 }
    $segments = if ($size -ge 96) { 96 } elseif ($size -ge 32) { 48 } else { 28 }

    # ① 外轮廓：手抖的方圆
    $outlineWidth = [Math]::Max(1.2, $size * 0.030)
    $outlinePen = New-Object System.Drawing.Pen $inkColor, ([single]$outlineWidth)
    $outlinePen.LineJoin = 'Round'
    $outline = New-WobbleSquircle -cx ($size / 2) -cy ($size / 2) -a ($size * 0.455) -n 3.1 -amp $jitter -segments $segments -phase 1.9
    $g.DrawClosedCurve($outlinePen, $outline, 0.4, 'Winding')

    # ② 放大镜：镜片
    $lensR = $size * 0.185
    $cx = $size * 0.445
    $cy = $size * 0.435
    $stroke = [Math]::Max(1.5, $size * 0.058)
    $pen = New-Object System.Drawing.Pen $inkColor, ([single]$stroke)
    $pen.StartCap = 'Round'
    $pen.EndCap = 'Round'
    $pen.LineJoin = 'Round'

    $lens = New-WobbleCircle -cx $cx -cy $cy -r $lensR -amp ($jitter * 1.6) -segments $segments -phase 0.7
    $g.DrawClosedCurve($pen, $lens, 0.42, 'Winding')

    # ③ 手柄
    $hx0 = $cx + $lensR * 0.70
    $hy0 = $cy + $lensR * 0.70
    $hx2 = $hx0 + $size * 0.155
    $hy2 = $hy0 + $size * 0.155
    $g.DrawCurve($pen, [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new([single]$hx0, [single]$hy0),
        [System.Drawing.PointF]::new([single](($hx0 + $hx2) / 2 + $size * 0.010), [single](($hy0 + $hy2) / 2)),
        [System.Drawing.PointF]::new([single]$hx2, [single]$hy2)), 0.3)

    # ④ 第二遍淡描（只在大尺寸上）
    if ($sketch) {
        $ghostPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(80, 32, 32, 34)), ([single]($stroke * 0.65))
        $ghostPen.StartCap = 'Round'
        $ghostPen.EndCap = 'Round'
        $ghostPen.LineJoin = 'Round'
        $ghostLens = New-WobbleCircle -cx ($cx + $size * 0.008) -cy ($cy - $size * 0.006) -r ($lensR * 1.03) -amp ($jitter * 2.0) -segments $segments -phase 2.6
        $g.DrawClosedCurve($ghostPen, $ghostLens, 0.42, 'Winding')

        $ghostOutlinePen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, 32, 32, 34)), ([single]($outlineWidth * 0.8))
        $ghostOutlinePen.LineJoin = 'Round'
        $ghostOutline = New-WobbleSquircle -cx ($size / 2 + $size * 0.004) -cy ($size / 2 - $size * 0.003) -a ($size * 0.462) -n 3.1 -amp ($jitter * 1.4) -segments $segments -phase 3.4
        $g.DrawClosedCurve($ghostOutlinePen, $ghostOutline, 0.4, 'Winding')

        $ghostPen.Dispose(); $ghostOutlinePen.Dispose()
    }

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , @{ Size = $size; Bytes = $ms.ToArray() }

    $ms.Dispose(); $pen.Dispose(); $outlinePen.Dispose(); $g.Dispose(); $bmp.Dispose()
}

# 组装 ICO（PNG 负载，Windows Vista+ 支持）
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
