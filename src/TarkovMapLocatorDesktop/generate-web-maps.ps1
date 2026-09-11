param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'assets\maps\web'),
    [string]$ManifestPath = (Join-Path $PSScriptRoot 'web-map-projections.json'),
    [int]$Zoom = 4,
    [string]$MapId,
    [switch]$ForceDownload
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Net.Http

$nativeDirectory = Join-Path $PSScriptRoot 'assets\maps\native-cache'
$cacheDirectory = Join-Path $PSScriptRoot '.web-map-tile-cache'
$layerCatalog = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'map-layers.json') -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Force -Path $OutputDirectory, $cacheDirectory | Out-Null

$maps = @(
    @{ Id='customs'; Name='海关'; Transform=@(.239,168.65,.239,136.35); Rotation=180; Bounds=@(@(698,-307),@(-372,237)); TileSize=256; TilePath='https://assets.tarkov.dev/maps/customs_0.16/main/{z}/{x}/{y}.png'; Layers=@(
        @{ Id='underground'; Name='地下层'; TilePath='https://assets.tarkov.dev/maps/customs_0.16/underground/{z}/{x}/{y}.png' },
        @{ Id='2nd-floor'; Name='二层'; TilePath='https://assets.tarkov.dev/maps/customs_0.16/2nd/{z}/{x}/{y}.png' },
        @{ Id='3rd-floor'; Name='三层'; TilePath='https://assets.tarkov.dev/maps/customs_0.16/3rd/{z}/{x}/{y}.png' },
        @{ Id='4th-floor'; Name='四层'; TilePath='https://assets.tarkov.dev/maps/customs_0.16/4th/{z}/{x}/{y}.png' }
    )},
    @{ Id='shoreline'; Name='海岸线'; Transform=@(.16,83.2,.16,111.1); Rotation=180; Bounds=@(@(504,-415),@(-1056,618)); TileSize=256; TilePath='https://assets.tarkov.dev/maps/shoreline/main_summer/{z}/{x}/{y}.png'; Layers=@() },
    @{ Id='reserve'; Name='储备站'; Transform=@(.395,122,.395,137.65); Rotation=180; Bounds=@(@(289,-293),@(-303,244)); TileSize=256; TilePath='https://assets.tarkov.dev/maps/reserve/main/{z}/{x}/{y}.png'; Layers=@(
        @{ Id='bunkers'; Name='地下掩体'; TilePath='https://assets.tarkov.dev/maps/reserve/bunkers/{z}/{x}/{y}.png' },
        @{ Id='2nd-floor'; Name='二层'; TilePath='https://assets.tarkov.dev/maps/reserve/2nd/{z}/{x}/{y}.png'; Extents=@(
            @{ height=@(22.1,25.7); bounds=@(@(@(1,164),@(-17,199))) },
            @{ height=@(-3.5,-.64); bounds=@(@(@(-77,26),@(-177,106)),@(@(62,59),@(51,108)),@(@(-104,-37),@(-177,5))) },
            @{ height=@(-3.9,-.6); bounds=@(@(@(-47,-47),@(-85,-18))) },
            @{ height=@(-4.3,-2.2); bounds=@(@(@(-19.91,-13),@(-78,39))) },
            @{ height=@(-3.8,-1.1); bounds=@(@(@(99,-50),@(-2,7))) },
            @{ height=@(-1.9,11.3); bounds=@(@(@(191,-175),@(137,-120))) },
            @{ height=@(1,8); bounds=@(@(@(-109,-156),@(-119,-147)),@(@(289,-92),@(299,-82)),@(@(3,-210),@(-7,-200)),@(@(195,-260),@(185,-250)),@(@(276,17),@(266,27))) },
            @{ height=@(-4.1,-1.2); bounds=@(@(@(-128,-139),@(-146,-120))) }
        )},
        @{ Id='3rd-floor'; Name='三层'; TilePath='https://assets.tarkov.dev/maps/reserve/3rd/{z}/{x}/{y}.png'; Extents=@(
            @{ height=@(25.7,29.3); bounds=@(@(@(1,164),@(-17,199))) },
            @{ height=@(-.64,2.23); bounds=@(@(@(-77,26),@(-177,106)),@(@(-104,-37),@(-177,5))) },
            @{ height=@(-.6,10); bounds=@(@(@(-47,-47),@(-85,-18))) },
            @{ height=@(-2.2,2.14); bounds=@(@(@(-19.91,-13),@(-78,39))) },
            @{ height=@(-1.1,1.6); bounds=@(@(@(99,-50),@(-2,7))) }
        )},
        @{ Id='4th-floor'; Name='四层'; TilePath='https://assets.tarkov.dev/maps/reserve/4th/{z}/{x}/{y}.png'; Extents=@(
            @{ height=@(29.3,36); bounds=@(@(@(1,164),@(-17,199))) },
            @{ height=@(2.23,5); bounds=@(@(@(-77,26),@(-177,106))) },
            @{ height=@(2.15,6.6); bounds=@(@(@(-19.91,-13),@(-78,39))) },
            @{ height=@(1.6,4.7); bounds=@(@(@(99,-50),@(-2,7))) }
        )},
        @{ Id='5th-floor'; Name='五层'; TilePath='https://assets.tarkov.dev/maps/reserve/5th/{z}/{x}/{y}.png'; Extents=@(
            @{ height=@(5,9.5); bounds=@(@(@(-77,26),@(-177,106))) }
        )}
    )},
    @{ Id='woods'; Name='森林'; Transform=@(.1855,112.95,.1855,167.85); Rotation=180; Bounds=@(@(646,-914),@(-761,442)); TileSize=256; TilePath='https://assets.tarkov.dev/maps/woods/main_0.16/{z}/{x}/{y}.png'; Layers=@() },
    @{ Id='ground-zero'; Name='中心区'; Transform=@(.524,167.3,.524,65.1); Rotation=180; Bounds=@(@(249,-124),@(-99,364)); TileSize=256; TilePath='https://assets.tarkov.dev/maps/groundzero/main/{z}/{x}/{y}.png'; Layers=@(
        @{ Id='garage'; Name='地下车库'; TilePath='https://assets.tarkov.dev/maps/groundzero/garage/{z}/{x}/{y}.png' },
        @{ Id='2nd-floor'; Name='二层'; TilePath='https://assets.tarkov.dev/maps/groundzero/2nd/{z}/{x}/{y}.png' },
        @{ Id='3rd-floor'; Name='三层'; TilePath='https://assets.tarkov.dev/maps/groundzero/3rd/{z}/{x}/{y}.png' }
    )},
    @{ Id='factory'; Name='工厂'; Transform=@(1.629,119.9,1.629,139.3); Rotation=90; Bounds=@(@(77,-64.5),@(-65.5,67.4)); TileSize=256; TilePath='https://assets.tarkov.dev/maps/factory/main/{z}/{x}/{y}.png'; Layers=@(
        @{ Id='tunnels'; Name='地下隧道'; TilePath='https://assets.tarkov.dev/maps/factory/tunnels/{z}/{x}/{y}.png' },
        @{ Id='2nd-floor'; Name='二层'; TilePath='https://assets.tarkov.dev/maps/factory/2nd/{z}/{x}/{y}.png' },
        @{ Id='3rd-floor'; Name='三层'; TilePath='https://assets.tarkov.dev/maps/factory/3rd/{z}/{x}/{y}.png' }
    )},
    @{ Id='the-labyrinth'; Name='迷宫'; Transform=@(2.115,85.5,2.115,128); Rotation=270; Bounds=@(@(-52,-37),@(53,76)); TileSize=256; TilePath='https://assets.tarkov.dev/maps/labyrinth/main/{z}/{x}/{y}.png'; Layers=@() },
    @{ Id='icebreaker'; Name='破冰船'; SurfaceName='医务室'; Transform=@(2,125,3.5,91); Rotation=180; Bounds=@(@(20,-28),@(-23,52)); TileSize=256; TilePath='https://assets.tarkov.dev/maps/icebreaker/06_infirmary/{z}/{x}/{y}.png'; Layers=@(
        @{ Id='control-room'; Name='控制室'; TilePath='https://assets.tarkov.dev/maps/icebreaker/00_control_room/{z}/{x}/{y}.png' },
        @{ Id='engine-room'; Name='发动机舱'; TilePath='https://assets.tarkov.dev/maps/icebreaker/01_engine_room/{z}/{x}/{y}.png' },
        @{ Id='engine-room-upper'; Name='发动机舱（上层）'; TilePath='https://assets.tarkov.dev/maps/icebreaker/02_engine_room_upper/{z}/{x}/{y}.png' },
        @{ Id='fuel-pumps-lower'; Name='燃油泵（下层）'; TilePath='https://assets.tarkov.dev/maps/icebreaker/03_fuel_pumps_lower/{z}/{x}/{y}.png' },
        @{ Id='fuel-pumps'; Name='燃油泵'; TilePath='https://assets.tarkov.dev/maps/icebreaker/04_fuel_pumps/{z}/{x}/{y}.png' },
        @{ Id='storage-security'; Name='储藏区 / 安保区'; TilePath='https://assets.tarkov.dev/maps/icebreaker/05_storage_ecurity/{z}/{x}/{y}.png' },
        @{ Id='helipad'; Name='直升机坪'; TilePath='https://assets.tarkov.dev/maps/icebreaker/07_helipad/{z}/{x}/{y}.png' },
        @{ Id='gym-canteen'; Name='健身房 / 食堂'; TilePath='https://assets.tarkov.dev/maps/icebreaker/08_gym-canteen/{z}/{x}/{y}.png' },
        @{ Id='accommodation-lower'; Name='居住区（下层）'; TilePath='https://assets.tarkov.dev/maps/icebreaker/09_accommodation_lower/{z}/{x}/{y}.png' },
        @{ Id='accommodation-mid'; Name='居住区（中层）'; TilePath='https://assets.tarkov.dev/maps/icebreaker/10_accommodation_mid/{z}/{x}/{y}.png' },
        @{ Id='accommodation-upper'; Name='居住区（上层）'; TilePath='https://assets.tarkov.dev/maps/icebreaker/11_accommodation_upper/{z}/{x}/{y}.png' },
        @{ Id='officers-deck'; Name='军官甲板'; TilePath='https://assets.tarkov.dev/maps/icebreaker/12_officers_deck/{z}/{x}/{y}.png' },
        @{ Id='stairs-blocked'; Name='楼梯（封锁）'; TilePath='https://assets.tarkov.dev/maps/icebreaker/13_stairs_blocked/{z}/{x}/{y}.png' },
        @{ Id='bridge'; Name='舰桥'; TilePath='https://assets.tarkov.dev/maps/icebreaker/14_bridge/{z}/{x}/{y}.png' },
        @{ Id='bridge-roof'; Name='舰桥顶部'; TilePath='https://assets.tarkov.dev/maps/icebreaker/15_bridge_roof/{z}/{x}/{y}.png' }
    )}
)

function Project-WorldPoint {
    param($Map, [double]$X, [double]$Z, [double]$Scale)
    $rotation = [double]$Map['Rotation']
    $transform = [object[]]$Map['Transform']
    $a = [double]$transform[0]
    $b = [double]$transform[1]
    $c = [double]$transform[2]
    $d = [double]$transform[3]
    $radians = $rotation * [Math]::PI / 180
    $cosine = [Math]::Cos($radians)
    $sine = [Math]::Sin($radians)
    $rotatedX = $X * $cosine - $Z * $sine
    $rotatedZ = $X * $sine + $Z * $cosine
    return [pscustomobject]@{
        X = [double]$Scale * ([double]$a * [double]$rotatedX + [double]$b)
        Y = [double]$Scale * (-[double]$c * [double]$rotatedZ + [double]$d)
    }
}

function Get-ProjectedBounds {
    param($Map, [double]$Scale)
    $x0 = [double]$Map.Bounds[0][0]
    $z0 = [double]$Map.Bounds[0][1]
    $x1 = [double]$Map.Bounds[1][0]
    $z1 = [double]$Map.Bounds[1][1]
    $points = @(
        (Project-WorldPoint $Map $x0 $z0 $Scale),
        (Project-WorldPoint $Map $x0 $z1 $Scale),
        (Project-WorldPoint $Map $x1 $z0 $Scale),
        (Project-WorldPoint $Map $x1 $z1 $Scale)
    )
    return @{
        MinX = [double](($points | ForEach-Object { $_.X } | Measure-Object -Minimum).Minimum)
        MaxX = [double](($points | ForEach-Object { $_.X } | Measure-Object -Maximum).Maximum)
        MinY = [double](($points | ForEach-Object { $_.Y } | Measure-Object -Minimum).Minimum)
        MaxY = [double](($points | ForEach-Object { $_.Y } | Measure-Object -Maximum).Maximum)
    }
}

function ConvertTo-ManifestExtents {
    param($Extents)

    $result = @()
    foreach ($extent in @($Extents)) {
        $rawBounds = @($extent.bounds)
        $isSingleBoundsPair = $rawBounds.Count -eq 2 -and
            @($rawBounds[0]).Count -eq 2 -and
            -not ($rawBounds[0][0] -is [System.Array])
        if ($isSingleBoundsPair) {
            $normalizedBounds = ,$rawBounds
        }
        else {
            $normalizedBounds = $rawBounds
        }
        $result += [ordered]@{
            height = @([double]$extent.height[0], [double]$extent.height[1])
            bounds = $normalizedBounds
        }
    }
    return @($result)
}

function Get-TileFile {
    param([System.Net.Http.HttpClient]$Client, $Map, [string]$AssetId, [string]$TileUrlPattern, [int]$TileX, [int]$TileY)
    $mapCache = Join-Path (Join-Path $cacheDirectory $Map.Id) $AssetId
    New-Item -ItemType Directory -Force -Path $mapCache | Out-Null
    $path = Join-Path $mapCache ("{0}_{1}_{2}.png" -f $Zoom, $TileX, $TileY)
    if ((Test-Path -LiteralPath $path) -and -not $ForceDownload) { return $path }
    $url = $TileUrlPattern.Replace('{z}', [string]$Zoom).Replace('{x}', [string]$TileX).Replace('{y}', [string]$TileY)
    try {
        $bytes = $Client.GetByteArrayAsync($url).GetAwaiter().GetResult()
        [System.IO.File]::WriteAllBytes($path, $bytes)
        return $path
    }
    catch {
        return $null
    }
}

function Build-TileMap {
    param([System.Net.Http.HttpClient]$Client, $Map, $Projected, [string]$AssetId, [string]$TileUrlPattern)
    $tileSize = [int]$Map.TileSize
    $cropX = [int][Math]::Floor($Projected.MinX)
    $cropY = [int][Math]::Floor($Projected.MinY)
    $cropRight = [int][Math]::Ceiling($Projected.MaxX)
    $cropBottom = [int][Math]::Ceiling($Projected.MaxY)
    $width = $cropRight - $cropX
    $height = $cropBottom - $cropY
    $minTileX = [int][Math]::Floor($cropX / [double]$tileSize)
    $maxTileX = [int][Math]::Floor(($cropRight - 1) / [double]$tileSize)
    $minTileY = [int][Math]::Floor($cropY / [double]$tileSize)
    $maxTileY = [int][Math]::Floor(($cropBottom - 1) / [double]$tileSize)
    $canvasMinTileX = $minTileX
    $canvasMaxTileX = $maxTileX
    $canvasMinTileY = $minTileY
    $canvasMaxTileY = $maxTileY
    $maximumTileIndex = [int][Math]::Pow(2, $Zoom) - 1
    $minTileX = [Math]::Max(0, $minTileX)
    $maxTileX = [Math]::Min($maximumTileIndex, $maxTileX)
    $minTileY = [Math]::Max(0, $minTileY)
    $maxTileY = [Math]::Min($maximumTileIndex, $maxTileY)
    $canvas = [System.Drawing.Bitmap]::new(($canvasMaxTileX - $canvasMinTileX + 1) * $tileSize, ($canvasMaxTileY - $canvasMinTileY + 1) * $tileSize, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($canvas)
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            for ($tileY = $minTileY; $tileY -le $maxTileY; $tileY++) {
                for ($tileX = $minTileX; $tileX -le $maxTileX; $tileX++) {
                    $tileFilePath = Get-TileFile $Client $Map $AssetId $TileUrlPattern $tileX $tileY
                    if (-not $tileFilePath) { continue }
                    try {
                        $tile = [System.Drawing.Image]::FromFile($tileFilePath)
                        try {
                            $destination = [System.Drawing.Rectangle]::new(($tileX - $canvasMinTileX) * $tileSize, ($tileY - $canvasMinTileY) * $tileSize, $tileSize, $tileSize)
                            $graphics.DrawImage($tile, $destination)
                        }
                        finally { $tile.Dispose() }
                    }
                    catch { Write-Host "  跳过无效瓦片 $tileX,$tileY" }
                }
            }
        }
        finally { $graphics.Dispose() }

        $cropped = [System.Drawing.Bitmap]::new($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $cropGraphics = [System.Drawing.Graphics]::FromImage($cropped)
            try {
                $cropGraphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
                $source = [System.Drawing.Rectangle]::new($cropX - $canvasMinTileX * $tileSize, $cropY - $canvasMinTileY * $tileSize, $width, $height)
                $destination = [System.Drawing.Rectangle]::new(0, 0, $width, $height)
                $cropGraphics.DrawImage($canvas, $destination, $source, [System.Drawing.GraphicsUnit]::Pixel)
            }
            finally { $cropGraphics.Dispose() }
            $relativeImage = if ($AssetId -eq 'surface') {
                "assets/maps/web/$($Map.Id).png"
            }
            else {
                "assets/maps/web/layers/$($Map.Id)--$AssetId.png"
            }
            $output = Join-Path $PSScriptRoot ($relativeImage.Replace('/', [IO.Path]::DirectorySeparatorChar))
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $output) | Out-Null
            $temporary = "$output.tmp.png"
            $cropped.Save($temporary, [System.Drawing.Imaging.ImageFormat]::Png)
            Move-Item -LiteralPath $temporary -Destination $output -Force
        }
        finally { $cropped.Dispose() }
    }
    finally { $canvas.Dispose() }

    return @{
        Image = $relativeImage
        CropX = [double]$cropX
        CropY = [double]$cropY
        CropWidth = [double]$width
        CropHeight = [double]$height
        PixelWidth = $width
        PixelHeight = $height
        Mode = 'tiles'
    }
}

$scale = [Math]::Pow(2, $Zoom)
$selectedMaps = if ([string]::IsNullOrWhiteSpace($MapId)) { $maps } else { @($maps | Where-Object Id -EQ $MapId) }
if ($selectedMaps.Count -eq 0) { throw "未知地图：$MapId" }
$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.AutomaticDecompression = [System.Net.DecompressionMethods]::All
$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds(25)
$client.DefaultRequestHeaders.UserAgent.ParseAdd('TarkovToolbox/31')
$manifestMaps = @()
try {
    foreach ($map in $selectedMaps) {
        Write-Host "生成 $($map.Name) ($($map.Id))..."
        $projected = Get-ProjectedBounds $map $scale
        if ($map.TilePath) {
            $asset = Build-TileMap $client $map $projected 'surface' $map.TilePath
        }
        else {
            $nativePath = Join-Path $nativeDirectory $map.Native
            if (-not (Test-Path -LiteralPath $nativePath)) { throw "缺少卫星地图备用底图：$nativePath" }
            $native = [System.Drawing.Image]::FromFile($nativePath)
            try {
                $asset = @{
                    Image = "assets/maps/native-cache/$($map.Native)"
                    CropX = $projected.MinX
                    CropY = $projected.MinY
                    CropWidth = $projected.MaxX - $projected.MinX
                    CropHeight = $projected.MaxY - $projected.MinY
                    PixelWidth = $native.Width
                    PixelHeight = $native.Height
                    Mode = 'svg'
                }
            }
            finally { $native.Dispose() }
        }

        $manifestLayers = @()
        foreach ($layer in @($map.Layers)) {
            $layerAsset = Build-TileMap $client $map $projected $layer.Id $layer.TilePath
            $catalogMap = $layerCatalog.maps | Where-Object id -EQ $map.Id | Select-Object -First 1
            $catalogLayer = $catalogMap.layers | Where-Object id -EQ $layer.Id | Select-Object -First 1
            $extents = if ($null -ne $layer.Extents) { $layer.Extents } else { @($catalogLayer.extents) }
            $manifestLayers += [ordered]@{
                id = $layer.Id
                name = $layer.Name
                image = $layerAsset.Image
                pixelSize = @($layerAsset.PixelWidth, $layerAsset.PixelHeight)
                extents = @(ConvertTo-ManifestExtents $extents)
            }
            Write-Host "  楼层完成：$($layer.Name)"
        }

        $manifestMaps += [ordered]@{
            id = $map.Id
            name = $map.Name
            image = $asset.Image
            mode = $asset.Mode
            zoom = $Zoom
            transform = @([double]$map.Transform[0], [double]$map.Transform[1], [double]$map.Transform[2], [double]$map.Transform[3])
            coordinateRotation = [double]$map.Rotation
            bounds = $map.Bounds
            crop = [ordered]@{ x=$asset.CropX; y=$asset.CropY; width=$asset.CropWidth; height=$asset.CropHeight }
            pixelSize = @($asset.PixelWidth, $asset.PixelHeight)
            surfaceName = if ($map.SurfaceName) { $map.SurfaceName } else { '地面' }
            layers = $manifestLayers
        }
        Write-Host "  完成：$($asset.PixelWidth) x $($asset.PixelHeight)"
    }
}
finally {
    $client.Dispose()
    $handler.Dispose()
}

$existingMaps = @()
if (-not [string]::IsNullOrWhiteSpace($MapId) -and (Test-Path -LiteralPath $ManifestPath)) {
    $existingMaps = @((Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json).maps | Where-Object id -NE $MapId)
}
$manifest = [ordered]@{
    schemaVersion = 1
    source = 'https://www.tarkov-helper.cn/maps'
    generatedAt = [DateTime]::UtcNow.ToString('o')
    maps = @($existingMaps) + @($manifestMaps)
}
[System.IO.File]::WriteAllText($ManifestPath, ($manifest | ConvertTo-Json -Depth 12) + "`n", [System.Text.UTF8Encoding]::new($false))
Write-Host "卫星地图清单已生成：$ManifestPath"
