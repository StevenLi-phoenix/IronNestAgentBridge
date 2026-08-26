using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSleepyNodes;
using MelonLoader;
using UnityEngine;

namespace IronNestAgentBridge.GameState;

/// <summary>
/// Spawns a scout plane straight from its prefab.
///
/// <b>Debug back door / cheat.</b> It bypasses the requisition points a real scout costs, so it is
/// reachable only from <c>POST /scoutplane</c> and never from a tool the LLM can call. The
/// legitimate route is buying a <c>ScoutPlane</c> / <c>ScoutPlane_OnTimeUse</c> punch card.
///
/// Flight and fog reveal are entirely the prefab's own components; nothing is simulated here.
/// </summary>
public static class ScoutPlaneOperator
{
    private const string InstanceName = "AgentBridge ScoutPlane";

    /// <summary>Source tag handed to the impact marker manager's scout registry.</summary>
    private const string SourceTag = "AgentBridge";

    public static object Spawn(float kmX, float kmY, float bearingDeg)
    {
        var surfaceObject = Il2CppSafe.GetRef(() => GameObject.Find(MapReader.MapSurfaceName));
        if (surfaceObject == null)
        {
            return new { error = "Draggable Surface not found (scene unbound?)" };
        }
        var surface = surfaceObject.transform;

        var template = FindSpawnTemplate(out var templateName);
        if (template == null)
        {
            return new { error = "no State_SpawnScoutPlane asset with a PlanePrefab is loaded in this mission" };
        }

        GameObject? instance;
        Vector3 world;
        try
        {
            world = surface.TransformPoint(MapFrame.KmToLocal(kmX, kmY));
            instance = UnityEngine.Object.Instantiate(template);
            if (instance == null) return new { error = "scout plane prefab could not be instantiated" };

            instance.name = InstanceName;
            instance.transform.position = world;
            // 0 degrees is map north (+Y in surface local) and bearings increase clockwise, which
            // is where the negative sign on the Z rotation comes from.
            instance.transform.rotation = surface.rotation * Quaternion.Euler(0f, 0f, -bearingDeg);
            instance.SetActive(true);
        }
        catch (Exception ex)
        {
            return new { error = $"scout plane spawn failed: {ex.Message}" };
        }

        // Not fatal: the plane still flies, it just is not tracked by the scouting system.
        try { ImpactMarkerManager.Instance?.RegisterScoutPlane(instance, SourceTag); }
        catch (Exception ex) { MelonLogger.Warning($"[AgentBridge] RegisterScoutPlane failed: {ex.Message}"); }

        var components = ReadComponentTypeNames(instance);

        MelonLogger.Msg(
            $"[AgentBridge] scout plane spawned from '{templateName}' at km({kmX:F2},{kmY:F2}) brg {bearingDeg:F0}");
        EventLog.Append("scout_plane", "map",
            $"侦察机已起飞: km({kmX:F2},{kmY:F2}) 方位 {bearingDeg:F0}°");
        Agent.TransactionLog.Write("scout_plane",
            $"spawned at km({kmX:F2},{kmY:F2}) brg {bearingDeg:F0}",
            new { templateName, components });

        return new
        {
            result = "ok",
            templateName,
            world = new { x = world.x, y = world.y, z = world.z },
            components,
        };
    }

    /// <summary>First loaded spawn node that actually carries a prefab.</summary>
    private static GameObject? FindSpawnTemplate(out string templateName)
    {
        templateName = "";

        Il2CppReferenceArray<UnityEngine.Object>? found;
        try { found = Resources.FindObjectsOfTypeAll(Il2CppType.Of<State_SpawnScoutPlane>()); }
        catch { return null; }
        if (found == null) return null;

        Il2CppReferenceArray<UnityEngine.Object> nodes = found;
        var count = Il2CppSafe.Get(() => nodes.Length, 0);

        for (var i = 0; i < count; i++)
        {
            var index = i;
            var node = Il2CppSafe.GetRef(() => nodes[index]?.TryCast<State_SpawnScoutPlane>());
            if (node == null) continue;

            var prefab = Il2CppSafe.GetRef(() => node.PlanePrefab);
            if (prefab == null) continue;

            templateName = Il2CppSafe.Get(() => node.name, "");
            return prefab;
        }

        return null;
    }

    /// <summary>De-duplicated Il2Cpp type names on the instance, for reverse engineering.</summary>
    private static List<string> ReadComponentTypeNames(GameObject instance)
    {
        var names = new List<string>();

        Il2CppArrayBase<Component>? found;
        try { found = instance.GetComponentsInChildren<Component>(true); }
        catch { return names; }
        if (found == null) return names;

        Il2CppArrayBase<Component> components = found;
        var count = Il2CppSafe.Get(() => components.Length, 0);

        for (var i = 0; i < count; i++)
        {
            var index = i;
            var component = Il2CppSafe.GetRef(() => components[index]);
            if (component == null) continue;

            var typeName = Il2CppSafe.GetRef(() => component.GetIl2CppType()?.Name);
            if (string.IsNullOrEmpty(typeName)) continue;
            if (!names.Contains(typeName!)) names.Add(typeName!);
        }

        return names;
    }
}
