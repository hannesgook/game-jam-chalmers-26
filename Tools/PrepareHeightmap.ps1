param(
    [string]$SourceDirectory = "Assets/Map_data/639_14",
    [string]$OutputPath = "Assets/Map_data/gothenburg_height_513.bytes",
    [int]$Resolution = 513,
    [int]$SmoothingPasses = 2
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName PresentationCore

# Gameplay bounds and Göteborg Stad height model projection (EPSG:3007).
$south = 57.695
$west = 11.965
$north = 57.710
$east = 11.985
$axis = 6378137.0
$flattening = 1.0 / 298.257222101
$eccentricitySquared = $flattening * (2.0 - $flattening)
$secondEccentricitySquared = $eccentricitySquared / (1.0 - $eccentricitySquared)
$centralMeridian = 12.0 * [Math]::PI / 180.0
# EPSG:3007 (SWEREF 99 12 00) is a local zone: k0 is exactly 1.0, not the 0.9996/0.9999
# used by the nationwide SWEREF 99 TM zone. A wrong k0 biases northing by (1 - k0) * ~6.4e6 m,
# which at 0.9999 sampled the DEM 640 m south of the gameplay bounds.
$scale = 1.0

function Convert-ToSweref991200([double]$latitude, [double]$longitude) {
    $phi = $latitude * [Math]::PI / 180.0
    $lambda = $longitude * [Math]::PI / 180.0
    $sinPhi = [Math]::Sin($phi)
    $cosPhi = [Math]::Cos($phi)
    $tanPhi = [Math]::Tan($phi)
    $nRadius = $axis / [Math]::Sqrt(1.0 - $eccentricitySquared * $sinPhi * $sinPhi)
    $t = $tanPhi * $tanPhi
    $c = $secondEccentricitySquared * $cosPhi * $cosPhi
    $aTerm = $cosPhi * ($lambda - $centralMeridian)
    $e4 = $eccentricitySquared * $eccentricitySquared
    $e6 = $e4 * $eccentricitySquared
    $meridian = $axis * (
        (1.0 - $eccentricitySquared / 4.0 - 3.0 * $e4 / 64.0 - 5.0 * $e6 / 256.0) * $phi -
        (3.0 * $eccentricitySquared / 8.0 + 3.0 * $e4 / 32.0 + 45.0 * $e6 / 1024.0) * [Math]::Sin(2.0 * $phi) +
        (15.0 * $e4 / 256.0 + 45.0 * $e6 / 1024.0) * [Math]::Sin(4.0 * $phi) -
        (35.0 * $e6 / 3072.0) * [Math]::Sin(6.0 * $phi))
    $easting = 150000.0 + $scale * $nRadius * (
        $aTerm + (1.0 - $t + $c) * [Math]::Pow($aTerm, 3) / 6.0 +
        (5.0 - 18.0 * $t + $t * $t + 72.0 * $c - 58.0 * $secondEccentricitySquared) * [Math]::Pow($aTerm, 5) / 120.0)
    $northing = $scale * ($meridian + $nRadius * $tanPhi * (
        $aTerm * $aTerm / 2.0 +
        (5.0 - $t + 9.0 * $c + 4.0 * $c * $c) * [Math]::Pow($aTerm, 4) / 24.0 +
        (61.0 - 58.0 * $t + $t * $t + 600.0 * $c - 330.0 * $secondEccentricitySquared) * [Math]::Pow($aTerm, 6) / 720.0))
    return @($easting, $northing)
}

$tileCache = @{}
function Get-Tile([int]$tileNorth, [int]$tileEast) {
    $key = "${tileNorth}_${tileEast}"
    if ($tileCache.ContainsKey($key)) { return $tileCache[$key] }
    $path = Join-Path $SourceDirectory "$key.tif"
    if (-not (Test-Path -LiteralPath $path)) { throw "Required height tile is missing: $path" }
    $stream = [IO.File]::OpenRead((Resolve-Path -LiteralPath $path))
    try {
        $decoder = [Windows.Media.Imaging.TiffBitmapDecoder]::new(
            $stream,
            [Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
            [Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
        $frame = $decoder.Frames[0]
        if ($frame.Format.ToString() -ne "Gray32Float" -or $frame.PixelWidth -ne 2000 -or $frame.PixelHeight -ne 2000) {
            throw "Unexpected GeoTIFF format in ${path}: $($frame.PixelWidth)x$($frame.PixelHeight) $($frame.Format)"
        }
        $bytes = [byte[]]::new($frame.PixelWidth * $frame.PixelHeight * 4)
        $frame.CopyPixels($bytes, $frame.PixelWidth * 4, 0)
        $tile = @{ Bytes = $bytes; Width = $frame.PixelWidth; Height = $frame.PixelHeight }
        $tileCache[$key] = $tile
        return $tile
    }
    finally { $stream.Dispose() }
}

function Get-Height([double]$easting, [double]$northing) {
    $tileEast = [Math]::Floor($easting / 1000.0)
    $tileNorth = [Math]::Floor($northing / 1000.0)
    $tile = Get-Tile $tileNorth $tileEast
    $pixelX = ($easting - ($tileEast * 1000.0 + 0.25)) / 0.5
    $pixelY = (($tileNorth + 1) * 1000.0 - 0.25 - $northing) / 0.5
    $x0 = [Math]::Max(0, [Math]::Min(1999, [Math]::Floor($pixelX)))
    $y0 = [Math]::Max(0, [Math]::Min(1999, [Math]::Floor($pixelY)))
    $x1 = [Math]::Min(1999, $x0 + 1)
    $y1 = [Math]::Min(1999, $y0 + 1)
    $tx = [Math]::Max(0.0, [Math]::Min(1.0, $pixelX - $x0))
    $ty = [Math]::Max(0.0, [Math]::Min(1.0, $pixelY - $y0))
    $stride = 2000 * 4
    $v00 = [BitConverter]::ToSingle($tile.Bytes, $y0 * $stride + $x0 * 4)
    $v10 = [BitConverter]::ToSingle($tile.Bytes, $y0 * $stride + $x1 * 4)
    $v01 = [BitConverter]::ToSingle($tile.Bytes, $y1 * $stride + $x0 * 4)
    $v11 = [BitConverter]::ToSingle($tile.Bytes, $y1 * $stride + $x1 * 4)
    return (1.0 - $ty) * ((1.0 - $tx) * $v00 + $tx * $v10) + $ty * ((1.0 - $tx) * $v01 + $tx * $v11)
}

if ($Resolution -lt 33) { throw "Resolution must be at least 33." }
$heights = [single[]]::new($Resolution * $Resolution)
$minimum = [single]::PositiveInfinity
$maximum = [single]::NegativeInfinity
for ($y = 0; $y -lt $Resolution; $y++) {
    $latitude = $south + ($north - $south) * $y / ($Resolution - 1)
    for ($x = 0; $x -lt $Resolution; $x++) {
        $longitude = $west + ($east - $west) * $x / ($Resolution - 1)
        $coordinate = Convert-ToSweref991200 $latitude $longitude
        $height = [single](Get-Height $coordinate[0] $coordinate[1])
        if ($height -le -9990.0 -or [single]::IsNaN($height)) { throw "NoData encountered at $latitude, $longitude" }
        $index = $y * $Resolution + $x
        $heights[$index] = $height
        if ($height -lt $minimum) { $minimum = $height }
        if ($height -gt $maximum) { $maximum = $height }
    }
}

# Remove sub-grid kerbs, retaining-wall spikes, and scan noise. At 513 samples,
# two 3x3 passes smooth only a few metres and keep the city's larger slopes.
for ($pass = 0; $pass -lt $SmoothingPasses; $pass++) {
    $smoothed = [single[]]::new($heights.Length)
    for ($y = 0; $y -lt $Resolution; $y++) {
        for ($x = 0; $x -lt $Resolution; $x++) {
            [double]$total = 0.0
            [int]$sampleCount = 0
            for ($offsetY = -1; $offsetY -le 1; $offsetY++) {
                $sampleY = [Math]::Max(0, [Math]::Min($Resolution - 1, $y + $offsetY))
                for ($offsetX = -1; $offsetX -le 1; $offsetX++) {
                    $sampleX = [Math]::Max(0, [Math]::Min($Resolution - 1, $x + $offsetX))
                    $total += $heights[$sampleY * $Resolution + $sampleX]
                    $sampleCount++
                }
            }
            $smoothed[$y * $Resolution + $x] = [single]($total / $sampleCount)
        }
    }
    $heights = $smoothed
}

$minimum = [single]::PositiveInfinity
$maximum = [single]::NegativeInfinity
foreach ($height in $heights) {
    if ($height -lt $minimum) { $minimum = $height }
    if ($height -gt $maximum) { $maximum = $height }
}
$range = [Math]::Max(0.001, $maximum - $minimum)
$memory = [IO.MemoryStream]::new()
$writer = [IO.BinaryWriter]::new($memory)
try {
    $writer.Write([Text.Encoding]::ASCII.GetBytes("SRH1"))
    $writer.Write([int]$Resolution)
    $writer.Write([int]$Resolution)
    $writer.Write([single]$minimum)
    $writer.Write([single]$maximum)
    foreach ($height in $heights) {
        $normalized = ($height - $minimum) / $range
        $writer.Write([uint16][Math]::Round([Math]::Max(0.0, [Math]::Min(1.0, $normalized)) * 65535.0))
    }
    $writer.Flush()
    $absoluteOutput = if ([IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { [IO.Path]::GetFullPath((Join-Path (Get-Location).ProviderPath $OutputPath)) }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($absoluteOutput)) | Out-Null
    [IO.File]::WriteAllBytes($absoluteOutput, $memory.ToArray())
}
finally {
    $writer.Dispose()
    $memory.Dispose()
}

Write-Host ("Prepared {0}x{0} heightmap: {1:N2}m to {2:N2}m ({3:N1} KiB), using {4} source tiles." -f $Resolution, $minimum, $maximum, ((Get-Item -LiteralPath $absoluteOutput).Length / 1KB), $tileCache.Count)
