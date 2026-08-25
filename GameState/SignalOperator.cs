using Il2Cpp;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;

namespace IronNestAgentBridge.GameState;

/// <summary>
/// Physically pulls the bunker signal horn: OnClickDown → short hold → OnClickUp on the
/// prop's LookAtTarget — the same event path a player look-click takes. Physical orthodoxy:
/// mission notifications are never injected directly; no horn in the scene = no signal.
/// </summary>
public static class SignalOperator
{
    private static readonly string[] Keywords = { "horn", "signal", "siren", "klaxon", "bugle" };
    private static bool _loggedInventory;

    public static LookAtTarget? FindHorn(out List<string> candidates)
    {
        candidates = new List<string>();
        LookAtTarget? best = null;
        var all = new List<string>();
        foreach (var obj in Resources.FindObjectsOfTypeAll(Il2CppType.Of<LookAtTarget>()))
        {
            var button = obj.TryCast<LookAtTarget>();
            if (button == null)
                continue;
            try
            {
                if (!button.gameObject.scene.IsValid())
                    continue;
                var path = ObjectPath(button.transform, 3);
                all.Add(path);
                if (!Keywords.Any(k => path.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    continue;
                candidates.Add(path);
                best ??= button;
            }
            catch { }
        }

        // No match: dump the interactable inventory once so the real prop name can be
        // identified in the log and added to Keywords.
        if (best == null && !_loggedInventory)
        {
            _loggedInventory = true;
            MelonLogger.Msg($"[AgentBridge] no horn-like LookAtTarget; scene has {all.Count}: {string.Join(" | ", all.Take(60))}");
        }
        return best;
    }

    private static string ObjectPath(Transform? t, int depth)
    {
        var parts = new List<string>();
        while (t != null && depth-- > 0)
        {
            parts.Insert(0, t.name);
            t = t.parent;
        }
        return string.Join("/", parts);
    }
}
