using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;

namespace IronNestAgentBridge.GameState;

/// <summary>
/// Draws on the tactical map with the player's own tools: the same
/// <c>MapMarkerPlacer.RestoreMarker</c> path the game uses to restore hand-drawn strokes from a
/// save. Save coordinates ARE the km frame (measured), so nothing here converts.
/// </summary>
public static class MapDrawer
{
    /// <summary>Yellow pen — observation strokes (from to).</summary>
    public const string PrefabYellow = "MapMarkerYellow";

    /// <summary>Compass — Origin is the centre, Target a point on the circumference.</summary>
    public const string PrefabCompass = "MapMarkerDiscCompass";

    /// <summary>Red pen — solved points, drawn as a zero-length stroke (Origin == Target).</summary>
    public const string PrefabRed = "MapMarkerRED";

    /// <summary>White pen — exists in the game, currently unused by the bridge.</summary>
    public const string PrefabWhite = "MapMarkerWhite";

    /// <summary>
    /// Appends one stroke. Both points are in the km frame.
    ///
    /// Only the INSTANCE method <c>RestoreMarker</c> may be used. The static
    /// <c>RestoreMissionMarkers(list)</c> is a clear-then-restore-everything operation and would
    /// wipe the player's own drawings along with ours.
    /// </summary>
    public static string Draw(int placerIndex, string prefabName, Vector2 origin, Vector2 target)
    {
        Il2CppArrayBase<MapMarkerPlacer>? found;
        try { found = UnityEngine.Object.FindObjectsOfType<MapMarkerPlacer>(true); }
        catch (Exception ex) { return DrawFailed(ex); }
        if (found == null || found.Length == 0) return "no MapMarkerPlacer in scene";

        Il2CppArrayBase<MapMarkerPlacer> placers = found;

        // Out of range is a caller bug, not something to paper over by silently drawing on a
        // different placer: the HTTP layer turns this into a 400.
        if (placerIndex < 0 || placerIndex >= placers.Length)
        {
            return $"placerIndex {placerIndex} out of range (0..{placers.Length - 1})";
        }

        try
        {
            var placer = placers[placerIndex];
            if (placer == null) return "no MapMarkerPlacer in scene";

            var save = new MapMarkerSaveData
            {
                // The index actually used, so a captured marker round-trips onto the same placer.
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
            return DrawFailed(ex);
        }
    }

    private static string DrawFailed(Exception ex)
    {
        MelonLogger.Warning($"[AgentBridge] map draw failed: {ex.Message}");
        return $"draw failed: {ex.Message}";
    }

    /// <summary>
    /// Reverse-engineering dump: every placer with its prefab catalogue, plus everything currently
    /// captured on the map. Never throws — a capture failure is reported as an entry, not an error.
    /// </summary>
    public static object Inspect()
    {
        var placerRows = new List<object>();
        var capturedRows = new List<object>();

        Il2CppArrayBase<MapMarkerPlacer>? found = null;
        try { found = UnityEngine.Object.FindObjectsOfType<MapMarkerPlacer>(true); }
        catch { /* reported as an empty placer list */ }

        if (found != null)
        {
            var count = Il2CppSafe.Get(() => found.Length, 0);
            for (var i = 0; i < count; i++)
            {
                var index = i;
                var placer = Il2CppSafe.GetRef(() => found[index]);
                if (placer == null) continue;

                placerRows.Add(new
                {
                    name = Il2CppSafe.Get(() => placer.gameObject.name, ""),
                    path = Il2CppSafe.GetRef(() => MapMarkerPlacer.GetHierarchyPath(placer.transform)) ?? "",
                    active = Il2CppSafe.Get(() => placer.isActiveAndEnabled, false),
                    prefabs = ReadPrefabNames(placer),
                    placed = Il2CppSafe.Get(() => placer.placedMarkers == null ? 0 : placer.placedMarkers.Count, 0),
                });
            }
        }

        try
        {
            var captured = MapMarkerPlacer.CaptureMissionMarkers();
            var count = captured == null ? 0 : captured.Count;
            for (var i = 0; i < count; i++)
            {
                var save = captured![i];
                if (save == null) continue;

                Vector2 origin = save.Origin;
                Vector2 target = save.Target;
                capturedRows.Add(new
                {
                    placerIndex = save.PlacerIndex,
                    prefabName = save.PrefabName,
                    origin = new { x = origin.x, y = origin.y },
                    target = new { x = target.x, y = target.y },
                });
            }
        }
        catch (Exception ex)
        {
            capturedRows.Add(new { error = ex.Message });
        }

        return new { placers = placerRows, captured = capturedRows };
    }

    /// <summary>Known prefabs first, then any extra registered prefab, de-duplicated by name.</summary>
    private static List<string> ReadPrefabNames(MapMarkerPlacer placer)
    {
        var names = new List<string>();
        try
        {
            var known = placer.knownMarkerPrefabs;
            if (known != null)
            {
                for (var i = 0; i < known.Count; i++)
                {
                    var prefab = known[i];
                    if (prefab != null && !names.Contains(prefab.name)) names.Add(prefab.name);
                }
            }

            var registered = placer.markerPrefabs;
            if (registered != null)
            {
                for (var i = 0; i < registered.Count; i++)
                {
                    var prefab = registered[i];
                    if (prefab != null && !names.Contains(prefab.name)) names.Add(prefab.name);
                }
            }
        }
        catch
        {
            // Partial catalogue is still useful.
        }
        return names;
    }

    /// <summary>
    /// Wipes every placer. This takes the player's own drawings with it, so it is only ever
    /// reached from an explicit <c>POST /draw/clear</c> — never automatically.
    /// </summary>
    public static string ClearAll()
    {
        var cleared = 0;

        Il2CppArrayBase<MapMarkerPlacer>? found = null;
        try { found = UnityEngine.Object.FindObjectsOfType<MapMarkerPlacer>(true); }
        catch { /* nothing to clear */ }

        if (found != null)
        {
            var count = Il2CppSafe.Get(() => found.Length, 0);
            for (var i = 0; i < count; i++)
            {
                var index = i;
                var placer = Il2CppSafe.GetRef(() => found[index]);
                if (placer == null) continue;

                try
                {
                    placer.ClearPlacedMarkers();
                    cleared++;
                }
                catch
                {
                    // One stubborn placer must not stop the rest.
                }
            }
        }

        return $"cleared markers on {cleared} placer(s)";
    }
}
