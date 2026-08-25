using Il2Cpp;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace IronNestAgentBridge.GameState;

/// <summary>
/// Reads which shell punchcards physically exist on the Requisition Console — the
/// authoritative list of what this mission allows buying. Same scan as FCS PurchaseDeck.
/// </summary>
public static class AmmoReader
{
    public static List<string> ReadAvailableShells() => ReadCards().Select(c => c.Id).ToList();

    private static List<ShellSpecDto>? _specCache;

    /// <summary>
    /// Static shell specs from the game's ShellDefinition assets: blast radius, damage,
    /// submunition count, per-charge min/max range. Cached — asset data never changes.
    /// </summary>
    public static List<ShellSpecDto> ReadShellSpecs()
    {
        if (_specCache is { Count: > 0 })
            return _specCache;

        var result = new List<ShellSpecDto>();
        foreach (var obj in Resources.FindObjectsOfTypeAll(Il2CppType.Of<ShellDefinition>()))
        {
            var def = obj.TryCast<ShellDefinition>();
            if (def == null || string.IsNullOrWhiteSpace(def.ShellId))
                continue;
            var id = def.ShellId.Replace("SMOKE", "SMK").Replace("Shell", "").Trim();
            if (result.Any(s => s.Id == id))
                continue;
            var spec = new ShellSpecDto { Id = id };
            try { spec.Damage = def.Damage; } catch { }
            try { spec.ImpactRadius = def.ImpactRadius; } catch { }
            try { spec.ProjectilesPerShell = def.projectilesPerShell; } catch { }
            try { spec.MaxCharges = def.maxPowderCharges; } catch { }
            try
            {
                if (def.chargeRangeMappings != null)
                    foreach (var m in def.chargeRangeMappings)
                        if (m != null)
                            spec.ChargeRanges.Add(new ChargeRangeDto
                            {
                                Charge = m.chargeLevel,
                                MinKm = m.minRange,
                                MaxKm = m.maxRange,
                            });
            }
            catch { }
            result.Add(spec);
        }
        if (result.Count > 0)
            _specCache = result;
        return result;
    }

    public static List<CardDto> ReadCards()
    {
        var result = new List<CardDto>();
        var console = GameObject.Find("Requisition Console");
        if (console == null)
            return result;

        PunchcardRuntime[] cards;
        try { cards = console.transform.GetComponentsInChildren<PunchcardRuntime>(true); }
        catch { return result; }

        foreach (var card in cards)
        {
            PunchcardDefinitionV2? def;
            try { def = card.CurrentDefinition; }
            catch { continue; }
            var id = def?.ID;
            if (def == null || string.IsNullOrWhiteSpace(id) || id == "PowderCharges")
                continue;
            var shell = id!.Replace("SMOKE", "SMK").Replace("Shell", "").Trim();
            if (shell.Length == 0 || result.Any(c => c.Id == shell))
                continue;
            var dto = new CardDto { Id = shell };
            try { dto.Cost = def.Cost; } catch { }
            try { dto.RemainingUses = def.RemainingUses; } catch { }
            try { dto.IsRecon = def.IsRecon; } catch { }
            result.Add(dto);
        }
        return result;
    }
}
