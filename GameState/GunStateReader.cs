using Il2Cpp;
using UnityEngine;

namespace IronNestAgentBridge.GameState;

/// <summary>
/// Reads physical gun state straight from Il2Cpp.GunController ("GunLeft"/"GunRight").
/// Independent of IronNestFCS; survives its F9 reloads trivially.
/// </summary>
public static class GunStateReader
{
    public static GunDto Read(string side)
    {
        var dto = new GunDto { Side = side };
        var go = GameObject.Find("Gun" + side);
        var gun = go?.GetComponent<GunController>();
        if (gun == null)
            return dto;

        dto.Bound = true;
        try
        {
            var blueprint = gun.ChamberedShellBlueprint;
            if (blueprint != null && blueprint.shellDefinition != null)
                dto.ChamberedShell = blueprint.shellDefinition.ShellId;
        }
        catch { }
        try { dto.PowderCharges = gun.PowderCharges; } catch { }
        try { dto.CanFire = gun.CanFire; } catch { }
        try { dto.IsReloading = gun.IsReloading; } catch { }
        try { dto.CurrentElevation = gun.CurrentElevation; } catch { }
        return dto;
    }

    public static List<GunDto> ReadBoth() => new() { Read("Left"), Read("Right") };
}
