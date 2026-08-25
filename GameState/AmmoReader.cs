using Il2Cpp;
using UnityEngine;

namespace IronNestAgentBridge.GameState;

/// <summary>
/// Reads which shell punchcards physically exist on the Requisition Console — the
/// authoritative list of what this mission allows buying. Same scan as FCS PurchaseDeck.
/// </summary>
public static class AmmoReader
{
    public static List<string> ReadAvailableShells() => ReadCards().Select(c => c.Id).ToList();

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
