using System.Collections;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;

namespace IronNestAgentBridge.GameState;

/// <summary>
/// Buys a punch card by physically operating the requisition console — slot the card, set the
/// dials, press the button. Nothing here fabricates a purchase or edits a balance.
///
/// Scope: this is the FALLBACK path for non-shell cards. Shell decks go through FCS's
/// <c>PurchaseDeck</c>, and the main road for special cards is FCS's
/// <c>RequestConsoleCard(...)</c> priority queue; the bridge only drives the console itself when
/// that API is absent.
/// </summary>
public static class RequisitionOperator
{
    /// <summary>World position of the console's card slot (measured).</summary>
    public static readonly Vector3 CardSlot = new(6.4814f, -2.4675f, -22.0968f);

    private const string ConsoleRootName = "Requisition Console";
    private const string ConsoleBoxName = "Console Box";
    private const string BuyButtonName = "Universal Button";

    /// <summary>Bearing readback tolerance, degrees.</summary>
    private const float BearingToleranceDeg = 1f;

    /// <summary>Distance readback tolerance, km. One dial detent is finer than this.</summary>
    private const float DistanceToleranceKm = 0.1f;

    private const float ControlsAppearTimeoutSeconds = 4f;
    private const float ButtonActiveTimeoutSeconds = 10f;

    /// <summary>
    /// Resolves FCS's console <c>CoroutineLock</c>. Injected by the mod as
    /// <c>() =&gt; _fcs.GetRequisitionLock()</c> and re-resolved on EVERY call: the FCS logic
    /// lives in a collectible AssemblyLoadContext and a cached reference would pin a dead F9
    /// generation. Null (no FCS) means we drive the console unlocked.
    /// </summary>
    public static Func<object?>? RequisitionLockProvider { get; set; }

    /// <summary>True while a purchase coroutine is in flight; only one card at a time.</summary>
    public static bool Busy { get; private set; }

    /// <summary>Outcome of the most recent purchase, same string as the event carries.</summary>
    public static string LastResult { get; private set; } = "";

    // ---------------------------------------------------------------- entry point

    /// <summary>
    /// Starts a purchase. Main thread only, and non-blocking: the physical sequence takes about
    /// four seconds and reports its outcome through the <c>requisition</c> event.
    /// </summary>
    public static string StartPurchase(string cardId, float? bearingDeg = null, float? distanceKm = null)
    {
        if (Busy) return "requisition operator busy with a previous card";

        var card = FindCard(cardId, out var available);
        if (card == null)
        {
            return $"card '{cardId}' not on the console; available: [{string.Join(", ", available)}]";
        }

        Busy = true;
        try
        {
            MelonCoroutines.Start(PurchaseRoutine(card, cardId, bearingDeg, distanceKm));
        }
        catch (Exception ex)
        {
            // The routine's finally never ran, so the busy flag has to be cleared here or the
            // console stays locked out for the rest of the mission.
            Busy = false;
            return $"could not start the requisition coroutine: {ex.Message}";
        }

        return "started (physical purchase takes ~4s; watch events for the outcome)";
    }

    // ---------------------------------------------------------------- card lookup

    /// <summary>
    /// Finds a card by raw or normalised id, case-insensitively. The LAST match wins, matching
    /// FCS's <c>BuyCardById</c>: when several copies of a type are on the table both components
    /// must reach for the same physical card.
    ///
    /// <paramref name="available"/> collects RAW ids — that list goes into the failure receipt,
    /// where the operator needs to see what is actually printed on the cards.
    /// </summary>
    private static PunchcardRuntime? FindCard(string cardId, out List<string> available)
    {
        available = new List<string>();

        var console = Il2CppSafe.GetRef(() => GameObject.Find(ConsoleRootName));
        if (console == null) return null;

        Il2CppArrayBase<PunchcardRuntime>? found;
        try { found = console.transform.GetComponentsInChildren<PunchcardRuntime>(true); }
        catch { return null; }
        if (found == null) return null;

        Il2CppArrayBase<PunchcardRuntime> cards = found;
        var count = Il2CppSafe.Get(() => cards.Length, 0);

        PunchcardRuntime? match = null;
        for (var i = 0; i < count; i++)
        {
            var index = i;
            var card = Il2CppSafe.GetRef(() => cards[index]);
            if (card == null) continue;

            var rawId = Il2CppSafe.GetRef(() => card.CurrentDefinition?.ID);
            if (string.IsNullOrWhiteSpace(rawId)) continue;

            available.Add(rawId!);

            if (string.Equals(rawId, cardId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(AmmoReader.NormalizeShellId(rawId!), cardId, StringComparison.OrdinalIgnoreCase))
            {
                match = card;
            }
        }

        return match;
    }

    // ---------------------------------------------------------------- purchase coroutine

    private static IEnumerator PurchaseRoutine(PunchcardRuntime card, string cardId,
        float? bearingDeg, float? distanceKm)
    {
        object? heldLock = null;
        try
        {
            // 1. Take FCS's console lock if there is one. No lock simply means no coordination.
            var acquire = TryAcquireLock(out heldLock);
            if (acquire != null) yield return acquire;

            // 2. Slot the card.
            var draggable = InsertCard(card);
            if (draggable == null)
            {
                Finish(cardId, "card has no DraggableItem");
                yield break;
            }

            // 3. Let the slot animation settle.
            Il2CppSafe.Do(() => draggable.MoveToSlot());
            yield return new WaitForSeconds(0.6f);

            // 4. Recon cards grow a control panel; set its dials.
            if (bearingDeg.HasValue || distanceKm.HasValue)
            {
                DialOdometerPunchcardBridge? bridge = null;
                var deadline = Time.realtimeSinceStartup + ControlsAppearTimeoutSeconds;
                while (Time.realtimeSinceStartup < deadline)
                {
                    bridge = Il2CppSafe.GetRef(() => UnityEngine.Object.FindObjectOfType<DialOdometerPunchcardBridge>());
                    if (bridge != null) break;
                    yield return new WaitForSeconds(0.25f);
                }

                if (bridge == null)
                {
                    Finish(cardId, "card accepted but no bearing/distance controls appeared (not a recon card?)");
                    yield break;
                }

                if (bearingDeg.HasValue)
                {
                    // Stage 1: turn the physical dial.
                    RequestBearing(bridge, bearingDeg.Value);
                    yield return new WaitForSeconds(0.3f);
                    // Stages 2 and 3: read back, and compensate through the internal setter when
                    // the dial did not land where we asked.
                    SettleBearing(bridge, bearingDeg.Value);
                }

                if (distanceKm.HasValue)
                {
                    RequestDistance(bridge, distanceKm.Value);
                    yield return new WaitForSeconds(0.3f);
                    SettleDistance(bridge, distanceKm.Value);
                }

                yield return new WaitForSeconds(0.5f);
            }

            // 5. Locate the buy button.
            var button = FindBuyButton();
            if (button == null)
            {
                Finish(cardId, "buy button not found");
                yield break;
            }

            // 6. Wait for it to become live. NEVER press a dead button: the press is swallowed,
            // nothing is bought, and we would report success.
            var buttonDeadline = Time.realtimeSinceStartup + ButtonActiveTimeoutSeconds;
            while (!IsClickable(button))
            {
                if (Time.realtimeSinceStartup >= buttonDeadline)
                {
                    Finish(cardId, "FAILED: buy button never became active — purchase NOT made, retry later");
                    yield break;
                }
                yield return null;
            }

            // 7. Press it the way a player would.
            Il2CppSafe.Do(() => button.OnClickDown());
            yield return new WaitForSeconds(0.2f);
            Il2CppSafe.Do(() => button.OnClickUp());
            yield return new WaitForSeconds(2f);

            Finish(cardId, "ok (button pressed while active)");
        }
        finally
        {
            // Every early exit above passes through here. Failing to release the lock or clear the
            // busy flag would make the console unusable for the rest of the mission.
            ReleaseLock(heldLock);
            Busy = false;
        }
    }

    // ---------------------------------------------------------------- coroutine steps

    private static IEnumerator? TryAcquireLock(out object? heldLock)
    {
        heldLock = null;

        object? gate;
        try { gate = RequisitionLockProvider?.Invoke(); }
        catch { return null; }
        if (gate == null) return null;

        try
        {
            // Explicitly the no-argument overload: CoroutineLock also exposes a priority overload
            // and an ambiguous GetMethod("Acquire") silently resolves to neither.
            var acquire = gate.GetType().GetMethod("Acquire", Type.EmptyTypes);
            if (acquire?.Invoke(gate, null) is not IEnumerator waiter) return null;

            heldLock = gate;
            return waiter;
        }
        catch
        {
            heldLock = null;
            return null;
        }
    }

    private static void ReleaseLock(object? heldLock)
    {
        if (heldLock == null) return;
        try { heldLock.GetType().GetMethod("Release", Type.EmptyTypes)?.Invoke(heldLock, null); }
        catch { /* the logic assembly may already be gone */ }
    }

    private static DraggableItem? InsertCard(PunchcardRuntime card)
    {
        var moved = false;
        Il2CppSafe.Do(() =>
        {
            card.transform.position = CardSlot;
            moved = true;
        });
        if (!moved) return null;

        return Il2CppSafe.GetRef(() => card.GetComponent<DraggableItem>());
    }

    private static LookAtTarget? FindBuyButton()
    {
        var console = Il2CppSafe.GetRef(() => GameObject.Find(ConsoleRootName));
        if (console == null) return null;

        Il2CppArrayBase<LookAtTarget>? found;
        try { found = console.transform.GetComponentsInChildren<LookAtTarget>(true); }
        catch { return null; }
        if (found == null) return null;

        Il2CppArrayBase<LookAtTarget> buttons = found;
        var count = Il2CppSafe.Get(() => buttons.Length, 0);
        for (var i = 0; i < count; i++)
        {
            var index = i;
            var button = Il2CppSafe.GetRef(() => buttons[index]);
            if (button == null) continue;
            if (Il2CppSafe.Get(() => button.gameObject.name, "") == BuyButtonName) return button;
        }

        return null;
    }

    private static bool IsClickable(LookAtTarget button)
        => Il2CppSafe.Get(() => button.isActive && Time.realtimeSinceStartup >= button.nextAllowedClickTime, false);

    // ---------------------------------------------------------------- dials

    private static void RequestBearing(DialOdometerPunchcardBridge bridge, float bearing)
        => Il2CppSafe.Do(() => bridge.bearingDial?.SetDialValue(bearing));

    /// <summary>
    /// Reads the bearing back and, if the dial did not take, forces it through the internal
    /// setter. The physical dial is tried first on purpose — it drives the odometer flaps that the
    /// punch card actually reads.
    /// </summary>
    private static void SettleBearing(DialOdometerPunchcardBridge bridge, float bearing)
    {
        var applied = Il2CppSafe.Get(() => bridge.Bearing, float.NaN);

        if (float.IsNaN(applied) || Math.Abs(Mathf.DeltaAngle(applied, bearing)) > BearingToleranceDeg)
        {
            Il2CppSafe.Do(() =>
            {
                bridge.SetBearingInternal(bearing, true);
                bridge.ForceRefreshAll();
                applied = bridge.Bearing;
            });
        }

        MelonLogger.Msg($"[AgentBridge] scout bearing requested {bearing:F1}° applied {applied:F1}°");
        EventLog.Append("requisition", "console",
            $"侦察卡方位已设定: 请求 {bearing:F1}°, 实际 {applied:F1}°");
    }

    private static void RequestDistance(DialOdometerPunchcardBridge bridge, float distance)
        => Il2CppSafe.Do(() => bridge.distanceDial?.SetDialValue(distance));

    /// <summary>Distance counterpart of <see cref="SettleBearing"/>; MoveDirection depends on it.</summary>
    private static void SettleDistance(DialOdometerPunchcardBridge bridge, float distance)
    {
        var applied = Il2CppSafe.Get(() => bridge.Distance, float.NaN);

        if (float.IsNaN(applied) || Math.Abs(applied - distance) > DistanceToleranceKm)
        {
            Il2CppSafe.Do(() =>
            {
                bridge.SetDistanceInternal(distance, true);
                bridge.ForceRefreshAll();
                applied = bridge.Distance;
            });
        }

        MelonLogger.Msg($"[AgentBridge] scout distance requested {distance:F1}km applied {applied:F1}km");
        EventLog.Append("requisition", "console",
            $"侦察卡距离已设定: 请求 {distance:F1}km, 实际 {applied:F1}km");
    }

    // ---------------------------------------------------------------- outcome

    /// <summary>
    /// Books the outcome in all four places at once: the readable property, the English mod log,
    /// the Chinese battlefield event and the audit trail. The result string itself is the public
    /// contract shared with <c>POST /requisition</c>, so it stays verbatim inside the event.
    /// </summary>
    private static void Finish(string cardId, string result)
    {
        LastResult = result;
        MelonLogger.Msg($"[AgentBridge] requisition '{cardId}' -> {result}");
        EventLog.Append("requisition", "console", $"征用卡 '{cardId}' -> {result}");
        Agent.TransactionLog.Write("requisition", $"{cardId} -> {result}");
    }

    // ---------------------------------------------------------------- console inspection

    /// <summary>
    /// Dumps the console hierarchy — object names, interesting components, card ids — so new
    /// controls can be spotted without a decompiler.
    /// </summary>
    public static object InspectConsole()
    {
        var roots = new List<object>();

        foreach (var rootName in new[] { ConsoleRootName, ConsoleBoxName })
        {
            var root = Il2CppSafe.GetRef(() => GameObject.Find(rootName));
            if (root == null)
            {
                roots.Add(new { root = rootName, error = "not found" });
                continue;
            }

            var nodes = new List<object>();
            Il2CppSafe.Do(() => Walk(root.transform, rootName, 0, nodes));
            roots.Add(new { root = rootName, nodes });
        }

        return roots;
    }

    /// <summary>Structural noise that would drown out the interesting components.</summary>
    private static readonly HashSet<string> BoringComponents = new(StringComparer.Ordinal)
    {
        "Transform", "MeshFilter", "MeshRenderer", "BoxCollider", "MeshCollider",
    };

    private static void Walk(Transform node, string path, int depth, List<object> nodes)
    {
        if (depth > 6) return;

        var comps = new List<string>();
        Il2CppArrayBase<Component>? found = null;
        try { found = node.GetComponents<Component>(); }
        catch { /* no component list for this node */ }

        if (found != null)
        {
            var count = Il2CppSafe.Get(() => found.Length, 0);
            for (var i = 0; i < count; i++)
            {
                var index = i;
                var component = Il2CppSafe.GetRef(() => found[index]);
                if (component == null) continue;

                var typeName = Il2CppSafe.GetRef(() => component.GetIl2CppType()?.Name);
                if (string.IsNullOrEmpty(typeName)) continue;
                if (BoringComponents.Contains(typeName!)) continue;

                comps.Add(typeName! + Annotate(component, typeName!));
            }
        }

        if (comps.Count > 0) nodes.Add(new { path, comps });

        var children = Il2CppSafe.Get(() => node.childCount, 0);
        for (var i = 0; i < children; i++)
        {
            var index = i;
            var child = Il2CppSafe.GetRef(() => node.GetChild(index));
            if (child == null) continue;
            Walk(child, path + "/" + Il2CppSafe.Get(() => child.name, "?"), depth + 1, nodes);
        }
    }

    /// <summary>Extra detail for the two component types whose value is the point of the dump.</summary>
    private static string Annotate(Component component, string typeName)
    {
        if (typeName == nameof(DialInteractable))
        {
            // The dial's live reading. No suffix at all when it cannot be read — a placeholder
            // would look like a value.
            var dial = Il2CppSafe.GetRef(() => component.TryCast<DialInteractable>());
            if (dial == null) return "";

            var value = Il2CppSafe.Get(() => dial.AccumulatedValue, float.NaN);
            return float.IsNaN(value) ? "" : $" value={value:F2}";
        }

        if (typeName == nameof(PunchcardRuntime))
        {
            var punch = Il2CppSafe.GetRef(() => component.TryCast<PunchcardRuntime>());
            if (punch == null) return "";

            var id = Il2CppSafe.GetRef(() => punch.CurrentDefinition?.ID);
            return $" id={id}";
        }

        return "";
    }
}
