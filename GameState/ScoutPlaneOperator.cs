using Il2Cpp;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;

namespace IronNestAgentBridge.GameState;

/// <summary>
/// Spawns a scout plane the way mission graphs do: borrow the PlanePrefab from a loaded
/// State_SpawnScoutPlane node asset, instantiate it over the tactical map at a km position
/// with a bearing, and register it with ImpactMarkerManager (the recon system).
/// Experimental — flight/reveal behavior is driven by the prefab's own components.
/// </summary>
public static class ScoutPlaneOperator
{
    private const float MapLocalToKm = 3.8164f;
    private const float MapOffsetX = 10.016f;
    private const float MapOffsetY = 5.235f;

    public static object Spawn(float kmX, float kmY, float bearingDeg)
    {
        var surface = GameObject.Find("Draggable Surface")?.transform;
        if (surface == null)
            return new { error = "Draggable Surface not found (scene unbound?)" };

        GameObject? prefab = null;
        string templateName = "";
        foreach (var node in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Il2CppSleepyNodes.State_SpawnScoutPlane>()))
        {
            var spawn = node.TryCast<Il2CppSleepyNodes.State_SpawnScoutPlane>();
            if (spawn?.PlanePrefab != null)
            {
                prefab = spawn.PlanePrefab;
                templateName = spawn.name;
                break;
            }
        }
        if (prefab == null)
            return new { error = "no State_SpawnScoutPlane asset with a PlanePrefab is loaded in this mission" };

        var local = new Vector3((kmX - MapOffsetX) / MapLocalToKm, (kmY - MapOffsetY) / MapLocalToKm, 0f);
        var world = surface.TransformPoint(local);

        var instance = UnityEngine.Object.Instantiate(prefab);
        instance.name = "AgentBridge ScoutPlane";
        instance.transform.position = world;
        // Bearing 0 = map north (+Y local), clockwise — rotate around the table normal.
        instance.transform.rotation = surface.rotation * Quaternion.Euler(0f, 0f, -bearingDeg);
        instance.SetActive(true);

        try { ImpactMarkerManager.Instance?.RegisterScoutPlane(instance, "AgentBridge"); }
        catch (Exception ex) { MelonLogger.Warning($"[AgentBridge] RegisterScoutPlane failed: {ex.Message}"); }

        var components = new List<string>();
        foreach (var c in instance.GetComponentsInChildren<Component>(true))
            try { if (c != null && !components.Contains(c.GetIl2CppType().Name)) components.Add(c.GetIl2CppType().Name); }
            catch { }

        MelonLogger.Msg($"[AgentBridge] scout plane spawned from '{templateName}' at km({kmX:F2},{kmY:F2}) brg {bearingDeg:F0}");
        EventLog.Append("scout_plane", "map", $"scout plane launched at km({kmX:F2},{kmY:F2}) bearing {bearingDeg:F0}°");
        Agent.TransactionLog.Write("scout_plane", $"spawned at km({kmX:F2},{kmY:F2}) brg {bearingDeg:F0}", new { templateName, components });

        return new { result = "ok", templateName, world = new { world.x, world.y, world.z }, components };
    }
}
