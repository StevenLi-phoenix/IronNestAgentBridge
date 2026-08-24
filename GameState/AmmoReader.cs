using Il2Cpp;
using UnityEngine;

namespace IronNestAgentBridge.GameState;

/// <summary>
/// Reads which shell punchcards physically exist on the Requisition Console — the
/// authoritative list of what this mission allows buying. Same scan as FCS PurchaseDeck.
/// </summary>
public static class AmmoReader
{
    public static List<string> ReadAvailableShells()
    {
        var result = new List<string>();
        var console = GameObject.Find("Requisition Console");
        if (console == null)
            return result;

        PunchcardRuntime[] cards;
        try { cards = console.transform.GetComponentsInChildren<PunchcardRuntime>(true); }
        catch { return result; }

        foreach (var card in cards)
        {
            string? id;
            try { id = card.CurrentDefinition?.ID; }
            catch { continue; }
            if (string.IsNullOrWhiteSpace(id) || id == "PowderCharges")
                continue;
            var shell = id.Replace("SMOKE", "SMK").Replace("Shell", "").Trim();
            if (shell.Length > 0 && !result.Contains(shell))
                result.Add(shell);
        }
        return result;
    }
}
