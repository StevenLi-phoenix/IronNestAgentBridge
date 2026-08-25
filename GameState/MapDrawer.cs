using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace IronNestAgentBridge.GameState;

/// <summary>
/// Draws on the tactical map with the player's own drawing tools: markers are injected
/// through MapMarkerPlacer.RestoreMarker(MapMarkerSaveData) — the exact pipeline the game
/// uses to restore player-drawn pen lines / compass circles from a save. Different tools
/// (yellow pen, compass) are different marker prefabs on the placers.
/// </summary>
public static class MapDrawer
{
    public static object Inspect()
    {
        var placers = new List<object>();
        foreach (var placer in UnityEngine.Object.FindObjectsOfType<MapMarkerPlacer>(true))
        {
            var prefabs = new List<string>();
            try
            {
                if (placer.knownMarkerPrefabs != null)
                    foreach (var p in placer.knownMarkerPrefabs)
                        if (p != null)
                            prefabs.Add(p.name);
                if (placer.markerPrefabs != null)
                    foreach (var p in placer.markerPrefabs)
                        if (p != null && !prefabs.Contains(p.name))
                            prefabs.Add(p.name);
            }
            catch { }
            placers.Add(new
            {
                name = placer.gameObject.name,
                path = MapMarkerPlacer.GetHierarchyPath(placer.transform),
                active = placer.isActiveAndEnabled,
                prefabs,
                placed = placer.placedMarkers?.Count ?? 0,
            });
        }

        var captured = new List<object>();
        try
        {
            foreach (var m in MapMarkerPlacer.CaptureMissionMarkers())
                captured.Add(new
                {
                    placerIndex = m.PlacerIndex,
                    prefabName = m.PrefabName,
                    origin = new { x = ((Vector2)m.Origin).x, y = ((Vector2)m.Origin).y },
                    target = new { x = ((Vector2)m.Target).x, y = ((Vector2)m.Target).y },
                });
        }
        catch (Exception ex)
        {
            captured.Add(new { error = ex.Message });
        }

        return new { placers, captured };
    }

    /// <summary>
    /// Draw one marker (line/circle depending on prefab) by appending through the placer's
    /// instance RestoreMarker. NEVER use static RestoreMissionMarkers per stroke — it is
    /// clear-then-restore and wipes every existing drawing including the player's.
    /// </summary>
    public static string Draw(int placerIndex, string prefabName, Vector2 origin, Vector2 target)
    {
        try
        {
            var placers = UnityEngine.Object.FindObjectsOfType<MapMarkerPlacer>(true);
            if (placers.Length == 0)
                return "no MapMarkerPlacer in scene";
            var placer = placerIndex >= 0 && placerIndex < placers.Length ? placers[placerIndex] : placers[0];

            var save = new MapMarkerSaveData
            {
                PlacerIndex = placerIndex,
                PrefabName = prefabName,
                Origin = origin,
                Target = target,
            };
            placer.RestoreMarker(save);
            return "ok";
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[AgentBridge] map draw failed: {ex.Message}");
            return $"draw failed: {ex.Message}";
        }
    }

    public static string ClearAll()
    {
        var cleared = 0;
        foreach (var placer in UnityEngine.Object.FindObjectsOfType<MapMarkerPlacer>(true))
        {
            try { placer.ClearPlacedMarkers(); cleared++; } catch { }
        }
        return $"cleared markers on {cleared} placer(s)";
    }
}
