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

    /// <summary>Kick off the physical purchase. Main thread only. Result lands in LastResult and the event log.</summary>
    public static string StartPurchase(string cardId)
    {
        if (Busy)
            return "requisition operator busy with a previous card";

        var card = FindCard(cardId, out var available);
        if (card == null)
            return $"card '{cardId}' not on the console; available: [{string.Join(", ", available)}]";

        Busy = true;
        MelonCoroutines.Start(PurchaseRoutine(cardId, card));
        return "started (physical purchase takes ~3s; watch events for the outcome)";
    }

    private static IEnumerator PurchaseRoutine(string cardId, Transform card)
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

            var console = GameObject.Find("Requisition Console");
            var button = console?.transform.FindChild("Universal Button")?.GetComponent<LookAtTarget>();
            if (button == null)
            {
                Finish(cardId, "buy button not found");
                yield break;
            }

            var deadline = Time.realtimeSinceStartup + 5f;
            while ((!button.isActive || Time.realtimeSinceStartup < button.nextAllowedClickTime)
                   && Time.realtimeSinceStartup < deadline)
                yield return null;

            button.OnClickDown();
            yield return new WaitForSeconds(0.2f);
            button.OnClickUp();
            yield return new WaitForSeconds(2f);

            Finish(cardId, "ok");
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
