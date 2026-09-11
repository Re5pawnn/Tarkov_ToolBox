using TarkovMapLocatorDesktop.Models;

namespace TarkovMapLocatorDesktop.Services;

/// <summary>
/// Applies one consistent floor rule to static and live markers. A marker that
/// has no usable world height is treated as a surface-only marker; this keeps
/// hand-calibrated outdoor exits, BTR predictions and airdrop guides from
/// leaking onto indoor floor overlays.
/// </summary>
public static class MapLayerVisibilityService
{
    public static bool IsVisible(
        MapMarker marker,
        MapLayerDefinition? activeLayer,
        IReadOnlyList<MapLayerDefinition>? mapLayers)
    {
        if (activeLayer is not null)
            return activeLayer.MatchesMarker(marker);

        if (marker.VisibleOnSurface)
            return true;

        if (marker.WorldHeight is not { } height || !double.IsFinite(height))
            return true;

        return mapLayers is null || !mapLayers.Any(layer => layer.MatchesMarker(marker));
    }
}
