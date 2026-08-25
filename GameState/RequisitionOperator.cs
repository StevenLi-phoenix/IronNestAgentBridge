using System.Collections;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace IronNestAgentBridge.GameState;

/// <summary>
/// Physically operates the Requisition Console the same way FCS's PurchaseDeck does:
/// teleport the punchcard into the reader slot, MoveToSlot, press the buy button.
/// Used for NON-shell cards (recon/scout etc.) — shell purchasing stays FCS's job.
/// Must only run while FCS is idle (caller checks) to avoid fighting over the console.
/// </summary>
public static class RequisitionOperator
{
    private static readonly Vector3 CardSlot = new(6.4814f, -2.4675f, -22.0968f);

    /// <summary>Set by AgentBridgeMod: resolves FCS's shared Requisition CoroutineLock (or null).</summary>
    public static Func<object?>? RequisitionLockProvider;

    public static bool Busy { get; private set; }
    public static string LastResult { get; private set; } = "";

    /// <summary>Find a card by (normalized) id. Returns null and the available list when absent.</summary>
    private static Transform? FindCard(string cardId, out List<string> available)
    {
        available = new List<string>();
        var console = GameObject.Find("Requisition Console");
        if (console == null)
            return null;

        Transform? match = null;
        foreach (var card in console.transform.GetComponentsInChildren<PunchcardRuntime>(true))
        {
            string? id = null;
            try { id = card.CurrentDefinition?.ID; } catch { }
            if (string.IsNullOrWhiteSpace(id))
                continue;
            available.Add(id!);
            if (string.Equals(id, cardId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(id!.Replace("SMOKE", "SMK").Replace("Shell", "").Trim(), cardId, StringComparison.OrdinalIgnoreCase))
                match = card.transform;
        }
        return match;
    }

    /// <summary>
    /// Dump the Requisition Console hierarchy: object names, components, dial values and
    /// card ids — for reverse-engineering extra controls (scout plane start/bearing knobs).
    /// </summary>
    public static object InspectConsole()
    {
        var roots = new List<object>();
        foreach (var rootName in new[] { "Requisition Console", "Console Box" })
        {
            var root = GameObject.Find(rootName);
            if (root == null)
            {
                roots.Add(new { root = rootName, error = "not found" });
                continue;
            }
            var nodes = new List<object>();
            void Walk(Transform t, string path, int depth)
            {
                if (depth > 6) return;
                var comps = new List<string>();
                foreach (var c in t.GetComponents<Component>())
                {
                    if (c == null) continue;
                    string typeName;
                    try { typeName = c.GetIl2CppType().Name; } catch { continue; }
                    if (typeName is "Transform" or "MeshFilter" or "MeshRenderer" or "BoxCollider" or "MeshCollider")
                        continue;
                    var extra = "";
                    try
                    {
                        var dial = c.TryCast<DialInteractable>();
                        if (dial != null) extra = $" value?";
                        var punch = c.TryCast<PunchcardRuntime>();
                        if (punch != null) extra = $" id={punch.CurrentDefinition?.ID}";
                    }
                    catch { }
                    comps.Add(typeName + extra);
                }
                if (comps.Count > 0)
                    nodes.Add(new { path, comps });
                for (var i = 0; i < t.childCount; i++)
                {
                    var child = t.GetChild(i);
                    Walk(child, path + "/" + child.name, depth + 1);
                }
            }
            Walk(root.transform, rootName, 0);
            roots.Add(new { root = rootName, nodes });
        }
        return roots;
    }

    /// <summary>Kick off the physical purchase. Main thread only. Result lands in LastResult and the event log.</summary>
    public static string StartPurchase(string cardId, float? bearingDeg = null, float? distanceKm = null)
    {
        if (Busy)
            return "requisition operator busy with a previous card";

        var card = FindCard(cardId, out var available);
        if (card == null)
            return $"card '{cardId}' not on the console; available: [{string.Join(", ", available)}]";

        Busy = true;
        MelonCoroutines.Start(PurchaseRoutine(cardId, card, bearingDeg, distanceKm));
        return "started (physical purchase takes ~4s; watch events for the outcome)";
    }

    private static IEnumerator PurchaseRoutine(string cardId, Transform card, float? bearingDeg, float? distanceKm)
    {
        // Take FCS's own console lock so its auto-purchases and ours serialize instead of
        // colliding mid-transaction. CoroutineLock.Acquire() yields until the lock is held.
        object? consoleLock = null;
        try { consoleLock = RequisitionLockProvider?.Invoke(); } catch { }
        if (consoleLock != null)
        {
            IEnumerator? acquire = null;
            try { acquire = consoleLock.GetType().GetMethod("Acquire")?.Invoke(consoleLock, null) as IEnumerator; }
            catch { consoleLock = null; }
            if (acquire != null)
                yield return acquire;
        }

        try
        {
            card.position = CardSlot;
            var draggable = card.GetComponent<DraggableItem>();
            if (draggable == null)
            {
                Finish(cardId, "card has no DraggableItem");
                yield break;
            }
            draggable.MoveToSlot();
            yield return new WaitForSeconds(0.6f);

            // Recon-style cards spawn their own console controls (Prefab_ConsoleControls):
            // a bearing dial + distance dial pair. Set them physically before buying.
            if (bearingDeg is not null || distanceKm is not null)
            {
                DialOdometerPunchcardBridge? bridge = null;
                var waitUntil = Time.realtimeSinceStartup + 4f;
                while (bridge == null && Time.realtimeSinceStartup < waitUntil)
                {
                    bridge = UnityEngine.Object.FindObjectOfType<DialOdometerPunchcardBridge>();
                    if (bridge == null)
                        yield return new WaitForSeconds(0.25f);
                }
                if (bridge == null)
                {
                    Finish(cardId, "card accepted but no bearing/distance controls appeared (not a recon card?)");
                    yield break;
                }
                if (bearingDeg is { } b)
                {
                    if (bridge.bearingDial != null)
                        bridge.bearingDial.SetDialValue(b);
                    yield return new WaitForSeconds(0.3f);

                    // The dial's raw range may not be degrees — verify what actually landed
                    // on the card and correct through the bridge's own setter if needed.
                    var applied = float.NaN;
                    try { applied = bridge.Bearing; } catch { }
                    if (float.IsNaN(applied) || Mathf.Abs(Mathf.DeltaAngle(applied, b)) > 1f)
                    {
                        try
                        {
                            bridge.SetBearingInternal(b, true);
                            bridge.ForceRefreshAll();
                            applied = bridge.Bearing;
                        }
                        catch { }
                    }
                    MelonLogger.Msg($"[AgentBridge] scout bearing requested {b:F1}° applied {applied:F1}°");
                    EventLog.Append("requisition", "console", $"scout bearing set: requested {b:F1}°, applied {applied:F1}°");
                }
                if (distanceKm is { } d && bridge.distanceDial != null)
                    bridge.distanceDial.SetDialValue(d);
                yield return new WaitForSeconds(0.5f);
            }

            var console = GameObject.Find("Requisition Console");
            var button = console?.transform.FindChild("Universal Button")?.GetComponent<LookAtTarget>();
            if (button == null)
            {
                Finish(cardId, "buy button not found");
                yield break;
            }

            var deadline = Time.realtimeSinceStartup + 10f;
            while ((!button.isActive || Time.realtimeSinceStartup < button.nextAllowedClickTime)
                   && Time.realtimeSinceStartup < deadline)
                yield return null;

            // Never press a dead button: it silently buys nothing while we report success.
            if (!button.isActive || Time.realtimeSinceStartup < button.nextAllowedClickTime)
            {
                Finish(cardId, "FAILED: buy button never became active — purchase NOT made, retry later");
                yield break;
            }

            button.OnClickDown();
            yield return new WaitForSeconds(0.2f);
            button.OnClickUp();
            yield return new WaitForSeconds(2f);

            Finish(cardId, "ok (button pressed while active)");
        }
        finally
        {
            if (consoleLock != null)
                try { consoleLock.GetType().GetMethod("Release")?.Invoke(consoleLock, null); } catch { }
            Busy = false;
        }
    }

    private static void Finish(string cardId, string result)
    {
        LastResult = result;
        MelonLogger.Msg($"[AgentBridge] requisition '{cardId}' -> {result}");
        EventLog.Append("requisition", "console", $"requisition card '{cardId}' -> {result}");
        Agent.TransactionLog.Write("requisition", $"{cardId} -> {result}");
    }
}
