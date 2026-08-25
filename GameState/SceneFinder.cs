using UnityEngine;

namespace IronNestAgentBridge.GameState;

/// <summary>Debug scene search: transforms whose name contains a substring, with map-frame coords.</summary>
public static class SceneFinder
{
    public static object Find(string nameSubstring)
    {
        var surface = GameObject.Find("Draggable Surface")?.transform;
        var hits = new List<object>();
        foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
        {
            if (t == null || !t.gameObject.scene.IsValid())
                continue;
            if (t.name.IndexOf(nameSubstring, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            var path = t.name;
            var parent = t.parent;
            for (var depth = 0; parent != null && depth < 12; depth++, parent = parent.parent)
                path = parent.name + "/" + path;

            object? mapLocal = null;
            if (surface != null)
            {
                var local = surface.InverseTransformPoint(t.position);
                mapLocal = new
                {
                    x = Math.Round(local.x, 3),
                    y = Math.Round(local.y, 3),
                    kmX = Math.Round(10.016f + local.x * 3.8164f, 2),
                    kmY = Math.Round(5.235f + local.y * 3.8164f, 2),
                };
            }

            hits.Add(new
            {
                path,
                active = t.gameObject.activeInHierarchy,
                world = new { x = Math.Round(t.position.x, 3), y = Math.Round(t.position.y, 3), z = Math.Round(t.position.z, 3) },
                mapLocal,
            });

            if (hits.Count >= 60)
                break;
        }
        return new { count = hits.Count, hits };
    }
}
