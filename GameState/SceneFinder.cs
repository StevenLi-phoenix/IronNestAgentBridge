using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace IronNestAgentBridge.GameState;

/// <summary>
/// Debug-only scene search: find live objects by name substring and report where they sit in
/// world, map-local and km coordinates. Read-only, no side effects.
/// </summary>
public static class SceneFinder
{
    /// <summary>Hard result cap. There is no paging; the query is meant to be narrowed instead.</summary>
    public const int MaxHits = 60;

    /// <summary>Longest hierarchy path reported, counted upwards from the hit.</summary>
    private const int MaxPathDepth = 12;

    /// <summary>
    /// Case-insensitive substring search over scene transforms. Prefabs and other assets are
    /// filtered out by the scene validity check — only live instances are of interest.
    ///
    /// The minimum query length is enforced by the HTTP layer, not here.
    /// </summary>
    public static object Find(string nameSubstring)
    {
        var hits = new List<object>();
        var truncated = false;

        var surface = Il2CppSafe.GetRef(() => GameObject.Find(MapReader.MapSurfaceName));
        var surfaceTransform = surface == null ? null : surface.transform;

        Il2CppArrayBase<Transform>? found;
        try { found = Resources.FindObjectsOfTypeAll<Transform>(); }
        catch { return new { count = 0, hits, note = (string?)null }; }
        if (found == null) return new { count = 0, hits, note = (string?)null };

        Il2CppArrayBase<Transform> transforms = found;
        var count = Il2CppSafe.Get(() => transforms.Length, 0);

        for (var i = 0; i < count; i++)
        {
            var index = i;
            var transform = Il2CppSafe.GetRef(() => transforms[index]);
            if (transform == null) continue;

            // Assets and prefabs live outside any scene.
            if (!Il2CppSafe.Get(() => transform.gameObject.scene.IsValid(), false)) continue;

            var name = Il2CppSafe.Get(() => transform.name, "");
            if (name.IndexOf(nameSubstring, StringComparison.OrdinalIgnoreCase) < 0) continue;

            if (hits.Count >= MaxHits)
            {
                truncated = true;
                break;
            }

            var world = Il2CppSafe.Get(() => transform.position, Vector3.zero);
            object? mapLocal = null;

            if (surfaceTransform != null)
            {
                Il2CppSafe.Do(() =>
                {
                    var local = surfaceTransform.InverseTransformPoint(world);
                    var km = MapFrame.LocalToKm(local);
                    mapLocal = new
                    {
                        x = Math.Round(local.x, 3),
                        y = Math.Round(local.y, 3),
                        kmX = Math.Round(km.x, 2),
                        kmY = Math.Round(km.y, 2),
                    };
                });
            }

            hits.Add(new
            {
                path = PathOf(transform),
                active = Il2CppSafe.Get(() => transform.gameObject.activeInHierarchy, false),
                world = new
                {
                    x = Math.Round(world.x, 3),
                    y = Math.Round(world.y, 3),
                    z = Math.Round(world.z, 3),
                },
                mapLocal,
            });
        }

        // Say so explicitly: a silently clipped list reads as "these are all of them".
        return new { count = hits.Count, hits, note = truncated ? $"(truncated at {MaxHits})" : null };
    }

    private static string PathOf(Transform transform)
    {
        var parts = new List<string>();
        var node = transform;

        for (var depth = 0; depth < MaxPathDepth && node != null; depth++)
        {
            parts.Insert(0, Il2CppSafe.Get(() => node!.name, "?"));
            node = Il2CppSafe.GetRef(() => node!.parent);
        }

        return string.Join("/", parts);
    }
}
