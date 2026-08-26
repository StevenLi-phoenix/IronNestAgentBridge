using System.Collections;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;

namespace IronNestAgentBridge.GameState;

/// <summary>
/// The bunker horn — the physical way to signal High Command.
///
/// Mission notifications are never injected: if the scene has no horn, no signal can be sent, and
/// that is what we report. Pulling it goes down the same event path as a player's look-and-click.
/// </summary>
public static class SignalOperator
{
    /// <summary>
    /// Substrings that identify a horn-like interactable. Whether any of these actually match the
    /// real prop is STILL UNTESTED — the one-shot inventory dump below exists to find out.
    /// </summary>
    private static readonly string[] HornKeywords = { "horn", "signal", "siren", "klaxon", "bugle" };

    /// <summary>Parent levels included in the path used for matching and reporting.</summary>
    private const int PathDepth = 3;

    /// <summary>The inventory dump is worth one line per process, not one per attempt.</summary>
    private static bool _inventoryLogged;

    /// <summary>
    /// Locates the horn. Matching runs against the OBJECT PATH, not just the leaf name, because
    /// the prop's own name is often generic while its parent carries the meaning.
    /// </summary>
    /// <param name="candidates">Every matching path; the first one is returned.</param>
    public static LookAtTarget? FindHorn(out List<string> candidates)
    {
        candidates = new List<string>();
        var all = new List<string>();
        LookAtTarget? best = null;

        Il2CppReferenceArray<UnityEngine.Object>? found;
        try { found = Resources.FindObjectsOfTypeAll(Il2CppType.Of<LookAtTarget>()); }
        catch { return null; }
        if (found == null) return null;

        Il2CppReferenceArray<UnityEngine.Object> objects = found;
        var count = Il2CppSafe.Get(() => objects.Length, 0);

        for (var i = 0; i < count; i++)
        {
            var index = i;
            var target = Il2CppSafe.GetRef(() => objects[index]?.TryCast<LookAtTarget>());
            if (target == null) continue;

            // Prefabs are not in any scene and cannot be clicked.
            if (!Il2CppSafe.Get(() => target.gameObject.scene.IsValid(), false)) continue;

            var path = ObjectPath(Il2CppSafe.GetRef(() => target.transform), PathDepth);
            all.Add(path);

            foreach (var keyword in HornKeywords)
            {
                if (path.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0) continue;

                candidates.Add(path);
                best ??= target;
                break;
            }
        }

        if (best == null && !_inventoryLogged)
        {
            _inventoryLogged = true;
            var listed = all.Count > 60 ? all.GetRange(0, 60) : all;
            MelonLogger.Msg(
                $"[AgentBridge] no horn-like LookAtTarget; scene has {all.Count}: {string.Join(" | ", listed)}");
        }

        return best;
    }

    /// <summary>
    /// Pulls the horn: down, a beat, up. Main thread only. Returns the receipt shown to the
    /// commander and the LLM.
    /// </summary>
    public static string Sound()
    {
        var horn = FindHorn(out var candidates);
        if (horn == null)
        {
            return "本关场景中没有找到号角装置(无匹配horn/signal/siren的交互件) — 无法发出信号";
        }

        var name = Il2CppSafe.Get(() => horn.gameObject.name, "?");

        // Never press a dead control: the click is swallowed and we would claim a signal that
        // never went out.
        if (!Il2CppSafe.Get(() => horn.isActive, false))
        {
            return $"号角 '{name}' 当前不可交互 — 可能尚未满足拉响条件";
        }

        Il2CppSafe.Do(() => horn.OnClickDown());
        MelonCoroutines.Start(ReleaseAfterBeat(horn));

        var extra = candidates.Count > 1 ? $" (场景候选: {string.Join(", ", candidates)})" : "";
        EventLog.Append("signal", "game", $"号角已拉响: {name}{extra}");

        return $"号角已拉响: {name}";
    }

    private static IEnumerator ReleaseAfterBeat(LookAtTarget horn)
    {
        yield return new WaitForSeconds(0.15f);
        Il2CppSafe.Do(() => horn.OnClickUp());
    }

    /// <summary>Leaf name plus up to <paramref name="depth"/>-1 parents, joined with '/'.</summary>
    private static string ObjectPath(Transform? node, int depth)
    {
        var parts = new List<string>();
        for (var level = 0; level < depth && node != null; level++)
        {
            parts.Insert(0, Il2CppSafe.Get(() => node!.name, "?"));
            node = Il2CppSafe.GetRef(() => node!.parent);
        }
        return string.Join("/", parts);
    }
}
