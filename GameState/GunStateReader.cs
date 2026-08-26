using Il2Cpp;
using UnityEngine;

namespace IronNestAgentBridge.GameState;

/// <summary>
/// Gun barrel state, read straight off the game objects rather than through FCS. That is the
/// point: an F9 reload of the fire-control system cannot blind us to what is in the chamber.
/// </summary>
public static class GunStateReader
{
    public const string LeftSide = "Left";
    public const string RightSide = "Right";

    /// <summary>
    /// Reads one gun. An unbound gun comes back as a DTO with <c>Bound = false</c> and default
    /// values — never null, so the snapshot always has two rows.
    /// </summary>
    public static GunDto Read(string side)
    {
        var dto = new GunDto { Side = side };

        var host = Il2CppSafe.GetRef(() => GameObject.Find("Gun" + side));
        if (host == null) return dto;

        var gun = Il2CppSafe.GetRef(() => host.GetComponent<GunController>());
        if (gun == null) return dto;

        dto.Bound = true;

        // Raw ShellId, deliberately NOT normalised the way AmmoReader normalises card ids
        // (SMOKE->SMK, "Shell" stripped). Comparing the two directly will mismatch.
        Il2CppSafe.Do(() =>
        {
            var blueprint = gun.ChamberedShellBlueprint;
            if (blueprint == null) return;
            var definition = blueprint.shellDefinition;
            if (definition == null) return;
            dto.ChamberedShell = definition.ShellId;
        });

        Il2CppSafe.Do(() => dto.PowderCharges = gun.PowderCharges);
        Il2CppSafe.Do(() => dto.CanFire = gun.CanFire);
        Il2CppSafe.Do(() => dto.IsReloading = gun.IsReloading);
        Il2CppSafe.Do(() => dto.CurrentElevation = gun.CurrentElevation);

        return dto;
    }

    /// <summary>Fixed order [Left, Right]; the panel and the snapshot both index it positionally.</summary>
    public static List<GunDto> ReadBoth() => new() { Read(LeftSide), Read(RightSide) };
}
