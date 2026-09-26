param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Drawing

function Read-BrandColor {
    param(
        [Parameter(Mandatory=$true)][string]$Source,
        [Parameter(Mandatory=$true)][string]$Name
    )

    $match = [regex]::Match($Source, "public static readonly SrgbColor $Name = SrgbColor\.Parse\(""(?<hex>#[0-9A-Fa-f]{6})""\)")
    if (-not $match.Success) {
        throw "Could not find ScribeBrand.$Name."
    }

    return $match.Groups['hex'].Value
}

function Convert-HexColor {
    param([Parameter(Mandatory=$true)][string]$Hex)

    return [System.Drawing.Color]::FromArgb(
        [Convert]::ToInt32($Hex.Substring(1, 2), 16),
        [Convert]::ToInt32($Hex.Substring(3, 2), 16),
        [Convert]::ToInt32($Hex.Substring(5, 2), 16))
}

function Get-SignalShare {
    param(
        [System.Drawing.Color]$Color,
        [System.Drawing.Color]$Paper,
        [System.Drawing.Color]$Signal
    )

    $shares = @()
    foreach ($channel in @('R', 'G')) {
        $paperValue = [double]$Paper.$channel
        $signalValue = [double]$Signal.$channel
        $denominator = $paperValue - $signalValue
        if ([Math]::Abs($denominator) -gt 0.001) {
            $shares += [Math]::Clamp(($paperValue - [double]$Color.$channel) / $denominator, 0.0, 1.0)
        }
    }

    if ($shares.Count -eq 0) {
        return 0.0
    }

    return ($shares | Measure-Object -Average).Average
}

function Remove-SignalShare {
    param(
        [System.Drawing.Color]$Color,
        [System.Drawing.Color]$Paper,
        [System.Drawing.Color]$Signal
    )

    $share = Get-SignalShare -Color $Color -Paper $Paper -Signal $Signal
    if ($share -le 0.001) {
        return $Color
    }

    if ($share -ge 0.985) {
        return [System.Drawing.Color]::FromArgb($Color.A, $Paper.R, $Paper.G, $Paper.B)
    }

    $remaining = 1.0 - $share
    $r = [Math]::Clamp([int][Math]::Round(([double]$Color.R - ($share * [double]$Signal.R)) / $remaining), 0, 255)
    $g = [Math]::Clamp([int][Math]::Round(([double]$Color.G - ($share * [double]$Signal.G)) / $remaining), 0, 255)
    $b = [Math]::Clamp([int][Math]::Round(([double]$Color.B - ($share * [double]$Signal.B)) / $remaining), 0, 255)
    return [System.Drawing.Color]::FromArgb($Color.A, $r, $g, $b)
}

$brandPath = Join-Path $Root 'src\Scribe.Core\Appearance\ScribeBrand.cs'
$brandSource = Get-Content -LiteralPath $brandPath -Raw
$paper = Convert-HexColor (Read-BrandColor -Source $brandSource -Name 'Paper')
$signal = Convert-HexColor (Read-BrandColor -Source $brandSource -Name 'Signal')

$sourcePath = Join-Path $Root 'docs\icon.png'
$outputPath = Join-Path $Root 'src\Scribe.App\Assets\scribe-mark-nobars.png'
$image = [System.Drawing.Bitmap]::new($sourcePath)
try {
    if ($image.Width -ne 512 -or $image.Height -ne 512) {
        throw "Expected docs\icon.png to be 512 px, got $($image.Width) x $($image.Height)."
    }

    $bluePixels = @{}
    for ($y = 80; $y -lt 270; $y++) {
        for ($x = 150; $x -lt 360; $x++) {
            $color = $image.GetPixel($x, $y)
            if ($color.A -gt 0 -and $color.B -gt 180 -and ($color.B - $color.R) -gt 90 -and ($color.B - $color.G) -gt -30) {
                $bluePixels["$x,$y"] = $true
            }
        }
    }

    $visited = @{}
    $components = New-Object System.Collections.Generic.List[object]
    foreach ($key in @($bluePixels.Keys)) {
        if ($visited.ContainsKey($key)) {
            continue
        }

        $queue = [System.Collections.Queue]::new()
        $queue.Enqueue($key)
        $visited[$key] = $true
        $xs = New-Object System.Collections.Generic.List[int]
        $ys = New-Object System.Collections.Generic.List[int]

        while ($queue.Count -gt 0) {
            $item = [string]$queue.Dequeue()
            $parts = $item.Split(',')
            $x = [int]$parts[0]
            $y = [int]$parts[1]
            $xs.Add($x)
            $ys.Add($y)

            foreach ($delta in @(@(1,0), @(-1,0), @(0,1), @(0,-1))) {
                $nextX = $x + $delta[0]
                $nextY = $y + $delta[1]
                $nextKey = "$nextX,$nextY"
                if ($bluePixels.ContainsKey($nextKey) -and -not $visited.ContainsKey($nextKey)) {
                    $visited[$nextKey] = $true
                    $queue.Enqueue($nextKey)
                }
            }
        }

        if ($xs.Count -ge 100) {
            $components.Add([pscustomobject]@{
                X = ($xs | Measure-Object -Minimum).Minimum
                Y = ($ys | Measure-Object -Minimum).Minimum
                Width = (($xs | Measure-Object -Maximum).Maximum - ($xs | Measure-Object -Minimum).Minimum + 1)
                Height = (($ys | Measure-Object -Maximum).Maximum - ($ys | Measure-Object -Minimum).Minimum + 1)
                Pixels = $xs.Count
            })
        }
    }

    $bars = @($components | Sort-Object X)
    if ($bars.Count -ne 5) {
        throw "Expected 5 waveform bars, found $($bars.Count)."
    }

    foreach ($bar in $bars) {
        $x0 = [int]$bar.X - 2
        $y0 = [int]$bar.Y - 2
        $width = [int]$bar.Width + 4
        $height = [int]$bar.Height + 4
        $x1 = $x0 + $width - 1
        $y1 = $y0 + $height - 1
        $radius = 8.0
        for ($y = $y0; $y -le $y1; $y++) {
            for ($x = $x0; $x -le $x1; $x++) {
                $inside = $true
                if ($x -lt $x0 + $radius -and $y -lt $y0 + $radius) {
                    $inside = ([Math]::Pow($x - ($x0 + $radius), 2) + [Math]::Pow($y - ($y0 + $radius), 2)) -le [Math]::Pow($radius, 2)
                }
                elseif ($x -gt $x1 - $radius -and $y -lt $y0 + $radius) {
                    $inside = ([Math]::Pow($x - ($x1 - $radius), 2) + [Math]::Pow($y - ($y0 + $radius), 2)) -le [Math]::Pow($radius, 2)
                }
                elseif ($x -gt $x1 - $radius -and $y -gt $y1 - $radius) {
                    $inside = ([Math]::Pow($x - ($x1 - $radius), 2) + [Math]::Pow($y - ($y1 - $radius), 2)) -le [Math]::Pow($radius, 2)
                }
                elseif ($x -lt $x0 + $radius -and $y -gt $y1 - $radius) {
                    $inside = ([Math]::Pow($x - ($x0 + $radius), 2) + [Math]::Pow($y - ($y1 - $radius), 2)) -le [Math]::Pow($radius, 2)
                }

                if ($inside) {
                    $topSample = [Math]::Max(0, $y0 - 8)
                    $bottomSample = [Math]::Min($image.Height - 1, $y1 + 8)
                    $topColor = $image.GetPixel($x, $topSample)
                    $bottomColor = $image.GetPixel($x, $bottomSample)
                    $fill = [System.Drawing.Color]::FromArgb(
                        [int][Math]::Round(($topColor.A + $bottomColor.A) / 2.0),
                        [int][Math]::Round(($topColor.R + $bottomColor.R) / 2.0),
                        [int][Math]::Round(($topColor.G + $bottomColor.G) / 2.0),
                        [int][Math]::Round(($topColor.B + $bottomColor.B) / 2.0))
                    $image.SetPixel($x, $y, $fill)
                }
            }
        }
    }
    $image.Save($outputPath, [System.Drawing.Imaging.ImageFormat]::Png)

    Write-Output "Wrote $outputPath"
    Write-Output "Waveform bars in 512 px coordinates, radius 8:"
    foreach ($bar in $bars) {
        Write-Output ("x={0} y={1} width={2} height={3}" -f [int]$bar.X, [int]$bar.Y, [int]$bar.Width, [int]$bar.Height)
    }
}
finally {
    $image.Dispose()
}





