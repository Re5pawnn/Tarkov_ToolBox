param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$MapId = "",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Net.Http

$manifestPath = Join-Path $ProjectRoot "map-layers.json"
$debugDirectory = Join-Path $ProjectRoot "assets\maps\debug"
$baseDirectory = Join-Path $ProjectRoot "assets\maps\native-cache"
$outputDirectory = Join-Path $ProjectRoot "assets\maps\layers"

$manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath $manifestPath | ConvertFrom-Json
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

$http = [System.Net.Http.HttpClient]::new()
$http.Timeout = [TimeSpan]::FromSeconds(20)
$http.DefaultRequestHeaders.UserAgent.ParseAdd("TarkovToolbox/31.1 layer asset builder")
[System.Net.ServicePointManager]::DefaultConnectionLimit = 24

function Get-NumberPair {
    param($Value)
    if ($null -eq $Value -or $Value.Count -lt 2) { return $null }
    return @([double]$Value[0], [double]$Value[1])
}

function Get-LivePixel {
    param(
        [double]$WorldX,
        [double]$WorldZ,
        $Projection,
        [int]$Zoom
    )

    $angle = [double]$Projection.coordinateRotation * [Math]::PI / 180
    $cosine = [Math]::Cos($angle)
    $sine = [Math]::Sin($angle)
    $rotatedX = $WorldX * $cosine - $WorldZ * $sine
    $rotatedZ = $WorldX * $sine + $WorldZ * $cosine
    $scale = [Math]::Pow(2, $Zoom)
    $transform = $Projection.transform
    return [System.Drawing.PointF]::new(
        [single](($rotatedX * [double]$transform[0] + [double]$transform[1]) * $scale),
        [single](($rotatedZ * -[double]$transform[2] + [double]$transform[3]) * $scale))
}

function Save-ResizedAsset {
    param(
        [System.Drawing.Bitmap]$Source,
        [int]$Width,
        [int]$Height,
        [string]$Destination
    )

    $resized = [System.Drawing.Bitmap]::new($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppPArgb)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($resized)
        try {
            if ([System.IO.Path]::GetExtension($Destination) -match "^\.(jpg|jpeg)$") {
                $graphics.Clear([System.Drawing.Color]::FromArgb(15, 20, 23))
            }
            else {
                $graphics.Clear([System.Drawing.Color]::Transparent)
            }
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.DrawImage($Source, [System.Drawing.Rectangle]::new(0, 0, $Width, $Height))
        }
        finally {
            $graphics.Dispose()
        }
        if ([System.IO.Path]::GetExtension($Destination) -match "^\.(jpg|jpeg)$") {
            $jpegCodec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() |
                Where-Object MimeType -eq "image/jpeg" |
                Select-Object -First 1
            $quality = [System.Drawing.Imaging.EncoderParameter]::new(
                [System.Drawing.Imaging.Encoder]::Quality,
                [long]94)
            $parameters = [System.Drawing.Imaging.EncoderParameters]::new(1)
            try {
                $parameters.Param[0] = $quality
                $resized.Save($Destination, $jpegCodec, $parameters)
            }
            finally {
                $parameters.Dispose()
                $quality.Dispose()
            }
        }
        else {
            $resized.Save($Destination, [System.Drawing.Imaging.ImageFormat]::Png)
        }
    }
    finally {
        $resized.Dispose()
    }
}

function Build-TileLayer {
    param(
        $Map,
        $Layer,
        [string]$AssetId,
        [string]$Destination,
        [int]$TargetWidth,
        [int]$TargetHeight
    )

    $projection = $Map.projection
    $preferredZoom = if ($null -ne $Map.buildZoom) { [int]$Map.buildZoom } elseif ($Map.id -eq "the-lab") { 5 } else { 4 }
    $zoom = [Math]::Min([int]$projection.maxZoom, [Math]::Max([int]$projection.minZoom, $preferredZoom))
    $projectionBounds = if ($null -ne $Map.assetBounds) { $Map.assetBounds } else { $projection.bounds }
    $bounds = @(
        (Get-NumberPair $projectionBounds[0]),
        (Get-NumberPair $projectionBounds[1])
    )
    if ($null -eq $bounds[0] -or $null -eq $bounds[1]) {
        throw "No usable world bounds for $($Map.id)."
    }

    $x0 = [double]$bounds[0][0]
    $z0 = [double]$bounds[0][1]
    $x1 = [double]$bounds[1][0]
    $z1 = [double]$bounds[1][1]
    $corners = @(
        (Get-LivePixel $x0 $z0 $projection $zoom),
        (Get-LivePixel $x0 $z1 $projection $zoom),
        (Get-LivePixel $x1 $z0 $projection $zoom),
        (Get-LivePixel $x1 $z1 $projection $zoom)
    )

    $minimumX = ($corners | Measure-Object X -Minimum).Minimum
    $maximumX = ($corners | Measure-Object X -Maximum).Maximum
    $minimumY = ($corners | Measure-Object Y -Minimum).Minimum
    $maximumY = ($corners | Measure-Object Y -Maximum).Maximum

    if ($Map.id -eq "the-lab" -and $zoom -eq 5) {
        # Labs v4's API bounds describe the playable coordinate window, not the
        # full visual tile canvas. Registering the v4 surface against the legacy
        # map (whose local points are already calibrated) yields this common
        # pixel frame. All Labs floors must use it unchanged.
        $minimumX = 280.5091309823984
        $maximumX = 7865.579834725457
        $minimumY = 1437.1224780176278
        $maximumY = 6893.853211746006
    }

    $canvasWidth = [Math]::Max(1, [int][Math]::Ceiling($maximumX - $minimumX))
    $canvasHeight = [Math]::Max(1, [int][Math]::Ceiling($maximumY - $minimumY))
    $tileSize = 256
    $minimumTileX = [int][Math]::Floor($minimumX / $tileSize)
    $maximumTileX = [int][Math]::Floor(($maximumX - 1) / $tileSize)
    $minimumTileY = [int][Math]::Floor($minimumY / $tileSize)
    $maximumTileY = [int][Math]::Floor(($maximumY - 1) / $tileSize)
    $maximumTileIndex = [int][Math]::Pow(2, $zoom) - 1
    $minimumTileX = [Math]::Max(0, $minimumTileX)
    $maximumTileX = [Math]::Min($maximumTileIndex, $maximumTileX)
    $minimumTileY = [Math]::Max(0, $minimumTileY)
    $maximumTileY = [Math]::Min($maximumTileIndex, $maximumTileY)

    $canvas = [System.Drawing.Bitmap]::new($canvasWidth, $canvasHeight, [System.Drawing.Imaging.PixelFormat]::Format32bppPArgb)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($canvas)
        $loadedTiles = 0
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
            $tileRequests = @()
            for ($tileY = $minimumTileY; $tileY -le $maximumTileY; $tileY++) {
                for ($tileX = $minimumTileX; $tileX -le $maximumTileX; $tileX++) {
                    $url = $Layer.tilePath.Replace("{z}", [string]$zoom).Replace("{x}", [string]$tileX).Replace("{y}", [string]$tileY)
                    $tileRequests += [pscustomobject]@{
                        X = $tileX
                        Y = $tileY
                        Url = $url
                    }
                }
            }

            $batchSize = 24
            for ($offset = 0; $offset -lt $tileRequests.Count; $offset += $batchSize) {
                $last = [Math]::Min($offset + $batchSize - 1, $tileRequests.Count - 1)
                $pending = @()
                foreach ($request in $tileRequests[$offset..$last]) {
                    $pending += [pscustomobject]@{
                        Request = $request
                        Task = $http.GetByteArrayAsync($request.Url)
                    }
                }

                foreach ($download in $pending) {
                    try {
                        $bytes = $download.Task.GetAwaiter().GetResult()
                        $stream = [System.IO.MemoryStream]::new($bytes, $false)
                        try {
                            $tile = [System.Drawing.Image]::FromStream($stream)
                            try {
                                $drawX = [single]($download.Request.X * $tileSize - $minimumX)
                                $drawY = [single]($download.Request.Y * $tileSize - $minimumY)
                                $graphics.DrawImage($tile, $drawX, $drawY, $tileSize, $tileSize)
                                $loadedTiles++
                            }
                            finally {
                                $tile.Dispose()
                            }
                        }
                        finally {
                            $stream.Dispose()
                        }
                    }
                    catch {
                        Write-Warning "Tile failed: $($download.Request.Url) ($($_.Exception.Message))"
                    }
                }
            }
        }
        finally {
            $graphics.Dispose()
        }
        if ($loadedTiles -eq 0) {
            throw "No tiles were downloaded for $($Map.id)/$($Layer.id)."
        }
        Save-ResizedAsset $canvas $TargetWidth $TargetHeight $Destination
        Write-Host "Built $($Map.id)/${AssetId}: $loadedTiles tiles -> $TargetWidth x $TargetHeight"
    }
    finally {
        $canvas.Dispose()
    }
}

try {
    foreach ($map in $manifest.maps) {
        if (-not [string]::IsNullOrWhiteSpace($MapId) -and
            -not [string]::Equals($map.id, $MapId, [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $baseFileName = if ($map.id -eq "the-lab") { "the-lab.jpg" } else { "$($map.id).png" }
        $basePath = Join-Path $baseDirectory $baseFileName
        $baseExists = Test-Path -LiteralPath $basePath

        if ($null -ne $map.outputWidth -and $null -ne $map.outputHeight) {
            $targetWidth = [int]$map.outputWidth
            $targetHeight = [int]$map.outputHeight
        }
        elseif ($map.id -eq "the-lab") {
            $targetWidth = 4096
            $targetHeight = 2946
        }
        elseif ($baseExists) {
            $baseImage = [System.Drawing.Image]::FromFile($basePath)
            try {
                $targetWidth = $baseImage.Width
                $targetHeight = $baseImage.Height
            }
            finally {
                $baseImage.Dispose()
            }
        }
        else {
            throw "Base map asset and output dimensions are missing: $basePath"
        }

        if ([string]::IsNullOrWhiteSpace($map.surface.svgPath) -and
            -not [string]::IsNullOrWhiteSpace($map.surface.tilePath) -and
            (-not $baseExists -or $Force)) {
            Build-TileLayer $map $map.surface "surface" $basePath $targetWidth $targetHeight
        }

        foreach ($layer in $map.layers) {
            $destination = Join-Path $outputDirectory $layer.image
            if ((Test-Path -LiteralPath $destination) -and -not $Force) {
                Write-Host "Kept $($map.id)/$($layer.id)"
                continue
            }

            if (-not [string]::IsNullOrWhiteSpace($layer.svgLayer) -and
                (Test-Path -LiteralPath $destination)) {
                Write-Host "Kept SVG-rendered $($map.id)/$($layer.id)"
                continue
            }

            if ([string]::IsNullOrWhiteSpace($layer.tilePath)) {
                throw "Layer has neither a local overlay nor a tile source: $($map.id)/$($layer.id)"
            }
            Build-TileLayer $map $layer $layer.id $destination $targetWidth $targetHeight
        }
    }
}
finally {
    $http.Dispose()
}
