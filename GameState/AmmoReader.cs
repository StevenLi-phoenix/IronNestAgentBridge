using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace IronNestAgentBridge.GameState;

/// <summary>
/// The requisition console: which punch cards are physically on the table, what they cost, how
/// many requisition points are left, and the static specification of every shell type.
/// </summary>
public static class AmmoReader
{
    public const string RequisitionConsoleName = "Requisition Console";

    /// <summary>Powder is a consumable, not a shell type; it must never appear as ammunition.</summary>
    private const string PowderCardId = "PowderCharges";

    // Shell specifications are asset data, so they never change inside a mission — but they only
    // materialise as the mission loads them (a shell's spec appears once a card of that type has
    // been bought and chambered). The cache is therefore per-mission, not per-process: cleared on
    // unbind / scene change, and merged additively on every rescan so a spec learned earlier is
    // never lost to a later scan that happens to miss it.
    private static readonly object SpecLock = new();
    private static readonly Dictionary<string, ShellSpecDto> SpecCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Card id normalisation, shared with <see cref="RequisitionOperator"/> and with the fire
    /// path. The quirks are the game's: the asset ids spell SMOKE where everything else says SMK,
    /// the cluster shell's asset id is PCLM while the FCS bullet enum member is PLCM, and some ids
    /// carry a redundant "Shell" suffix.
    ///
    /// The three replacements and their order are a VERBATIM copy of FCS's
    /// <c>PurchaseDeck.NormalizeCardId</c> (REQUIREMENTS §4: the FCS side is authoritative for
    /// these quirks). Dropping PCLM→PLCM is what once made the cluster shell unfireable — the
    /// model asks for the spelling it is shown, PCLM, and FCS's <c>Enum.Parse</c> only knows PLCM.
    /// The agent's shell whitelist keeps BOTH spellings so either one still classifies as ammunition.
    /// </summary>
    public static string NormalizeShellId(string id)
        => id.Replace("SMOKE", "SMK").Replace("PCLM", "PLCM").Replace("Shell", "").Trim();

    // ---------------------------------------------------------------- cards

    /// <summary>
    /// The punch cards lying on the requisition console — the authoritative list of what this
    /// mission is allowed to buy. No console in the scene simply means no cards, not an error.
    /// </summary>
    public static List<CardDto> ReadCards()
    {
        var result = new List<CardDto>();

        var console = Il2CppSafe.GetRef(() => GameObject.Find(RequisitionConsoleName));
        if (console == null) return result;

        Il2CppArrayBase<PunchcardRuntime>? found;
        // Inactive cards count: a card sitting in a closed drawer is still purchasable.
        try { found = console.transform.GetComponentsInChildren<PunchcardRuntime>(true); }
        catch { return result; }
        if (found == null) return result;

        Il2CppArrayBase<PunchcardRuntime> cards = found;
        var count = Il2CppSafe.Get(() => cards.Length, 0);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < count; i++)
        {
            var index = i;
            var card = Il2CppSafe.GetRef(() => cards[index]);
            if (card == null) continue;

            var definition = Il2CppSafe.GetRef(() => card.CurrentDefinition);
            if (definition == null) continue;

            var rawId = Il2CppSafe.GetRef(() => definition.ID);
            if (string.IsNullOrWhiteSpace(rawId)) continue;
            if (rawId == PowderCardId) continue;

            var id = NormalizeShellId(rawId!);
            // First card of a type wins; duplicates on the table are the same purchase option.
            if (id.Length == 0 || !seen.Add(id)) continue;

            var dto = new CardDto { Id = id };
            Il2CppSafe.Do(() => dto.Cost = definition.Cost);
            Il2CppSafe.Do(() => dto.RemainingUses = definition.RemainingUses);
            Il2CppSafe.Do(() => dto.IsRecon = definition.IsRecon);
            result.Add(dto);
        }

        return result;
    }

    /// <summary>Card ids only, in console order.</summary>
    public static List<string> ReadAvailableShells()
    {
        var ids = new List<string>();
        foreach (var card in ReadCards()) ids.Add(card.Id);
        return ids;
    }

    // ---------------------------------------------------------------- requisition points

    /// <summary>
    /// Remaining requisition points, or null when the tracker cannot be read. "Unknown" and
    /// "zero" must never be conflated: on an unreadable balance every budget gate lets the
    /// purchase through, whereas zero blocks special cards.
    /// </summary>
    public static int? ReadRequisitionPoints()
    {
        try
        {
            var tracker = MissionStatsTracker.Instance;
            if (tracker == null) return null;
            return tracker.requisitionPoints;
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- shell specifications

    /// <summary>
    /// Static shell data scanned off the loaded assets, merged into the per-mission cache.
    /// <see cref="ShellSpecDto.ImpactRadius"/> is in KILOMETRES (HE 0.25, HCHE 0.55, AP 0.15);
    /// displaying it as metres requires x1000.
    /// </summary>
    public static List<ShellSpecDto> ReadShellSpecs()
    {
        var scanned = ScanShellSpecs();

        lock (SpecLock)
        {
            foreach (var spec in scanned)
            {
                // Additive merge: a spec already known stays as it is, nothing is ever removed.
                if (!SpecCache.ContainsKey(spec.Id)) SpecCache[spec.Id] = spec;
            }
            return new List<ShellSpecDto>(SpecCache.Values);
        }
    }

    /// <summary>
    /// Drops the cache. Required on unbind / scene change: shell specifications belong to the
    /// mission that loaded them, and inheriting the previous mission's table would advertise
    /// ammunition this console does not stock.
    /// </summary>
    public static void ClearSpecCache()
    {
        lock (SpecLock) SpecCache.Clear();
    }

    private static List<ShellSpecDto> ScanShellSpecs()
    {
        var result = new List<ShellSpecDto>();

        Il2CppReferenceArray<UnityEngine.Object>? found;
        try { found = Resources.FindObjectsOfTypeAll(Il2CppType.Of<ShellDefinition>()); }
        catch { return result; }
        if (found == null) return result;

        Il2CppReferenceArray<UnityEngine.Object> assets = found;
        var count = Il2CppSafe.Get(() => assets.Length, 0);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < count; i++)
        {
            var index = i;
            var definition = Il2CppSafe.GetRef(() => assets[index]?.TryCast<ShellDefinition>());
            if (definition == null) continue;

            var rawId = Il2CppSafe.GetRef(() => definition.ShellId);
            if (string.IsNullOrWhiteSpace(rawId)) continue;

            var id = NormalizeShellId(rawId!);
            if (id.Length == 0 || !seen.Add(id)) continue;

            var dto = new ShellSpecDto { Id = id };
            Il2CppSafe.Do(() => dto.Damage = definition.Damage);
            Il2CppSafe.Do(() => dto.ImpactRadius = definition.ImpactRadius);
            Il2CppSafe.Do(() => dto.ProjectilesPerShell = definition.projectilesPerShell);
            Il2CppSafe.Do(() => dto.MaxCharges = definition.maxPowderCharges);
            Il2CppSafe.Do(() => dto.ChargeRanges = ReadChargeRanges(definition));
            result.Add(dto);
        }

        return result;
    }

    private static List<ChargeRangeDto> ReadChargeRanges(ShellDefinition definition)
    {
        var ranges = new List<ChargeRangeDto>();

        var mappings = definition.chargeRangeMappings;
        if (mappings == null) return ranges;

        var count = mappings.Length;
        for (var i = 0; i < count; i++)
        {
            var index = i;
            var mapping = Il2CppSafe.GetRef(() => mappings[index]);
            if (mapping == null) continue;

            var dto = new ChargeRangeDto();
            Il2CppSafe.Do(() => dto.Charge = mapping.chargeLevel);
            Il2CppSafe.Do(() => dto.MinKm = mapping.minRange);
            Il2CppSafe.Do(() => dto.MaxKm = mapping.maxRange);
            ranges.Add(dto);
        }

        return ranges;
    }
}
